using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.FloorAlerts;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

public sealed class FloorAlertService : IFloorAlertService
{
    private readonly FactoryBrainDbContext _db;
    private readonly IRealtimeNotifier _notifier;
    public FloorAlertService(FactoryBrainDbContext db, IRealtimeNotifier notifier)
    {
        _db = db;
        _notifier = notifier;
    }

    public async Task<FloorAlertListResponse> ListAsync(CancellationToken ct)
    {
        var rows = await _db.FloorAlerts.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return new FloorAlertListResponse(
            Simulated: true,
            Source: "Simulated — derived from src/data/floorAlerts.store.ts (mirror)",
            Count: rows.Count,
            Alerts: rows.Select(Map).ToList()
        );
    }

    public async Task<FloorAlertPatchResponse?> MarkReadAsync(string id, bool read, CancellationToken ct)
    {
        var entity = await _db.FloorAlerts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return null;
        entity.Read = read;
        await _db.SaveChangesAsync(ct);
        return new FloorAlertPatchResponse(true, Map(entity));
    }

    public async Task<FloorAlert> PushAsync(FloorAlert alert, CancellationToken ct)
    {
        var existing = await _db.FloorAlerts.FirstOrDefaultAsync(a => a.Id == alert.Id, ct);
        if (existing is null) await _db.FloorAlerts.AddAsync(alert, ct);
        else _db.Entry(existing).CurrentValues.SetValues(alert);
        await _db.SaveChangesAsync(ct);

        // Realtime broadcast — fires on both insert and upsert so the
        // supervisor page reflects every persisted alert. Best-effort:
        // the notifier swallows its own errors.
        await _notifier.FloorAlertAsync(alert, ct);

        return alert;
    }

    private static FloorAlertDto Map(FloorAlert a) => new(
        a.Id, a.Channel, a.ApprovalId, a.InsightId, a.BodyEn, a.BodyBn,
        a.Severity.ToString().ToLowerInvariant(),
        a.CreatedAt, a.Read);
}
