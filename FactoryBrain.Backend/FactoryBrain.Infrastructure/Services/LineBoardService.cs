using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.LineBoard;
using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

public sealed class LineBoardService : ILineBoardService
{
    private readonly FactoryBrainDbContext _db;
    private readonly ISensorService _sensors;
    private readonly IRealtimeNotifier _notifier;
    private static readonly Random _rng = new(0xBEEF);

    public LineBoardService(FactoryBrainDbContext db, ISensorService sensors, IRealtimeNotifier notifier)
    {
        _db = db; _sensors = sensors; _notifier = notifier;
    }

    public async Task<LineBoardResponse> BuildAsync(CancellationToken ct)
    {
        var rows = await _db.LineBoardMetrics.AsNoTracking()
            .OrderBy(x => x.Id).ToListAsync(ct);
        var state = await Task.Run(() => _sensors.CurrentState, ct);

        var mapped = rows.Select(r => new LineBoardRow(
            r.Id,
            r.Name,
            r.EfficiencyPct,
            r.SahTarget,
            r.SahActual,
            r.WipBundles,
            r.Bottleneck.ToString().ToLowerInvariant(),
            r.NptMinutes,
            r.UpdatedAt
        )).ToList();

        var live = _sensors.LiveConnected;
        return new LineBoardResponse(mapped, new LineBoardMeta(
            Simulated: !live,
            Source: live
                ? "Live — line efficiency updated from POST /api/sensors/live"
                : "Simulated — derived from FactoryBrain.Infrastructure/Services/SensorService sim buffer (no live PLC/Modbus/MQTT traffic)",
            Tick: state.Tick,
            UpdatedAt: DateTime.UtcNow,
            Notes: "Deterministic per-line seed + latest server sim state. NPT grows with efficiency gap; WIP drifts on tick."
        ));
    }

    public async Task<LineBoardResponse> RefreshAsync(int? tickOverride, CancellationToken ct)
    {
        if (tickOverride.HasValue) await _sensors.IngestAsync(new IngestRequest(tickOverride), ct);
        else await _sensors.IngestAsync(new IngestRequest(null), ct);
        var response = await BuildAsync(ct);

        // Realtime broadcast — best-effort, notifier swallows its own errors.
        await _notifier.LineBoardUpdatedAsync(response, ct);

        return response;
    }
}
