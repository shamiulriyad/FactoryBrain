using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Vision;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

public sealed class VisionService : IVisionService
{
    private static readonly string[] SampleFiles =
    {
        "defect-1-stitch-skip.jpg",
        "defect-2-buttonhole.jpg",
        "defect-3-seam-pucker.jpg",
        "defect-4-fabric-stain.jpg"
    };

    private readonly FactoryBrainDbContext _db;
    private readonly IFloorAlertService _alerts;
    private static readonly Random _rng = new(unchecked((int)0xC0FFEE71));

    public VisionService(FactoryBrainDbContext db, IFloorAlertService alerts)
    { _db = db; _alerts = alerts; }

    public VisionAnalyzeResponse Analyze(VisionAnalyzeRequest req, CancellationToken ct)
    {
        var anomaly = MapStagedSample(req.SampleFile);

        var result = new VisionResult
        {
            Id = $"vis-{DateTime.UtcNow.ToString("yyMMddHHmmssfff")}",
            SampleFile = req.SampleFile,
            AnomalyLabelEn = anomaly.AnomalyLabelEn,
            AnomalyLabelBn = anomaly.AnomalyLabelBn,
            RepairStepsEn = anomaly.RepairStepsEn.ToList(),
            RepairStepsBn = anomaly.RepairStepsBn.ToList(),
            Confidence = anomaly.Confidence,
            MachineId = anomaly.MachineId,
            CreatedAt = DateTime.UtcNow
        };
        _db.VisionResults.Add(result);

        var insight = new Insight
        {
            Id = $"ins-vision-{Guid.NewGuid():N}",
            AgentId = "vision-repair-agent",
            AgentLabel = "Vision Repair",
            Title = $"{result.AnomalyLabelEn} ({result.SampleFile})",
            TitleBn = $"{result.AnomalyLabelBn} ({result.SampleFile})",
            Finding = $"{result.AnomalyLabelEn}. Model confidence {(result.Confidence * 100):F0}%. Sample: {result.SampleFile}{(string.IsNullOrEmpty(result.MachineId) ? "" : $", attributed to {result.MachineId}")}.",
            FindingBn = $"{result.AnomalyLabelBn}। মডেল আত্মবিশ্বাস {(result.Confidence * 100):F0}%। নমুনা: {result.SampleFile}।",
            Stage = Stage.PendingApproval,
            Recommendation = new InsightRecommendation
            {
                Title = "Apply repair plan",
                TitleBn = "মেরামতি পরিকল্পনা প্রয়োগ করুন",
                Action = string.Join(" → ", result.RepairStepsEn),
                ActionBn = string.Join(" → ", result.RepairStepsBn),
                RiskTier = RiskTier.Medium,
                TargetStage = Stage.PendingApproval
            },
            Confidence = result.Confidence,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Insights.Add(insight);

        var approval = new ApprovalRequest
        {
            Id = $"apr-vision-{Guid.NewGuid():N}",
            InsightId = insight.Id,
            Title = insight.Title,
            TitleBn = insight.TitleBn,
            Reason = "Risk tier != low",
            RiskTier = RiskTier.Medium,
            Stage = Stage.PendingApproval,
            CreatedAt = DateTime.UtcNow
        };
        _db.Approvals.Add(approval);

        var act = new ActivityEvent
        {
            Id = $"act-vision-{Guid.NewGuid():N}",
            Actor = "vision-repair-agent",
            ActorLabel = "Vision Repair",
            Verb = "ran",
            VerbBn = "চলেছে",
            Target = req.SampleFile,
            TargetBn = req.SampleFile,
            Outcome = "completed",
            IsoDate = DateTime.UtcNow
        };
        _db.ActivityEvents.Add(act);

        var alert = new FloorAlert
        {
            Id = $"alert-vision-{Guid.NewGuid():N}",
            Channel = "whatsapp_sim",
            InsightId = insight.Id,
            BodyEn = $"Vision flagged {result.AnomalyLabelEn} on {result.MachineId}.",
            BodyBn = $"Vision {result.MachineId}-এ {result.AnomalyLabelEn} চিহ্নিত করেছে।",
            Severity = FloorAlertSeverity.Warn,
            CreatedAt = DateTime.UtcNow,
            Read = false
        };

        _db.SaveChangesAsync(ct).GetAwaiter().GetResult();

        return new VisionAnalyzeResponse(
            Simulated: true,
            Source: "Simulated — demo VLM mapping (no real model call)",
            Result: new VisionResultDto(result.Id, result.SampleFile, result.AnomalyLabelEn, result.AnomalyLabelBn,
                result.RepairStepsEn, result.RepairStepsBn, result.Confidence, result.MachineId, result.CreatedAt),
            Insight: new InsightLiteDto(insight.Id, insight.Title, insight.TitleBn, insight.Finding, insight.FindingBn,
                insight.Recommendation.Title, insight.Recommendation.Action, insight.Recommendation.RiskTier.ToString().ToLowerInvariant(),
                insight.Stage.ToString().ToLowerInvariant(), insight.Confidence),
            ApprovalPending: true,
            ApprovalReason: "Risk tier != low",
            FloorAlertId: alert.Id,
            ActivityEventId: act.Id
        );
    }

    public VisionAllowedResponse Allowed() =>
        new(true, "Simulated — no VLM backend wired", SampleFiles, "POST");

    private static Anomaly MapStagedSample(string sampleFile)
    {
        // Deterministic mapping. Each sampleFile → fixed En/Bn labels so
        // the Vision page preview tiles line up regardless of run order.
        return sampleFile switch
        {
            "defect-1-stitch-skip.jpg" => new Anomaly(
                "Stitch skip (>3 mm gap)", "সেলাই বাদ (>৩ মিমি ফাঁক)",
                new[] { "Re-thread the needle", "Replace the bobbin", "Tighten tension to 2.4 N·m", "QC spot-check next 50 pcs" },
                new[] { "সুই পুনর্গমন করুন", "ববিন প্রতিস্থাপন", "টেনশন ২.৪ N·m এ আঁটসাঁট করুন", "পরবর্তী ৫০ পিস QC" },
                0.92, "M-204"),
            "defect-2-buttonhole.jpg" => new Anomaly(
                "Buttonhole misalignment", "বোতামের ছিদ্র সারিবদ্ধ নয়",
                new[] { "Re-calibrate cutter blade", "Inspect 2 random samples under loupe", "Re-bind the sewing cycle" },
                new[] { "কাটার ব্লেড পুনঃক্যালিব্রেট", "লুপের নিচে দুটি নমুনা পরীক্ষা", "সেলাই চক্র পুনর্বন্ধন" },
                0.86, "M-117"),
            "defect-3-seam-pucker.jpg" => new Anomaly(
                "Seam pucker (>5 mm)", "সিম ভাঁজ (>৫ মিমি)",
                new[] { "Loosen top thread tension", "Reduce feed-dog pressure", "Switch to ballpoint needle" },
                new[] { "উপরের সুতার টেনশন শিথিল করুন", "ফিড-ডগ চাপ কমান", "বলপয়েন্ট সুই ব্যবহার করুন" },
                0.81, "M-309"),
            _ => new Anomaly(
                "Fabric stain (oil)", "কাপড়ে তেলের দাগ",
                new[] { "Re-wash affected bundles", "Switch cutting-table cover", "Notify QC spot-check" },
                new[] { "প্রভাবিত বান্ডল পুনঃধোয়া", "কাটিং টেবিলের কভার পরিবর্তন", "QC পরীক্ষা করুন" },
                0.74, "M-404")
        };
    }

    private sealed record Anomaly(
        string AnomalyLabelEn, string AnomalyLabelBn,
        string[] RepairStepsEn, string[] RepairStepsBn,
        double Confidence, string? MachineId);
}
