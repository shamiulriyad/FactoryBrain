using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Merchandising;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Infrastructure.Merchandising;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Merchandising &amp; document automation (<c>/api/merchandising</c>).
/// <list type="bullet">
///   <item>Read (any signed-in user): config, overview, orders (search / filters / pages), order detail,
///   documents, version detail (parsed text, extracted fields, evidence), the original file.</item>
///   <item>Operator or Admin: register and edit orders, milestones, upload documents and new versions
///   (multipart, optional <c>Idempotency-Key</c>), retry processing, confirm extracted values, resolve conflicts.</item>
/// </list>
/// Extracted values never change an order until a person confirms them. The acting user always comes from the validated JWT.
/// </summary>
[ApiController]
[Route("api/merchandising")]
public class MerchandisingController : ControllerBase
{
    /// <summary>Request ceiling for an upload (file + form fields); the configured file limit is enforced below it.</summary>
    private const long MaxRequestBytes = MerchOptions.MaxFileBytesCeiling + 1024 * 1024;

    private readonly IMerchOrderService _orders;
    private readonly IMerchDocumentService _documents;
    private readonly MerchOptions _options;

    public MerchandisingController(IMerchOrderService orders, IMerchDocumentService documents, MerchOptions options)
    {
        _orders = orders; _documents = documents; _options = options;
    }

