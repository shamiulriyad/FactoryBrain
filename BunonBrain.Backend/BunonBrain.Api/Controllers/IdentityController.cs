using BunonBrain.Api.Authorization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

/// <summary>
/// Verified worker identity association: RFID read ingestion, reader location
/// mappings, the association timeline, current track identities, manual
/// review and the development RFID simulator. A camera track gets a worker
/// only from independent evidence (card + reader location + assignment +
/// one track in the workstation zone); never from appearance. Raw card UIDs
/// are never returned.
/// </summary>
[ApiController]
[Route("api/identity")]
public class IdentityController : ControllerBase
{
    private readonly IRfidEventProcessor _reads;
    private readonly IIdentityAssociationService _identity;
    private readonly IRfidReaderService _readers;
    private readonly IAssociationReviewService _review;
    private readonly IRfidSimulatorService _sim;

    public IdentityController(IRfidEventProcessor reads, IIdentityAssociationService identity, IRfidReaderService readers, IAssociationReviewService review,
        IRfidSimulatorService sim)
    {
        _reads = reads; _identity = identity; _readers = readers; _review = review; _sim = sim;
    }

    /// <summary>
    /// POST /api/identity/rfid-reads — a read from a real reader (its gateway or an integration account with
    /// <c>devices.manage</c>). 201 stored (outcome Accepted or Rejected); 200 Duplicate for a repeated idempotency key.
    /// </summary>
    [HttpPost("rfid-reads")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> Ingest([FromBody] RfidReadRequest body, CancellationToken ct)
    {
        var r = await _reads.IngestAsync(this.Caller(), body, RfidReadSources.Api, ct);
        if (r.Outcome == AccessOutcome.Created) return new CreatedResult($"/api/identity/reads/{r.Value!.Read.Id}", r.Value);
        return AccessResults.From(this, r);
    }

    /// <summary>GET /api/identity/reads?factoryId&amp;deviceId&amp;page&amp;pageSize — recent reads (cards masked).</summary>
    [HttpGet("reads")]
    [RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
    public async Task<IActionResult> Reads([FromQuery] Guid? factoryId, [FromQuery] Guid? deviceId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _identity.ReadsAsync(this.Caller(), factoryId, deviceId, page ?? 1, pageSize ?? 50, ct));

    /// <summary>GET /api/identity/readers?factoryId — RFID readers with status, mapping and the cameras covering their workstation.</summary>
    [HttpGet("readers")]
    [RequirePermission(Permissions.DevicesView, Permissions.MonitoringView)]
    public async Task<IActionResult> Readers([FromQuery] Guid? factoryId, CancellationToken ct) => Ok(await _readers.ReadersAsync(this.Caller(), factoryId, ct));

    /// <summary>PUT /api/identity/readers/{deviceId}/mapping — map a reader (closes its current mapping).</summary>
    [HttpPut("readers/{deviceId}/mapping")]
    [RequirePermission(Permissions.DevicesManage)]
    public async Task<IActionResult> SetMapping([FromRoute] string deviceId, [FromBody] ReaderMappingRequest body, CancellationToken ct)
        => Guid.TryParse(deviceId, out var id)
            ? AccessResults.From(this, await _readers.SetMappingAsync(this.Caller(), id, body, AccessResults.TraceId(this), ct)) : NoReader();

    [HttpGet("readers/{deviceId}/mappings")]
    [RequirePermission(Permissions.DevicesView, Permissions.MonitoringView)]
    public async Task<IActionResult> MappingHistory([FromRoute] string deviceId, CancellationToken ct)
        => Guid.TryParse(deviceId, out var id) ? AccessResults.From(this, await _readers.MappingHistoryAsync(this.Caller(), id, ct)) : NoReader();

    /// <summary>GET /api/identity/overview?factoryId&amp;lineId — counts, current tracks, review queue, recent changes, readers.</summary>
    [HttpGet("overview")]
    [RequirePermission(Permissions.MonitoringView)]
    public async Task<IActionResult> Overview([FromQuery] Guid? factoryId, [FromQuery] string? lineId, CancellationToken ct)
        => Ok(await _identity.OverviewAsync(this.Caller(), factoryId, lineId, ct));

    /// <summary>GET /api/identity/tracks?cameraId — the identity of every track the camera currently sees.</summary>
    [HttpGet("tracks")]
    [RequirePermission(Permissions.MonitoringView)]
    public async Task<IActionResult> Tracks([FromQuery] Guid cameraId, CancellationToken ct) => Ok(await _identity.TracksAsync(this.Caller(), cameraId, ct));

    /// <summary>GET /api/identity/associations?factoryId&amp;cameraId&amp;trackId&amp;workerId&amp;status&amp;lineId&amp;reviewOnly&amp;from&amp;to&amp;page&amp;pageSize — timeline / history.</summary>
    [HttpGet("associations")]
    [RequirePermission(Permissions.MonitoringView)]
    public async Task<IActionResult> Associations([FromQuery] Guid? factoryId, [FromQuery] Guid? cameraId, [FromQuery] string? trackId, [FromQuery] Guid? workerId,
        [FromQuery] string? status, [FromQuery] string? lineId, [FromQuery] bool? reviewOnly, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
        => Ok(await _identity.ListAsync(this.Caller(), new AssociationQuery(factoryId, cameraId, trackId, workerId, status, lineId, reviewOnly, from, to,
            page ?? 1, pageSize ?? 50), ct));

    [HttpGet("associations/{id}")]
    [RequirePermission(Permissions.MonitoringView)]
    public async Task<IActionResult> Association([FromRoute] string id, CancellationToken ct)
        => Guid.TryParse(id, out var gid) ? AccessResults.From(this, await _identity.GetAsync(this.Caller(), gid, ct)) : NoAssociation();

    /// <summary>POST /api/identity/associations/{id}/review — CONFIRM (workerId from the candidates), REJECT, AMBIGUOUS or REVOKE, with a reason.</summary>
    [HttpPost("associations/{id}/review")]
    [RequirePermission(Permissions.MonitoringEventsReview)]
    public async Task<IActionResult> Review([FromRoute] string id, [FromBody] AssociationReviewRequest body, CancellationToken ct)
        => Guid.TryParse(id, out var gid)
            ? AccessResults.From(this, await _review.ReviewAsync(this.Caller(), gid, body, AccessResults.TraceId(this), ct)) : NoAssociation();

    private IActionResult NoReader() => AccessResults.Problem(this, 404, "Not found", "reader_not_found", "RFID reader not found.");
    private IActionResult NoAssociation() => AccessResults.Problem(this, 404, "Not found", "association_not_found", "Identity association not found.");

    /// <summary>POST /api/identity/simulator — development scenarios through the real pipeline (simulated readers only; always SIMULATED).</summary>
    [HttpPost("simulator")]
    [RequirePermission(Permissions.DevicesSimulate)]
    public async Task<IActionResult> Simulate([FromBody] RfidSimulationRequest body, CancellationToken ct)
        => AccessResults.From(this, await _sim.RunAsync(this.Caller(), body, ct));
}
