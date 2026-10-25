using System.Security.Cryptography;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Approvals;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Qc;
using FactoryBrain.Application.Validation;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// The inspection workflow over <c>qc_inspections</c>. Every state change is
/// checked against <see cref="QcLifecycle"/> and written with the audit
/// entry in the same transaction; reviews use the row's <c>xmin</c> as an
/// optimistic concurrency token, and creation honours a per-user
/// <c>Idempotency-Key</c> (enforced by a unique index).
/// </summary>
public sealed class QcInspectionService : IQcInspectionService
{
    public const string EntityType = "qc_inspection";

    private readonly FactoryBrainDbContext _db;
    private readonly IQcImageStore _store;
    private readonly IDefectDetectionProvider _provider;
    private readonly QcOptions _options;
    private readonly IQcWorkSignal _signal;
    private readonly ILogger<QcInspectionService> _log;

    public QcInspectionService(FactoryBrainDbContext db, IQcImageStore store, IDefectDetectionProvider provider,
        QcOptions options, IQcWorkSignal signal, ILogger<QcInspectionService> log)
    {
        _db = db; _store = store; _provider = provider; _options = options; _signal = signal; _log = log;
    }

    // ── Create ──────────────────────────────────────────────────────────────

    public async Task<QcCreateResult> CreateAsync(QcCreateInspectionInput input, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = QcCreateInputValidation.Validate(input);
        if (!QcImageInspector.TryInspect(input.Image, _options.MaxImageBytes, out var image, out var imageError))
        {
            errors["image"] = new[]
            {
                imageError == QcImageInspector.ErrTooLarge
                    ? $"The image is larger than {_options.MaxImageBytes / (1024 * 1024.0):0.#} MB."
                    : QcImageInspector.Describe(imageError!),
            };
        }

        var active = await _db.QcDefectCategories.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var requested = input.CategoryCodes.Count == 0
            ? active.Select(c => c.Code).ToList()
            : input.CategoryCodes.Distinct(StringComparer.Ordinal).ToList();
        if (!errors.ContainsKey("categoryCodes"))
        {
            var unknown = requested.Where(c => active.All(a => a.Code != c)).ToList();
            if (active.Count == 0) errors["categoryCodes"] = new[] { "No active defect categories exist. An Admin must add one first." };
            else if (unknown.Count > 0) errors["categoryCodes"] = new[] { $"Unknown or inactive categories: {string.Join(", ", unknown)}." };
        }
        if (errors.Count > 0) return new QcCreateResult(QcCreateOutcome.Invalid, null, errors);

        QcWire.TryParseStage(input.Stage, out var stage);
        var sha = Convert.ToHexString(SHA256.HashData(input.Image)).ToLowerInvariant();
        var key = input.IdempotencyKey;

        if (key is not null && actor.UserId is { } uid)
        {
            var existing = await _db.QcInspections.AsNoTracking()
                .FirstOrDefaultAsync(i => i.CreatedByUserId == uid && i.IdempotencyKey == key, ct);
            if (existing is not null) return await ReplayAsync(existing, sha, ct);
        }

        var storageKey = await _store.SaveAsync(input.Image, image!.Extension, ct);
        var status = _provider.Describe();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var inspection = new QcInspection
        {
            Id = id,
            Reference = $"QC-{now:yyyyMMdd}-{id.ToString("N")[..8].ToUpperInvariant()}",
            ProductType = input.ProductType.Trim(),
            Stage = stage,
            LineId = Clean(input.LineId),
            ProductionOrder = Clean(input.ProductionOrder),
            Notes = Clean(input.Notes),
            RequestedCategoryCodes = requested,
            ImageStorageKey = storageKey,
            ImageContentType = image.ContentType,
            ImageSizeBytes = input.Image.LongLength,
            ImageSha256 = sha,
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            OriginalFileName = QcCreateInputValidation.SanitizeFileName(input.OriginalFileName),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = actor.UserId,
            CreatedByEmail = actor.Email,
            IdempotencyKey = key,
        };
        if (status.Configured)
        {
            inspection.Status = QcInspectionStatus.Pending;
        }
        else
        {
            // Nothing can analyse it: record that honestly instead of queueing work that can only fail.
            inspection.Status = QcInspectionStatus.Failed;
            inspection.FailureCode = DefectDetectionException.NotConfigured;
            inspection.FailureMessage = Truncate(status.Reason, 500);
            inspection.Provider = status.Mode;
        }
        _db.QcInspections.Add(inspection);
        _db.AuditLog.Add(Audit(actor, traceId, "qc_inspection.created", id, null, QcWire.Status(inspection.Status),
            $"{inspection.Reference} · {inspection.ProductType} · {QcWire.Stage(stage)}{(inspection.LineId is null ? "" : " · " + inspection.LineId)}"));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (key is not null && IsUniqueViolation(ex, "IdempotencyKey"))
        {
            // A concurrent request with the same key won the race.
            await _store.DeleteAsync(storageKey, CancellationToken.None);
            _db.ChangeTracker.Clear();
            var winner = await _db.QcInspections.AsNoTracking()
                .FirstAsync(i => i.CreatedByUserId == actor.UserId && i.IdempotencyKey == key, ct);
            return await ReplayAsync(winner, sha, ct);
        }
        catch
        {
            await _store.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }

        _log.LogInformation("QC inspection {InspectionId} created by user {UserId}: status={Status}.", id, actor.UserId, inspection.Status);
        if (inspection.Status == QcInspectionStatus.Pending) _signal.Notify();
        return new QcCreateResult(QcCreateOutcome.Created, await GetAsync(id, ct));
    }

