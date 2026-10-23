namespace FactoryBrain.Domain.Entities;

public class VisionResult
{
    public string Id { get; set; } = default!;
    public string SampleFile { get; set; } = default!;
    public string AnomalyLabelEn { get; set; } = default!;
    public string AnomalyLabelBn { get; set; } = default!;
    public List<string> RepairStepsEn { get; set; } = new();
    public List<string> RepairStepsBn { get; set; } = new();
    public double Confidence { get; set; }
    public string? MachineId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
