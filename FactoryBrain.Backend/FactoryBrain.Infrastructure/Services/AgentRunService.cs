using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Agents;
using FactoryBrain.Application.Dtos.Vision;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// Mirrors <c>src/app/api/agents/[agentId]/run/route.ts</c>. Demo path
/// produces a deterministic Insight + a secondary energy Insight when the
/// maintenance or manager agent scores above 0.3 on the duty tool.
/// </summary>
public sealed class AgentRunService : IAgentRunService
{
    private readonly FactoryBrainDbContext _db;
    private readonly IFloorAlertService _alerts;
    private readonly ISensorService _sensors;
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AgentRunService> _log;

    public AgentRunService(
        FactoryBrainDbContext db, IFloorAlertService alerts, ISensorService sensors,
        IConfiguration cfg, IHttpClientFactory http, ILogger<AgentRunService> log)
    { _db = db; _alerts = alerts; _sensors = sensors; _cfg = cfg; _http = http; _log = log; }

    public async Task<AgentRunResponse> RunAsync(string agentId, CancellationToken ct)
    {
        var agent = await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) throw new KeyNotFoundException($"agent_not_found:{agentId}");

        var persist = await PersistRunAsync(agent, warning: null, source: "demo", ct);
        var energy  = await MaybeRaiseEnergyInsightAsync(agent, ct);

