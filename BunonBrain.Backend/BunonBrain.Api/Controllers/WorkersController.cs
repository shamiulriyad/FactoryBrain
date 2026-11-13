using System.Text;
using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Application.Workers;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Worker management: the directory, registration, the profile, status
/// changes, the optional login account, cards, CSV import and export.
/// A worker is addressed by its id (GUID) or its Worker ID (<c>W-001</c>).
/// Every action checks the permission named on it; the service additionally
/// enforces factory / line / self scope and decides how much of a worker the
/// caller sees (see <see cref="IWorkerService"/>).
/// </summary>
[ApiController]
[Route("api/workers")]
public class WorkersController : ControllerBase
{
    /// <summary>Photo upload limit for the whole request (the store's own limit is lower).</summary>
    private const long MaxPhotoRequestBytes = 4 * 1024 * 1024;
    private const long MaxImportRequestBytes = WorkerImportParser.MaxBytes + 64 * 1024;

    private readonly IWorkerService _workers;
    public WorkersController(IWorkerService workers) { _workers = workers; }

    /// <summary>
    /// GET /api/workers?search=&amp;factoryId=&amp;departmentId=&amp;floorId=&amp;lineId=&amp;status=&amp;designation=&amp;employmentType=
    /// &amp;sort=workerId|name|joined|status|department|designation|created (prefix - for descending)&amp;page=&amp;pageSize=
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId,
        [FromQuery] Guid? floorId, [FromQuery] string? lineId, [FromQuery] string? status, [FromQuery] string? designation,
        [FromQuery] string? employmentType, [FromQuery] string? sort, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _workers.ListAsync(this.Caller(),
            new WorkerListQuery(search, factoryId, departmentId, floorId, lineId, status, designation, employmentType, sort, page ?? 1, pageSize ?? 25), ct));

    /// <summary>GET /api/workers/me — the caller's own worker record (404 when the account isn't linked to one).</summary>
    [HttpGet("me")]
    [RequirePermission(Permissions.SelfView)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var mine = await _workers.MineAsync(this.Caller(), ct);
        return mine is null
            ? AccessResults.Problem(this, 404, "Not found", "worker_profile_not_linked", "Your account is not linked to a worker profile.")
            : Ok(mine);
    }

    /// <summary>GET /api/workers/statistics?factoryId= — counts over the workers the caller can see.</summary>
    [HttpGet("statistics")]
    [RequirePermission(Permissions.WorkersView)]
    public async Task<IActionResult> Statistics([FromQuery] Guid? factoryId, CancellationToken ct)
        => Ok(await _workers.StatisticsAsync(this.Caller(), factoryId, ct));

    /// <summary>GET /api/workers/export — the filtered directory as CSV (same filters as the list).</summary>
    [HttpGet("export")]
    [RequirePermission(Permissions.WorkersDetailsView, Permissions.WorkersManage)]
    public async Task<IActionResult> Export([FromQuery] string? search, [FromQuery] Guid? factoryId, [FromQuery] Guid? departmentId,
        [FromQuery] Guid? floorId, [FromQuery] string? lineId, [FromQuery] string? status, [FromQuery] string? designation,
        [FromQuery] string? employmentType, [FromQuery] string? sort, CancellationToken ct)
    {
        var result = await _workers.ExportAsync(this.Caller(),
            new WorkerListQuery(search, factoryId, departmentId, floorId, lineId, status, designation, employmentType, sort),
            AccessResults.TraceId(this), ct);
        if (result.Outcome != AccessOutcome.Ok) return AccessResults.From(this, result);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Exported-Rows"] = result.Value!.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(result.Value.Content, "text/csv; charset=utf-8", result.Value.FileName);
    }

    /// <summary>GET /api/workers/import/template — the CSV header the import expects, with one example row.</summary>
    [HttpGet("import/template")]
    [RequirePermission(Permissions.WorkersManage)]
    public IActionResult ImportTemplate()
        => File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(WorkerImportParser.Template())).ToArray(),
            "text/csv; charset=utf-8", "worker-import-template.csv");

    /// <summary>
    /// POST /api/workers/import (multipart: <c>file</c>, <c>factoryId</c>, <c>dryRun</c>).
    /// <c>dryRun=true</c> (the default) validates and returns a preview; <c>dryRun=false</c>
    /// registers the valid rows in one transaction and skips the invalid ones.
    /// </summary>
    [HttpPost("import")]
    [RequirePermission(Permissions.WorkersManage)]
    [RequestSizeLimit(MaxImportRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportRequestBytes)]
    public async Task<IActionResult> Import([FromForm] WorkerImportForm form, CancellationToken ct)
    {
        if (form.FactoryId is not { } factoryId || factoryId == Guid.Empty)
            return AccessResults.Problem(this, 400, "Invalid request", "factory_required", "Choose the factory to import into.");
        if (form.File is null || form.File.Length == 0)
            return AccessResults.Problem(this, 400, "Invalid request", "import_file_invalid", "Choose a CSV file.");
        if (form.File.Length > WorkerImportParser.MaxBytes)
            return AccessResults.Problem(this, 400, "Invalid request", "import_file_invalid",
                $"The file is larger than {WorkerImportParser.MaxBytes / 1024} KB. Split it into smaller files.");

        byte[] bytes;
        using (var ms = new MemoryStream((int)form.File.Length))
        {
            await form.File.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        if (DecodeCsv(bytes) is not { } text)
            return AccessResults.Problem(this, 400, "Invalid request", "import_file_invalid",
                "The file is not UTF-8 text. In your spreadsheet program choose \"Save as → CSV UTF-8\".");

        var result = await _workers.ImportAsync(this.Caller(), factoryId, text, form.DryRun ?? true, AccessResults.TraceId(this), ct);
        return result.Outcome == AccessOutcome.Created ? StatusCode(StatusCodes.Status201Created, result.Value) : AccessResults.From(this, result);
    }

    /// <summary>UTF-8 (with or without a byte-order mark) or UTF-16 with one; null when the bytes are neither.</summary>
    private static string? DecodeCsv(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [HttpPost]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> Create([FromBody] CreateWorkerRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.CreateAsync(this.Caller(), body, AccessResults.TraceId(this), ct), w => $"/api/workers/{w.Id}");

    [HttpGet("{id}")]
    [RequirePermission(Permissions.WorkersView, Permissions.SelfView)]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct)
        => AccessResults.From(this, await _workers.GetAsync(this.Caller(), id, ct));

    /// <summary>GET /api/workers/{id}/profile — the worker with factory, account, cards, status history and capabilities.</summary>
    [HttpGet("{id}/profile")]
    [RequirePermission(Permissions.WorkersView, Permissions.SelfView)]
    public async Task<IActionResult> Profile([FromRoute] string id, CancellationToken ct)
        => AccessResults.From(this, await _workers.ProfileAsync(this.Caller(), id, ct));

    [HttpPut("{id}")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> Update([FromRoute] string id, [FromBody] UpdateWorkerRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.UpdateAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct));

    /// <summary>PATCH /api/workers/{id}/status — a controlled status change (see WorkerStatusTransitions).</summary>
    [HttpPatch("{id}/status")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> ChangeStatus([FromRoute] string id, [FromBody] ChangeWorkerStatusRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.ChangeStatusAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct));

    [HttpPut("{id}/workstation")]
    [RequirePermission(Permissions.WorkersAssign, Permissions.WorkersManage)]
    public async Task<IActionResult> AssignWorkstation([FromRoute] string id, [FromBody] AssignWorkstationRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.AssignWorkstationAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct));

    /// <summary>PUT /api/workers/{id}/account — link or unlink an existing login account.</summary>
    [HttpPut("{id}/account")]
    [RequirePermission(Permissions.UsersUpdate)]
    public async Task<IActionResult> LinkAccount([FromRoute] string id, [FromBody] LinkWorkerAccountRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.LinkAccountAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct));

    /// <summary>POST /api/workers/{id}/account — create a login (Worker role, temporary password) for the worker and link it.</summary>
    [HttpPost("{id}/account")]
    [RequirePermission(Permissions.UsersCreate)]
    public async Task<IActionResult> CreateAccount([FromRoute] string id, [FromBody] CreateWorkerAccountRequest body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return AccessResults.From(this, await _workers.CreateAccountAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct), w => $"/api/workers/{w.Id}");
    }

    /// <summary>GET /api/workers/{id}/photo — the profile photograph, to callers who can see the worker.</summary>
    [HttpGet("{id}/photo")]
    [RequirePermission(Permissions.WorkersView, Permissions.SelfView)]
    public async Task<IActionResult> Photo([FromRoute] string id, CancellationToken ct)
    {
        var result = await _workers.PhotoAsync(this.Caller(), id, ct);
        if (result.Outcome != AccessOutcome.Ok) return AccessResults.From(this, result);
        // Served only with the validated image type; never sniffed, never rendered as a document.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.ContentDisposition = "inline";
        return File(result.Value!.Content, result.Value.ContentType);
    }

    /// <summary>PUT /api/workers/{id}/photo (multipart: <c>photo</c>) — JPEG, PNG or WebP.</summary>
    [HttpPut("{id}/photo")]
    [RequirePermission(Permissions.WorkersManage)]
    [RequestSizeLimit(MaxPhotoRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxPhotoRequestBytes)]
    public async Task<IActionResult> SetPhoto([FromRoute] string id, [FromForm] WorkerPhotoForm form, CancellationToken ct)
    {
        if (form.Photo is null || form.Photo.Length == 0)
            return AccessResults.Problem(this, 400, "Invalid request", "image_empty", "Choose a photo.");
        byte[] bytes;
        using (var ms = new MemoryStream((int)form.Photo.Length))
        {
            await form.Photo.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        return AccessResults.From(this, await _workers.SetPhotoAsync(this.Caller(), id, bytes, AccessResults.TraceId(this), ct));
    }

    [HttpDelete("{id}/photo")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> RemovePhoto([FromRoute] string id, CancellationToken ct)
        => AccessResults.From(this, await _workers.RemovePhotoAsync(this.Caller(), id, AccessResults.TraceId(this), ct));

    /// <summary>GET /api/workers/{id}/cards — every RFID / NFC card ever issued to the worker.</summary>
    [HttpGet("{id}/cards")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> Cards([FromRoute] string id, CancellationToken ct)
        => AccessResults.From(this, await _workers.CardsAsync(this.Caller(), id, ct));

    [HttpPost("{id}/cards")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> IssueCard([FromRoute] string id, [FromBody] IssueWorkerCardRequest body, CancellationToken ct)
        => AccessResults.From(this, await _workers.IssueCardAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct), c => $"/api/workers/{c.WorkerId}/cards");

    /// <summary>PATCH /api/workers/{id}/cards/{cardId} — deactivate a card (returned, lost, damaged, replaced).</summary>
    [HttpPatch("{id}/cards/{cardId}")]
    [RequirePermission(Permissions.WorkersManage)]
    public async Task<IActionResult> DeactivateCard([FromRoute] string id, [FromRoute] string cardId, [FromBody] DeactivateWorkerCardRequest body, CancellationToken ct)
        => Guid.TryParse(cardId, out var cid)
            ? AccessResults.From(this, await _workers.DeactivateCardAsync(this.Caller(), id, cid, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "card_not_found", "Card not found.");
}

/// <summary>multipart/form-data body of POST /api/workers/import.</summary>
public sealed class WorkerImportForm
{
    public IFormFile? File { get; set; }
    public Guid? FactoryId { get; set; }
    public bool? DryRun { get; set; }
}

/// <summary>multipart/form-data body of PUT /api/workers/{id}/photo.</summary>
public sealed class WorkerPhotoForm
{
    public IFormFile? Photo { get; set; }
}

/// <summary>
/// Departments and production floors of a factory: the reference data worker
/// records point at. Listing is open to everyone who works with workers or
/// lines in the factory; editing needs <c>departments.manage</c> (departments)
/// or <c>lines.manage</c> (floors).
/// </summary>
[ApiController]
[Route("api/factories/{factoryId}")]
public class OrganizationController : ControllerBase
{
    private readonly IOrganizationService _org;
    public OrganizationController(IOrganizationService org) { _org = org; }

    [HttpGet("departments")]
    [RequirePermission(Permissions.WorkersView, Permissions.DepartmentsManage, Permissions.LinesView)]
    public async Task<IActionResult> Departments([FromRoute] string factoryId, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid) ? AccessResults.From(this, await _org.DepartmentsAsync(this.Caller(), fid, ct)) : NoFactory();

    [HttpPost("departments")]
    [RequirePermission(Permissions.DepartmentsManage)]
    public async Task<IActionResult> CreateDepartment([FromRoute] string factoryId, [FromBody] CreateOrgUnitRequest body, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid)
            ? AccessResults.From(this, await _org.CreateDepartmentAsync(this.Caller(), fid, body, AccessResults.TraceId(this), ct), d => $"/api/factories/{d.FactoryId}/departments")
            : NoFactory();

    [HttpPut("departments/{departmentId}")]
    [RequirePermission(Permissions.DepartmentsManage)]
    public async Task<IActionResult> UpdateDepartment([FromRoute] string factoryId, [FromRoute] string departmentId, [FromBody] UpdateOrgUnitRequest body, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid) && AccessResults.TryId(departmentId, out var did)
            ? AccessResults.From(this, await _org.UpdateDepartmentAsync(this.Caller(), fid, did, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "department_not_found", "Department not found.");

    [HttpGet("floors")]
    [RequirePermission(Permissions.WorkersView, Permissions.LinesView)]
    public async Task<IActionResult> Floors([FromRoute] string factoryId, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid) ? AccessResults.From(this, await _org.FloorsAsync(this.Caller(), fid, ct)) : NoFactory();

    [HttpPost("floors")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> CreateFloor([FromRoute] string factoryId, [FromBody] CreateOrgUnitRequest body, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid)
            ? AccessResults.From(this, await _org.CreateFloorAsync(this.Caller(), fid, body, AccessResults.TraceId(this), ct), d => $"/api/factories/{d.FactoryId}/floors")
            : NoFactory();

    [HttpPut("floors/{floorId}")]
    [RequirePermission(Permissions.LinesManage)]
    public async Task<IActionResult> UpdateFloor([FromRoute] string factoryId, [FromRoute] string floorId, [FromBody] UpdateOrgUnitRequest body, CancellationToken ct)
        => AccessResults.TryId(factoryId, out var fid) && AccessResults.TryId(floorId, out var id)
            ? AccessResults.From(this, await _org.UpdateFloorAsync(this.Caller(), fid, id, body, AccessResults.TraceId(this), ct))
            : AccessResults.Problem(this, 404, "Not found", "floor_not_found", "Production floor not found.");

    private IActionResult NoFactory() => AccessResults.Problem(this, 404, "Not found", "factory_not_found", "Factory not found.");
}
