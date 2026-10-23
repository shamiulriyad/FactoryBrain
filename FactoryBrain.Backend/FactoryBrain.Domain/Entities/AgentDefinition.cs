using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Domain.Entities;

public class AgentDefinition
{
    public string Id { get; set; } = default!;          // "line-throughput-agent"
    public string Name { get; set; } = default!;
    public string NameBn { get; set; } = default!;
    public string Purpose { get; set; } = default!;
    public string PurposeBn { get; set; } = default!;
    public RiskTier Risk { get; set; }
    public string Execution { get; set; } = "auto";     // auto | approval_required | ...
    public string Model { get; set; } = default!;
    public List<string> ContextSlices { get; set; } = new();
    public string Status { get; set; } = "ready";       // ready | draft
    public int TasksToday { get; set; }
    public int RecentCount { get; set; }
    public string? Glyph { get; set; }
}
