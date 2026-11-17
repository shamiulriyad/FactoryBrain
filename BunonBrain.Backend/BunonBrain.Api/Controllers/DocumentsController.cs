using System.Text.Json.Nodes;
using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using Microsoft.AspNetCore.Mvc;
using BunonBrain.Api.Middleware;
using Microsoft.AspNetCore.RateLimiting;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Factory documentation automation (<c>/api/documents</c>). Every route checks a document
/// permission here; the service then enforces the document's category (department), factory and
/// line scope, the workflow state and separation of duties (nobody approves what they prepared).
/// <list type="bullet">
///   <item>Read: <c>documents.view</c>. Prepare / generate / upload / submit: <c>documents.create</c>.</item>
///   <item>Review and verify: <c>documents.review</c>. Approve: <c>documents.approve</c> (high risk: <c>documents.approve_high</c>).</item>
///   <item>Archive and type settings: <c>documents.control</c>. Status board: <c>documents.monitor</c>.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private const long MaxRequestBytes = 26L * 1024 * 1024;
    private readonly IDocumentService _docs;

    public DocumentsController(IDocumentService docs) { _docs = docs; }

    private string? Trace => AccessResults.TraceId(this);
    private IActionResult BadId() => AccessResults.Problem(this, StatusCodes.Status404NotFound, "Not found", "document_not_found", "Document not found.");

    [HttpGet("catalog")]
    [RequirePermission(Permissions.DocumentsView, Permissions.DocumentsMonitor)]
    public async Task<IActionResult> Catalog([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _docs.CatalogAsync(this.Caller(), factoryId, ct));

    [HttpPut("types/{code}/settings")]
    [RequirePermission(Permissions.DocumentsControl)]
    public async Task<IActionResult> SaveSettings([FromRoute] string code, [FromQuery] Guid? factoryId, [FromBody] DocTypeSettingWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _docs.SaveTypeSettingAsync(this.Caller(), factoryId, code, body, Trace, ct));

    [HttpGet]
    [RequirePermission(Permissions.DocumentsView)]
    public async Task<IActionResult> List([FromQuery] Guid? factoryId, [FromQuery] string? q, [FromQuery] string? category, [FromQuery] string? type, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? lineId, [FromQuery] string? view, [FromQuery] bool? includeArchived,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _docs.ListAsync(this.Caller(), new DocListQuery(factoryId, q, category, type, status, from, to, lineId, view, includeArchived ?? false, page ?? 1, pageSize ?? 25), ct));

    [HttpPost]
    [RequirePermission(Permissions.DocumentsCreate)]
    public async Task<IActionResult> Create([FromBody] DocCreateRequest body, CancellationToken ct) =>
        AccessResults.From(this, await _docs.CreateAsync(this.Caller(), body, Trace, ct), d => $"/api/documents/{d.Document.Id}");

    [HttpGet("monitor")]
    [RequirePermission(Permissions.DocumentsMonitor)]
    public async Task<IActionResult> Monitor([FromQuery] Guid? factoryId, CancellationToken ct) => AccessResults.From(this, await _docs.MonitorAsync(this.Caller(), factoryId, ct));

    [HttpGet("ai-usage")]
    [RequirePermission(Permissions.DocumentsControl)]
    public async Task<IActionResult> Usage([FromQuery] Guid? factoryId, [FromQuery] int? days, CancellationToken ct) =>
        AccessResults.From(this, await _docs.UsageAsync(this.Caller(), factoryId, days ?? 30, ct));

    [HttpGet("schedule-runs")]
    [RequirePermission(Permissions.DocumentsControl, Permissions.DocumentsMonitor)]
    public async Task<IActionResult> ScheduleRuns([FromQuery] Guid? factoryId, [FromQuery] int? days, CancellationToken ct) =>
        AccessResults.From(this, await _docs.ScheduleRunsAsync(this.Caller(), factoryId, days ?? 30, ct));

    [HttpGet("lookup")]
    [RequirePermission(Permissions.DocumentsView)]
    public async Task<IActionResult> Lookup([FromQuery] Guid? factoryId, [FromQuery] string lookup, [FromQuery] string key, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(key) || key.Length > 100
            ? AccessResults.Problem(this, StatusCodes.Status400BadRequest, "Invalid request", "invalid_key", "Give a value to look up.")
            : AccessResults.From(this, await _docs.LookupAsync(this.Caller(), factoryId, lookup, key, ct));

    [HttpPost("classify")]
    [RequirePermission(Permissions.DocumentsCreate)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Classify([FromForm] DocumentUploadForm form, [FromQuery] Guid? factoryId, [FromQuery] bool? ai, CancellationToken ct)
    {
        if (form.File is null || form.File.Length == 0) return AccessResults.Problem(this, 400, "Invalid request", "file_required", "A file is required.");
        return AccessResults.From(this, await _docs.ClassifyAsync(this.Caller(), factoryId, await ReadAsync(form, ct), ai ?? true, ct));
    }

    [HttpGet("{id}")]
    [RequirePermission(Permissions.DocumentsView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _docs.GetAsync(this.Caller(), g, ct)) : BadId();

    [HttpPut("{id}")]
    [RequirePermission(Permissions.DocumentsCreate)]
    public async Task<IActionResult> Save([FromRoute] string id, [FromBody] DocSaveRequest body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _docs.SaveAsync(this.Caller(), g, body, Trace, ct)) : BadId();

    [HttpPost("{id}/regenerate")]
    [RequirePermission(Permissions.DocumentsCreate)]
    public async Task<IActionResult> Regenerate([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _docs.RegenerateAsync(this.Caller(), g, body ?? new DocActionRequest(null, null), Trace, ct)) : BadId();

    private async Task<IActionResult> Act(string id, string action, DocActionRequest? body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _docs.ActAsync(this.Caller(), g, action, body ?? new DocActionRequest(null, null), Trace, ct)) : BadId();

    [HttpPost("{id}/submit")]
    [RequirePermission(Permissions.DocumentsCreate)]
    public Task<IActionResult> Submit([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "submit", body, ct);

    [HttpPost("{id}/start-review")]
    [RequirePermission(Permissions.DocumentsReview)]
    public Task<IActionResult> StartReview([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "start-review", body, ct);

    [HttpPost("{id}/verify")]
    [RequirePermission(Permissions.DocumentsReview)]
    public Task<IActionResult> Verify([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "verify", body, ct);

    [HttpPost("{id}/approve")]
    [RequirePermission(Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh)]
    public Task<IActionResult> Approve([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "approve", body, ct);

    [HttpPost("{id}/reject")]
    [RequirePermission(Permissions.DocumentsReview, Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh)]
    public Task<IActionResult> Reject([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "reject", body, ct);

    [HttpPost("{id}/revise")]
    [RequirePermission(Permissions.DocumentsCreate)]
    public Task<IActionResult> Revise([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "revise", body, ct);

    [HttpPost("{id}/archive")]
    [RequirePermission(Permissions.DocumentsControl)]
    public Task<IActionResult> Archive([FromRoute] string id, [FromBody] DocActionRequest? body, CancellationToken ct) => Act(id, "archive", body, ct);

    [HttpGet("{id}/versions/{number}")]
    [RequirePermission(Permissions.DocumentsView)]
    public async Task<IActionResult> Version([FromRoute] string id, [FromRoute] string number, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && int.TryParse(number, out var n) ? AccessResults.From(this, await _docs.VersionAsync(this.Caller(), g, n, ct)) : BadId();

    [HttpGet("{id}/export")]
    [RequirePermission(Permissions.DocumentsView)]
    public async Task<IActionResult> Export([FromRoute] string id, [FromQuery] string? format, [FromQuery] int? version, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return BadId();
        var r = await _docs.RenderAsync(this.Caller(), g, (format ?? "pdf").ToLowerInvariant(), version, Trace, ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(r.Value.Content, r.Value.ContentType, r.Value.FileName);
    }

    [HttpPost("{id}/files")]
    [RequirePermission(Permissions.DocumentsCreate)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Upload([FromRoute] string id, [FromForm] DocumentUploadForm form, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return BadId();
        if (form.File is null || form.File.Length == 0) return AccessResults.Problem(this, 400, "Invalid request", "file_required", "A file is required.");
        return AccessResults.From(this, await _docs.UploadAsync(this.Caller(), g, await ReadAsync(form, ct), Trace, ct), f => $"/api/documents/{id}/files/{f.Id}");
    }

    [HttpGet("{id}/files/{fileId}")]
    [RequirePermission(Permissions.DocumentsView)]
    public async Task<IActionResult> File([FromRoute] string id, [FromRoute] string fileId, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g) || !Guid.TryParse(fileId, out var f)) return BadId();
        var r = await _docs.OpenFileAsync(this.Caller(), g, f, ct);
        if (r.Outcome != AccessOutcome.Ok) return AccessResults.From(this, r);
        // Served only with the validated type, never sniffed, never cached, and sandboxed when shown inline.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; sandbox";
        Response.Headers["Cache-Control"] = "private, no-store";
        var inline = r.Value.ContentType is "application/pdf" || r.Value.ContentType.StartsWith("image/", StringComparison.Ordinal);
        Response.Headers["Content-Disposition"] = new System.Net.Mime.ContentDisposition { FileName = r.Value.FileName, Inline = inline }.ToString();
        return File(r.Value.Content, r.Value.ContentType);
    }

    [HttpPost("{id}/files/{fileId}/extract")]
    [RequirePermission(Permissions.DocumentsCreate)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    public async Task<IActionResult> Extract([FromRoute] string id, [FromRoute] string fileId, [FromQuery] bool? ai, CancellationToken ct) =>
        Guid.TryParse(id, out var g) && Guid.TryParse(fileId, out var f) ? AccessResults.From(this, await _docs.ExtractAsync(this.Caller(), g, f, ai ?? true, Trace, ct)) : BadId();

    [HttpPost("{id}/summary")]
    [RequirePermission(Permissions.DocumentsCreate, Permissions.DocumentsReview, Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    public async Task<IActionResult> Summary([FromRoute] string id, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _docs.SummarizeAsync(this.Caller(), g, Trace, ct)) : BadId();

    [HttpPost("{id}/draft-text")]
    [RequirePermission(Permissions.DocumentsCreate)]
    [EnableRateLimiting(RateLimitPolicies.QcUpload)]
    public async Task<IActionResult> DraftText([FromRoute] string id, [FromBody] DraftTextRequest body, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return BadId();
        var r = await _docs.DraftTextAsync(this.Caller(), g, body.Field ?? "", Trace, ct);
        return r.Outcome == AccessOutcome.Ok ? Ok(new { field = body.Field, text = r.Value, aiGenerated = true }) : AccessResults.From(this, r);
    }

    private static async Task<DocUploadInput> ReadAsync(DocumentUploadForm form, CancellationToken ct)
    {
        using var ms = new MemoryStream((int)Math.Min(form.File!.Length, int.MaxValue));
        await form.File.CopyToAsync(ms, ct);
        return new DocUploadInput(ms.ToArray(), form.File.FileName);
    }
}

public sealed class DocumentUploadForm
{
    public IFormFile? File { get; set; }
}

public sealed record DraftTextRequest(string? Field);

/// <summary>Store items and the stock ledger (<c>/api/inventory</c>). Stock moves only through approved GRN / issue documents.</summary>
[ApiController]
[Route("api/inventory")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventory;
    public InventoryController(IInventoryService inventory) { _inventory = inventory; }

    [HttpGet("items")]
    [RequirePermission(Permissions.InventoryView)]
    public async Task<IActionResult> Items([FromQuery] Guid? factoryId, [FromQuery] string? q, [FromQuery] string? category, [FromQuery] bool? includeInactive, CancellationToken ct) =>
        AccessResults.From(this, await _inventory.ItemsAsync(this.Caller(), factoryId, q, category, includeInactive ?? false, ct));

    [HttpPost("items")]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Create([FromBody] InventoryItemWrite body, CancellationToken ct) =>
        AccessResults.From(this, await _inventory.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), i => $"/api/inventory/items/{i.Id}");

    [HttpPut("items/{id}")]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] InventoryItemWrite body, CancellationToken ct) =>
        Guid.TryParse(id, out var g) ? AccessResults.From(this, await _inventory.UpdateAsync(this.Caller(), g, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "item_not_found", "Item not found.");

    [HttpGet("movements")]
    [RequirePermission(Permissions.InventoryView)]
    public async Task<IActionResult> Movements([FromQuery] Guid? factoryId, [FromQuery] Guid? itemId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _inventory.MovementsAsync(this.Caller(), factoryId, itemId, from, to, page ?? 1, pageSize ?? 50, ct));

    [HttpGet("rolls")]
    [RequirePermission(Permissions.InventoryView)]
    public async Task<IActionResult> Rolls([FromQuery] Guid? factoryId, [FromQuery] Guid? itemId, [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        AccessResults.From(this, await _inventory.RollsAsync(this.Caller(), factoryId, itemId, status, q, page ?? 1, pageSize ?? 50, ct));
}

/// <summary>The caller's own notifications (<c>/api/notifications</c>).</summary>
[ApiController]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;
    public NotificationsController(INotificationService notifications) { _notifications = notifications; }

    [HttpGet]
    [RequirePermission(Permissions.NotificationsView)]
    public async Task<IActionResult> List([FromQuery] bool? unread, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(await _notifications.ListAsync(this.Caller(), unread ?? false, limit ?? 30, ct));

    [HttpPost("read")]
    [RequirePermission(Permissions.NotificationsView)]
    public async Task<IActionResult> Read([FromBody] MarkReadRequest? body, CancellationToken ct) =>
        Ok(new { marked = await _notifications.MarkReadAsync(this.Caller(), body?.Ids, ct) });
}

public sealed record MarkReadRequest(IReadOnlyList<Guid>? Ids);
