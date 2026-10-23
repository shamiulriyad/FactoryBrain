using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Domain.Entities;

/// <summary>One row on the live Line-Board panel.</summary>
public class LineBoardMetric
{
    public string Id { get; set; } = default!;          // "line-1"
    public string Name { get; set; } = default!;        // "Line 1 — Polo Tee"
    public int EfficiencyPct { get; set; }              // 0..100
    public int SahTarget { get; set; }                  // 0..100
    public int SahActual { get; set; }                  // 0..100
    public int WipBundles { get; set; }
    public Bottleneck Bottleneck { get; set; }
    public int NptMinutes { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
