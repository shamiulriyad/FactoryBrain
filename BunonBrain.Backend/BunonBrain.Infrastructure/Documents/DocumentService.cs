using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Documents;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Merchandising;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Planning;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Documents.DocScope;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>
/// The common document engine: every document type of the catalog is
/// created, generated, edited, validated, versioned, submitted, reviewed,
/// approved or rejected, archived, rendered and searched here. Access is
/// role + permission + category (department) + factory (+ line for
/// supervisors). Separation of duties: nobody approves a document they
/// prepared or submitted, and at REVIEW_AND_APPROVE the approver is not the
/// verifier. AI only suggests (extraction, classification, summaries, text
/// drafts); every decision is a person's.
/// </summary>
public sealed class DocumentService : IDocumentService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxFilesPerDocument = 20;

    private readonly BunonBrainDbContext _db;
    private readonly DocumentGenerators _generators;
    private readonly DocumentEnricher _enricher;
    private readonly DocumentNotifier _notifier;
    private readonly DocumentFileStore _files;
    private readonly DocumentExtractor _extractor;
    private readonly DocumentAiClient _ai;
    private readonly DocumentOptions _options;
    private readonly ILogger<DocumentService> _log;

    public DocumentService(BunonBrainDbContext db, DocumentGenerators generators, DocumentEnricher enricher, DocumentNotifier notifier, DocumentFileStore files,
        DocumentExtractor extractor, DocumentAiClient ai, DocumentOptions options, ILogger<DocumentService> log)
    {
        _db = db; _generators = generators; _enricher = enricher; _notifier = notifier; _files = files; _extractor = extractor; _ai = ai; _options = options; _log = log;
    }

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("document_not_found", "Document not found.");

    // ── Factory, settings, catalog ──────────────────────────────────────────

    private async Task<Factory?> FactoryAsync(AccessContext c, Guid? factoryId, CancellationToken ct)
    {
        if (factoryId is { } id)
            return c.CanAccessFactory(id) ? await _db.Factories.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.IsActive, ct) : null;
        var mine = c.FactoryIds.ToList();
        var q = _db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!c.IsPlatform) q = q.Where(f => mine.Contains(f.Id));
        return await q.OrderBy(f => f.Code).FirstOrDefaultAsync(ct);
    }

    private async Task<Dictionary<string, DocumentTypeSetting>> SettingsAsync(Guid factoryId, CancellationToken ct) =>
        await _db.DocumentTypeSettings.AsNoTracking().Where(s => s.FactoryId == factoryId).ToDictionaryAsync(s => s.TypeCode, ct);

    private static DocFieldDto Field(FieldDef f) => new(f.Key, f.Label, f.Kind.ToString().ToLowerInvariant(), f.Required, f.Computed, f.Options,
        f.Columns?.Select(Field).ToList(), f.Help, f.Unit, f.Lookup);

    internal static DocTypeDto TypeDto(DocumentTypeDef t, DocumentTypeSetting? s, AccessContext c) => new(
        t.Code, t.Name, t.Category, DocumentCategories.All.First(x => x.Code == t.Category).Name, t.Description, t.Risk, t.DefaultApproval,
        DocumentCatalog.EffectiveApproval(t, s?.ApprovalLevel), t.Period.ToString().ToUpperInvariant(), t.Generated, t.LineOption, t.NumberField,
        s?.AutoGenerate ?? t.DefaultAutoGenerate, s?.ExpectedDaily ?? t.DefaultExpectedDaily, s?.DecisionDueHours ?? 24,
        t.Fields.Select(Field).ToList(), (t.Parameters ?? Array.Empty<FieldDef>()).Select(Field).ToList(),
        CanPrepare(c, t.Category) && (!c.IsLineScoped || t.LineOption),
        DocumentSchedule.Frequency(t, s?.ScheduleFrequency), DocumentSchedule.Scope(t, s?.ScheduleScope), s?.WeekStartDay ?? 6, DocumentSchedule.Allowed(t));

    public async Task<AccessResult<DocCatalogDto>> CatalogAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocCatalogDto>.NotFound("factory_not_found", "No factory in your scope.");
        var settings = await SettingsAsync(factory.Id, ct);
        var cats = DocumentCategories.For(caller);
        var types = DocumentCatalog.All.Where(t => cats.Contains(t.Category)).Select(t => TypeDto(t, settings.GetValueOrDefault(t.Code), caller)).ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factory.Id && l.IsActive).OrderBy(l => l.Name).ToListAsync(ct);
        if (caller.IsLineScoped) lines = lines.Where(l => caller.LineIds.Contains(l.Id)).ToList();
        var clock = await FactoryClock.LoadAsync(_db, factory.Id, ct);
        var can = new DocCapabilities(caller.Has(Permissions.DocumentsCreate), caller.Has(Permissions.DocumentsReview),
            caller.HasAny(Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh), caller.Has(Permissions.DocumentsApproveHigh),
            caller.Has(Permissions.DocumentsControl), caller.HasAny(Permissions.DocumentsMonitor, Permissions.DocumentsControl), cats, caller.IsLineScoped);
        return AccessResult<DocCatalogDto>.Ok(new DocCatalogDto(factory.Id, factory.Name, clock.Today(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", Inv), types,
            DocumentCategories.All.Where(x => cats.Contains(x.Code)).Select(x => new DocCategoryDto(x.Code, x.Name)).ToList(), can,
            lines.Select(l => new DocLineRef(l.Id, l.Name)).ToList(), _ai.Status()));
    }

    public async Task<AccessResult<DocTypeDto>> SaveTypeSettingAsync(AccessContext caller, Guid? factoryId, string typeCode, DocTypeSettingWrite body, string? traceId, CancellationToken ct)
    {
        var type = DocumentCatalog.Get(typeCode);
        if (type is null) return AccessResult<DocTypeDto>.NotFound("type_not_found", "Unknown document type.");
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocTypeDto>.NotFound("factory_not_found", "No factory in your scope.");
        if (!ApprovalLevels.All.Contains(body.ApprovalLevel)) return AccessResult<DocTypeDto>.Invalid("invalid_approval_level", "Approval level must be NONE, SINGLE or REVIEW_AND_APPROVE.");
        if (type.Risk == DocumentRisks.High && body.ApprovalLevel == ApprovalLevels.None)
            return AccessResult<DocTypeDto>.Invalid("approval_required", "High-risk documents (financial, compliance, shipment) always need an approver.");
        if (body.DecisionDueHours is < 1 or > 720) return AccessResult<DocTypeDto>.Invalid("invalid_due_hours", "Decision due time must be 1–720 hours.");
        if (body.AutoGenerate && !type.Generated) return AccessResult<DocTypeDto>.Invalid("not_generated", "Only report types generated from records can be generated automatically.");
        var allowed = DocumentSchedule.Allowed(type);
        if (body.ScheduleFrequency is { } fq && !(allowed.Contains(fq) || (!type.Generated && fq == ScheduleFrequencies.Daily)))
            return AccessResult<DocTypeDto>.Invalid("invalid_frequency", allowed.Count == 0 ? $"{type.Name} is not generated on a schedule." : $"{type.Name} can be scheduled {string.Join(" or ", allowed.Select(a => a.ToLowerInvariant()))}.");
        if (body.ScheduleScope is { } sc && !ScheduleScopes.All.Contains(sc)) return AccessResult<DocTypeDto>.Invalid("invalid_scope", "Scope must be FACTORY or EACH_LINE.");
        if (body.ScheduleScope == ScheduleScopes.EachLine && !type.LineOption) return AccessResult<DocTypeDto>.Invalid("invalid_scope", $"{type.Name} covers the whole factory; it can't be generated per line.");
        if (body.ScheduleScope == ScheduleScopes.Factory && DocumentSchedule.RequiresLine(type)) return AccessResult<DocTypeDto>.Invalid("invalid_scope", $"{type.Name} is always prepared per line.");
        if (body.WeekStartDay is < 0 or > 6) return AccessResult<DocTypeDto>.Invalid("invalid_week_start", "Week start must be 0 (Sunday) to 6 (Saturday).");
        var s = await _db.DocumentTypeSettings.FirstOrDefaultAsync(x => x.FactoryId == factory.Id && x.TypeCode == type.Code, ct);
        string Describe(DocumentTypeSetting x) => $"{x.ApprovalLevel} auto={x.AutoGenerate} {x.ScheduleFrequency}/{x.ScheduleScope} week-start={x.WeekStartDay} expected={x.ExpectedDaily} due={x.DecisionDueHours}h";
        var before = s is null ? $"default {type.DefaultApproval}" : Describe(s);
        if (s is null)
        {
            s = new DocumentTypeSetting { FactoryId = factory.Id, TypeCode = type.Code, UpdatedByEmail = caller.Email, ScheduleFrequency = DocumentSchedule.DefaultFrequency(type),
                ScheduleScope = DocumentSchedule.Scope(type, null) };
            _db.DocumentTypeSettings.Add(s);
        }
        s.ApprovalLevel = body.ApprovalLevel; s.AutoGenerate = body.AutoGenerate; s.ExpectedDaily = body.ExpectedDaily; s.DecisionDueHours = body.DecisionDueHours;
        if (body.ScheduleFrequency is not null) s.ScheduleFrequency = DocumentSchedule.Frequency(type, body.ScheduleFrequency);
        if (body.ScheduleScope is not null) s.ScheduleScope = DocumentSchedule.Scope(type, body.ScheduleScope);
        if (body.WeekStartDay is { } wsd) s.WeekStartDay = wsd;
        s.UpdatedAt = DateTimeOffset.UtcNow; s.UpdatedByEmail = caller.Email;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "document.type_settings_changed", "document_type_setting", $"{factory.Code}:{type.Code}", before,
            Describe(s), "Document type settings changed", traceId, factory.Id));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) || ex is DbUpdateConcurrencyException)
        {
            return AccessResult<DocTypeDto>.Conflict("version_conflict", "Someone else changed these settings; reload and try again.");
        }
        return AccessResult<DocTypeDto>.Ok(TypeDto(type, s, caller));
    }

    // ── List ────────────────────────────────────────────────────────────────

    private static DocListItem Item(FactoryDocument d) => new(d.Id, d.FactoryId, d.Reference, d.TypeCode, DocumentCatalog.Get(d.TypeCode)?.Name ?? d.TypeCode, d.Category, d.Title,
        d.Status, d.Risk, d.ApprovalLevel, d.Source, d.PeriodFrom?.ToString("yyyy-MM-dd", Inv), d.PeriodTo?.ToString("yyyy-MM-dd", Inv), d.LineId, d.CurrentVersion,
        d.MissingCount, d.ErrorCount, d.WarningCount, d.CreatedAt, d.CreatedByEmail, d.UpdatedAt, d.SubmittedAt, d.DecidedAt, d.DecidedByEmail);

    public async Task<AccessResult<DocPaged<DocListItem>>> ListAsync(AccessContext caller, DocListQuery query, CancellationToken ct)
    {
        var q = Visible(_db, caller);
        if (query.FactoryId is { } fid) q = q.Where(d => d.FactoryId == fid);
        if (Clean(query.Category) is { } cat) q = q.Where(d => d.Category == cat);
        if (Clean(query.Type) is { } type) q = q.Where(d => d.TypeCode == type);
        if (Clean(query.Status) is { } status) q = q.Where(d => d.Status == status);
        else if (!query.IncludeArchived) q = q.Where(d => d.Status != DocumentStatuses.Archived);
        if (query.From is { } from) q = q.Where(d => (d.PeriodTo ?? d.PeriodFrom) >= from || (d.PeriodFrom == null && DateOnly.FromDateTime(d.CreatedAt.UtcDateTime) >= from));
        if (query.To is { } to) q = q.Where(d => d.PeriodFrom <= to || (d.PeriodFrom == null && DateOnly.FromDateTime(d.CreatedAt.UtcDateTime) <= to));
        if (Clean(query.LineId) is { } line) q = q.Where(d => d.LineId == line);
        if (Clean(query.Q) is { } text)
        {
            var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(d => EF.Functions.ILike(d.Reference, like) || EF.Functions.ILike(d.Title, like) || EF.Functions.ILike(d.CreatedByEmail, like));
        }
        var me = caller.UserId;
        switch (query.View)
        {
            case "mine":
                q = q.Where(d => d.CreatedByUserId == me || d.SubmittedByUserId == me);
                break;
            case "action":
            {
                var decides = caller.HasAny(Permissions.DocumentsReview, Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh);
                var prepares = caller.Has(Permissions.DocumentsCreate);
                q = q.Where(d => (decides && (d.Status == DocumentStatuses.Submitted || d.Status == DocumentStatuses.UnderReview) && d.SubmittedByUserId != me && d.CreatedByUserId != me)
                                 || (d.CreatedByUserId == me && (d.Status == DocumentStatuses.Draft || d.Status == DocumentStatuses.Rejected))
                                 || (prepares && d.CreatedByUserId == null && d.Status == DocumentStatuses.Draft));
                break;
            }
            case "missing":
                q = q.Where(d => d.MissingCount > 0 || d.ErrorCount > 0);
                break;
        }
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.UpdatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return AccessResult<DocPaged<DocListItem>>.Ok(new DocPaged<DocListItem>(rows.Select(Item).ToList(), page, size, total));
    }

    // ── Detail ──────────────────────────────────────────────────────────────

    private async Task<FactoryDocument?> LoadAsync(AccessContext caller, Guid id, bool tracking, CancellationToken ct) =>
        await Visible(_db, caller, tracking).FirstOrDefaultAsync(d => d.Id == id, ct);

    private async Task<FactoryDocumentVersion> CurrentVersionAsync(FactoryDocument doc, CancellationToken ct) =>
        await _db.FactoryDocumentVersions.AsNoTracking().FirstAsync(v => v.DocumentId == doc.Id && v.Number == doc.CurrentVersion, ct);

    private DocActions Actions(FactoryDocument d, DocumentTypeDef type, AccessContext c, DocumentChecks checks)
    {
        var prepare = CanPrepare(c, d.Category) && (!c.IsLineScoped || (d.LineId is not null && c.LineIds.Contains(d.LineId)));
        var editable = DocumentStatuses.Editable(d.Status);
        var pending = DocumentStatuses.Pending.Contains(d.Status);
        var me = c.UserId;
        var ownWork = d.CreatedByUserId == me || d.SubmittedByUserId == me;
        var review = CanReview(c, d.Category);
        var approver = CanApprove(c, d.Category, d.Risk);
        var needsVerify = d.ApprovalLevel == ApprovalLevels.ReviewAndApprove && d.VerifiedAt is null;
        string? whyNotSubmit = !editable ? null : checks.Missing.Count > 0 ? $"{checks.Missing.Count} required value(s) missing." : checks.Errors.Count > 0 ? $"{checks.Errors.Count} error(s) to fix." : null;
        string? whyNotApprove = !pending ? null : ownWork ? "You prepared or submitted this document; someone else must approve it."
            : !approver ? (d.Risk == DocumentRisks.High ? "High-risk documents need the 'approve high-risk documents' permission." : "You can't approve documents of this category.")
            : needsVerify ? "A reviewer must verify it first." : d.VerifiedByUserId == me ? "You verified this document; a different person approves it." : null;
        var ai = _ai.Status().Configured;
        return new DocActions(
            Edit: prepare && editable,
            Submit: prepare && editable && checks.CanSubmit,
            StartReview: review && d.Status == DocumentStatuses.Submitted && !ownWork,
            Verify: review && pending && d.ApprovalLevel == ApprovalLevels.ReviewAndApprove && d.VerifiedAt is null && !ownWork,
            Approve: approver && pending && !ownWork && !needsVerify && d.VerifiedByUserId != me,
            Reject: (review || approver) && pending && d.SubmittedByUserId != me,
            Revise: prepare && d.Status == DocumentStatuses.Approved && !d.StockPosted,
            Archive: c.Has(Permissions.DocumentsControl) && d.Status is DocumentStatuses.Approved or DocumentStatuses.Rejected,
            Upload: prepare && editable,
            Regenerate: prepare && editable && type.Generated,
            UseAi: ai && (prepare || review || approver),
            whyNotSubmit, whyNotApprove);
    }

    private async Task<DocDetail> DetailAsync(FactoryDocument doc, AccessContext caller, CancellationToken ct)
    {
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var version = await CurrentVersionAsync(doc, ct);
        var data = DocJson.Parse(version.DataJson);
        var sources = DocJson.Parse(version.SourcesJson);
        var checks = DocumentChecks.FromJson(version.ChecksJson);
        var setting = await _db.DocumentTypeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.FactoryId == doc.FactoryId && s.TypeCode == doc.TypeCode, ct);
        var versions = await _db.FactoryDocumentVersions.AsNoTracking().Where(v => v.DocumentId == doc.Id).OrderByDescending(v => v.Number)
            .Select(v => new DocVersionDto(v.Number, v.CreatedAt, v.CreatedByEmail, v.ChangeNote, v.MissingCount, v.ErrorCount, v.WarningCount, v.ContentSha256)).ToListAsync(ct);
        var history = await _db.FactoryDocumentEvents.AsNoTracking().Where(e => e.DocumentId == doc.Id).OrderByDescending(e => e.Id).Take(200)
            .Select(e => new DocEventDto(e.Id, e.Action, e.FromStatus, e.ToStatus, e.VersionNumber, e.Note, e.OccurredAt, e.ActorEmail)).ToListAsync(ct);
        var files = (await _db.FactoryDocumentFiles.AsNoTracking().Where(f => f.DocumentId == doc.Id).OrderBy(f => f.CreatedAt).ToListAsync(ct)).Select(FileDto).ToList();
        var lineName = doc.LineId is null ? null : await _db.ProductionLines.AsNoTracking().Where(l => l.Id == doc.LineId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        sources.Remove("$gaps");
        return new DocDetail(Item(doc), TypeDto(type, setting, caller), data, sources, DocumentChecksDto.From(checks), Actions(doc, type, caller, checks), versions, history, files,
            doc.SubmittedByEmail, doc.ReviewStartedByEmail, doc.ReviewStartedAt, doc.VerifiedByEmail, doc.VerifiedAt, doc.DecisionNote, doc.ArchivedAt, doc.ArchivedByEmail, doc.StockPosted,
            doc.AiSummary, doc.AiSummaryAt, doc.AiSummaryModel, lineName);
    }

    private static DocFileDto FileDto(FactoryDocumentFile f) => new(f.Id, f.OriginalFileName ?? $"file.{f.Format}", f.Format, f.ContentType, f.SizeBytes, f.Sha256, f.ExtractionStatus,
        f.ExtractionMethod, f.ExtractionModel, f.ExtractedAt, f.ExtractionAttempts, f.FailureCode, f.FailureMessage, f.SuggestedTypeCode,
        f.ExtractionJson is null ? null : JsonNode.Parse(f.ExtractionJson), f.CreatedAt, f.CreatedByEmail);

    public async Task<AccessResult<DocDetail>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, false, ct);
        return doc is null ? NotFound<DocDetail>() : AccessResult<DocDetail>.Ok(await DetailAsync(doc, caller, ct));
    }

    public async Task<AccessResult<JsonObject>> VersionAsync(AccessContext caller, Guid id, int number, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, false, ct);
        if (doc is null) return NotFound<JsonObject>();
        var v = await _db.FactoryDocumentVersions.AsNoTracking().FirstOrDefaultAsync(x => x.DocumentId == id && x.Number == number, ct);
        if (v is null) return AccessResult<JsonObject>.NotFound("version_not_found", "Version not found.");
        var sources = DocJson.Parse(v.SourcesJson);
        sources.Remove("$gaps");
        return AccessResult<JsonObject>.Ok(new JsonObject
        {
            ["number"] = v.Number, ["createdAt"] = v.CreatedAt.ToString("o", Inv), ["createdByEmail"] = v.CreatedByEmail, ["changeNote"] = v.ChangeNote,
            ["data"] = DocJson.Parse(v.DataJson), ["sources"] = sources, ["checks"] = DocJson.Parse(v.ChecksJson), ["contentSha256"] = v.ContentSha256,
        });
    }

    // ── Content pipeline ────────────────────────────────────────────────────

    private static (string? From, string? To) DateFields(string code) => code switch
    {
        "hr.leave" => ("fromDate", "toDate"),
        "store.grn" => ("receivedOn", null), "store.issue" => ("issuedOn", null), "store.requisition" => ("requiredBy", null),
        "qc.aql" => ("inspectionDate", null), "qc.capa" => ("dueDate", null),
        "compliance.audit" => ("auditDate", null), "compliance.safety" => ("inspectionDate", null), "compliance.training" => ("trainingDate", null),
        "commercial.invoice" => ("invoiceDate", null), "commercial.shipment" => ("etd", null),
        "devices.maintenance" => ("maintenanceDate", null), "merch.po" => ("deliveryDate", null), "merch.order_sheet" => ("deliveryDate", null),
        _ => (null, null),
    };

    private static string AutoTitle(DocumentTypeDef type, FactoryDocument doc, JsonObject data, string? lineName)
    {
        var sb = new StringBuilder(type.Name);
        if (type.NumberField is { } nf && DocJson.Str(data[nf]) is { } no) sb.Append(' ').Append(no);
        else if (type.Period != PeriodKind.None && doc.PeriodFrom is { } f)
            sb.Append(' ').Append(f.ToString("yyyy-MM-dd", Inv)).Append(doc.PeriodTo is { } t && t != f ? " to " + t.ToString("yyyy-MM-dd", Inv) : "");
        else if (doc.PeriodFrom is { } d) sb.Append(' ').Append(d.ToString("yyyy-MM-dd", Inv));
        foreach (var k in new[] { "workerCode", "machineCode", "buyer", "style", "title", "area" })
            if (DocJson.Str(data[k]) is { } v) { sb.Append(" · ").Append(v); break; }
        if (lineName is not null) sb.Append(" · ").Append(lineName);
        var s = sb.ToString();
        return s.Length > 300 ? s[..300] : s;
    }

    /// <summary>Compute, look up, validate, add generator gaps; then derive the document's dates, duplicate key and counts.</summary>
    private async Task<DocumentChecks> PrepareAsync(FactoryDocument doc, DocumentTypeDef type, JsonObject data, JsonObject sources, Guid factoryId, CancellationToken ct)
    {
        var clock = await FactoryClock.LoadAsync(_db, factoryId, ct);
        var checks = DocumentRules.Validate(type, data, clock.Today(DateTimeOffset.UtcNow));
        await _enricher.EnrichAsync(doc, type, data, checks, ct);
        if (sources["$gaps"] is JsonArray gaps)
            foreach (var g in gaps.OfType<JsonObject>())
                checks.Warn(DocJson.Str(g["field"]) ?? "", DocJson.Str(g["code"]) ?? "data_gap", DocJson.Str(g["message"]) ?? "");
        if (type.Period == PeriodKind.None)
        {
            var (fk, tk) = DateFields(type.Code);
            doc.PeriodFrom = fk is null ? null : DocJson.Date(data[fk]);
            doc.PeriodTo = tk is null ? doc.PeriodFrom : DocJson.Date(data[tk]);
            if (doc.PeriodFrom is null || doc.PeriodTo < doc.PeriodFrom) doc.PeriodTo = doc.PeriodFrom;
        }
        doc.DedupKey = DocumentRules.DedupKey(type, data, doc.PeriodFrom, doc.PeriodTo, doc.LineId);
        // Period documents are identified by their period; form documents only when they carry a number.
        if (type.Period == PeriodKind.None && type.NumberField is null) doc.DedupKey = null;
        if (type.Code == "hr.employee") doc.DedupKey = DocJson.Str(data["workerCode"]) is { } w ? $"hr.employee|{w.ToUpperInvariant()}" : null;
        doc.MissingCount = checks.Missing.Count; doc.ErrorCount = checks.Errors.Count; doc.WarningCount = checks.Warnings.Count;
        return checks;
    }

    private static string Sha(JsonObject data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString()))).ToLowerInvariant();

    private FactoryDocumentVersion AddVersion(FactoryDocument doc, JsonObject data, JsonObject sources, DocumentChecks checks, string? note, AuditActor actor)
    {
        var v = new FactoryDocumentVersion
        {
            DocumentId = doc.Id, Number = doc.CurrentVersion + 1, DataJson = data.ToJsonString(), SourcesJson = sources.ToJsonString(), ChecksJson = checks.ToJson(),
            MissingCount = checks.Missing.Count, ErrorCount = checks.Errors.Count, WarningCount = checks.Warnings.Count, ContentSha256 = Sha(data),
            ChangeNote = note is null ? null : note.Length > 500 ? note[..500] : note, CreatedByUserId = actor.UserId, CreatedByEmail = actor.Email,
        };
        _db.FactoryDocumentVersions.Add(v);
        doc.CurrentVersion = v.Number;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        return v;
    }

    private void Event(FactoryDocument doc, string action, string? from, string? to, string? note, AuditActor actor, string? traceId, bool audit = true)
    {
        _db.FactoryDocumentEvents.Add(new FactoryDocumentEvent
        {
            DocumentId = doc.Id, FactoryId = doc.FactoryId, Action = action, FromStatus = from, ToStatus = to, VersionNumber = doc.CurrentVersion,
            Note = note is null ? null : note.Length > 1000 ? note[..1000] : note, ActorUserId = actor.UserId, ActorEmail = actor.Email,
        });
        if (audit)
            _db.AuditLog.Add(AccessAudit.Entry(actor, "document." + action, "factory_document", doc.Id.ToString(), from, to,
                $"{doc.Reference}{(note is null ? "" : ": " + note)}", traceId, doc.FactoryId));
    }

    private async Task<string> NewReferenceAsync(string category, DateOnly today, CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var r = $"DOC-{DocumentCategories.PrefixFor(category)}-{today:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";
            if (!await _db.FactoryDocuments.AsNoTracking().AnyAsync(d => d.Reference == r, ct)) return r;
        }
        throw new InvalidOperationException("Could not allocate a document reference.");
    }

    private async Task<string?> DuplicateOfAsync(FactoryDocument doc, CancellationToken ct) =>
        doc.DedupKey is null ? null : await _db.FactoryDocuments.AsNoTracking()
            .Where(d => d.FactoryId == doc.FactoryId && d.DedupKey == doc.DedupKey && d.Status != DocumentStatuses.Archived && d.Id != doc.Id)
            .Select(d => d.Reference).FirstOrDefaultAsync(ct);

    private static void MarkSources(JsonObject sources, DocumentTypeDef type, JsonObject before, JsonObject after, string by)
    {
        foreach (var f in type.Fields.Where(f => !f.Computed))
        {
            var a = after[f.Key]?.ToJsonString(); var b = before[f.Key]?.ToJsonString();
            if (a == b) continue;
            if (a is null) sources.Remove(f.Key); else sources[f.Key] = by;
        }
    }

    // ── Create ──────────────────────────────────────────────────────────────

    public async Task<AccessResult<DocDetail>> CreateAsync(AccessContext caller, DocCreateRequest body, string? traceId, CancellationToken ct)
    {
        var type = DocumentCatalog.Get(body.TypeCode);
        if (type is null) return AccessResult<DocDetail>.Invalid("type_not_found", "Unknown document type.");
        if (!CanPrepare(caller, type.Category)) return AccessResult<DocDetail>.Forbidden("category_not_permitted", $"You can't prepare {type.Category} documents.");
        var factory = await FactoryAsync(caller, body.FactoryId, ct);
        if (factory is null) return AccessResult<DocDetail>.NotFound("factory_not_found", "No factory in your scope.");
        var clock = await FactoryClock.LoadAsync(_db, factory.Id, ct);
        var today = clock.Today(DateTimeOffset.UtcNow);

        // Line: only for types that can be about one line; a supervisor must name one of theirs.
        var lineId = Clean(body.LineId);
        string? lineName = null;
        if (lineId is not null)
        {
            if (!type.LineOption) return AccessResult<DocDetail>.Invalid("line_not_applicable", $"{type.Name} is not prepared per line.");
            var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId && l.FactoryId == factory.Id, ct);
            if (line is null || !caller.CanAccessLine(line.Id, line.FactoryId)) return AccessResult<DocDetail>.Forbidden("line_out_of_scope", "That line is not in your scope.");
            lineName = line.Name;
        }
        else if (caller.IsLineScoped)
            return AccessResult<DocDetail>.Invalid("line_required", type.LineOption ? "Choose one of your lines." : $"{type.Name} covers the whole factory; supervisors prepare line documents.");
        if (type.Code == "production.line" && lineId is null) return AccessResult<DocDetail>.Invalid("line_required", "Choose the production line.");

        // Period.
        DateOnly? from = null, to = null;
        if (type.Period != PeriodKind.None)
        {
            from = body.PeriodFrom ?? (type.Generated ? today.AddDays(-1) : null);
            to = type.Period == PeriodKind.Day ? from : body.PeriodTo ?? from;
            if (from is null) return AccessResult<DocDetail>.Invalid("period_required", "Choose the date / period.");
            if (to < from) return AccessResult<DocDetail>.Invalid("period_order", "The period ends before it starts.");
            if (to!.Value.DayNumber - from.Value.DayNumber > 92) return AccessResult<DocDetail>.Invalid("period_too_long", "A period can be at most 93 days.");
            if (to > today) return AccessResult<DocDetail>.Invalid("future_period", "A report can't cover days that haven't started.");
        }

        var mode = (Clean(body.Mode) ?? (type.Generated ? "generate" : "form")).ToLowerInvariant();
        var data = new JsonObject();
        var sources = new JsonObject();
        var source = DocumentSources.Form;
        var note = "Created";
        switch (mode)
        {
            case "generate":
            {
                if (!type.Generated) return AccessResult<DocDetail>.Invalid("not_generated", $"{type.Name} is filled in by people, not generated.");
                var g = await _generators.GenerateAsync(type, factory.Id, from!.Value, to!.Value, lineId, body.Parameters, ct);
                data = DocumentRules.Sanitize(type, g.Data, keepComputed: true);
                foreach (var p in type.Parameters ?? Array.Empty<FieldDef>())
                    if (DocJson.Str(body.Parameters?[p.Key]) is { } pv) data[p.Key] = pv;
                foreach (var f in type.Fields.Where(f => f.Computed)) sources[f.Key] = "records";
                sources["$gaps"] = new JsonArray(g.Gaps.Select(x => (JsonNode)new JsonObject { ["field"] = x.Field, ["code"] = x.Code, ["message"] = x.Message }).ToArray());
                sources["$generatedAt"] = DateTimeOffset.UtcNow.ToString("o", Inv);
                source = DocumentSources.Generated;
                note = g.HasSourceData ? "Generated from records" : "Generated: no source records found for the period";
                if (body.Data?["remarks"] is { } rm && DocJson.Str(rm) is { } r) { data["remarks"] = r; sources["remarks"] = "user"; }
                break;
            }
            case "template":
            {
                var last = await Visible(_db, caller).Where(d => d.FactoryId == factory.Id && d.TypeCode == type.Code && d.Status == DocumentStatuses.Approved)
                    .OrderByDescending(d => d.DecidedAt).FirstOrDefaultAsync(ct);
                if (last is null) return AccessResult<DocDetail>.NotFound("no_template", $"There is no approved {type.Name} to start from yet.");
                var prev = DocJson.Parse((await CurrentVersionAsync(last, ct)).DataJson);
                data = DocumentRules.Sanitize(type, prev, keepComputed: false);
                // A copy keeps what repeats (buyer, supplier, checklist, instructions) but not what identifies the old one.
                if (type.NumberField is { } nf) data.Remove(nf);
                foreach (var f in type.Fields.Where(f => f.Kind == FieldKind.Date)) data.Remove(f.Key);
                foreach (var k in data.Select(kv => kv.Key).ToList()) sources[k] = "template:" + last.Reference;
                source = DocumentSources.Template;
                note = $"Started from approved {last.Reference}";
                break;
            }
            case "upload":
                source = DocumentSources.Upload;
                note = "Created for an uploaded document";
                goto case "form";
            case "form":
            case "blank":
            {
                if (body.Data is not null)
                {
                    data = DocumentRules.Sanitize(type, body.Data, keepComputed: false);
                    foreach (var k in data.Select(kv => kv.Key).ToList()) sources[k] = "user";
                }
                if (Clean(body.PrefillKey) is { } key)
                {
                    var lookup = type.Fields.FirstOrDefault(f => f.Lookup is "merch_order" or "worker" or "machine")?.Lookup;
                    if (lookup is null) return AccessResult<DocDetail>.Invalid("prefill_not_supported", $"{type.Name} can't be filled from a record.");
                    var found = await LookupValuesAsync(caller, factory.Id, lookup, key, ct);
                    if (found.Values is null) return AccessResult<DocDetail>.NotFound("prefill_not_found", found.Message ?? "Record not found.");
                    var clean = DocumentRules.Sanitize(type, found.Values, keepComputed: false);
                    foreach (var kv in clean.ToList()) { data[kv.Key] = kv.Value?.DeepClone(); sources[kv.Key] = $"{lookup}:{key}"; }
                    note = $"Filled from {lookup.Replace('_', ' ')} {key}";
                }
                break;
            }
            default:
                return AccessResult<DocDetail>.Invalid("invalid_mode", "Mode must be generate, form, template or upload.");
        }

        var setting = await _db.DocumentTypeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.FactoryId == factory.Id && s.TypeCode == type.Code, ct);
        var doc = new FactoryDocument
        {
            FactoryId = factory.Id, TypeCode = type.Code, Category = type.Category, Reference = await NewReferenceAsync(type.Category, today, ct), Title = "",
            PeriodFrom = from, PeriodTo = to, LineId = lineId, Status = DocumentStatuses.Draft, Risk = type.Risk,
            ApprovalLevel = DocumentCatalog.EffectiveApproval(type, setting?.ApprovalLevel), Source = source,
            CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
        };
        var checks = await PrepareAsync(doc, type, data, sources, factory.Id, ct);
        doc.Title = Clean(body.Title) is { } t ? (t.Length > 300 ? t[..300] : t) : AutoTitle(type, doc, data, lineName);
        if (await DuplicateOfAsync(doc, ct) is { } dup)
            return AccessResult<DocDetail>.Conflict("duplicate_document", $"{dup} already covers this ({doc.DedupKey!.Split('|', 2)[1]}). Open it instead of creating another.");
        _db.FactoryDocuments.Add(doc);
        AddVersion(doc, data, sources, checks, note, caller.Actor);
        Event(doc, "created", null, DocumentStatuses.Draft, note, caller.Actor, traceId);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "UX_factory_documents_live_dedup"))
        {
            return AccessResult<DocDetail>.Conflict("duplicate_document", "Another document for the same period / number was just created.");
        }
        return AccessResult<DocDetail>.Created(await DetailAsync(doc, caller, ct));
    }

    // ── Save / regenerate ───────────────────────────────────────────────────

    private AccessResult<DocDetail>? CheckEditable(FactoryDocument doc, AccessContext caller)
    {
        if (!CanPrepare(caller, doc.Category) || (caller.IsLineScoped && (doc.LineId is null || !caller.LineIds.Contains(doc.LineId))))
            return AccessResult<DocDetail>.Forbidden("cannot_prepare", "You can't edit documents of this category.");
        if (!DocumentStatuses.Editable(doc.Status))
            return AccessResult<DocDetail>.Conflict("not_editable", $"The document is {doc.Status.ToLowerInvariant().Replace('_', ' ')}; only drafts and rejected documents are edited.");
        return null;
    }

    public async Task<AccessResult<DocDetail>> SaveAsync(AccessContext caller, Guid id, DocSaveRequest body, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, true, ct);
        if (doc is null) return NotFound<DocDetail>();
        if (CheckEditable(doc, caller) is { } refuse) return refuse;
        if (body.ExpectedVersion is { } ev && ev != doc.CurrentVersion)
            return AccessResult<DocDetail>.Conflict("version_conflict", $"The document changed (now version {doc.CurrentVersion}); reload before saving.");
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var current = await CurrentVersionAsync(doc, ct);
        var before = DocJson.Parse(current.DataJson);
        var sources = DocJson.Parse(current.SourcesJson);
        var input = DocumentRules.Sanitize(type, body.Data ?? new JsonObject(), keepComputed: false);

        // People change the editable fields; figures generated from records stay as generated.
        var data = new JsonObject();
        foreach (var f in type.Fields)
        {
            var node = f.Computed ? (type.Generated ? before[f.Key] : null) : input[f.Key];
            if (node is not null) data[f.Key] = node.DeepClone();
        }
        foreach (var p in type.Parameters ?? Array.Empty<FieldDef>()) if (before[p.Key] is { } pv) data[p.Key] = pv.DeepClone();
        MarkSources(sources, type, before, data, "user");

        // Values accepted from an AI suggestion of one of this document's files are labelled as such.
        if (body.AiFileId is { } fileId && body.AcceptedAiFields is { Count: > 0 } accepted)
        {
            var file = await _db.FactoryDocumentFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.DocumentId == doc.Id, ct);
            var suggested = file?.ExtractionJson is null ? null : DocJson.Parse(file.ExtractionJson)["values"] as JsonObject;
            if (suggested is not null)
                foreach (var key in accepted.Where(k => suggested.ContainsKey(k) && data.ContainsKey(k)))
                    sources[key] = $"{(file!.ExtractionMethod ?? "ai")}:{file.OriginalFileName ?? file.Id.ToString()}";
        }

        var checks = await PrepareAsync(doc, type, data, sources, doc.FactoryId, ct);
        if (Clean(body.Title) is { } t) doc.Title = t.Length > 300 ? t[..300] : t;
        else doc.Title = AutoTitle(type, doc, data, doc.LineId is null ? null : await _db.ProductionLines.AsNoTracking().Where(l => l.Id == doc.LineId).Select(l => l.Name).FirstOrDefaultAsync(ct));
        if (await DuplicateOfAsync(doc, ct) is { } dup)
            return AccessResult<DocDetail>.Conflict("duplicate_document", $"{dup} already has this {(type.NumberField is null ? "period" : "number")}.");
        if (Sha(data) == current.ContentSha256 && current.ChecksJson == checks.ToJson())
        {
            await _db.SaveChangesAsync(ct); // title only
            return AccessResult<DocDetail>.Ok(await DetailAsync(doc, caller, ct));
        }
        var from = doc.Status;
        AddVersion(doc, data, sources, checks, Clean(body.ChangeNote), caller.Actor);
        if (doc.Status == DocumentStatuses.Rejected) doc.Status = DocumentStatuses.Draft;
        Event(doc, from == DocumentStatuses.Rejected ? "revised_after_rejection" : "saved", from, doc.Status, Clean(body.ChangeNote), caller.Actor, traceId);
        return await CommitAsync(doc, caller, ct);
    }

    private async Task<AccessResult<DocDetail>> CommitAsync(FactoryDocument doc, AccessContext caller, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<DocDetail>.Conflict("version_conflict", "Someone else changed this document; reload and try again."); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "UX_factory_documents_live_dedup"))
        {
            return AccessResult<DocDetail>.Conflict("duplicate_document", "Another live document has the same period / number.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return AccessResult<DocDetail>.Conflict("version_conflict", "Someone else saved this document at the same moment; reload and try again.");
        }
        return AccessResult<DocDetail>.Ok(await DetailAsync(doc, caller, ct));
    }

    public async Task<AccessResult<DocDetail>> RegenerateAsync(AccessContext caller, Guid id, DocActionRequest body, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, true, ct);
        if (doc is null) return NotFound<DocDetail>();
        if (CheckEditable(doc, caller) is { } refuse) return refuse;
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        if (!type.Generated || doc.PeriodFrom is null) return AccessResult<DocDetail>.Invalid("not_generated", "This document is not generated from records.");
        var current = await CurrentVersionAsync(doc, ct);
        var before = DocJson.Parse(current.DataJson);
        var g = await _generators.GenerateAsync(type, doc.FactoryId, doc.PeriodFrom.Value, doc.PeriodTo ?? doc.PeriodFrom.Value, doc.LineId, before, ct);
        var data = DocumentRules.Sanitize(type, g.Data, keepComputed: true);
        foreach (var f in type.Fields.Where(f => !f.Computed)) if (before[f.Key] is { } v) data[f.Key] = v.DeepClone();
        foreach (var p in type.Parameters ?? Array.Empty<FieldDef>()) if (before[p.Key] is { } pv) data[p.Key] = pv.DeepClone();
        var sources = DocJson.Parse(current.SourcesJson);
        sources["$gaps"] = new JsonArray(g.Gaps.Select(x => (JsonNode)new JsonObject { ["field"] = x.Field, ["code"] = x.Code, ["message"] = x.Message }).ToArray());
        sources["$generatedAt"] = DateTimeOffset.UtcNow.ToString("o", Inv);
        var checks = await PrepareAsync(doc, type, data, sources, doc.FactoryId, ct);
        if (Sha(data) == current.ContentSha256 && checks.ToJson() == current.ChecksJson)
            return AccessResult<DocDetail>.Ok(await DetailAsync(doc, caller, ct));
        var from = doc.Status;
        AddVersion(doc, data, sources, checks, Clean(body.Note) ?? "Refreshed from records", caller.Actor);
        if (doc.Status == DocumentStatuses.Rejected) doc.Status = DocumentStatuses.Draft;
        Event(doc, "regenerated", from, doc.Status, Clean(body.Note), caller.Actor, traceId);
        return await CommitAsync(doc, caller, ct);
    }

    // ── Workflow ────────────────────────────────────────────────────────────

    public async Task<AccessResult<DocDetail>> ActAsync(AccessContext caller, Guid id, string action, DocActionRequest body, string? traceId, CancellationToken ct)
    {
        // The whole action runs in one transaction under the connection-retry strategy; notifications go out after it committed.
        var notify = new List<Func<Task>>();
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            notify.Clear();
            return await ActCoreAsync(caller, id, action, body, traceId, notify, ct);
        });
        if (result.Outcome == AccessOutcome.Ok)
            foreach (var n in notify)
            {
                try { await n(); }
                catch (Exception ex) { _log.LogWarning(ex, "Notification for document {Id} failed.", id); }
            }
        return result;
    }

    private async Task<AccessResult<DocDetail>> ActCoreAsync(AccessContext caller, Guid id, string action, DocActionRequest body, string? traceId, List<Func<Task>> notify, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, true, ct);
        if (doc is null) return NotFound<DocDetail>();
        if (body.ExpectedVersion is { } ev && ev != doc.CurrentVersion)
            return AccessResult<DocDetail>.Conflict("version_conflict", $"The document changed (now version {doc.CurrentVersion}); reload first.");
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var version = await CurrentVersionAsync(doc, ct);
        var data = DocJson.Parse(version.DataJson);
        var sources = DocJson.Parse(version.SourcesJson);
        var note = Clean(body.Note);
        var from = doc.Status;
        var me = caller.UserId;
        var now = DateTimeOffset.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        switch (action)
        {
            case "submit":
            {
                if (CheckEditable(doc, caller) is { } refuse) return refuse;
                // Re-check now: stock, references and other records may have changed since the last save.
                var checks = await PrepareAsync(doc, type, data, sources, doc.FactoryId, ct);
                if (!checks.CanSubmit)
                    return AccessResult<DocDetail>.Invalid("not_ready", $"Fix before submitting: {string.Join(" ", checks.Missing.Concat(checks.Errors).Take(5).Select(i => i.Message))}");
                var setting = await _db.DocumentTypeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.FactoryId == doc.FactoryId && s.TypeCode == doc.TypeCode, ct);
                doc.ApprovalLevel = DocumentCatalog.EffectiveApproval(type, setting?.ApprovalLevel);
                doc.SubmittedAt = now; doc.SubmittedByUserId = me; doc.SubmittedByEmail = caller.Email;
                doc.ReviewStartedAt = null; doc.ReviewStartedByEmail = null; doc.VerifiedAt = null; doc.VerifiedByUserId = null; doc.VerifiedByEmail = null;
                doc.DecidedAt = null; doc.DecidedByUserId = null; doc.DecidedByEmail = null; doc.DecisionNote = null;
                if (doc.ApprovalLevel == ApprovalLevels.None)
                {
                    // Filed: low-risk records this factory chose not to route for approval.
                    if (await PostStockAsync(doc, type, data, caller, ct) is { } stockError) return stockError;
                    doc.Status = DocumentStatuses.Approved;
                    doc.DecidedAt = now; doc.DecidedByEmail = caller.Email; doc.DecidedByUserId = me; doc.DecisionNote = "Filed on submission (no approval required for this document type).";
                    Event(doc, "filed", from, doc.Status, note, caller.Actor, traceId);
                }
                else
                {
                    doc.Status = DocumentStatuses.Submitted;
                    Event(doc, "submitted", from, doc.Status, note, caller.Actor, traceId);
                    var next = doc.ApprovalLevel == ApprovalLevels.ReviewAndApprove ? new[] { Permissions.DocumentsReview } : ApproversFor(doc.Risk);
                    notify.Add(() => NotifyAsync(doc, next, "document.pending_" + (doc.ApprovalLevel == ApprovalLevels.ReviewAndApprove ? "review" : "approval"),
                        $"{doc.Reference} waits for your {(doc.ApprovalLevel == ApprovalLevels.ReviewAndApprove ? "review" : "approval")}", $"{doc.Title} was submitted by {caller.Email}.",
                        $"pending:{doc.Id}:{doc.CurrentVersion}", exclude: me, ct));
                }
                break;
            }
            case "start-review":
            {
                if (!CanReview(caller, doc.Category)) return AccessResult<DocDetail>.Forbidden("cannot_review", "You can't review documents of this category.");
                if (doc.Status != DocumentStatuses.Submitted) return AccessResult<DocDetail>.Conflict("not_submitted", "Only submitted documents are taken under review.");
                if (doc.SubmittedByUserId == me || doc.CreatedByUserId == me) return AccessResult<DocDetail>.Forbidden("own_document", "You can't review your own document.");
                doc.Status = DocumentStatuses.UnderReview; doc.ReviewStartedAt = now; doc.ReviewStartedByEmail = caller.Email;
                Event(doc, "review_started", from, doc.Status, note, caller.Actor, traceId);
                break;
            }
            case "verify":
            {
                if (!CanReview(caller, doc.Category)) return AccessResult<DocDetail>.Forbidden("cannot_review", "You can't review documents of this category.");
                if (!DocumentStatuses.Pending.Contains(doc.Status)) return AccessResult<DocDetail>.Conflict("not_pending", "Only submitted documents are verified.");
                if (doc.ApprovalLevel != ApprovalLevels.ReviewAndApprove) return AccessResult<DocDetail>.Conflict("verification_not_needed", "This document goes straight to an approver.");
                if (doc.VerifiedAt is not null) return AccessResult<DocDetail>.Conflict("already_verified", $"Already verified by {doc.VerifiedByEmail}.");
                if (doc.SubmittedByUserId == me || doc.CreatedByUserId == me) return AccessResult<DocDetail>.Forbidden("own_document", "You can't verify your own document.");
                doc.Status = DocumentStatuses.UnderReview; doc.ReviewStartedAt ??= now; doc.ReviewStartedByEmail ??= caller.Email;
                doc.VerifiedAt = now; doc.VerifiedByUserId = me; doc.VerifiedByEmail = caller.Email;
                Event(doc, "verified", from, doc.Status, note, caller.Actor, traceId);
                notify.Add(() => NotifyAsync(doc, ApproversFor(doc.Risk), "document.pending_approval", $"{doc.Reference} is verified and waits for approval",
                    $"{doc.Title} was verified by {caller.Email}.", $"approve:{doc.Id}:{doc.CurrentVersion}", exclude: me, ct));
                break;
            }
            case "approve":
            {
                if (!CanApprove(caller, doc.Category, doc.Risk))
                    return AccessResult<DocDetail>.Forbidden("cannot_approve", doc.Risk == DocumentRisks.High ? "High-risk documents need the 'approve high-risk documents' permission." : "You can't approve documents of this category.");
                if (!DocumentStatuses.Pending.Contains(doc.Status)) return AccessResult<DocDetail>.Conflict("not_pending", "Only submitted documents are approved.");
                if (doc.SubmittedByUserId == me || doc.CreatedByUserId == me) return AccessResult<DocDetail>.Forbidden("own_document", "Someone other than the preparer must approve.");
                if (doc.ApprovalLevel == ApprovalLevels.ReviewAndApprove && doc.VerifiedAt is null) return AccessResult<DocDetail>.Conflict("verification_required", "A reviewer must verify it first.");
                if (doc.VerifiedByUserId == me) return AccessResult<DocDetail>.Forbidden("verifier_cannot_approve", "You verified this document; a different person approves it.");
                var checks = await PrepareAsync(doc, type, data, sources, doc.FactoryId, ct);
                if (checks.Errors.Count > 0)
                    return AccessResult<DocDetail>.Conflict("has_errors", $"The document no longer passes its checks: {string.Join(" ", checks.Errors.Take(5).Select(e => e.Message))} Reject it so the preparer can correct it.");
                if (await PostStockAsync(doc, type, data, caller, ct) is { } stockError) return stockError;
                doc.Status = DocumentStatuses.Approved; doc.DecidedAt = now; doc.DecidedByUserId = me; doc.DecidedByEmail = caller.Email; doc.DecisionNote = note;
                Event(doc, "approved", from, doc.Status, note, caller.Actor, traceId);
                notify.Add(() => NotifyPeopleAsync(doc, "document.approved", $"{doc.Reference} approved", $"{doc.Title} was approved by {caller.Email}.", ct));
                break;
            }
            case "reject":
            {
                if (!(CanReview(caller, doc.Category) || CanApprove(caller, doc.Category, doc.Risk)))
                    return AccessResult<DocDetail>.Forbidden("cannot_reject", "You can't review or approve documents of this category.");
                if (!DocumentStatuses.Pending.Contains(doc.Status)) return AccessResult<DocDetail>.Conflict("not_pending", "Only submitted documents are rejected.");
                if (doc.SubmittedByUserId == me) return AccessResult<DocDetail>.Forbidden("own_document", "You can't reject your own submission; ask a reviewer, or wait.");
                if (note is null) return AccessResult<DocDetail>.Invalid("reason_required", "Give the reason so the preparer can correct it.");
                doc.Status = DocumentStatuses.Rejected; doc.DecidedAt = now; doc.DecidedByUserId = me; doc.DecidedByEmail = caller.Email; doc.DecisionNote = note;
                Event(doc, "rejected", from, doc.Status, note, caller.Actor, traceId);
                notify.Add(() => NotifyPeopleAsync(doc, "document.rejected", $"{doc.Reference} rejected", $"{caller.Email}: {note}", ct));
                break;
            }
            case "revise":
            {
                if (!CanPrepare(caller, doc.Category)) return AccessResult<DocDetail>.Forbidden("cannot_prepare", "You can't edit documents of this category.");
                if (doc.Status != DocumentStatuses.Approved) return AccessResult<DocDetail>.Conflict("not_approved", "Only approved documents are revised.");
                if (doc.StockPosted) return AccessResult<DocDetail>.Conflict("stock_posted", "Stock was posted from this document; record a correcting GRN or issue note instead.");
                if (note is null) return AccessResult<DocDetail>.Invalid("reason_required", "Say why the approved document is revised.");
                doc.Status = DocumentStatuses.Draft;
                doc.SubmittedAt = null; doc.SubmittedByUserId = null; doc.SubmittedByEmail = null; doc.ReviewStartedAt = null; doc.ReviewStartedByEmail = null;
                doc.VerifiedAt = null; doc.VerifiedByUserId = null; doc.VerifiedByEmail = null; doc.DecidedAt = null; doc.DecidedByUserId = null; doc.DecidedByEmail = null; doc.DecisionNote = null;
                Event(doc, "reopened_for_revision", from, doc.Status, note, caller.Actor, traceId);
                break;
            }
            case "archive":
            {
                if (!caller.Has(Permissions.DocumentsControl)) return AccessResult<DocDetail>.Forbidden("cannot_archive", "Archiving needs document control.");
                if (doc.Status is not (DocumentStatuses.Approved or DocumentStatuses.Rejected)) return AccessResult<DocDetail>.Conflict("not_final", "Only approved or rejected documents are archived.");
                doc.Status = DocumentStatuses.Archived; doc.ArchivedAt = now; doc.ArchivedByEmail = caller.Email;
                Event(doc, "archived", from, doc.Status, note, caller.Actor, traceId);
                break;
            }
            default:
                return AccessResult<DocDetail>.NotFound("unknown_action", "Unknown action.");
        }
        doc.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { return AccessResult<DocDetail>.Conflict("version_conflict", "Someone else acted on this document; reload and try again."); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return AccessResult<DocDetail>.Conflict("duplicate", "This was already recorded."); }
        return AccessResult<DocDetail>.Ok(await DetailAsync(doc, caller, ct));
    }

    private static string[] ApproversFor(string risk) => risk == DocumentRisks.High ? new[] { Permissions.DocumentsApproveHigh } : new[] { Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh };

    private async Task NotifyAsync(FactoryDocument doc, IReadOnlyCollection<string> anyOf, string kind, string title, string body, string dedup, Guid exclude, CancellationToken ct)
    {
        var users = (await _notifier.ResponsibleAsync(doc.FactoryId, doc.Category, doc.LineId, anyOf, ct))
            .Where(u => u != exclude && u != doc.CreatedByUserId && u != doc.SubmittedByUserId).ToList();
        await _notifier.NotifyAsync(users, doc.FactoryId, kind, title, body, $"/app/documents/{doc.Id}", "factory_document", doc.Id.ToString(), dedup, ct);
    }

    private async Task NotifyPeopleAsync(FactoryDocument doc, string kind, string title, string body, CancellationToken ct)
    {
        var people = new[] { doc.CreatedByUserId, doc.SubmittedByUserId }.Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
        await _notifier.NotifyAsync(people, doc.FactoryId, kind, title, body, $"/app/documents/{doc.Id}", "factory_document", doc.Id.ToString(),
            $"{kind}:{doc.Id}:{doc.CurrentVersion}", ct);
    }

    /// <summary>
    /// Approved GRN adds accepted quantities to stock (creating new item codes) and its listed rolls to the roll stock; an approved
    /// issue note removes quantities (and roll quantities), refusing to go below zero on an item or a roll. Runs once per document,
    /// under a factory stock lock, inside the approval's transaction; roll movements always add up to their item movement.
    /// </summary>
    private async Task<AccessResult<DocDetail>?> PostStockAsync(FactoryDocument doc, DocumentTypeDef type, JsonObject data, AccessContext caller, CancellationToken ct)
    {
        if (type.Code is not ("store.grn" or "store.issue") || doc.StockPosted) return null;
        await LockAsync(_db, "stock:" + doc.FactoryId, ct);
        var rows = DocJson.Rows(data, "items").OfType<JsonObject>().ToList();
        var rollRows = DocJson.Rows(data, "rolls").OfType<JsonObject>().ToList();
        var codes = rows.Select(r => DocJson.Str(r["itemCode"])?.ToUpperInvariant()).Where(c => c is not null).Distinct().ToList();
        var items = await _db.InventoryItems.Where(i => i.FactoryId == doc.FactoryId && codes.Contains(i.CodeKey)).ToDictionaryAsync(i => i.CodeKey, ct);
        var posted = new List<(StockMovement Movement, string Code)>();
        if (type.Code == "store.grn")
        {
            var date = DocJson.Date(data["receivedOn"]) ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var supplier = DocJson.Str(data["supplier"]);
            var supplierKey = DocumentEnricher.SupplierKey(supplier);
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var code = DocJson.Str(r["itemCode"])!.ToUpperInvariant();
                var qty = DocJson.Dec(r["acceptedQty"]) ?? 0;
                if (!items.TryGetValue(code, out var item))
                {
                    var cat = DocJson.Str(r["category"]);
                    item = new InventoryItem
                    {
                        FactoryId = doc.FactoryId, Code = DocJson.Str(r["itemCode"])!, CodeKey = code, Name = DocJson.Str(r["itemName"]) ?? code,
                        Category = cat is "FABRIC" or "ACCESSORIES" or "TRIMS" or "PACKING" or "CHEMICALS" ? cat : "OTHER",
                        Unit = DocJson.Str(r["unit"]) ?? "pcs", CreatedByEmail = caller.Email,
                    };
                    _db.InventoryItems.Add(item);
                    items[code] = item;
                    _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "inventory.item_created", "inventory_item", item.Id.ToString(), null, item.Code, $"Created by {doc.Reference}", null, doc.FactoryId));
                }
                if (qty <= 0) continue;
                var m = new StockMovement
                {
                    FactoryId = doc.FactoryId, ItemId = item.Id, Kind = "RECEIPT", Quantity = qty, UnitCost = DocJson.Dec(r["unitCost"]), MovementDate = date,
                    DocumentId = doc.Id, DocumentReference = doc.Reference, LineNo = i + 1, Counterparty = supplier, RecordedByEmail = caller.Email,
                };
                _db.StockMovements.Add(m);
                posted.Add((m, code));
            }
            if (rollRows.Count > 0)
            {
                // Same roll already in the store (another GRN approved meanwhile)?
                var keys = rollRows.Select(r => DocJson.Str(r["rollNumber"])?.ToUpperInvariant()).Where(k => k is not null).Distinct().ToList();
                var existing = await _db.InventoryRolls.AsNoTracking().Where(x => x.FactoryId == doc.FactoryId && x.SupplierKey == supplierKey && keys.Contains(x.RollKey)).ToListAsync(ct);
                foreach (var rr in rollRows)
                {
                    var key = DocJson.Str(rr["rollNumber"])?.ToUpperInvariant();
                    var lot = (DocJson.Str(rr["lotNumber"]) ?? "").ToUpperInvariant();
                    if (existing.FirstOrDefault(x => x.RollKey == key && x.LotKey == lot) is { } dup)
                        return AccessResult<DocDetail>.Conflict("duplicate_roll", $"Roll {dup.RollNumber} from this supplier is already in the store ({dup.GrnReference}); reject the GRN so it can be corrected.");
                }
                await _db.SaveChangesAsync(ct); // movement ids for the roll detail
                for (var k = 0; k < rollRows.Count; k++)
                {
                    var rr = rollRows[k];
                    var code = DocJson.Str(rr["itemCode"])!.ToUpperInvariant();
                    var movement = posted.FirstOrDefault(p => p.Code == code).Movement;
                    if (movement is null) return AccessResult<DocDetail>.Conflict("roll_item_not_listed", $"Roll row {k + 1}: item {code} has no accepted quantity on this GRN.");
                    var item = items[code];
                    var qty = DocJson.Dec(rr["quantity"]) ?? 0;
                    var roll = new InventoryRoll
                    {
                        FactoryId = doc.FactoryId, ItemId = item.Id, RollNumber = DocJson.Str(rr["rollNumber"])!, RollKey = DocJson.Str(rr["rollNumber"])!.ToUpperInvariant(),
                        LotNumber = DocJson.Str(rr["lotNumber"]), LotKey = (DocJson.Str(rr["lotNumber"]) ?? "").ToUpperInvariant(), Supplier = supplier, SupplierKey = supplierKey,
                        Color = DocJson.Str(rr["color"]), ShadeGroup = DocJson.Str(rr["shadeGroup"]), WidthCm = DocJson.Dec(rr["widthCm"]),
                        Warehouse = DocJson.Str(rr["warehouse"]), Location = DocJson.Str(rr["location"]), ReceivedQuantity = qty, Unit = item.Unit, ReceivedOn = date,
                        GrnDocumentId = doc.Id, GrnReference = doc.Reference, CreatedByEmail = caller.Email,
                    };
                    _db.InventoryRolls.Add(roll);
                    _db.InventoryRollMovements.Add(new InventoryRollMovement
                    {
                        FactoryId = doc.FactoryId, RollId = roll.Id, StockMovementId = movement.Id, DocumentId = doc.Id, DocumentReference = doc.Reference,
                        RowNo = k + 1, Kind = "RECEIPT", Quantity = qty, MovementDate = date, RecordedByEmail = caller.Email,
                    });
                }
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "inventory.rolls_received", "factory_document", doc.Id.ToString(), null, rollRows.Count.ToString(Inv),
                    $"{rollRows.Count} roll(s) received by {doc.Reference}", null, doc.FactoryId));
            }
        }
        else
        {
            var date = DocJson.Date(data["issuedOn"]) ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var balances = await _enricher.BalancesAsync(doc.FactoryId, items.Values.Select(i => i.Id).ToList(), ct);
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var code = DocJson.Str(r["itemCode"])!.ToUpperInvariant();
                if (!items.TryGetValue(code, out var item)) return AccessResult<DocDetail>.Conflict("unknown_item", $"Row {i + 1}: item {code} doesn't exist.");
                var qty = DocJson.Dec(r["quantity"]) ?? 0;
                var bal = balances.GetValueOrDefault(item.Id.ToString());
                if (qty > bal) return AccessResult<DocDetail>.Conflict("insufficient_stock", $"Row {i + 1}: only {bal} {item.Unit} of {item.Code} in stock; reject the note so it can be corrected.");
                balances[item.Id.ToString()] = bal - qty;
                var m = new StockMovement
                {
                    FactoryId = doc.FactoryId, ItemId = item.Id, Kind = "ISSUE", Quantity = qty, MovementDate = date, DocumentId = doc.Id, DocumentReference = doc.Reference,
                    LineNo = i + 1, Counterparty = DocJson.Str(data["issuedTo"]), RecordedByEmail = caller.Email,
                };
                _db.StockMovements.Add(m);
                posted.Add((m, code));
            }
            // Rolls: re-checked here, under the lock (the checks at submit / approval ran without it).
            var itemIds = items.Values.Select(i => i.Id).ToList();
            var rolls = await _db.InventoryRolls.AsNoTracking().Where(x => x.FactoryId == doc.FactoryId && itemIds.Contains(x.ItemId)).ToListAsync(ct);
            var left = await _enricher.RollRemainingAsync(doc.FactoryId, rolls.Select(x => x.Id).ToList(), ct);
            var plan = new List<(InventoryRoll Roll, decimal Qty, int Row)>();
            for (var k = 0; k < rollRows.Count; k++)
            {
                var rr = rollRows[k];
                var key = DocJson.Str(rr["rollNumber"])?.ToUpperInvariant();
                var lot = DocJson.Str(rr["lotNumber"])?.ToUpperInvariant();
                var match = rolls.Where(x => x.RollKey == key && (lot is null || x.LotKey == lot)).ToList();
                if (match.Count != 1) return AccessResult<DocDetail>.Conflict("unknown_roll", $"Roll row {k + 1}: roll {key} can't be identified among the rolls of the items on this note.");
                var qty = DocJson.Dec(rr["quantity"]) ?? 0;
                var have = left.GetValueOrDefault(match[0].Id);
                if (qty <= 0 || qty > have) return AccessResult<DocDetail>.Conflict("roll_insufficient", $"Roll row {k + 1}: only {have} left on roll {match[0].RollNumber}; reject the note so it can be corrected.");
                left[match[0].Id] = have - qty;
                plan.Add((match[0], qty, k + 1));
            }
            foreach (var (code, item) in items)
            {
                var lineQty = posted.Where(p => p.Code == code).Sum(p => p.Movement.Quantity);
                var rolled = plan.Where(p => p.Roll.ItemId == item.Id).Sum(p => p.Qty);
                var onRolls = rolls.Where(x => x.ItemId == item.Id).Sum(x => left.GetValueOrDefault(x.Id)) + rolled; // before this issue
                var loose = balances.GetValueOrDefault(item.Id.ToString()) + lineQty - onRolls;
                if (rolled > lineQty || lineQty - rolled > Math.Max(0, loose))
                    return AccessResult<DocDetail>.Conflict("rolls_required", $"{item.Code} is stocked by roll: the rolls listed must cover what is issued beyond loose stock; reject the note so it can be corrected.");
            }
            if (plan.Count > 0)
            {
                await _db.SaveChangesAsync(ct); // movement ids for the roll detail
                foreach (var (roll, qty, row) in plan)
                {
                    var movement = posted.First(p => items[p.Code].Id == roll.ItemId).Movement;
                    _db.InventoryRollMovements.Add(new InventoryRollMovement
                    {
                        FactoryId = doc.FactoryId, RollId = roll.Id, StockMovementId = movement.Id, DocumentId = doc.Id, DocumentReference = doc.Reference,
                        RowNo = row, Kind = "ISSUE", Quantity = qty, MovementDate = date, RecordedByEmail = caller.Email,
                    });
                }
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "inventory.rolls_issued", "factory_document", doc.Id.ToString(), null, plan.Count.ToString(Inv),
                    $"{plan.Count} roll(s) issued by {doc.Reference}", null, doc.FactoryId));
            }
        }
        doc.StockPosted = true;
        return null;
    }

    // ── Lookups ─────────────────────────────────────────────────────────────

    public async Task<AccessResult<DocLookupResult>> LookupAsync(AccessContext caller, Guid? factoryId, string lookup, string key, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocLookupResult>.NotFound("factory_not_found", "No factory in your scope.");
        var allowed = lookup switch
        {
            "item" => caller.HasAny(Permissions.InventoryView, Permissions.DocCatStore, Permissions.DocumentsControl),
            "worker" => caller.HasAny(Permissions.WorkersView, Permissions.DocCatHr, Permissions.DocCatCompliance, Permissions.DocumentsControl),
            "machine" => caller.HasAny(Permissions.DevicesView, Permissions.DocCatDevices, Permissions.DocumentsControl),
            "merch_order" => caller.HasAny(Permissions.MerchandisingView, Permissions.DocCatMerchandising, Permissions.DocCatCommercial, Permissions.DocCatQuality, Permissions.DocumentsControl),
            "document" => true,
            _ => false,
        };
        if (!allowed) return AccessResult<DocLookupResult>.Forbidden("lookup_not_permitted", "You can't look up these records.");
        var r = await LookupValuesAsync(caller, factory.Id, lookup, key.Trim(), ct);
        return AccessResult<DocLookupResult>.Ok(r);
    }

    private async Task<DocLookupResult> LookupValuesAsync(AccessContext caller, Guid factoryId, string lookup, string key, CancellationToken ct)
    {
        var upper = key.Trim().ToUpperInvariant();
        switch (lookup)
        {
            case "item":
            {
                var it = await _db.InventoryItems.AsNoTracking().FirstOrDefaultAsync(i => i.FactoryId == factoryId && i.CodeKey == upper, ct);
                if (it is null) return new DocLookupResult(lookup, key, false, null, $"No store item {key}.");
                var bal = (await _enricher.BalancesAsync(factoryId, new[] { it.Id }, ct)).GetValueOrDefault(it.Id.ToString());
                return new DocLookupResult(lookup, key, true, new JsonObject { ["itemCode"] = it.Code, ["itemName"] = it.Name, ["unit"] = it.Unit, ["category"] = it.Category, ["balance"] = bal }, null);
            }
            case "worker":
            {
                var w = await _db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.FactoryId == factoryId && x.WorkerCode.ToUpper() == upper, ct);
                if (w is null) return new DocLookupResult(lookup, key, false, null, $"No worker {key} in this factory.");
                return new DocLookupResult(lookup, key, true, new JsonObject
                {
                    ["workerCode"] = w.WorkerCode, ["name"] = w.FullName, ["workerName"] = w.FullName, ["fullName"] = w.FullName, ["designation"] = w.Designation, ["line"] = w.LineId, ["status"] = w.Status,
                }, null);
            }
            case "machine":
            {
                var m = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.FactoryId == factoryId && x.Code == upper, ct);
                if (m is null) return new DocLookupResult(lookup, key, false, null, $"No machine {key} in this factory.");
                return new DocLookupResult(lookup, key, true, new JsonObject { ["machineCode"] = m.Code, ["machineType"] = m.MachineType, ["brand"] = m.Brand, ["serialNumber"] = m.SerialNumber }, null);
            }
            case "merch_order":
            {
                var o = await _db.MerchOrders.AsNoTracking().Where(x => x.BuyerOrderNumber.ToUpper() == upper || x.Reference == upper).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
                if (o is null) return new DocLookupResult(lookup, key, false, null, $"No buyer order with PO {key}.");
                var v = new JsonObject
                {
                    ["buyer"] = o.Buyer, ["poNumber"] = o.BuyerOrderNumber, ["orderReference"] = o.BuyerOrderNumber, ["style"] = o.StyleRef, ["description"] = o.ProductDescription,
                    ["quantity"] = o.Quantity, ["orderQuantity"] = o.Quantity, ["unit"] = o.Unit, ["unitPrice"] = o.UnitPrice, ["currency"] = o.Currency,
                    ["deliveryDate"] = o.DeliveryDate?.ToString("yyyy-MM-dd", Inv), ["destination"] = o.Destination,
                };
                if (o.BreakdownJson is not null && JsonNode.Parse(o.BreakdownJson) is JsonArray bd) v["breakdown"] = bd;
                foreach (var k in v.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) v.Remove(k);
                return new DocLookupResult(lookup, key, true, v, null);
            }
            case "document":
            {
                var d = await Visible(_db, caller).FirstOrDefaultAsync(x => x.FactoryId == factoryId && x.Reference == upper, ct);
                if (d is null) return new DocLookupResult(lookup, key, false, null, $"No document {key} you can see.");
                return new DocLookupResult(lookup, key, true, new JsonObject { ["reference"] = d.Reference, ["type"] = d.TypeCode, ["status"] = d.Status, ["title"] = d.Title, ["id"] = d.Id.ToString() }, null);
            }
        }
        return new DocLookupResult(lookup, key, false, null, "Unknown lookup.");
    }

    // ── Status board ────────────────────────────────────────────────────────

    public async Task<AccessResult<DocMonitorDto>> MonitorAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocMonitorDto>.NotFound("factory_not_found", "No factory in your scope.");
        var r = await DocumentMonitor.BuildAsync(_db, factory.Id, DateTimeOffset.UtcNow, ct);
        return AccessResult<DocMonitorDto>.Ok(r);
    }

    public async Task<AccessResult<DocUsageDto>> UsageAsync(AccessContext caller, Guid? factoryId, int days, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocUsageDto>.NotFound("factory_not_found", "No factory in your scope.");
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 366));
        var rows = await _db.AiApiUsage.AsNoTracking().Where(u => u.FactoryId == factory.Id && u.OccurredAt >= since)
            .GroupBy(u => u.Purpose).Select(g => new { g.Key, Calls = g.Count(), Failures = g.Count(x => x.Outcome != "ok"), In = g.Sum(x => x.InputTokens ?? 0), Out = g.Sum(x => x.OutputTokens ?? 0) })
            .ToListAsync(ct);
        var s = _ai.Status();
        return AccessResult<DocUsageDto>.Ok(new DocUsageDto(rows.Sum(r => r.Calls), rows.Sum(r => r.Failures), rows.Sum(r => r.In), rows.Sum(r => r.Out),
            rows.Select(r => new DocPurposeCount(r.Key, r.Calls, r.Failures)).ToList(), s.Provider, s.Model));
    }

    /// <summary>Scheduled generation log of a factory (created, already existing, no data yet, failed with the error), newest first.</summary>
    public async Task<AccessResult<DocScheduleStatus>> ScheduleRunsAsync(AccessContext caller, Guid? factoryId, int days, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocScheduleStatus>.NotFound("factory_not_found", "No factory in your scope.");
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 366));
        var rows = await (from r in _db.DocumentScheduleRuns.AsNoTracking()
                          join d in _db.FactoryDocuments.AsNoTracking() on r.DocumentId equals d.Id into dj
                          from d in dj.DefaultIfEmpty()
                          where r.FactoryId == factory.Id && r.LastAttemptAt >= since
                          orderby r.LastAttemptAt descending
                          select new { r, Reference = d == null ? null : d.Reference }).Take(300).ToListAsync(ct);
        return AccessResult<DocScheduleStatus>.Ok(new DocScheduleStatus(_options.AutomationEnabled, rows.Select(x => new DocScheduleRunDto(x.r.Id, x.r.TypeCode,
            DocumentCatalog.Get(x.r.TypeCode)?.Name ?? x.r.TypeCode, x.r.PeriodFrom.ToString("yyyy-MM-dd", Inv), x.r.PeriodTo.ToString("yyyy-MM-dd", Inv), x.r.LineKey,
            x.r.Status, x.r.Attempts, x.r.ErrorCode, x.r.ErrorMessage, x.r.DocumentId, x.Reference, x.r.FirstAttemptAt, x.r.LastAttemptAt)).ToList()));
    }

    // ── Files and AI ────────────────────────────────────────────────────────

    public async Task<AccessResult<DocFileDto>> UploadAsync(AccessContext caller, Guid id, DocUploadInput input, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, true, ct);
        if (doc is null) return NotFound<DocFileDto>();
        if (CheckEditable(doc, caller) is { } refuse) return AccessResult<DocFileDto>.Forbidden(refuse.Code!, refuse.Message!);
        if (!DocumentFileReader.TryInspect(input.Content, input.FileName, _options.MaxFileBytes, out var info, out var error))
            return AccessResult<DocFileDto>.Invalid(error ?? "invalid_file", DocumentFileReader.Describe(error ?? "", _options.MaxFileBytes));
        if (await _db.FactoryDocumentFiles.CountAsync(f => f.DocumentId == doc.Id, ct) >= MaxFilesPerDocument)
            return AccessResult<DocFileDto>.Conflict("too_many_files", $"A document holds at most {MaxFilesPerDocument} files.");
        var sha = Convert.ToHexString(SHA256.HashData(input.Content)).ToLowerInvariant();
        if (await _db.FactoryDocumentFiles.AsNoTracking().AnyAsync(f => f.DocumentId == doc.Id && f.Sha256 == sha, ct))
            return AccessResult<DocFileDto>.Conflict("duplicate_file", "This file is already attached.");
        var key = await _files.SaveAsync(input.Content, info!.Extension, ct);
        var name = Path.GetFileName(input.FileName ?? $"upload.{info.Extension}");
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray());
        var file = new FactoryDocumentFile
        {
            DocumentId = doc.Id, FactoryId = doc.FactoryId, StorageKey = key, ContentType = info.ContentType, Format = info.Format, SizeBytes = input.Content.LongLength,
            Sha256 = sha, OriginalFileName = name.Length > 255 ? name[..255] : name, CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
        };
        _db.FactoryDocumentFiles.Add(file);
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        Event(doc, "file_attached", doc.Status, doc.Status, file.OriginalFileName, caller.Actor, traceId);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await _files.DeleteAsync(key, CancellationToken.None);
            return AccessResult<DocFileDto>.Conflict("duplicate_file", "This file is already attached.");
        }
        return AccessResult<DocFileDto>.Created(FileDto(file));
    }

    public async Task<AccessResult<(Stream Content, string ContentType, string FileName)>> OpenFileAsync(AccessContext caller, Guid id, Guid fileId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, false, ct);
        if (doc is null) return NotFound<(Stream, string, string)>();
        var f = await _db.FactoryDocumentFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.DocumentId == id, ct);
        if (f is null) return AccessResult<(Stream, string, string)>.NotFound("file_not_found", "File not found.");
        var s = await _files.OpenReadAsync(f.StorageKey, ct);
        if (s is null) return AccessResult<(Stream, string, string)>.NotFound("file_missing", "The stored file is missing.");
        return AccessResult<(Stream, string, string)>.Ok((s, f.ContentType, f.OriginalFileName ?? $"file.{f.Format}"));
    }

    private async Task<byte[]?> ReadBytesAsync(string key, CancellationToken ct)
    {
        await using var s = await _files.OpenReadAsync(key, ct);
        if (s is null) return null;
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task<AccessResult<DocFileDto>> ExtractAsync(AccessContext caller, Guid id, Guid fileId, bool allowAi, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, true, ct);
        if (doc is null) return NotFound<DocFileDto>();
        if (CheckEditable(doc, caller) is { } refuse) return AccessResult<DocFileDto>.Forbidden(refuse.Code!, refuse.Message!);
        var file = await _db.FactoryDocumentFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.DocumentId == id, ct);
        if (file is null) return AccessResult<DocFileDto>.NotFound("file_not_found", "File not found.");
        if (file.ExtractionAttempts >= _options.MaxExtractionsPerFile)
            return AccessResult<DocFileDto>.Conflict("extraction_limit", $"This file was already read {file.ExtractionAttempts} times (limit {_options.MaxExtractionsPerFile}).");
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var bytes = await ReadBytesAsync(file.StorageKey, ct);
        if (bytes is null) return AccessResult<DocFileDto>.NotFound("file_missing", "The stored file is missing.");
        file.ExtractionAttempts++;
        var ctx = new AiCallContext(doc.FactoryId, "factory_document", doc.Id.ToString(), caller.UserId);
        try
        {
            ExtractionOutcome outcome;
            if (file.Format == "image")
            {
                if (!allowAi) throw new DocumentExtractionException("ocr_required", "Reading an image needs the AI provider (OCR). Allow AI, or type the values.");
                outcome = await _extractor.ExtractAsync(type, null, new AiImage(bytes, file.ContentType), true, ctx, ct);
            }
            else
            {
                var parsed = DocumentFileReader.ReadText(bytes, file.Format, _options.MaxPages, 1_000_000);
                var text = string.Join("\n", parsed.Pages.Select(p => p.Text));
                outcome = await _extractor.ExtractAsync(type, text, null, allowAi && _ai.Status().Configured, ctx, ct);
                if (outcome.Values.Count == 0 && !allowAi) outcome.Rejected.Add("Nothing labelled was found; allow AI to read unlabelled values.");
            }
            file.ExtractionStatus = "COMPLETED"; file.FailureCode = null; file.FailureMessage = null;
            file.ExtractionMethod = outcome.Method; file.ExtractionModel = outcome.Model; file.ExtractedAt = DateTimeOffset.UtcNow;
            file.ExtractionJson = new JsonObject
            {
                ["values"] = outcome.Values, ["methods"] = new JsonObject(outcome.Methods.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["confidence"] = new JsonObject(outcome.Confidence.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Math.Round(kv.Value, 3)))),
                ["rejected"] = new JsonArray(outcome.Rejected.Take(50).Select(r => (JsonNode)r).ToArray()),
            }.ToJsonString();
            Event(doc, "file_read", doc.Status, doc.Status, $"{file.OriginalFileName}: {outcome.Values.Count} value(s) suggested ({outcome.Method})", caller.Actor, traceId);
        }
        catch (DocumentParseException ex) { Fail(file, ex.Code, ex.Message); }
        catch (DocumentExtractionException ex) { Fail(file, ex.Code, ex.Message); }
        await _db.SaveChangesAsync(ct);
        return AccessResult<DocFileDto>.Ok(FileDto(file));
    }

    private static void Fail(FactoryDocumentFile f, string code, string message)
    {
        f.ExtractionStatus = "FAILED"; f.FailureCode = code.Length > 40 ? code[..40] : code; f.FailureMessage = message.Length > 500 ? message[..500] : message; f.ExtractedAt = DateTimeOffset.UtcNow;
    }

    public async Task<AccessResult<DocClassification>> ClassifyAsync(AccessContext caller, Guid? factoryId, DocUploadInput input, bool allowAi, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<DocClassification>.NotFound("factory_not_found", "No factory in your scope.");
        if (!DocumentFileReader.TryInspect(input.Content, input.FileName, _options.MaxFileBytes, out var info, out var error))
            return AccessResult<DocClassification>.Invalid(error ?? "invalid_file", DocumentFileReader.Describe(error ?? "", _options.MaxFileBytes));
        var types = DocumentCatalog.All.Where(t => CanPrepare(caller, t.Category) && (!caller.IsLineScoped || t.LineOption)).ToList();
        if (types.Count == 0) return AccessResult<DocClassification>.Forbidden("cannot_prepare", "You can't prepare documents.");
        string? text = null;
        if (info!.Format != "image")
        {
            try { text = string.Join("\n", DocumentFileReader.ReadText(input.Content, info.Format, 5, 20_000).Pages.Select(p => p.Text)); }
            catch (DocumentParseException ex) { return AccessResult<DocClassification>.Invalid(ex.Code, ex.Message); }
        }
        var scores = text is null ? new List<DocTypeScore>() : DocumentExtractor.ScoreTypes(types, text).ToList();
        var best = scores.FirstOrDefault();
        if (best is not null && (best.Score >= 0.6 || !allowAi || !_ai.Status().Configured))
            return AccessResult<DocClassification>.Ok(new DocClassification(best.Code, "rules", best.Score, scores, "Matched by the document's own words."));
        if (!allowAi || !_ai.Status().Configured)
            return AccessResult<DocClassification>.Ok(new DocClassification(null, "rules", null, scores, best is null ? "No document type matched; choose one." : null));
        try
        {
            var list = string.Join("\n", types.Select(t => $"- {t.Code}: {t.Name}"));
            var user = $"Which one of these factory document types is this document? Answer {{\"type\":\"<code or null>\",\"confidence\":0.0-1.0}}.\nTypes:\n{list}\n" +
                       (text is null ? "The document is the attached image." : $"Beginning of the document:\n<<<\n{(text.Length > 2000 ? text[..2000] : text)}\n>>>");
            var r = await _ai.CallAsync("document.classify", "You classify factory documents. Answer only JSON. Use null when none fits.", user,
                text is null ? new[] { new AiImage(input.Content, info.ContentType) } : null, 200, new AiCallContext(factory.Id, null, null, caller.UserId), ct);
            var code = DocJson.Str(r.Json["type"]);
            var conf = DocJson.Dec(r.Json["confidence"]);
            if (code is null || types.All(t => t.Code != code))
                return AccessResult<DocClassification>.Ok(new DocClassification(null, "ai", null, scores, "The AI found no matching type; choose one."));
            return AccessResult<DocClassification>.Ok(new DocClassification(code, "ai", conf is { } c && c is >= 0 and <= 1 ? (double)c : null, scores, $"Suggested by {r.Provider}/{r.Model}; confirm it."));
        }
        catch (DocumentExtractionException ex)
        {
            return AccessResult<DocClassification>.Ok(new DocClassification(best?.Code, "rules", best?.Score, scores, $"AI unavailable ({ex.Message})."));
        }
    }

    private static JsonObject Brief(DocumentTypeDef type, JsonObject data, int rowsPerTable)
    {
        var o = new JsonObject();
        foreach (var f in type.Fields)
        {
            if (f.Kind == FieldKind.Table)
            {
                var rows = DocJson.Rows(data, f.Key);
                o[f.Label] = new JsonObject { ["rows"] = rows.Count, ["first"] = new JsonArray(rows.Take(rowsPerTable).Select(r => r?.DeepClone()).ToArray()) };
            }
            else if (data[f.Key] is { } v) o[f.Label] = v.DeepClone();
        }
        return o;
    }

    public async Task<AccessResult<DocDetail>> SummarizeAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, true, ct);
        if (doc is null) return NotFound<DocDetail>();
        if (!_ai.Status().Configured) return AccessResult<DocDetail>.Invalid(DocumentExtractionException.NotConfigured, _ai.Status().Reason ?? "AI is not configured.");
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var v = await CurrentVersionAsync(doc, ct);
        var checks = DocumentChecks.FromJson(v.ChecksJson);
        // Only the figures and the findings: never the whole document.
        var payload = new JsonObject
        {
            ["document"] = type.Name, ["period"] = doc.PeriodFrom is null ? null : $"{doc.PeriodFrom:yyyy-MM-dd} to {doc.PeriodTo:yyyy-MM-dd}", ["status"] = doc.Status,
            ["values"] = Brief(type, DocJson.Parse(v.DataJson), 5),
            ["missing"] = new JsonArray(checks.Missing.Take(20).Select(i => (JsonNode)i.Message).ToArray()),
            ["errors"] = new JsonArray(checks.Errors.Take(20).Select(i => (JsonNode)i.Message).ToArray()),
            ["warnings"] = new JsonArray(checks.Warnings.Take(20).Select(i => (JsonNode)i.Message).ToArray()),
        };
        var user = payload.ToJsonString();
        if (user.Length > _options.AiMaxChars) user = user[.._options.AiMaxChars];
        try
        {
            var r = await _ai.CallAsync("document.summarize",
                "You brief a garment factory manager on one document. Using only the JSON given, write a 3-6 sentence summary of what it shows and explain each missing value, error and warning in plain words with what a person should check. " +
                "Do not recommend approving or rejecting. Answer JSON {\"summary\":\"...\",\"discrepancies\":[\"...\"]}.",
                user, null, 800, new AiCallContext(doc.FactoryId, "factory_document", doc.Id.ToString(), caller.UserId), ct);
            var summary = DocJson.Str(r.Json["summary"]) ?? "";
            var items = (r.Json["discrepancies"] as JsonArray)?.Select(DocJson.Str).Where(s => s is not null).Take(15).ToList() ?? new List<string?>();
            var text = summary + (items.Count > 0 ? "\n" + string.Join("\n", items.Select(i => "• " + i)) : "");
            doc.AiSummary = text.Length > 4000 ? text[..4000] : text;
            doc.AiSummaryAt = DateTimeOffset.UtcNow;
            doc.AiSummaryModel = $"{r.Provider}/{r.Model}";
            Event(doc, "ai_summary", doc.Status, doc.Status, $"AI summary by {doc.AiSummaryModel}", caller.Actor, traceId);
            await _db.SaveChangesAsync(ct);
            return AccessResult<DocDetail>.Ok(await DetailAsync(doc, caller, ct));
        }
        catch (DocumentExtractionException ex) { return AccessResult<DocDetail>.Conflict(ex.Code, ex.Message); }
    }

    public async Task<AccessResult<string>> DraftTextAsync(AccessContext caller, Guid id, string fieldKey, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, false, ct);
        if (doc is null) return NotFound<string>();
        if (CheckEditable(doc, caller) is { } refuse) return AccessResult<string>.Forbidden(refuse.Code!, refuse.Message!);
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var field = DocumentCatalog.Field(type, fieldKey);
        if (field is null || field.Kind != FieldKind.TextArea || field.Computed) return AccessResult<string>.Invalid("not_a_text_field", "AI drafts only free-text fields.");
        if (!_ai.Status().Configured) return AccessResult<string>.Invalid(DocumentExtractionException.NotConfigured, _ai.Status().Reason ?? "AI is not configured.");
        var data = DocJson.Parse((await CurrentVersionAsync(doc, ct)).DataJson);
        data.Remove(fieldKey);
        var user = new JsonObject { ["document"] = type.Name, ["field"] = field.Label, ["facts"] = Brief(type, data, 5) }.ToJsonString();
        if (user.Length > _options.AiMaxChars) user = user[.._options.AiMaxChars];
        try
        {
            var r = await _ai.CallAsync("document.draft_text",
                "You draft one text field of a garment factory document for a person to edit. Use only the facts given; where facts are missing write [needs input: ...] instead of inventing them. " +
                "Answer JSON {\"text\":\"...\"}.", user, null, 600, new AiCallContext(doc.FactoryId, "factory_document", doc.Id.ToString(), caller.UserId), ct);
            var text = DocJson.Str(r.Json["text"]);
            return text is null ? AccessResult<string>.Conflict(DocumentExtractionException.InvalidOutput, "The AI returned no text.") : AccessResult<string>.Ok(text.Length > 4000 ? text[..4000] : text);
        }
        catch (DocumentExtractionException ex) { return AccessResult<string>.Conflict(ex.Code, ex.Message); }
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    public async Task<AccessResult<(byte[] Content, string ContentType, string FileName)>> RenderAsync(AccessContext caller, Guid id, string format, int? version, string? traceId, CancellationToken ct)
    {
        var doc = await LoadAsync(caller, id, false, ct);
        if (doc is null) return NotFound<(byte[], string, string)>();
        if (format is not ("pdf" or "xlsx" or "docx")) return AccessResult<(byte[], string, string)>.Invalid("invalid_format", "Format must be pdf, xlsx or docx.");
        var type = DocumentCatalog.Get(doc.TypeCode)!;
        var n = version ?? doc.CurrentVersion;
        var v = await _db.FactoryDocumentVersions.AsNoTracking().FirstOrDefaultAsync(x => x.DocumentId == id && x.Number == n, ct);
        if (v is null) return AccessResult<(byte[], string, string)>.NotFound("version_not_found", "Version not found.");
        var factory = await _db.Factories.AsNoTracking().Where(f => f.Id == doc.FactoryId).Select(f => f.Name).FirstAsync(ct);
        var line = doc.LineId is null ? null : await _db.ProductionLines.AsNoTracking().Where(l => l.Id == doc.LineId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        var model = DocumentRender.Build(doc, type, factory, line, v, DocJson.Parse(v.DataJson), DocumentChecks.FromJson(v.ChecksJson));
        var (bytes, ct2) = format switch
        {
            "pdf" => (DocumentRender.Pdf(model), "application/pdf"),
            "xlsx" => (OfficeWriters.Xlsx(model), DocumentFileReader.XlsxContentType),
            _ => (OfficeWriters.Docx(model), MerchFileInspectorDocx),
        };
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "document.exported", "factory_document", doc.Id.ToString(), null, $"{format} v{n}", doc.Reference, traceId, doc.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<(byte[], string, string)>.Ok((bytes, ct2, $"{doc.Reference}-v{n}.{format}"));
    }

    private const string MerchFileInspectorDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}

