using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Qc;
using FactoryBrain.Infrastructure.Qc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// Garment quality inspections.
/// <list type="bullet">
///   <item>Read (any signed-in user): <c>GET /api/qc/inspections</c>, <c>/{id}</c>, <c>/{id}/image</c>,
///   <c>GET /api/qc/summary</c>, <c>GET /api/qc/vision/status</c>, <c>GET /api/qc/categories</c>.</item>
///   <item>Operator or Admin: <c>POST /api/qc/inspections</c> (multipart upload, optional
///   <c>Idempotency-Key</c> header), <c>POST /{id}/review</c>, <c>POST /{id}/retry</c>.</item>
///   <item>Admin: <c>POST /api/qc/categories</c>, <c>PUT /api/qc/categories/{code}</c>.</item>
/// </list>
/// The acting user always comes from the validated JWT.
/// </summary>
[ApiController]
[Route("api/qc")]
public class QcInspectionsController : ControllerBase
{
    /// <summary>Request ceiling for an upload (image + form fields); the configured image limit is enforced below it.</summary>
    private const long MaxRequestBytes = QcLimits.MaxImageBytesCeiling + 1024 * 1024;

    private readonly IQcInspectionService _svc;
    private readonly IQcDefectCategoryService _categories;
    private readonly QcOptions _options;

    public QcInspectionsController(IQcInspectionService svc, IQcDefectCategoryService categories, QcOptions options)
    {
        _svc = svc; _categories = categories; _options = options;
    }

    [HttpGet("inspections")]
    public async Task<IActionResult> List(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? stage, [FromQuery] string? lineId,
        [FromQuery] string? productType, [FromQuery] string? outcome, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? sort, CancellationToken ct)
    {
        var query = new QcInspectionQuery(Blank(q), Blank(status), Blank(stage), Blank(lineId), Blank(productType), Blank(outcome),
            from, to, page ?? 1, pageSize ?? 20, Blank(sort));
        var errors = QcInspectionService.ValidateQuery(query);
        if (errors.Count > 0) return Invalid(errors);
        return Ok(await _svc.ListAsync(query, ct));
    }

