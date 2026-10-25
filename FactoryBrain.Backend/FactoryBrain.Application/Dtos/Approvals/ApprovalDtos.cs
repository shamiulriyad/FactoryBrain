using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Application.Dtos.Approvals;

/// <summary>Body for POST /api/approvals/{id}/approve and /reject. A reason is required to reject.</summary>
public record ApprovalDecisionRequest(string? Reason);

/// <summary>The insight an approval authorises, in the shape the frontend's InsightPublic expects.</summary>
public record ApprovalInsightDto(
    string Id,
    string AgentId,
    string AgentLabel,
    string Title,
    string TitleBn,
    string Finding,
    string FindingBn,
    IReadOnlyList<AskFactor> Factors,
    ApprovalRecommendationDto Recommendation,
    IReadOnlyList<EvidenceRefDto> Evidence,
    string Stage,
    double Confidence,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ApprovalRecommendationDto(
    string Title, string TitleBn, string Action, string ActionBn, string RiskTier, string TargetStage);

public record ApprovalDto(
    string Id,
    string InsightId,
    string Title,
    string TitleBn,
    string? Reason,
    string RiskTier,
    /// <summary><c>pending_approval</c> | <c>approved</c> | <c>rejected</c>.</summary>
    string Status,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    string? DecidedByEmail,
    string? DecisionReason,
    ApprovalInsightDto? Insight);

public record ApprovalListResponse(int PendingCount, IReadOnlyList<ApprovalDto> Items);

public record AuditEntryDto(
    long Id,
    DateTime OccurredAt,
    Guid? ActorUserId,
    string ActorEmail,
    string ActorRole,
    string Action,
    string EntityType,
    string EntityId,
    string? FromState,
    string? ToState,
    string? Reason,
    string? TraceId);

/// <summary>Who is making a decision, taken from the validated JWT.</summary>
public record ApprovalActor(Guid? UserId, string Email, string Role);

public enum ApprovalDecisionOutcome { Ok, NotFound, AlreadyDecided, ReasonRequired, RequiresAdmin }

public record ApprovalDecisionResult(ApprovalDecisionOutcome Outcome, ApprovalDto? Approval);

/// <summary>Wire names for <see cref="Stage"/>, matching the frontend's snake_case Stage union.</summary>
public static class StageNames
{
    public static string ToWire(Stage stage) => stage switch
    {
        Stage.Suggested       => "suggested",
        Stage.PendingApproval => "pending_approval",
        Stage.Executing       => "executing",
        Stage.Done            => "done",
        Stage.Logged          => "logged",
        Stage.Rejected        => "rejected",
        Stage.Failed          => "failed",
        Stage.Approved        => "approved",
        _                     => stage.ToString().ToLowerInvariant(),
    };

    public static string ToWire(RiskTier tier) => tier.ToString().ToLowerInvariant();
}
