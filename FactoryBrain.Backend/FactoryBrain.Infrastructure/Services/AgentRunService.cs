using System.Globalization;
using System.Text;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Agents;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// POST /api/agents/{agentId}/run. An agent run asks the Python RAG service
/// (<see cref="IPyRagClient"/>) one question: the agent's configured purpose
/// plus the current line-board and machine readings its context slices name.
/// The stored insight is that answer and the chunks it cites, nothing else.
/// No source cited → <see cref="AgentNoSourceException"/>; service down →
/// <see cref="PyRagUnavailableException"/>. Either way nothing is stored.
/// </summary>
public sealed class AgentRunService : IAgentRunService
{
    private readonly FactoryBrainDbContext _db;
    private readonly IFloorAlertService _alerts;
    private readonly ISensorService _sensors;
    private readonly ILineBoardService _board;
    private readonly IPyRagClient _rag;
    private readonly ILogger<AgentRunService> _log;

    public const string RunSource = "rag";

    public AgentRunService(
        FactoryBrainDbContext db, IFloorAlertService alerts, ISensorService sensors,
        ILineBoardService board, IPyRagClient rag, ILogger<AgentRunService> log)
    { _db = db; _alerts = alerts; _sensors = sensors; _board = board; _rag = rag; _log = log; }

    public async Task<AgentRunResponse> RunAsync(string agentId, CancellationToken ct)
    {
        var agent = await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) throw new KeyNotFoundException($"agent_not_found:{agentId}");

        var context = await ReadContextAsync(agent, ct);
        var result = await _rag.QueryAsync(BuildQuestion(agent, context), ct);
        if (result.Sources.Count == 0 || string.IsNullOrWhiteSpace(result.Answer))
        {
            _log.LogInformation("Agent {AgentId}: RAG answer cited no source; nothing stored.", agent.Id);
            throw new AgentNoSourceException(result.Answer);
        }

