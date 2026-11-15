using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Dtos.Monitoring;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>Ingests RFID reads (readers, integrations, the simulator) and starts their evaluation.</summary>
public interface IRfidEventProcessor
{
    Task<AccessResult<RfidReadResult>> IngestAsync(AccessContext caller, RfidReadRequest request, string source, CancellationToken ct);
}

/// <summary>Reacts to camera track observations (monitoring events) that matter for identity: zone entries and lost tracks.</summary>
public interface ITrackObservationProcessor
{
    Task OnVisionEventAsync(MonitoringEventDto e, CancellationToken ct);
}

/// <summary>Evaluates reads, keeps associations valid, and answers identity queries.</summary>
public interface IIdentityAssociationService
{
    Task<IReadOnlyList<AssociationDto>> EvaluateReadAsync(long readId, bool final, CancellationToken ct);
    /// <summary>Expire elapsed / lost / reassigned associations and close pending reads whose window passed. Returns how many changed.</summary>
    Task<int> MaintainAsync(CancellationToken ct);
    Task<IdentityOverviewDto> OverviewAsync(AccessContext caller, Guid? factoryId, string? lineId, CancellationToken ct);
    Task<IReadOnlyList<TrackIdentityDto>> TracksAsync(AccessContext caller, Guid cameraId, CancellationToken ct);
    Task<PagedResult<AssociationDto>> ListAsync(AccessContext caller, AssociationQuery query, CancellationToken ct);
    Task<AccessResult<AssociationDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<PagedResult<RfidReadDto>> ReadsAsync(AccessContext caller, Guid? factoryId, Guid? deviceId, int page, int pageSize, CancellationToken ct);
}

/// <summary>Reader location mappings (device administrators).</summary>
public interface IRfidReaderService
{
    Task<IReadOnlyList<RfidReaderDto>> ReadersAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
    Task<AccessResult<RfidReaderDto>> SetMappingAsync(AccessContext caller, Guid deviceId, ReaderMappingRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<IReadOnlyList<ReaderMappingDto>>> MappingHistoryAsync(AccessContext caller, Guid deviceId, CancellationToken ct);
}

/// <summary>Manual identity decisions, recorded beside the engine's own.</summary>
public interface IAssociationReviewService
{
    Task<AccessResult<AssociationDetailDto>> ReviewAsync(AccessContext caller, Guid id, AssociationReviewRequest request, string? traceId, CancellationToken ct);
}

/// <summary>Development-only RFID scenarios through the real pipeline.</summary>
public interface IRfidSimulatorService
{
    Task<AccessResult<RfidSimulationResult>> RunAsync(AccessContext caller, RfidSimulationRequest request, CancellationToken ct);
}

/// <summary>Pushes association changes to the monitoring groups allowed to see them.</summary>
public interface IIdentityPublisher
{
    Task AssociationChangedAsync(AssociationDto association, string change, CancellationToken ct);
}

public sealed class NullIdentityPublisher : IIdentityPublisher
{
    public Task AssociationChangedAsync(AssociationDto association, string change, CancellationToken ct) => Task.CompletedTask;
}
