namespace FactoryBrain.Domain.Entities;

public class PolicyDocument
{
    public string Id { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string TitleBn { get; set; } = default!;
    public string Type { get; set; } = default!;        // return | supplier-agreement | ...
    public string Body { get; set; } = default!;
    public string BodyBn { get; set; } = default!;
    public DateTime EffectiveDate { get; set; }
}