        var persist = await PersistRunAsync(agent, result, context, ct);
        return new AgentRunResponse(
            Source: RunSource,
            AgentId: agentId,
            Insight: ToLite(persist.Insight),
            ApprovalPending: persist.ApprovalPending,
            ApprovalReason: persist.ApprovalReason,
            FloorAlertId: persist.FloorAlertId,
            ActivityEventId: persist.ActivityEventId,
            EnergyInsight: null,
            Warning: null
        );
    }

    /// <summary>
    /// The seeded roster, with the run counts and model label replaced by
    /// real values: insights each agent stored (today / in total) and the
    /// RAG service's configured model. Status follows the service's health.
    /// </summary>
    public async Task<IReadOnlyList<AgentDefinition>> RosterAsync(CancellationToken ct = default)
    {
        var agents = await _db.Agents.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var today = DateTime.UtcNow.Date;
        var counts = await _db.Insights.AsNoTracking()
            .GroupBy(i => i.AgentId)
            .Select(g => new { AgentId = g.Key, Total = g.Count(), Today = g.Count(i => i.CreatedAt >= today) })
            .ToDictionaryAsync(x => x.AgentId, ct);
        var health = await _rag.HealthAsync(ct);
        var o = _rag.Options;

        foreach (var a in agents)
        {
            counts.TryGetValue(a.Id, out var c);
            a.TasksToday = c?.Today ?? 0;
            a.RecentCount = c?.Total ?? 0;
            a.Model = $"RAG · {o.LlmModel} · {o.EmbeddingModel} ({o.Collection})";
            a.Status = health.Reachable && health.Ready && health.Qdrant == "healthy" ? "ready" : "unavailable";
        }
        return agents;
    }

    // ── Context: the live readings the question is about ────────────────────

    private sealed record ContextLine(string Label, string Value);

    private async Task<IReadOnlyList<ContextLine>> ReadContextAsync(AgentDefinition agent, CancellationToken ct)
    {
        var slices = agent.ContextSlices;
        bool all = slices.Contains("*");
        var lines = new List<ContextLine>();

        if (all || slices.Contains("lines") || slices.Contains("targets"))
        {
            var board = await _board.BuildAsync(ct);
            // Lines below target first; the question is about what needs attention.
            foreach (var r in board.Rows.Where(r => r.EfficiencyPct < r.SahTarget || r.Bottleneck != "green")
                                        .OrderBy(r => r.EfficiencyPct - r.SahTarget))
                lines.Add(new ContextLine(r.Name,
                    $"efficiency {r.EfficiencyPct}% vs target {r.SahTarget}%, WIP {r.WipBundles} bundles, non-productive time {r.NptMinutes} min, board flag {r.Bottleneck}"));
        }

        if (all || slices.Contains("machines"))
        {
            var snap = _sensors.Snapshot();
            foreach (var m in snap.Machines.Where(m => m.Status is "down" or "at_risk")
                                           .OrderBy(m => m.Status == "down" ? 0 : 1).ThenBy(m => m.Id))
                lines.Add(new ContextLine($"Machine {m.Id}",
                    string.Create(CultureInfo.InvariantCulture,
                        $"status {m.Status.Replace('_', ' ')}, vibration {m.Vibration:F2} mm/s, temperature {m.Temperature:F1} °C, duty cycle {m.DutyCycle * 100:F0}%")));
        }
        return lines;
    }

    private static string BuildQuestion(AgentDefinition agent, IReadOnlyList<ContextLine> context)
    {
        var q = new StringBuilder();
        q.AppendLine($"Role: {agent.Purpose}");
        q.AppendLine();
        if (context.Count == 0)
        {
            q.AppendLine("No line or machine is currently below target or flagged.");
            q.AppendLine("According to the documents, which routine checks should be done now?");
        }
        else
        {
            q.AppendLine("Current floor readings:");
            foreach (var c in context) q.AppendLine($"- {c.Label}: {c.Value}");
            q.AppendLine();
            q.AppendLine("According to the documents, what are the likely causes and the corrective actions for these items? Answer in English.");
        }
        return q.ToString();
    }

    // ── Persist: insight, approval, alert, activity ─────────────────────────

    private async Task<PersistResult> PersistRunAsync(AgentDefinition agent, PyRagAnswer result, IReadOnlyList<ContextLine> context, CancellationToken ct)
    {
        var title = Headline(result.Answer);
        var risk = agent.Risk;
        var insight = new Insight
        {
            Id = $"ins-{Guid.NewGuid():N}",
            AgentId = agent.Id,
            AgentLabel = agent.Name,
            Title = title,
            TitleBn = title,
            Finding = result.Answer,
            FindingBn = result.Answer,
            // What the agent looked at: the readings it put in the question.
            Factors = context.Select(c => new InsightFactor { Label = c.Label, LabelBn = c.Label, Magnitude = c.Value, MagnitudeBn = c.Value }).ToList(),
            Recommendation = new InsightRecommendation
            {
                Title = title,
                TitleBn = title,
                Action = string.Empty,
                ActionBn = string.Empty,
                RiskTier = risk,
                TargetStage = Stage.PendingApproval
            },
            Evidence = new List<EvidenceRow>
            {
                new()
                {
                    // The cited chunks (file + page). Filter stays null: the jsonb
                    // column can't take a Dictionary without dynamic JSON.
                    Domain = "manuals",
                    Count = result.Sources.Count,
                    PreviewIds = result.Sources.Select(s => s.Page is { } p ? $"{s.Source} p.{p}" : s.Source).Distinct().ToList()
                }
            },
            Stage = Stage.PendingApproval,
            // Similarity of the best cited chunk; a retrieval score, not a model-reported certainty.
            Confidence = Math.Clamp(result.Sources.Max(s => s.Score), 0.0, 1.0),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Insights.Add(insight);

        var approvalPending = risk != RiskTier.Low;
        string? approvalReason = approvalPending ? $"{agent.Name} is configured as {risk.ToString().ToLowerInvariant()} risk" : null;
        string? approvalId = null;
        if (approvalPending)
        {
            var app = new ApprovalRequest
            {
                Id = $"apr-{Guid.NewGuid():N}",
                InsightId = insight.Id,
                Title = insight.Title,
                TitleBn = insight.TitleBn,
                Reason = approvalReason,
                RiskTier = risk,
                Stage = Stage.PendingApproval,
                CreatedAt = DateTime.UtcNow
            };
            _db.Approvals.Add(app);
            approvalId = app.Id;
        }

        var alert = await _alerts.PushAsync(new FloorAlert
        {
            Id = $"alert-{Guid.NewGuid():N}",
            Channel = "whatsapp_sim",
            InsightId = insight.Id,
            ApprovalId = approvalId,
            BodyEn = $"{agent.Name}: {title}",
            BodyBn = $"{agent.NameBn}: {title}",
            Severity = risk == RiskTier.High ? FloorAlertSeverity.Critical : risk == RiskTier.Medium ? FloorAlertSeverity.Warn : FloorAlertSeverity.Info,
            CreatedAt = DateTime.UtcNow,
            Read = false
        }, ct);

        var act = new ActivityEvent
        {
            Id = $"act-{Guid.NewGuid():N}",
            Actor = agent.Id,
            ActorLabel = agent.Name,
            Verb = "ran",
            VerbBn = "চলেছে",
            Target = insight.Title,
            TargetBn = insight.TitleBn,
            Outcome = "completed",
            IsoDate = DateTime.UtcNow
        };
        _db.ActivityEvents.Add(act);

        await _db.SaveChangesAsync(ct);
        return new PersistResult(insight, approvalPending, approvalReason, alert.Id, act.Id, approvalId);
    }

    /// <summary>First sentence (or line) of the answer, at most 140 characters.</summary>
    internal static string Headline(string answer)
    {
        var text = answer.Trim().TrimStart('*', '#', '-', ' ');
        var end = text.IndexOfAny(new[] { '\n', '.', '।', '?', '!' });
        var first = (end > 0 ? text[..(end + (text[end] == '\n' ? 0 : 1))] : text).Replace("**", "").Trim();
        return first.Length <= 140 ? first : first[..139].TrimEnd() + "…";
    }

    private static InsightLiteDto ToLite(Insight i) => new(
        i.Id, i.Title, i.TitleBn, i.Finding, i.FindingBn,
        i.Recommendation.Title, i.Recommendation.Action,
        i.Recommendation.RiskTier.ToString().ToLowerInvariant(),
        i.Stage.ToString().ToLowerInvariant(), i.Confidence);

    private sealed record PersistResult(
        Insight Insight, bool ApprovalPending, string? ApprovalReason,
        string FloorAlertId, string ActivityEventId, string? ApprovalId);
}
