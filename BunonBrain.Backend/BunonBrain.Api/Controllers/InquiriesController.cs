using System.Text.Json;
using System.Text.RegularExpressions;
using BunonBrain.Api.Authorization;
using BunonBrain.Api.Middleware;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Api.Controllers;

public sealed record InquiryCreateRequest(
    string? Kind,
    string? Name,
    string? Email,
    string? Phone,
    string? Company,
    string? JobTitle,
    string? Subject,
    string? Message,
    Dictionary<string, string?>? Details,
    string? SourcePath,
    /// <summary>Honeypot: hidden from people, filled by naive bots.</summary>
    string? Website);

public sealed record InquiryStatusRequest(string? Status);

/// <summary>
/// Website forms. <c>POST /api/inquiries</c> is public and rate limited per IP;
/// reading and triaging needs <c>inquiries.manage</c> (Super Admin).
/// </summary>
[ApiController]
[Route("api/inquiries")]
public class InquiriesController : ControllerBase
{
    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$", RegexOptions.Compiled);
    private static readonly Regex PhonePattern = new(@"^\+?[\d\s\-().]{7,20}$", RegexOptions.Compiled);

    private readonly BunonBrainDbContext _db;

    public InquiriesController(BunonBrainDbContext db) { _db = db; }

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Inquiry)]
    public async Task<IActionResult> Create([FromBody] InquiryCreateRequest body, CancellationToken ct)
    {
        var errors = Validate(body, out var clean);
        if (errors.Count > 0) return ValidationProblem(new ValidationProblemDetails(errors) { Title = "Some fields need attention." });

        // A filled honeypot is a bot: answer like a success so it learns nothing, but store nothing.
        if (!string.IsNullOrWhiteSpace(body.Website)) return Accepted(new { received = true });

        _db.Inquiries.Add(clean);
        await _db.SaveChangesAsync(ct);
        return Accepted(new { received = true, id = clean.Id });
    }

    [HttpGet]
    [RequirePermission(Permissions.InquiriesManage)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? kind, [FromQuery] int? limit, CancellationToken ct)
    {
        var q = _db.Inquiries.AsNoTracking();
        if (!string.IsNullOrEmpty(status) && InquiryStatuses.All.Contains(status)) q = q.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(kind) && InquiryKinds.All.Contains(kind)) q = q.Where(x => x.Kind == kind);
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var rows = await q.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct);
        var counts = await _db.Inquiries.AsNoTracking().GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return Ok(new
        {
            items = rows.Select(ToDto),
            counts = InquiryStatuses.All.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0),
        });
    }

    [HttpPatch("{id}")]
    [RequirePermission(Permissions.InquiriesManage)]
    public async Task<IActionResult> SetStatus([FromRoute] string id, [FromBody] InquiryStatusRequest body, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var key)) return NotFound(new { error = "inquiry_not_found", id });
        if (body.Status is null || !InquiryStatuses.All.Contains(body.Status))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["status"] = new[] { "Status must be new, in_progress or closed." },
            }));
        var row = await _db.Inquiries.FirstOrDefaultAsync(x => x.Id == key, ct);
        if (row is null) return NotFound(new { error = "inquiry_not_found", id });
        row.Status = body.Status;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(row));
    }

    private static object ToDto(Inquiry x) => new
    {
        x.Id,
        x.Kind,
        x.Name,
        x.Email,
        x.Phone,
        x.Company,
        x.JobTitle,
        x.Subject,
        x.Message,
        Details = JsonSerializer.Deserialize<Dictionary<string, string>>(x.DetailsJson) ?? new(),
        x.SourcePath,
        x.Status,
        x.CreatedAt,
        x.UpdatedAt,
    };

    private static string? Trim(string? v, int max) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim() is var t && t.Length > max ? t[..max] : v.Trim();

    private static Dictionary<string, string[]> Validate(InquiryCreateRequest b, out Inquiry clean)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string field, string msg) => errors[field] = new[] { msg };

        var kind = b.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !InquiryKinds.All.Contains(kind)) Err("kind", "Unknown form type.");

        var name = b.Name?.Trim() ?? "";
        if (name.Length < 2 || name.Length > 120) Err("name", "Enter your name (2 to 120 characters).");

        var email = b.Email?.Trim();
        var phone = b.Phone?.Trim();
        if (string.IsNullOrEmpty(email)) email = null;
        if (string.IsNullOrEmpty(phone)) phone = null;
        if (email is null && phone is null) Err("email", "Enter an email address or phone number so we can reply.");
        if (email is not null && (email.Length > 254 || !EmailPattern.IsMatch(email))) Err("email", "That doesn't look like an email address.");
        if (phone is not null && (!PhonePattern.IsMatch(phone) || phone.Count(char.IsDigit) < 7)) Err("phone", "That doesn't look like a phone number.");

        var message = b.Message?.Trim() ?? "";
        if (message.Length < 10) Err("message", "Tell us a little more (at least 10 characters).");
        if (message.Length > 4000) Err("message", "Keep the message under 4,000 characters.");

        var details = new Dictionary<string, string>();
        if (b.Details is not null)
        {
            if (b.Details.Count > 20) Err("details", "Too many extra fields.");
            foreach (var (k, v) in b.Details.Take(20))
            {
                if (string.IsNullOrWhiteSpace(k) || k.Length > 40 || string.IsNullOrWhiteSpace(v)) continue;
                details[k.Trim()] = v.Trim().Length > 500 ? v.Trim()[..500] : v.Trim();
            }
        }

        var path = b.SourcePath?.Trim();
        if (path is not null && (!path.StartsWith('/') || path.Length > 200)) path = null;

        clean = new Inquiry
        {
            Kind = kind ?? "",
            Name = name,
            Email = email,
            Phone = phone,
            Company = Trim(b.Company, 160),
            JobTitle = Trim(b.JobTitle, 120),
            Subject = Trim(b.Subject, 200),
            Message = message,
            DetailsJson = JsonSerializer.Serialize(details),
            SourcePath = path,
        };
        return errors;
    }
}
