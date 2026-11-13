namespace BunonBrain.Domain.Entities;

/// <summary>
/// One public-website setting, stored as JSON under a key (for example
/// <c>hero</c>: which homepage photos are shown and whether they rotate).
/// Read by anyone; changed only with <c>platform.settings.manage</c>, and every
/// change is written to the audit log.
/// </summary>
public class SiteSetting
{
    public string Key { get; set; } = default!;
    public string ValueJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