        return new AgentRunResponse(
            Source: "demo",
            AgentId: agentId,
            Insight: ToLite(persist.Insight),
            ApprovalPending: persist.ApprovalPending,
            ApprovalReason: persist.ApprovalReason,
            FloorAlertId: persist.FloorAlertId,
            ActivityEventId: persist.ActivityEventId,
            EnergyInsight: energy,
            Warning: null
        );
    }

    public async Task<IReadOnlyList<AgentDefinition>> RosterAsync(CancellationToken ct = default) =>
        await _db.Agents.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);

    private async Task<PersistResult> PersistRunAsync(AgentDefinition agent, string? warning, string source, CancellationToken ct)
    {
        var seed = await _db.Insights.AsNoTracking()
            .Where(i => i.AgentId == agent.Id)
            .OrderByDescending(i => i.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        string title   = seed?.Title   ?? $"{agent.Name} produced a draft insight";
        string titleBn = seed?.TitleBn ?? $"{agent.NameBn} একটি খসড়া অন্তর্দৃষ্টি তৈরি করেছে";
        string finding = (warning ?? seed?.Finding) ?? "Agent produced no observations in this window.";
        string findingBn = seed?.FindingBn ?? "এই উইন্ডোতে এজেন্ট কোন পর্যবেক্ষণ দেয়নি।";
        string action = seed?.Recommendation.Action ?? "Open the brief and approve the suggested next step.";
        string actionBn = seed?.Recommendation.ActionBn ?? "ব্রিফ খুলুন এবং প্রস্তাবিত পরবর্তী পদক্ষেপ অনুমোদন দিন।";
        var risk = seed?.Recommendation.RiskTier ?? RiskTier.Medium;
        double conf = seed?.Confidence ?? 0.7;

        var insight = new Insight
        {
            Id = $"ins-{Guid.NewGuid():N}",
            AgentId = agent.Id,
            AgentLabel = agent.Name,
            Title = title,
            TitleBn = titleBn,
            Finding = finding,
            FindingBn = findingBn,
            Recommendation = new InsightRecommendation
            {
                Title = $"Apply {agent.Name}",
                TitleBn = $"{agent.NameBn} প্রয়োগ করুন",
                Action = action,
                ActionBn = actionBn,
                RiskTier = risk,
                TargetStage = Stage.PendingApproval
            },
            Stage = Stage.PendingApproval,
            Confidence = conf,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Insights.Add(insight);

        var approvalPending = risk != RiskTier.Low;
        string? approvalReason = approvalPending ? "Risk tier != low" : null;
        var approvalId = (string?)null;
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
            BodyEn = $"{agent.Name}: {finding}",
            BodyBn = $"{agent.NameBn}: {findingBn}",
            Severity = risk == RiskTier.High ? FloorAlertSeverity.Critical : FloorAlertSeverity.Warn,
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

    private async Task<EnergyInsightDto?> MaybeRaiseEnergyInsightAsync(AgentDefinition agent, CancellationToken ct)
    {
        if (agent.Id is not ("maintenance-agent" or "manager-agent")) return null;
        var rec = RecommendEnergyDuty();
        if (rec.Score < 0.3) return null;
        var riskTier = rec.Score > 0.6 ? RiskTier.High : RiskTier.Medium;

        var insight = new Insight
        {
            Id = $"ins-energy-{Guid.NewGuid():N}",
            AgentId = agent.Id,
            AgentLabel = agent.Name,
            Title = "Trim compressor duty cycle — energy spike",
            TitleBn = "কম্প্রেসর ডিউটি সাইকেল কমান — শক্তি স্পাইক",
            Finding = $"Latest energy readings and per-line totals imply a compressor duty score of {rec.Score:F2}. " +
                      $"Recommendation: cap duty at {rec.RecommendedDutyPct}% (current {rec.CurrentDutyPct}%); " +
                      $"expected saving ~{rec.ExpectedKwhSaved:F1} kWh over the {rec.Window} window.",
            FindingBn = $"সর্বশেষ শক্তি রিডিং এবং প্রতি-লাইন মোট অনুযায়ী কম্প্রেসর ডিউটি স্কোর {rec.Score:F2}। " +
                        $"পরামর্শ: ডিউটি {rec.RecommendedDutyPct}% এ ক্যাপ করুন।",
            Recommendation = new InsightRecommendation
            {
                Title = "Trim compressor duty",
                TitleBn = "কম্প্রেসর ডিউটি কমান",
                Action = $"Trim duty {rec.CurrentDutyPct}% → {rec.RecommendedDutyPct}%",
                ActionBn = $"{rec.CurrentDutyPct}% → {rec.RecommendedDutyPct}%",
                RiskTier = riskTier,
                TargetStage = Stage.PendingApproval
            },
            Stage = Stage.PendingApproval,
            Confidence = Math.Min(0.95, 0.55 + rec.Score * 0.4),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Insights.Add(insight);

        var alert = await _alerts.PushAsync(new FloorAlert
        {
            Id = $"alert-energy-{Guid.NewGuid():N}",
            Channel = "whatsapp_sim",
            InsightId = insight.Id,
            BodyEn = $"{agent.Name}: trim compressor duty {rec.CurrentDutyPct}% → {rec.RecommendedDutyPct}% (score {rec.Score:F2}).",
            BodyBn = $"{agent.NameBn}: কম্প্রেসর ডিউটি {rec.CurrentDutyPct}% → {rec.RecommendedDutyPct}% এ কমান।",
            Severity = riskTier == RiskTier.High ? FloorAlertSeverity.Critical : FloorAlertSeverity.Warn,
            CreatedAt = DateTime.UtcNow,
            Read = false
        }, ct);

        await _db.SaveChangesAsync(ct);
        return new EnergyInsightDto(insight.Id, alert.Id, rec.Score);
    }

    /// <summary>
    /// Deterministic compressor duty recommendation — mirrors
    /// <c>factory.tools.recommend_energy_duty</c>.
    /// </summary>
    private (double Score, int CurrentDutyPct, int RecommendedDutyPct, double ExpectedKwhSaved, int LineCount, string Window)
    RecommendEnergyDuty()
    {
        var s = _sensors.CurrentState;
        double totalKwh = s.Lines.Sum(l => l.EnergyKwh);
        double peak = s.Machines.Count == 0 ? 0 : s.Machines.Average(m => m.Vibration);
        double score = Math.Clamp(totalKwh / 1000 + peak / 10, 0, 1);
        int current = 85, recommended = (int)Math.Round(current - score * 15);
        return (score, current, recommended, score * 4.7, s.Lines.Count, "1h");
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