    private async Task<QcCreateResult> ReplayAsync(QcInspection existing, string sha, CancellationToken ct) =>
        existing.ImageSha256 == sha
            ? new QcCreateResult(QcCreateOutcome.Replayed, await GetAsync(existing.Id, ct))
            : new QcCreateResult(QcCreateOutcome.IdempotencyConflict, null, new Dictionary<string, string[]>
            {
                ["idempotencyKey"] = new[] { "This Idempotency-Key was already used for a different image." },
            });

    // ── Read ────────────────────────────────────────────────────────────────

    public static Dictionary<string, string[]> ValidateQuery(QcInspectionQuery q)
    {
        var errors = new Dictionary<string, string[]>();
        if (q.Status is not null && !QcWire.TryParseStatus(q.Status, out _))
            errors["status"] = new[] { $"status must be one of: {string.Join(", ", QcWire.Statuses)}" };
        if (q.Stage is not null && !QcWire.TryParseStage(q.Stage, out _))
            errors["stage"] = new[] { $"stage must be one of: {string.Join(", ", QcWire.Stages)}" };
        if (q.Outcome is not null && !QcWire.TryParseOutcome(q.Outcome, out _))
            errors["outcome"] = new[] { $"outcome must be one of: {string.Join(", ", QcWire.Outcomes)}" };
        if (q.Page < 1) errors["page"] = new[] { "page must be 1 or more." };
        if (q.PageSize is < 1 or > QcLimits.MaxPageSize) errors["pageSize"] = new[] { $"pageSize must be between 1 and {QcLimits.MaxPageSize}." };
        if (q.Q is { Length: > 100 }) errors["q"] = new[] { "Search text is limited to 100 characters." };
        if (q.Sort is not null and not ("newest" or "oldest")) errors["sort"] = new[] { "sort must be newest or oldest." };
        if (q.From is { } f && q.To is { } t && f > t) errors["from"] = new[] { "from must not be after to." };
        return errors;
    }

