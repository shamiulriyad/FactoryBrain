using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Application.Dtos.Brief;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

public sealed class BriefService : IBriefService
{
    private readonly FactoryBrainDbContext _db;
    private readonly ILineBoardService _lineBoard;

    public BriefService(FactoryBrainDbContext db, ILineBoardService lineBoard)
    { _db = db; _lineBoard = lineBoard; }

    public async Task<MorningBriefResponse> BuildMorningBriefAsync(CancellationToken ct)
    {
        var board = await _lineBoard.BuildAsync(ct);

        // suggested insights mirrored via InsightsRepository (no Zustand)
        var suggested = await _db.Insights.AsNoTracking()
            .Where(i => i.Stage == Domain.Enums.Stage.Suggested)
            .OrderByDescending(i => i.Pinned).ThenByDescending(i => i.UpdatedAt)
            .Take(3)
            .Select(i => new { i.Title, i.TitleBn })
            .ToListAsync(ct);

        var top = board.Rows.FirstOrDefault(r => r.Bottleneck == "red")
                ?? board.Rows.FirstOrDefault(r => r.Bottleneck == "amber");

        var bulletsEn = new List<string>();
        var bulletsBn = new List<string>();

        if (top is not null)
        {
            var first = board.Rows.First(r => r.LineId == top.LineId);
            bulletsEn.Add($"Line {first.LineId} efficiency {first.EfficiencyPct}% vs target {first.SahTarget}% ({first.Bottleneck}).");
            bulletsBn.Add($"লাইন {first.LineId} দক্ষতা {first.EfficiencyPct}% বনাম লক্ষ্য {first.SahTarget}% ({first.Bottleneck}).");
        }
        else
        {
            bulletsEn.Add("All lines are running on target.");
            bulletsBn.Add("সব লাইন লক্ষ্যমাত্রায় চলছে।");
        }

        if (suggested.Count == 0)
        {
            bulletsEn.Add("No new recommendations.");
            bulletsBn.Add("নতুন কোনো সুপারিশ নেই।");
        }
        else
        {
            foreach (var s in suggested)
            {
                bulletsEn.Add(s.Title);
                bulletsBn.Add(s.TitleBn);
            }
        }

        var riskCount = await _db.Insights.CountAsync(i => i.Recommendation.RiskTier == Domain.Enums.RiskTier.Medium || i.Recommendation.RiskTier == Domain.Enums.RiskTier.High, ct);
        var pendingApprovals = await _db.Approvals.CountAsync(a => a.Stage == Domain.Enums.Stage.PendingApproval, ct);

        return new MorningBriefResponse(
            Date: DateTime.UtcNow.ToString("yyyy-MM-dd"),
            BulletsEn: bulletsEn,
            BulletsBn: bulletsBn,
            TopBottleneckLineId: top?.LineId,
            RiskCount: riskCount,
            PendingApprovals: pendingApprovals,
            Meta: new MorningBriefMeta(
                Simulated: true,
                Source: "Simulated — deterministic assembly from line-board + suggested insights + pending approvals (no live traffic, no LLM)",
                Tick: board.Meta.Tick,
                GeneratedAt: DateTime.UtcNow,
                Notes: "Same sim tick + insights snapshot → identical response."
            )
        );
    }
}
