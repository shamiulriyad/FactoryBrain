namespace FactoryBrain.Domain.Entities;

/// <summary>
/// Per-(operation × line) QC defect/rework cell for the QC Defects panel.
/// Stores weekly buckets (8 weeks) and running totals. Mirrors
/// <c>src/data/qc.defects.ts::generateOperationLineWeeks()</c>.
/// </summary>
public class QcDefect
{
    public string Id { get; set; } = default!;        // {operation}-{lineId}
    public string Operation { get; set; } = default!; // cutting|sewing|...
    public string LineId { get; set; } = default!;
    public List<QcDefectWeek> Weeks { get; set; } = new();
    public int InspectedTotal { get; set; }
    public int DefectsTotal { get; set; }
    public int MajorTotal { get; set; }
    public int MinorTotal { get; set; }
    public int ReworkTotal { get; set; }
}

public class QcDefectWeek
{
    public string WeekStart { get; set; } = default!;   // YYYY-MM-DD
    public int Inspected { get; set; }
    public int Defects { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Rework { get; set; }
}
