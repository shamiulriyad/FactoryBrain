using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.CameraAi;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Infrastructure.Cameras;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BunonBrain.Infrastructure.CameraAi;

/// <summary>Cameras through the same scope rule as the camera pages (supervisors: cameras on their lines).</summary>
public sealed class DbCameraAiCameraLookup : ICameraAiCameraLookup
{
    private readonly IServiceScopeFactory _scopes;
    public DbCameraAiCameraLookup(IServiceScopeFactory scopes) { _scopes = scopes; }

    public async Task<CameraAiCamera?> FindInScopeAsync(AccessContext caller, Guid cameraId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var cam = await CameraService.InScope(db, caller).Where(c => c.Id == cameraId)
            .Select(c => new { c.Id, c.FactoryId, c.Code, c.Name, c.Status }).FirstOrDefaultAsync(ct);
        if (cam is null) return null;
        var source = await db.CameraSources.AsNoTracking().Where(s => s.CameraId == cameraId && s.RetiredAt == null)
            .Select(s => s.SourceType).FirstOrDefaultAsync(ct);
        return new CameraAiCamera(cam.Id, cam.FactoryId, cam.Code, cam.Name, cam.Status, source);
    }
}

/// <summary>Camera AI events go through the one monitoring pipeline every producer uses (producer CAMERA_AI, system caller).</summary>
public sealed class MonitoringCameraAiEventSink : ICameraAiEventSink
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MonitoringCameraAiEventSink> _log;
    public MonitoringCameraAiEventSink(IServiceScopeFactory scopes, ILogger<MonitoringCameraAiEventSink> log) { _scopes = scopes; _log = log; }

    public async Task<CameraAiEventDto> EmitAsync(MonitoringEventInput input, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IMonitoringEventService>();
        var r = await events.IngestAsync(null, input, MonitoringProducers.CameraAi, ct);
        if (r.Outcome is AccessOutcome.Ok or AccessOutcome.Created && r.Value is { } v)
            return new CameraAiEventDto(v.Event.Id, v.Event.EventType, v.Event.Summary, v.Event.OccurredAt, true, null, v.Alert?.Id);
        _log.LogWarning("Camera AI event {Type} was refused by the monitoring pipeline: {Code} {Message}", input.EventType, r.Code, r.Message);
        return new CameraAiEventDto(null, input.EventType, null, input.OccurredAt ?? DateTimeOffset.UtcNow, false, r.Message ?? r.Code, null);
    }
}

/// <summary>Today's camera AI calls from ai_api_usage (every call is logged there by the vision client).</summary>
public sealed class DbCameraAiUsageStore : ICameraAiUsageStore
{
    private readonly IServiceScopeFactory _scopes;
    public DbCameraAiUsageStore(IServiceScopeFactory scopes) { _scopes = scopes; }

    public async Task<CameraAiUsageToday> TodayAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var since = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var rows = db.AiApiUsage.AsNoTracking().Where(u => u.Purpose == CameraAiContract.Purpose && u.OccurredAt >= since);
        var agg = await rows.GroupBy(_ => 1).Select(g => new
        {
            Calls = g.Count(),
            Failed = g.Count(u => u.Outcome != "ok"),
            In = g.Sum(u => (long?)u.InputTokens) ?? 0,
            Out = g.Sum(u => (long?)u.OutputTokens) ?? 0,
        }).FirstOrDefaultAsync(ct);
        return agg is null ? new CameraAiUsageToday(0, 0, 0, 0) : new CameraAiUsageToday(agg.Calls, agg.Failed, agg.In, agg.Out);
    }
}

/// <summary>Every few seconds: report cameras whose browser stopped sending frames (OFFLINE), end idle or expired sessions.</summary>
public sealed class CameraAiWatchdog : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
    private readonly CameraAiSessionService _sessions;
    private readonly ILogger<CameraAiWatchdog> _log;
    public CameraAiWatchdog(CameraAiSessionService sessions, ILogger<CameraAiWatchdog> log) { _sessions = sessions; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await _sessions.SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Camera AI watchdog pass failed."); }
        }
    }
}
