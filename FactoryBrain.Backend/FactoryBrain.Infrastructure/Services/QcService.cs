using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

public sealed class QcService : IQcService
{
    private static readonly string[] Operations = { "cutting", "sewing", "buttonhole", "top_stitch", "qc_inspection", "finishing" };
    private static readonly string[] Lines     = { "line-1", "line-2", "line-3", "line-4", "line-5", "line-6" };

    private readonly FactoryBrainDbContext _db;
    private readonly IFloorAlertService _alerts;
    public QcService(FactoryBrainDbContext db, IFloorAlertService alerts)
    { _db = db; _alerts = alerts; }

    public async Task<QcDefectsResponse> BuildAsync(CancellationToken ct)
    {
        var cells = await _db.QcDefects.AsNoTracking().ToListAsync(ct);
        var operations = cells.Select(c => new QcOperationStats(
            c.Operation, c.LineId,
            c.Weeks.Select(w => new QcWeekBucket(w.WeekStart, w.Inspected, w.Defects, w.Major, w.Minor, w.Rework)).ToList(),
            new QcTotals(c.InspectedTotal, c.DefectsTotal, c.MajorTotal, c.MinorTotal, c.ReworkTotal)
        )).ToList();

        var candidates = operations.Select(o => new QcTopOperation(
            o.Operation, o.LineId,
            Rate(o.Totals.Defects, o.Totals.Inspected),
            Rate(o.Totals.Rework, o.Totals.Inspected)
        )).ToList();

        var topByDefect = candidates.OrderByDescending(x => x.DefectRatePct).Take(5).ToList();
        var topByRework = candidates.OrderByDescending(x => x.ReworkRatePct).Take(5).ToList();

        return new QcDefectsResponse(operations, topByDefect, topByRework,
            new QcMeta(
                Simulated: true,
                Source: "Simulated — Bosch-shaped defect/rework seed (no live QA capture, no external data file)",
                DefectSource: "Demo seed · Bosch-shaped taxonomy (cutting / sewing / buttonhole / top_stitch / qc_inspection / finishing)",
                GeneratedAt: DateTime.UtcNow,
                Notes: "Deterministic per-cell PRNG seed; same calendar day → identical payload (except generatedAt)."
            ));
    }

    public async Task<QcFlagResponse> FlagAsync(QcFlagRequest req, CancellationToken ct)
    {
        double worst = Math.Max(req.DefectRatePct ?? 0, req.ReworkRatePct ?? 0);
        var risk = worst >= 6 ? RiskTier.High : worst >= 3 ? RiskTier.Medium : RiskTier.Low;

        var stamp = ($"{req.Operation}-{req.LineId}").Replace('/', '-').Replace(' ', '-').ToLowerInvariant();
        var id = $"qc-flag-{stamp}";

        var opTitle = req.Operation.Replace('_', ' ');
        var lineTitle = string.Join(' ', req.LineId.Replace('-', ' ').Split(' ').Select(w => char.ToUpper(w[0]) + w[1..]));

        var insight = new Insight
        {
            Id = id,
            AgentId = "qc-flag",
            AgentLabel = "QC Flag",
            Title  = $"Flagged {opTitle} on {lineTitle}",
            TitleBn = $"{lineTitle}-এ {opTitle} ফ্ল্যাগ করা হয়েছে",
            Finding  = $"User flagged {opTitle} on {lineTitle}. Defect rate {(req.DefectRatePct?.ToString("F2") ?? "—")}%, rework rate {(req.ReworkRatePct?.ToString("F2") ?? "—")}%. {(string.IsNullOrEmpty(req.Note) ? "No additional note." : "Note: " + req.Note)}",
            FindingBn = $"ইউজার {lineTitle}-এ {opTitle} ফ্ল্যাগ করেছেন। ত্রুটির হার {(req.DefectRatePct?.ToString("F2") ?? "—")}%, রি-ওয়ার্ক হার {(req.ReworkRatePct?.ToString("F2") ?? "—")}%।",
            Recommendation = new InsightRecommendation
            {
                Title = $"Review {opTitle} on {lineTitle}",
                TitleBn = $"{lineTitle}-এ {opTitle} পর্যালোচনা করুন",
                Action = $"Open the {opTitle} trend for {lineTitle}, validate the rate against the line-board SAH, and create an approval if the trend needs intervention.",
                ActionBn = $"{lineTitle}-এর {opTitle} প্রবণতা দেখুন, লাইন-বোর্ডের লক্ষ্যমাত্রার সাথে যাচাই করুন এবং প্রবণতা হস্তক্ষেপের প্রয়োজন হলে একটি অনুমোদন তৈরি করুন।",
                RiskTier = risk,
                TargetStage = Stage.Suggested
            },
            Confidence = 0.55,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var existing = await _db.Insights.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (existing is null) await _db.Insights.AddAsync(insight, ct);
        else _db.Entry(existing).CurrentValues.SetValues(insight);

        await _db.ActivityEvents.AddAsync(new ActivityEvent
        {
            Id = $"act-{Guid.NewGuid():N}",
            Actor = "user",
            ActorLabel = "You",
            Verb = "flagged",
            VerbBn = "ফ্ল্যাগ করেছেন",
            Target = $"{opTitle} ({lineTitle})",
            TargetBn = $"{opTitle} ({lineTitle})",
            Outcome = "flagged",
            IsoDate = DateTime.UtcNow
        }, ct);

        await _db.SaveChangesAsync(ct);

        return new QcFlagResponse(id, DateTime.UtcNow);
    }

    private static double Rate(int part, int whole)
        => whole <= 0 ? 0 : Math.Round((double)part / whole * 100, 2);
}