    public async Task<QcInspectionPage> ListAsync(QcInspectionQuery q, CancellationToken ct)
    {
        var query = Filter(_db.QcInspections.AsNoTracking(), q.Status, q.Stage, q.LineId, q.ProductType, q.Outcome, q.From, q.To);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var pattern = "%" + EscapeLike(q.Q.Trim()) + "%";
            query = query.Where(i =>
                EF.Functions.ILike(i.Reference, pattern, "\\") ||
                EF.Functions.ILike(i.ProductType, pattern, "\\") ||
                (i.ProductionOrder != null && EF.Functions.ILike(i.ProductionOrder, pattern, "\\")) ||
                (i.LineId != null && EF.Functions.ILike(i.LineId, pattern, "\\")) ||
                (i.OriginalFileName != null && EF.Functions.ILike(i.OriginalFileName, pattern, "\\")));
        }

        var total = await query.CountAsync(ct);
        var ordered = q.Sort == "oldest"
            ? query.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            : query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id);
        var items = await ordered
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(i => new
            {
                i.Id, i.Reference, i.Status, i.ProductType, i.Stage, i.LineId, i.ProductionOrder, i.CreatedAt, i.CreatedByEmail,
                i.Outcome, i.Provider, i.Model, i.FailureCode,
                Predictions = i.Findings.Count(f => f.Source == QcFindingSource.Model),
                Verified = i.Findings.Count(f =>
                    (f.Source == QcFindingSource.Inspector && f.Decision != QcFindingDecision.Rejected) ||
                    (f.Source == QcFindingSource.Model && (f.Decision == QcFindingDecision.Accepted || f.Decision == QcFindingDecision.Corrected))),
                MaxConfidence = i.Findings.Where(f => f.Source == QcFindingSource.Model).Max(f => f.Confidence),
            })
            .ToListAsync(ct);

        return new QcInspectionPage(
            items.Select(i => new QcInspectionListItem(i.Id, i.Reference, QcWire.Status(i.Status), i.ProductType, QcWire.Stage(i.Stage),
                i.LineId, i.ProductionOrder, i.CreatedAt, i.CreatedByEmail, i.Predictions,
                i.Status == QcInspectionStatus.Reviewed ? i.Verified : 0, i.MaxConfidence,
                i.Outcome is { } o ? QcWire.Outcome(o) : null, i.Provider, i.Model, i.FailureCode)).ToList(),
            q.Page, q.PageSize, total, (int)Math.Ceiling(total / (double)q.PageSize));
    }

    public async Task<QcInspectionDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        var i = await _db.QcInspections.AsNoTracking().Include(x => x.Findings).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return null;
        var names = await _db.QcDefectCategories.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.Name, ct);
        ApprovalRequest? approval = i.FollowUpApprovalId is null ? null
            : await _db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == i.FollowUpApprovalId, ct);
        return ToDetail(i, names, approval);
    }

    public async Task<QcImageContent?> OpenImageAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.QcInspections.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new { i.ImageStorageKey, i.ImageContentType, i.ImageSizeBytes, i.ImageSha256 })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var stream = await _store.OpenReadAsync(row.ImageStorageKey, ct);
        return stream is null ? null : new QcImageContent(stream, row.ImageContentType, row.ImageSizeBytes, row.ImageSha256);
    }

    // ── Review ──────────────────────────────────────────────────────────────

    public async Task<QcReviewResult> ReviewAsync(Guid id, QcReviewRequest req, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var inspection = await _db.QcInspections.Include(x => x.Findings).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (inspection is null) return new QcReviewResult(QcReviewOutcome.NotFound);
            if (!QcLifecycle.CanReview(inspection.Status)) return new QcReviewResult(QcReviewOutcome.InvalidState);
            if (req.ExpectedVersion != inspection.Version) return new QcReviewResult(QcReviewOutcome.VersionConflict);

            var categories = await _db.QcDefectCategories.AsNoTracking().ToDictionaryAsync(c => c.Code, ct);
            var errors = new Dictionary<string, string[]>();
            void Add(string k, string m) => errors[k] = errors.TryGetValue(k, out var e) ? e.Append(m).ToArray() : new[] { m };
            bool ActiveCategory(string? code) => code is not null && categories.TryGetValue(code, out var c) && c.IsActive;

            var byId = inspection.Findings.ToDictionary(f => f.Id);
            var decisions = req.Decisions ?? Array.Empty<QcFindingDecisionInput>();
            foreach (var d in decisions)
            {
                if (!byId.ContainsKey(d.FindingId)) { Add("decisions", $"Finding {d.FindingId} does not belong to this inspection."); continue; }
                if (d.CorrectedCategoryCode is not null && !ActiveCategory(d.CorrectedCategoryCode))
                    Add("decisions", $"Unknown or inactive category '{d.CorrectedCategoryCode}'.");
            }
            var decidedNow = decisions.Select(d => d.FindingId).ToHashSet();
            var undecided = inspection.Findings
                .Where(f => f.Source == QcFindingSource.Model && f.Decision == QcFindingDecision.Unreviewed && !decidedNow.Contains(f.Id))
                .ToList();
            if (undecided.Count > 0) Add("decisions", $"Decide every model prediction before completing the review ({undecided.Count} left).");
            // A prediction with no mapped category can only be accepted by correcting its category.
            foreach (var d in decisions)
            {
                if (byId.TryGetValue(d.FindingId, out var f) && f.CategoryCode is null && d.CorrectedCategoryCode is null
                    && QcWire.TryParseDecision(d.Decision, out var dec) && dec != QcFindingDecision.Rejected)
                    Add("decisions", $"Prediction '{f.RawLabel}' matches no category: correct it to a category or reject it.");
            }
            var additions = req.Additions ?? Array.Empty<QcFindingAdditionInput>();
            foreach (var a in additions)
                if (!ActiveCategory(a.CategoryCode)) Add("additions", $"Unknown or inactive category '{a.CategoryCode}'.");
            if (inspection.Findings.Count + additions.Count > QcLimits.MaxFindingsPerInspection)
                Add("additions", $"An inspection holds at most {QcLimits.MaxFindingsPerInspection} findings.");
            if (errors.Count > 0) return new QcReviewResult(QcReviewOutcome.Invalid, null, errors);

            var now = DateTime.UtcNow;
            int accepted = 0, corrected = 0, rejected = 0;
            foreach (var d in decisions)
            {
                var f = byId[d.FindingId];
                QcWire.TryParseDecision(d.Decision, out var decision);
                f.Decision = decision;
                if (decision == QcFindingDecision.Corrected)
                {
                    f.CorrectedCategoryCode = d.CorrectedCategoryCode;
                    f.CorrectedSeverity = QcWire.TryParseSeverity(d.CorrectedSeverity, out var s) ? s : null;
                    corrected++;
                }
                else
                {
                    f.CorrectedCategoryCode = null;
                    f.CorrectedSeverity = null;
                    if (decision == QcFindingDecision.Accepted) accepted++; else rejected++;
                }
                f.ReviewerNote = Clean(d.Note);
                f.ReviewedAt = now;
            }
            foreach (var a in additions)
            {
                var category = categories[a.CategoryCode!];
                var box = a.Box is null ? null : QcDetectionNormalizer.ValidBox(new NormalizedBox(a.Box.X, a.Box.Y, a.Box.Width, a.Box.Height));
                var finding = new QcInspectionFinding
                {
                    InspectionId = inspection.Id,
                    Source = QcFindingSource.Inspector,
                    CategoryCode = category.Code,
                    Severity = QcWire.TryParseSeverity(a.Severity, out var s) ? s : category.DefaultSeverity,
                    BoxX = box?.X, BoxY = box?.Y, BoxWidth = box?.Width, BoxHeight = box?.Height,
                    Decision = QcFindingDecision.Accepted,
                    ReviewerNote = Clean(a.Note),
                    ReviewedAt = now,
                    CreatedAt = now,
                };
                _db.QcInspectionFindings.Add(finding);
                inspection.Findings.Add(finding);
            }

            var verified = inspection.Findings.Where(f => QcMetricsCalculator.IsVerified(f.Source, f.Decision)).ToList();
            QcWire.TryParseOutcome(req.Outcome, out var outcome);
            if (outcome != QcInspectionOutcome.Pass && verified.Count == 0 && string.IsNullOrWhiteSpace(req.Notes))
                return new QcReviewResult(QcReviewOutcome.Invalid, null, new Dictionary<string, string[]>
                {
                    ["notes"] = new[] { "Explain a Rework or Reject outcome when no defect is recorded." },
                });

            var from = inspection.Status;
            inspection.Status = QcInspectionStatus.Reviewed;
            inspection.Outcome = outcome;
            inspection.ReviewNotes = Clean(req.Notes);
            inspection.ReviewedAt = now;
            inspection.ReviewedByUserId = actor.UserId;
            inspection.ReviewedByEmail = actor.Email;
            inspection.UpdatedAt = now;

            var summary = $"outcome={QcWire.Outcome(outcome)}; accepted={accepted}; corrected={corrected}; rejected={rejected}; added={additions.Count}";
            _db.AuditLog.Add(Audit(actor, traceId, "qc_inspection.reviewed", inspection.Id, QcWire.Status(from), QcWire.Status(inspection.Status),
                inspection.ReviewNotes is null ? summary : $"{summary}. {inspection.ReviewNotes}"));

            if (req.RequestFollowUp && inspection.FollowUpApprovalId is null)
                inspection.FollowUpApprovalId = AddFollowUp(inspection, verified, categories, req.FollowUpNote, actor, traceId, now);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct);
                return new QcReviewResult(QcReviewOutcome.VersionConflict);
            }
            await tx.CommitAsync(ct);
            _log.LogInformation("QC inspection {InspectionId} reviewed by user {UserId}: {Summary}.", inspection.Id, actor.UserId, summary);
            return new QcReviewResult(QcReviewOutcome.Ok);
        });

        return result.Outcome == QcReviewOutcome.Ok ? result with { Inspection = await GetAsync(id, ct) } : result;
    }

    /// <summary>A corrective-action insight and its pending approval, through the existing approval queue.</summary>
    private string AddFollowUp(QcInspection i, IReadOnlyList<QcInspectionFinding> verified,
        IReadOnlyDictionary<string, QcDefectCategory> categories, string? note, AuditActor actor, string? traceId, DateTime now)
    {
        var worst = verified.Count == 0 ? (QcDefectSeverity?)null : verified.Max(f => f.CorrectedSeverity ?? f.Severity);
        var risk = worst switch { QcDefectSeverity.Critical => RiskTier.High, QcDefectSeverity.Major => RiskTier.Medium, _ => RiskTier.Low };
        var defectList = verified.Count == 0 ? "no recorded defect"
            : string.Join(", ", verified
                .GroupBy(f => f.CorrectedCategoryCode ?? f.CategoryCode)
                .Select(g => $"{g.Count()}× {(g.Key is not null && categories.TryGetValue(g.Key, out var c) ? c.Name : g.Key ?? "unmapped")}"));
        var where = i.LineId is null ? i.ProductType : $"{i.ProductType} on {i.LineId}";
        var stamp = Guid.NewGuid().ToString("N");
        var insight = new Insight
        {
            Id = $"ins-qc-{stamp}",
            AgentId = "qc-inspection",
            AgentLabel = "QC inspection",
            Title = $"Corrective action: {i.Reference} ({where})",
            TitleBn = $"সংশোধনমূলক ব্যবস্থা: {i.Reference}",
            Finding = $"Inspection {i.Reference} was reviewed as {QcWire.Outcome(i.Outcome!.Value)} with {defectList}." +
                      (string.IsNullOrWhiteSpace(note) ? "" : $" Inspector note: {note.Trim()}"),
            FindingBn = $"পরিদর্শন {i.Reference}: {QcWire.Outcome(i.Outcome!.Value)}।",
            Stage = Stage.PendingApproval,
            Recommendation = new InsightRecommendation
            {
                Title = "Investigate and correct the defect source",
                TitleBn = "ত্রুটির উৎস তদন্ত ও সংশোধন করুন",
                Action = $"Check the operation and machine settings behind {defectList} on {where}, quarantine affected bundles, and re-inspect.",
                ActionBn = "প্রভাবিত বান্ডল আলাদা করুন এবং পুনরায় পরিদর্শন করুন।",
                RiskTier = risk,
                TargetStage = Stage.PendingApproval,
            },
            Evidence = new List<EvidenceRow> { new() { Domain = "qc_inspections", Count = 1, PreviewIds = new List<string> { i.Id.ToString() } } },
            // Inspector-verified, not a model score.
            Confidence = 1.0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var approval = new ApprovalRequest
        {
            Id = $"apr-qc-{stamp}",
            InsightId = insight.Id,
            Title = insight.Title,
            TitleBn = insight.TitleBn,
            Reason = $"QC follow-up requested by {actor.Email}",
            RiskTier = risk,
            Stage = Stage.PendingApproval,
            CreatedAt = now,
        };
        _db.Insights.Add(insight);
        _db.Approvals.Add(approval);
        _db.AuditLog.Add(Audit(actor, traceId, "qc_inspection.follow_up_requested", i.Id, null, StageNames.ToWire(Stage.PendingApproval),
            $"approval {approval.Id} · risk {StageNames.ToWire(risk)}"));
        return approval.Id;
    }

    // ── Retry ───────────────────────────────────────────────────────────────

    public async Task<QcRetryResult> RetryAsync(Guid id, AuditActor actor, string? traceId, CancellationToken ct)
    {
        if (!_provider.Describe().Configured)
        {
            return await _db.QcInspections.AnyAsync(i => i.Id == id, ct)
                ? new QcRetryResult(QcRetryOutcome.ProviderNotConfigured)
                : new QcRetryResult(QcRetryOutcome.NotFound);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var now = DateTime.UtcNow;
            var changed = await _db.QcInspections
                .Where(i => i.Id == id && i.Status == QcInspectionStatus.Failed)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.Status, QcInspectionStatus.Pending)
                    .SetProperty(i => i.FailureCode, (string?)null)
                    .SetProperty(i => i.FailureMessage, (string?)null)
                    .SetProperty(i => i.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(i => i.Attempts, 0)
                    .SetProperty(i => i.UpdatedAt, now), ct);
            if (changed == 0)
            {
                await tx.RollbackAsync(ct);
                return await _db.QcInspections.AnyAsync(i => i.Id == id, ct) ? QcRetryOutcome.InvalidState : QcRetryOutcome.NotFound;
            }
            _db.AuditLog.Add(Audit(actor, traceId, "qc_inspection.retried", id, QcWire.Status(QcInspectionStatus.Failed),
                QcWire.Status(QcInspectionStatus.Pending), null));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return QcRetryOutcome.Ok;
        });

        if (outcome != QcRetryOutcome.Ok) return new QcRetryResult(outcome);
        _signal.Notify();
        return new QcRetryResult(QcRetryOutcome.Ok, await GetAsync(id, ct));
    }

    // ── Summary ─────────────────────────────────────────────────────────────

    public static Dictionary<string, string[]> ValidateSummary(QcSummaryQuery q)
    {
        var errors = new Dictionary<string, string[]>();
        if (q.Days is { } d && (d < 1 || d > QcLimits.MaxSummaryDays))
            errors["days"] = new[] { $"days must be between 1 and {QcLimits.MaxSummaryDays}." };
        if (q.Stage is not null && !QcWire.TryParseStage(q.Stage, out _))
            errors["stage"] = new[] { $"stage must be one of: {string.Join(", ", QcWire.Stages)}" };
        if (q.From is { } f && q.To is { } t)
        {
            if (f.Date > t.Date) errors["from"] = new[] { "from must not be after to." };
            else if ((t.Date - f.Date).TotalDays + 1 > QcLimits.MaxSummaryDays)
                errors["to"] = new[] { $"The range is limited to {QcLimits.MaxSummaryDays} days." };
        }
        return errors;
    }

    public static (DateTime From, DateTime To) ResolveRange(QcSummaryQuery q, DateTime nowUtc)
    {
        if (q.From is { } f && q.To is { } t) return (ToUtc(f).Date, ToUtc(t).Date);
        var days = q.Days ?? QcLimits.DefaultSummaryDays;
        var to = q.To is { } onlyTo ? ToUtc(onlyTo).Date : nowUtc.Date;
        var from = q.From is { } onlyFrom ? ToUtc(onlyFrom).Date : to.AddDays(-(days - 1));
        if ((to - from).TotalDays + 1 > QcLimits.MaxSummaryDays) from = to.AddDays(-(QcLimits.MaxSummaryDays - 1));
        return (from, to);
    }

    public async Task<QcSummaryResponse> SummaryAsync(QcSummaryQuery q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (from, to) = ResolveRange(q, now);
        var inRange = Filter(_db.QcInspections.AsNoTracking(), null, q.Stage, q.LineId, q.ProductType, null, from, to);

        var inspections = await inRange
            .Select(i => new QcMetricInspection(i.Id, i.CreatedAt, i.LineId, i.Status, i.Outcome))
            .ToListAsync(ct);
        var rawFindings = await _db.QcInspectionFindings.AsNoTracking()
            .Where(f => inRange.Select(i => i.Id).Contains(f.InspectionId))
            .Select(f => new { f.InspectionId, f.Source, f.Decision, f.CategoryCode, f.CorrectedCategoryCode, f.Severity, f.CorrectedSeverity })
            .ToListAsync(ct);
        var findings = rawFindings
            .Select(f => new QcMetricFinding(f.InspectionId, f.Source, f.Decision,
                f.CorrectedCategoryCode ?? f.CategoryCode, f.CorrectedSeverity ?? f.Severity))
            .ToList();
        var names = await _db.QcDefectCategories.AsNoTracking().ToDictionaryAsync(c => c.Code, c => c.Name, ct);
        return QcMetricsCalculator.Calculate(from, to, inspections, findings, names, now);
    }

    public QcVisionStatus VisionStatus()
    {
        var s = _provider.Describe();
        return new QcVisionStatus(s.Configured, s.Mode, s.Provider, s.Model, s.LocalizationSupported, s.Reason,
            _options.MinConfidence, _options.MaxImageBytes, QcImageInspector.AcceptedContentTypes,
            s.Mode == QcOptions.ModeLlm
                ? "Confidence is the vision-language model's own estimate, not a calibrated probability; boxes are approximate."
                : "Confidence is the score reported by the detection model.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IQueryable<QcInspection> Filter(IQueryable<QcInspection> q, string? status, string? stage, string? lineId,
        string? productType, string? outcome, DateTime? from, DateTime? to)
    {
        if (QcWire.TryParseStatus(status, out var st)) q = q.Where(i => i.Status == st);
        if (QcWire.TryParseStage(stage, out var sg)) q = q.Where(i => i.Stage == sg);
        if (QcWire.TryParseOutcome(outcome, out var oc)) q = q.Where(i => i.Outcome == oc);
        if (!string.IsNullOrWhiteSpace(lineId))
        {
            var line = lineId.Trim();
            q = line == "unassigned" ? q.Where(i => i.LineId == null) : q.Where(i => i.LineId == line);
        }
        if (!string.IsNullOrWhiteSpace(productType))
        {
            var product = EscapeLike(productType.Trim());
            q = q.Where(i => EF.Functions.ILike(i.ProductType, product, "\\"));
        }
        // Dates are inclusive calendar days (UTC).
        if (from is { } f) { var start = ToUtc(f).Date; q = q.Where(i => i.CreatedAt >= start); }
        if (to is { } t) { var end = ToUtc(t).Date.AddDays(1); q = q.Where(i => i.CreatedAt < end); }
        return q;
    }

    internal QcInspectionDetail ToDetail(QcInspection i, IReadOnlyDictionary<string, string> names, ApprovalRequest? approval)
    {
        string? Name(string? code) => code is null ? null : names.TryGetValue(code, out var n) ? n : code;
        var findings = i.Findings
            .OrderBy(f => f.Source)
            .ThenByDescending(f => f.Confidence ?? 0)
            .ThenBy(f => f.CreatedAt)
            .Select(f =>
            {
                var effective = f.CorrectedCategoryCode ?? f.CategoryCode;
                return new QcFindingDto(
                    f.Id, QcWire.Source(f.Source), f.CategoryCode, Name(f.CategoryCode), f.RawLabel, f.Confidence,
                    f.BoxX is { } x && f.BoxY is { } y && f.BoxWidth is { } w && f.BoxHeight is { } h ? new QcBoxDto(x, y, w, h) : null,
                    f.Polygon is { Length: >= 6 } p ? Enumerable.Range(0, p.Length / 2).Select(k => new[] { p[2 * k], p[2 * k + 1] }).ToList() : null,
                    QcWire.Severity(f.Severity), f.Description, QcWire.Decision(f.Decision),
                    f.CorrectedCategoryCode, f.CorrectedSeverity is { } cs ? QcWire.Severity(cs) : null, f.ReviewerNote,
                    effective, Name(effective), QcWire.Severity(f.CorrectedSeverity ?? f.Severity),
                    QcMetricsCalculator.IsVerified(f.Source, f.Decision), f.CreatedAt, f.ReviewedAt);
            })
            .ToList();

        return new QcInspectionDetail(
            i.Id, i.Reference, QcWire.Status(i.Status), i.ProductType, QcWire.Stage(i.Stage), i.LineId, i.ProductionOrder, i.Notes,
            i.RequestedCategoryCodes,
            new QcImageDto($"/api/qc/inspections/{i.Id}/image", i.ImageContentType, i.ImageWidth, i.ImageHeight, i.ImageSizeBytes,
                i.OriginalFileName, i.ImageSha256),
            new QcAnalysisDto(i.Provider, i.Model, i.ModelVersion, i.LocalizationSupported, i.ConfidenceThreshold, i.InferenceMs,
                i.Attempts, i.ProcessingStartedAt, i.AnalyzedAt, i.FailureCode, i.FailureMessage,
                Retryable: QcLifecycle.CanRetry(i.Status)),
            findings,
            i.Outcome is { } o && i.ReviewedAt is { } at ? new QcReviewDto(QcWire.Outcome(o), i.ReviewNotes, at, i.ReviewedByEmail) : null,
            approval is null ? null : new QcFollowUpDto(approval.Id, StageNames.ToWire(approval.Stage), StageNames.ToWire(approval.RiskTier)),
            i.CreatedAt, i.UpdatedAt, i.CreatedByEmail, i.Version);
    }

    internal static AuditLogEntry Audit(AuditActor actor, string? traceId, string action, Guid id, string? from, string? to, string? reason) => new()
    {
        OccurredAt = DateTime.UtcNow,
        ActorUserId = actor.UserId,
        ActorEmail = actor.Email,
        ActorRole = actor.Role,
        Action = action,
        EntityType = EntityType,
        EntityId = id.ToString(),
        FromState = from,
        ToState = to,
        Reason = Truncate(reason, 1000),
        TraceId = traceId,
    };

    internal static DateTime ToUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static bool IsUniqueViolation(DbUpdateException ex, string column) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (pg.ConstraintName?.Contains(column, StringComparison.Ordinal) ?? false);

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