    [HttpGet("config")]
    public IActionResult Config() => Ok(_documents.Config());

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct) => Ok(await _orders.OverviewAsync(ct));

    // ── Orders ──────────────────────────────────────────────────────────────

    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] string? q, [FromQuery] string? buyer, [FromQuery] string? status,
        [FromQuery] string? deliveryFrom, [FromQuery] string? deliveryTo, [FromQuery] string? attention,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? sort, CancellationToken ct)
    {
        if (!TryDate(deliveryFrom, "deliveryFrom", out var from, out var bad) || !TryDate(deliveryTo, "deliveryTo", out var to, out bad)) return bad!;
        return Map(await _orders.ListAsync(new MerchOrderQuery(Blank(q), Blank(buyer), Blank(status), from, to, Blank(attention),
            page ?? 1, pageSize ?? 20, Blank(sort)), ct));
    }

    [HttpGet("orders/{id}")]
    public async Task<IActionResult> Order([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _orders.GetAsync(g, ct) is { } o ? Ok(o) : NotFound(new { error = "order_not_found" });

    [HttpPost("orders")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> CreateOrder([FromBody] MerchOrderWrite body, CancellationToken ct) =>
        Map(await _orders.CreateAsync(body, Actor, Trace, ct), o => $"/api/merchandising/orders/{o.Order.Id}");

    [HttpPut("orders/{id}")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> UpdateOrder([FromRoute] string id, [FromBody] MerchOrderWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _orders.UpdateAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "order_not_found" });

    [HttpPost("orders/{id}/milestones")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> AddMilestone([FromRoute] string id, [FromBody] MerchMilestoneWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _orders.AddMilestoneAsync(g, body, Actor, Trace, ct), _ => $"/api/merchandising/orders/{g}")
            : NotFound(new { error = "order_not_found" });

    [HttpPut("milestones/{id}")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> UpdateMilestone([FromRoute] string id, [FromBody] MerchMilestoneWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _orders.UpdateMilestoneAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "milestone_not_found" });

    [HttpPost("conflicts/{id}/resolve")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> ResolveConflict([FromRoute] string id, [FromBody] MerchConflictResolution body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _orders.ResolveConflictAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "conflict_not_found" });

    // ── Documents ───────────────────────────────────────────────────────────

    [HttpGet("documents")]
    public async Task<IActionResult> Documents([FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] string? orderId,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        Guid? oid = null;
        if (Blank(orderId) is { } raw)
        {
            if (!Guid.TryParse(raw, out var g)) return Invalid(new Dictionary<string, string[]> { ["orderId"] = new[] { "orderId must be an id." } });
            oid = g;
        }
        return Map(await _documents.ListAsync(new MerchDocumentQuery(Blank(q), Blank(status), Blank(type), oid, page ?? 1, pageSize ?? 20), ct));
    }

    [HttpGet("documents/{id}")]
    public async Task<IActionResult> Document([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _documents.GetDocumentAsync(g, ct) is { } d ? Ok(d) : NotFound(new { error = "document_not_found" });

    [HttpPost("orders/{id}/documents")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Upload([FromRoute] string id, [FromForm] MerchUploadForm form, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return NotFound(new { error = "order_not_found" });
        if (CheckFile(form) is { } bad) return bad;
        return MapUpload(await _documents.UploadAsync(g, await ReadFormAsync(form, ct), Actor, Trace, ct));
    }

    [HttpPost("documents/{id}/versions")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> UploadVersion([FromRoute] string id, [FromForm] MerchUploadForm form, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return NotFound(new { error = "document_not_found" });
        if (CheckFile(form) is { } bad) return bad;
        return MapUpload(await _documents.UploadVersionAsync(g, await ReadFormAsync(form, ct), Actor, Trace, ct));
    }

    [HttpGet("versions/{id}")]
    public async Task<IActionResult> Version([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && await _documents.GetVersionAsync(g, ct) is { } v ? Ok(v) : NotFound(new { error = "version_not_found" });

    [HttpGet("versions/{id}/file")]
    public async Task<IActionResult> File([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return NotFound(new { error = "version_not_found" });
        var file = await _documents.OpenFileAsync(g, ct);
        if (file is null) return NotFound(new { error = "file_not_found" });
        // Served only with the validated type, never sniffed; PDFs and images may be shown inline in a sandbox, anything else downloads.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; sandbox";
        Response.Headers["Cache-Control"] = "private, no-store";
        var inline = file.Format is "pdf" or "image";
        var disposition = new System.Net.Mime.ContentDisposition { FileName = file.FileName, Inline = inline };
        Response.Headers["Content-Disposition"] = disposition.ToString();
        return File(file.Content, file.ContentType);
    }

    [HttpPost("versions/{id}/retry")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> Retry([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return NotFound(new { error = "version_not_found" });
        var r = await _documents.RetryAsync(g, Actor, Trace, ct);
        return r.Outcome == MerchOutcome.Ok ? Accepted($"/api/merchandising/versions/{g}", r.Value) : Map(r);
    }

    [HttpPost("versions/{id}/confirm")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> Confirm([FromRoute] string id, [FromBody] MerchConfirmRequest body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? Map(await _documents.ConfirmAsync(g, body, Actor, Trace, ct)) : NotFound(new { error = "version_not_found" });

    // ── Mapping ─────────────────────────────────────────────────────────────

    private Application.Abstractions.AuditActor Actor => SettingsResults.Actor(User);
    private string Trace => SettingsResults.TraceId(HttpContext);

    /// <summary>A 400 for a missing or oversized file, before the body is read into memory.</summary>
    private IActionResult? CheckFile(MerchUploadForm form)
    {
        if (form.File is null || form.File.Length == 0)
            return Invalid(new Dictionary<string, string[]> { ["file"] = new[] { "A file is required." } });
        if (form.File.Length > _options.MaxFileBytes)
            return Invalid(new Dictionary<string, string[]> { ["file"] = new[] { MerchFileInspector.Describe(MerchFileInspector.ErrTooLarge, _options.MaxFileBytes) } });
        return null;
    }

    private async Task<MerchUploadInput> ReadFormAsync(MerchUploadForm form, CancellationToken ct)
    {
        byte[] bytes;
        using (var ms = new MemoryStream((int)form.File!.Length))
        {
            await form.File.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        var key = Request.Headers.TryGetValue("Idempotency-Key", out var raw) ? Blank(raw.ToString()) : null;
        return new MerchUploadInput(bytes, form.File.FileName, form.File.ContentType, form.Type, form.Title, form.ChangeNote, key);
    }

    private IActionResult MapUpload(MerchResult<MerchVersionDetail> r) => Map(r, v => $"/api/merchandising/versions/{v.Id}");

    private IActionResult Map<T>(MerchResult<T> r, Func<T, string>? location = null)
    {
        if (r.Replayed) Response.Headers["Idempotent-Replayed"] = "true";
        return r.Outcome switch
        {
            MerchOutcome.Ok => Ok(r.Value),
            MerchOutcome.Created => location is null ? StatusCode(StatusCodes.Status201Created, r.Value) : Created(location(r.Value!), r.Value),
            MerchOutcome.NotFound => NotFound(new { error = r.Code }),
            MerchOutcome.Conflict => Problem(statusCode: StatusCodes.Status409Conflict, title: r.Code, detail: r.Message),
            _ => Invalid(r.Errors ?? new Dictionary<string, string[]>()),
        };
    }

    private IActionResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(kv => kv.Key, kv => kv.Value)) { Status = StatusCodes.Status400BadRequest });

    private bool TryDate(string? raw, string field, out DateOnly? value, out IActionResult? bad)
    {
        value = null;
        bad = null;
        if (Blank(raw) is not { } s) return true;
        if (MerchWire.TryParseIsoDate(s, out var d)) { value = d; return true; }
        bad = Invalid(new Dictionary<string, string[]> { [field] = new[] { $"{field} must be a date (yyyy-MM-dd)." } });
        return false;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>multipart/form-data body of the document upload endpoints.</summary>
public sealed class MerchUploadForm
{
    public IFormFile? File { get; set; }
    /// <summary><c>purchase_order</c> | <c>tech_pack</c> | <c>measurement_sheet</c> | <c>buyer_instruction</c> | <c>other</c> (new documents only).</summary>
    public string? Type { get; set; }
    public string? Title { get; set; }
    /// <summary>Why this version replaces the previous one (new versions).</summary>
    public string? ChangeNote { get; set; }
}