    [HttpPost("inspections")]
    [Authorize(Policy = AuthPolicies.Operator)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Create([FromForm] QcCreateInspectionForm form, CancellationToken ct)
    {
        if (form.Image is null || form.Image.Length == 0)
            return Invalid(new Dictionary<string, string[]> { ["image"] = new[] { "An image file is required." } });
        if (form.Image.Length > _options.MaxImageBytes)
            return Invalid(new Dictionary<string, string[]> { ["image"] = new[] { $"The image is larger than {_options.MaxImageBytes / (1024 * 1024.0):0.#} MB." } });

        byte[] bytes;
        using (var ms = new MemoryStream((int)form.Image.Length))
        {
            await form.Image.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        var key = Request.Headers.TryGetValue("Idempotency-Key", out var raw) ? Blank(raw.ToString()) : null;
        var input = new QcCreateInspectionInput(
            form.ProductType ?? "", form.Stage ?? "", form.LineId, form.ProductionOrder, form.Notes,
            (form.CategoryCodes ?? new()).SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
            bytes, form.Image.FileName, key);

        var result = await _svc.CreateAsync(input, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct);
        switch (result.Outcome)
        {
            case QcCreateOutcome.Created:
                return Created($"/api/qc/inspections/{result.Inspection!.Id}", result.Inspection);
            case QcCreateOutcome.Replayed:
                Response.Headers["Idempotent-Replayed"] = "true";
                return Ok(result.Inspection);
            case QcCreateOutcome.IdempotencyConflict:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Idempotency key reused",
                    detail: "This Idempotency-Key was already used for a different image. Use a new key for a new submission.");
            default:
                return Invalid(result.Errors!);
        }
    }

    [HttpGet("inspections/{id}")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NotFound(new { error = "inspection_not_found" });
        var detail = await _svc.GetAsync(gid, ct);
        return detail is null ? NotFound(new { error = "inspection_not_found" }) : Ok(detail);
    }

    [HttpGet("inspections/{id}/image")]
    public async Task<IActionResult> Image([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NotFound(new { error = "inspection_not_found" });
        var image = await _svc.OpenImageAsync(gid, ct);
        if (image is null) return NotFound(new { error = "image_not_found" });
        // Served only with the validated image type; never sniffed, never rendered as a document.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Content-Disposition"] = "inline";
        return File(image.Content, image.ContentType);
    }

    [HttpPost("inspections/{id}/review")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> Review([FromRoute] string id, [FromBody] QcReviewRequest body, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NotFound(new { error = "inspection_not_found" });
        var result = await _svc.ReviewAsync(gid, body, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct);
        return result.Outcome switch
        {
            QcReviewOutcome.Ok => Ok(result.Inspection),
            QcReviewOutcome.NotFound => NotFound(new { error = "inspection_not_found" }),
            QcReviewOutcome.InvalidState => Problem(statusCode: StatusCodes.Status409Conflict, title: "Not ready for review",
                detail: "The inspection is still being analysed. Review it once analysis has completed or failed."),
            QcReviewOutcome.VersionConflict => Problem(statusCode: StatusCodes.Status409Conflict, title: "version_conflict",
                detail: "The inspection changed since you loaded it. Reload it and review again."),
            _ => Invalid(result.Errors!),
        };
    }

    [HttpPost("inspections/{id}/retry")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<IActionResult> Retry([FromRoute] string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return NotFound(new { error = "inspection_not_found" });
        var result = await _svc.RetryAsync(gid, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct);
        return result.Outcome switch
        {
            QcRetryOutcome.Ok => Accepted($"/api/qc/inspections/{gid}", result.Inspection),
            QcRetryOutcome.NotFound => NotFound(new { error = "inspection_not_found" }),
            QcRetryOutcome.ProviderNotConfigured => Problem(statusCode: StatusCodes.Status409Conflict, title: "Inference not configured",
                detail: _svc.VisionStatus().Reason ?? "No computer-vision provider is configured."),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Not retryable",
                detail: "Only a failed analysis can be retried."),
        };
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? days,
        [FromQuery] string? lineId, [FromQuery] string? productType, [FromQuery] string? stage, CancellationToken ct)
    {
        var query = new QcSummaryQuery(from, to, days, Blank(lineId), Blank(productType), Blank(stage));
        var errors = QcInspectionService.ValidateSummary(query);
        if (errors.Count > 0) return Invalid(errors);
        return Ok(await _svc.SummaryAsync(query, ct));
    }

    [HttpGet("vision/status")]
    public IActionResult VisionStatus() => Ok(_svc.VisionStatus());

    [HttpGet("categories")]
    public async Task<IActionResult> Categories([FromQuery] bool? includeInactive, CancellationToken ct)
        => Ok(await _categories.ListAsync(includeInactive ?? false, ct));

    [HttpPost("categories")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> CreateCategory([FromBody] QcCategoryCreateRequest body, CancellationToken ct)
        => CategoryResult(await _categories.CreateAsync(body, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct), created: true);

    [HttpPut("categories/{code}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> UpdateCategory([FromRoute] string code, [FromBody] QcCategoryUpdateRequest body, CancellationToken ct)
        => CategoryResult(await _categories.UpdateAsync(code, body, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct), created: false);

    private IActionResult CategoryResult(QcCategoryWriteResult r, bool created) => r.Outcome switch
    {
        QcCategoryWriteOutcome.Ok => created ? Created($"/api/qc/categories/{r.Category!.Code}", r.Category) : Ok(r.Category),
        QcCategoryWriteOutcome.NotFound => NotFound(new { error = "category_not_found" }),
        QcCategoryWriteOutcome.VersionConflict => Problem(statusCode: StatusCodes.Status409Conflict, title: "version_conflict",
            detail: "The category changed since you loaded it. Reload and try again."),
        QcCategoryWriteOutcome.Duplicate => Problem(statusCode: StatusCodes.Status409Conflict, title: "duplicate_category",
            detail: r.Errors?.Values.FirstOrDefault()?.FirstOrDefault() ?? "A category with this code or name already exists."),
        _ => Invalid(r.Errors!),
    };

    private IActionResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(kv => kv.Key, kv => kv.Value))
        {
            Status = StatusCodes.Status400BadRequest,
        });

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>multipart/form-data body of <c>POST /api/qc/inspections</c>.</summary>
public sealed class QcCreateInspectionForm
{
    public IFormFile? Image { get; set; }
    public string? ProductType { get; set; }
    /// <summary><c>fabric</c> | <c>cutting</c> | <c>inline</c> | <c>end_of_line</c> | <c>final</c>.</summary>
    public string? Stage { get; set; }
    public string? LineId { get; set; }
    public string? ProductionOrder { get; set; }
    public string? Notes { get; set; }
    /// <summary>Repeated field or comma-separated; empty means every active category.</summary>
    public List<string>? CategoryCodes { get; set; }
}
