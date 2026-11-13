using System.Text.Json;
using System.Text.RegularExpressions;
using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Api.Controllers;

/// <summary>Homepage hero photos: which ones, in what order, and whether they rotate.</summary>
public sealed record HeroSettingsDto(string[] ImageIds, bool Rotate, int IntervalSeconds);

/// <summary>
/// Public-website settings. GET is public (the website reads it to render the
/// homepage); PUT needs <c>platform.settings.manage</c> and is audited. Image
/// ids refer to photos the frontend ships (frontend/src/content/site/heroMedia.ts);
/// the frontend ignores ids it doesn't know and falls back to its default.
/// </summary>
[ApiController]
[Route("api/site-settings")]
public class SiteSettingsController : ControllerBase
{
    private const string HeroKey = "hero";
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BunonBrainDbContext _db;

    public SiteSettingsController(BunonBrainDbContext db) { _db = db; }

    /// <summary>Stored hero settings, or <c>{ configured: false }</c> when none were saved.</summary>
    [HttpGet("hero")]
    [AllowAnonymous]
    public async Task<IActionResult> GetHero(CancellationToken ct)
    {
        var row = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == HeroKey, ct);
        Response.Headers.CacheControl = "public, max-age=30";
        if (row is null) return Ok(new { configured = false });
        var value = JsonSerializer.Deserialize<HeroSettingsDto>(row.ValueJson, Json);
        return Ok(new { configured = true, value, row.UpdatedAt });
    }

    [HttpPut("hero")]
    [RequirePermission(Permissions.PlatformSettingsManage)]
    public async Task<IActionResult> PutHero([FromBody] HeroSettingsDto body, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var ids = (body.ImageIds ?? Array.Empty<string>()).Select(i => i?.Trim() ?? "").Distinct().ToArray();
        if (ids.Length is < 1 or > 10) errors["imageIds"] = new[] { "Choose between 1 and 10 photos." };
        else if (ids.Any(i => !IdPattern.IsMatch(i))) errors["imageIds"] = new[] { "Unknown photo id." };
        if (body.IntervalSeconds is < 2 or > 30) errors["intervalSeconds"] = new[] { "Use an interval between 2 and 30 seconds." };
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors));

        var clean = new HeroSettingsDto(ids, body.Rotate && ids.Length > 1, body.IntervalSeconds);
        var json = JsonSerializer.Serialize(clean, Json);
        var actor = SettingsResults.Actor(User);

        var row = await _db.SiteSettings.FirstOrDefaultAsync(x => x.Key == HeroKey, ct);
        var before = row?.ValueJson;
        if (row is null)
        {
            row = new SiteSetting { Key = HeroKey };
            _db.SiteSettings.Add(row);
        }
        row.ValueJson = json;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByEmail = actor.Email;

        _db.AuditLog.Add(new AuditLogEntry
        {
            OccurredAt = DateTime.UtcNow,
            ActorUserId = actor.UserId,
            ActorEmail = actor.Email,
            ActorRole = actor.Role,
            Action = "site_settings.updated",
            EntityType = "site_settings",
            EntityId = HeroKey,
            FromState = before is { Length: <= 500 } ? before : null,
            ToState = json.Length <= 500 ? json : null,
            TraceId = SettingsResults.TraceId(HttpContext),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { configured = true, value = clean, row.UpdatedAt });
    }
}
