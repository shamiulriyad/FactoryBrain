using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Domain.Entities;

public class Insight
{
    public string Id { get; set; } = default!;
    public string AgentId { get; set; } = default!;
    public string AgentLabel { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string TitleBn { get; set; } = default!;
    public string Finding { get; set; } = default!;
    public string FindingBn { get; set; } = default!;
    public List<InsightFactor> Factors { get; set; } = new();
    public InsightRecommendation Recommendation { get; set; } = new();
    public List<EvidenceRow> Evidence { get; set; } = new();
    public Stage Stage { get; set; } = Stage.Suggested;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public double Confidence { get; set; }
    public bool Pinned { get; set; }
}

public class InsightFactor
{
    public string Label { get; set; } = default!;
    public string LabelBn { get; set; } = default!;
    public string Magnitude { get; set; } = default!;
    public string MagnitudeBn { get; set; } = default!;
}

public class InsightRecommendation
{
    public string Title { get; set; } = default!;
    public string TitleBn { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string ActionBn { get; set; } = default!;
    public RiskTier RiskTier { get; set; } = RiskTier.Low;
    public Stage TargetStage { get; set; } = Stage.Suggested;
}

public class EvidenceRow
{
    public string Domain { get; set; } = default!;       // orders | manuals | ...
    public int Count { get; set; }
    public Dictionary<string, string>? Filter { get; set; }
    public List<string>? PreviewIds { get; set; }
}
