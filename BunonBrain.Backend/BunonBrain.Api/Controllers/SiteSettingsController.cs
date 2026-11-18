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

/// <summary>Body of PUT /api/site-settings/demo-video: any YouTube link, and an optional title.</summary>
public sealed record DemoVideoRequest(string? Url, string? Title);

/// <summary>The homepage demo video: the YouTube video id (11 characters) and its title.</summary>
public sealed record DemoVideoDto(string VideoId, string? Title);

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
    private const string VideoKey = "demo-video";
    // watch?v=ID, youtu.be/ID, /embed/ID, /shorts/ID, /live/ID, or a bare 11-character id.
    private static readonly Regex YouTubeUrl = new(
        @"^(?:https?://)?(?:(?:www\.|m\.|music\.)?youtube(?:-nocookie)?\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|live/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})(?:[?&#/].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
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

    /// <summary>The homepage demo video, or <c>{ configured: false }</c> when none is set.</summary>
    [HttpGet("demo-video")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDemoVideo(CancellationToken ct)
    {
        var row = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == VideoKey, ct);
        Response.Headers.CacheControl = "public, max-age=30";
        if (row is null) return Ok(new { configured = false });
        var value = JsonSerializer.Deserialize<DemoVideoDto>(row.ValueJson, Json);
        return Ok(new { configured = true, value, row.UpdatedAt });
    }

    /// <summary>Set the demo video from a YouTube link (watch, youtu.be, embed, shorts or live) or a bare video id.</summary>
    [HttpPut("demo-video")]
    [RequirePermission(Permissions.PlatformSettingsManage)]
    public async Task<IActionResult> PutDemoVideo([FromBody] DemoVideoRequest body, CancellationToken ct)
    {
        var url = (body.Url ?? "").Trim();
        var title = string.IsNullOrWhiteSpace(body.Title) ? null : body.Title.Trim();
        var id = ParseYouTubeId(url);
        var errors = new Dictionary<string, string[]>();
        if (id is null) errors["url"] = new[] { "Paste a YouTube video link, e.g. https://www.youtube.com/watch?v=… or https://youtu.be/…" };
        if (title is { Length: > 120 }) errors["title"] = new[] { "Keep the title to 120 characters." };
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors));

        var clean = new DemoVideoDto(id!, title);
        var row = await SaveAsync(VideoKey, JsonSerializer.Serialize(clean, Json), ct);
        return Ok(new { configured = true, value = clean, row.UpdatedAt });
    }

    /// <summary>The 11-character video id of a YouTube link or bare id, or null when it isn't one.</summary>
    public static string? ParseYouTubeId(string? input)
    {
        var url = (input ?? "").Trim();
        var match = YouTubeUrl.Match(url);
        return match.Success ? match.Groups[1].Value : VideoId.IsMatch(url) ? url : null;
    }

    /// <summary>Remove the demo video; the homepage shows its placeholder again.</summary>
    [HttpDelete("demo-video")]
    [RequirePermission(Permissions.PlatformSettingsManage)]
    public async Task<IActionResult> DeleteDemoVideo(CancellationToken ct)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(x => x.Key == VideoKey, ct);
        if (row is null) return Ok(new { configured = false });
        var actor = SettingsResults.Actor(User);
        _db.SiteSettings.Remove(row);
        _db.AuditLog.Add(new AuditLogEntry
        {
            OccurredAt = DateTime.UtcNow,
            ActorUserId = actor.UserId,
            ActorEmail = actor.Email,
            ActorRole = actor.Role,
            Action = "site_settings.updated",
            EntityType = "site_settings",
            EntityId = VideoKey,
            FromState = row.ValueJson.Length <= 500 ? row.ValueJson : null,
            ToState = null,
            TraceId = SettingsResults.TraceId(HttpContext),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { configured = false });
    }

    /// <summary>Upsert one setting and write the change to the audit log.</summary>
    private async Task<SiteSetting> SaveAsync(string key, string json, CancellationToken ct)
    {
        var actor = SettingsResults.Actor(User);
        var row = await _db.SiteSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        var before = row?.ValueJson;
        if (row is null)
        {
            row = new SiteSetting { Key = key };
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
            EntityId = key,
            FromState = before is { Length: <= 500 } ? before : null,
            ToState = json.Length <= 500 ? json : null,
            TraceId = SettingsResults.TraceId(HttpContext),
        });
        await _db.SaveChangesAsync(ct);
        return row;
    }
}