/// <summary>The status board: drafts, pending, overdue, rejected, missing expected documents. Shared by the API and the automation worker.</summary>
internal static class DocumentMonitor
{
    public static async Task<DocMonitorDto> BuildAsync(BunonBrainDbContext db, Guid factoryId, DateTimeOffset now, CancellationToken ct)
    {
        var clock = await FactoryClock.LoadAsync(db, factoryId, ct);
        var today = clock.Today(now);
        var yesterday = today.AddDays(-1);
        var settings = await db.DocumentTypeSettings.AsNoTracking().Where(s => s.FactoryId == factoryId).ToDictionaryAsync(s => s.TypeCode, ct);
        var live = await db.FactoryDocuments.AsNoTracking().Where(d => d.FactoryId == factoryId && d.Status != DocumentStatuses.Archived && d.Status != DocumentStatuses.Approved)
            .Select(d => new { d.Id, d.Reference, d.TypeCode, d.Category, d.Status, d.PeriodFrom, d.PeriodTo, d.LineId, d.SubmittedAt, d.UpdatedAt, d.CreatedByUserId, d.MissingCount, d.ErrorCount })
            .ToListAsync(ct);
        var weekAgo = now.AddDays(-7);
        var approvedWeek = await db.FactoryDocuments.AsNoTracking().CountAsync(d => d.FactoryId == factoryId && d.Status == DocumentStatuses.Approved && d.DecidedAt >= weekAgo, ct);
        var items = new List<DocMonitorItem>();
        string Name(string code) => DocumentCatalog.Get(code)?.Name ?? code;
        string? Period(DateOnly? f, DateOnly? t) => f is null ? null : t is null || t == f ? f.Value.ToString("yyyy-MM-dd") : $"{f:yyyy-MM-dd} to {t:yyyy-MM-dd}";
        var overdue = 0;
        foreach (var d in live)
        {
            var due = settings.GetValueOrDefault(d.TypeCode)?.DecisionDueHours ?? 24;
            if (DocumentStatuses.Pending.Contains(d.Status) && d.SubmittedAt is { } s && now - s > TimeSpan.FromHours(due))
            {
                overdue++;
                items.Add(new DocMonitorItem(d.Id, d.Reference, d.TypeCode, Name(d.TypeCode), d.Category, "OVERDUE_DECISION", d.Status, Period(d.PeriodFrom, d.PeriodTo), d.LineId, s,
                    $"Waiting {(int)(now - s).TotalHours} h for a decision (due within {due} h)."));
            }
            else if (d.Status == DocumentStatuses.Rejected && now - d.UpdatedAt > TimeSpan.FromDays(1))
                items.Add(new DocMonitorItem(d.Id, d.Reference, d.TypeCode, Name(d.TypeCode), d.Category, "REJECTED_NOT_CORRECTED", d.Status, Period(d.PeriodFrom, d.PeriodTo), d.LineId, d.UpdatedAt,
                    "Rejected more than a day ago and not corrected."));
            else if (d.Status == DocumentStatuses.Draft && d.CreatedByUserId is null && now - d.UpdatedAt > TimeSpan.FromHours(12))
                items.Add(new DocMonitorItem(d.Id, d.Reference, d.TypeCode, Name(d.TypeCode), d.Category, "GENERATED_NOT_SUBMITTED", d.Status, Period(d.PeriodFrom, d.PeriodTo), d.LineId, d.UpdatedAt,
                    d.MissingCount + d.ErrorCount > 0 ? $"Generated draft needs input ({d.MissingCount} missing, {d.ErrorCount} errors)." : "Generated draft waits to be checked and submitted."));
        }
        // Expected daily documents of the last working day that don't exist.
        var missing = new List<DocMonitorItem>();
        if (clock.IsWorkingDay(yesterday))
        {
            var expected = DocumentCatalog.All.Where(t => t.Period == PeriodKind.Day && (settings.GetValueOrDefault(t.Code)?.ExpectedDaily ?? t.DefaultExpectedDaily)).ToList();
            var codes = expected.Select(t => t.Code).ToList();
            var have = await db.FactoryDocuments.AsNoTracking().Where(d => d.FactoryId == factoryId && codes.Contains(d.TypeCode) && d.PeriodFrom == yesterday && d.LineId == null && d.Status != DocumentStatuses.Archived)
                .Select(d => d.TypeCode).Distinct().ToListAsync(ct);
            foreach (var t in expected.Where(t => !have.Contains(t.Code)))
                missing.Add(new DocMonitorItem(null, null, t.Code, t.Name, t.Category, "MISSING", null, yesterday.ToString("yyyy-MM-dd"), null, null,
                    $"No {t.Name} for {yesterday:yyyy-MM-dd} (expected every working day)."));
        }
        items.InsertRange(0, missing);
        var byCat = DocumentCategories.All.Select(c => new DocCategoryCount(c.Code,
            live.Count(d => d.Category == c.Code && DocumentStatuses.Pending.Contains(d.Status)), live.Count(d => d.Category == c.Code && d.Status == DocumentStatuses.Draft),
            missing.Count(m => m.Category == c.Code))).ToList();
        return new DocMonitorDto(factoryId, today.ToString("yyyy-MM-dd"), live.Count(d => d.Status == DocumentStatuses.Draft), live.Count(d => DocumentStatuses.Pending.Contains(d.Status)),
            overdue, live.Count(d => d.Status == DocumentStatuses.Rejected), missing.Count, approvedWeek, items.Take(200).ToList(), byCat);
    }
}
