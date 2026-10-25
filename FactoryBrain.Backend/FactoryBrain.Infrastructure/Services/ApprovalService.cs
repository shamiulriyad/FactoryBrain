using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Approvals;
using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// Reads and decides <see cref="ApprovalRequest"/> rows. A decision is a
/// conditional UPDATE (<c>WHERE Stage = PendingApproval</c>), so two reviewers
/// racing on the same item produce exactly one decision; the loser gets
/// <see cref="ApprovalDecisionOutcome.AlreadyDecided"/>. The linked insight's
/// stage and an <see cref="AuditLogEntry"/> are written in the same
/// transaction.
/// </summary>
public sealed class ApprovalService : IApprovalService
{
    public const int MaxListLimit = 200;
    public const string RoleAdmin = "Admin";

    private readonly FactoryBrainDbContext _db;
    private readonly ILogger<ApprovalService> _log;

    public ApprovalService(FactoryBrainDbContext db, ILogger<ApprovalService> log)
    {
        _db = db; _log = log;
    }

    public static bool TryParseStatus(string? raw, out Stage? stage)
    {
        switch ((raw ?? "pending").Trim().ToLowerInvariant())
        {
            case "pending": case "pending_approval": stage = Stage.PendingApproval; return true;
            case "approved": stage = Stage.Approved; return true;
            case "rejected": stage = Stage.Rejected; return true;
            case "all": stage = null; return true;
            default: stage = null; return false;
        }
    }

    public async Task<ApprovalListResponse> ListAsync(string status, int limit, CancellationToken ct)
    {
        if (!TryParseStatus(status, out var stage))
            throw new ArgumentException($"unknown status '{status}'", nameof(status));
        limit = Math.Clamp(limit, 1, MaxListLimit);

        var q = _db.Approvals.AsNoTracking();
        if (stage is { } s) q = q.Where(a => a.Stage == s);
        var rows = await q.OrderByDescending(a => a.CreatedAt).Take(limit).ToListAsync(ct);

        var insightIds = rows.Select(r => r.InsightId).Distinct().ToList();
        var insights = await _db.Insights.AsNoTracking()
            .Where(i => insightIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);

        var pending = await _db.Approvals.CountAsync(a => a.Stage == Stage.PendingApproval, ct);
        return new ApprovalListResponse(pending,
            rows.Select(r => ToDto(r, insights.GetValueOrDefault(r.InsightId))).ToList());
    }

    public async Task<ApprovalDecisionResult> DecideAsync(
        string id, bool approve, string? reason, ApprovalActor actor, string? traceId, CancellationToken ct)
    {
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var strategy = _db.Database.CreateExecutionStrategy();

        // The execution strategy may retry the whole block on a transient
        // failure, so each attempt starts from a clean change tracker.
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var current = await _db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (current is null) return new ApprovalDecisionResult(ApprovalDecisionOutcome.NotFound, null);
            if (current.Stage != Stage.PendingApproval)
                return new ApprovalDecisionResult(ApprovalDecisionOutcome.AlreadyDecided, ToDto(current, null));
            if (!approve && trimmedReason is null)
                return new ApprovalDecisionResult(ApprovalDecisionOutcome.ReasonRequired, ToDto(current, null));
            if (current.RiskTier == RiskTier.High && !string.Equals(actor.Role, RoleAdmin, StringComparison.Ordinal))
                return new ApprovalDecisionResult(ApprovalDecisionOutcome.RequiresAdmin, ToDto(current, null));

            var to = approve ? Stage.Approved : Stage.Rejected;
            var now = DateTime.UtcNow;

            var changed = await _db.Approvals
                .Where(a => a.Id == id && a.Stage == Stage.PendingApproval)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(a => a.Stage, to)
                    .SetProperty(a => a.DecidedAt, now)
                    .SetProperty(a => a.DecidedByUserId, actor.UserId)
                    .SetProperty(a => a.DecidedByEmail, actor.Email)
                    .SetProperty(a => a.DecisionReason, trimmedReason), ct);
            if (changed == 0)
            {
                // Someone else decided it between the read and the update.
                await tx.RollbackAsync(ct);
                var latest = await _db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, ct);
                return new ApprovalDecisionResult(ApprovalDecisionOutcome.AlreadyDecided, ToDto(latest, null));
            }

            await _db.Insights
                .Where(i => i.Id == current.InsightId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.Stage, to)
                    .SetProperty(i => i.UpdatedAt, now), ct);

            _db.AuditLog.Add(new AuditLogEntry
            {
                OccurredAt  = now,
                ActorUserId = actor.UserId,
                ActorEmail  = actor.Email,
                ActorRole   = actor.Role,
                Action      = approve ? "approval.approved" : "approval.rejected",
                EntityType  = "approval",
                EntityId    = id,
                FromState   = StageNames.ToWire(Stage.PendingApproval),
                ToState     = StageNames.ToWire(to),
                Reason      = trimmedReason,
                TraceId     = traceId,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("Approval {ApprovalId} {Decision} by user {UserId} ({Role}).",
                id, approve ? "approved" : "rejected", actor.UserId, actor.Role);

            var decided = await _db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, ct);
            var insight = await _db.Insights.AsNoTracking().FirstOrDefaultAsync(i => i.Id == decided.InsightId, ct);
            return new ApprovalDecisionResult(ApprovalDecisionOutcome.Ok, ToDto(decided, insight));
        });
    }

    public async Task<IReadOnlyList<AuditEntryDto>> AuditAsync(string? entityType, string? entityId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        var q = _db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(e => e.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId)) q = q.Where(e => e.EntityId == entityId);
        return await q.OrderByDescending(e => e.Id).Take(limit)
            .Select(e => new AuditEntryDto(e.Id, e.OccurredAt, e.ActorUserId, e.ActorEmail, e.ActorRole,
                e.Action, e.EntityType, e.EntityId, e.FromState, e.ToState, e.Reason, e.TraceId))
            .ToListAsync(ct);
    }

    private static ApprovalDto ToDto(ApprovalRequest a, Insight? i) => new(
        a.Id, a.InsightId, a.Title, a.TitleBn, a.Reason,
        StageNames.ToWire(a.RiskTier), StageNames.ToWire(a.Stage),
        a.CreatedAt, a.DecidedAt, a.DecidedByEmail, a.DecisionReason,
        i is null ? null : new ApprovalInsightDto(
            i.Id, i.AgentId, i.AgentLabel, i.Title, i.TitleBn, i.Finding, i.FindingBn,
            i.Factors.Select(f => new AskFactor(f.Label, f.LabelBn, f.Magnitude, f.MagnitudeBn)).ToList(),
            new ApprovalRecommendationDto(
                i.Recommendation.Title, i.Recommendation.TitleBn,
                i.Recommendation.Action, i.Recommendation.ActionBn,
                StageNames.ToWire(i.Recommendation.RiskTier), StageNames.ToWire(i.Recommendation.TargetStage)),
            i.Evidence.Select(e => new EvidenceRefDto(e.Domain, e.Count, e.Filter, e.PreviewIds)).ToList(),
            StageNames.ToWire(i.Stage), i.Confidence, i.CreatedAt, i.UpdatedAt));
}
