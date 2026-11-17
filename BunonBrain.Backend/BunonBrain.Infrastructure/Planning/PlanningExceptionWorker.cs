using BunonBrain.Application.Iot;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Runs the production exception check every few minutes for factories that have a published
/// plan for today or yesterday, so delays and data gaps surface without anyone opening the page.
/// It reads the same records as the manual check, writes only exceptions (one per detection key),
/// and checks REAL data only. A factory with no published plan costs a single query.
/// </summary>
public sealed class PlanningExceptionWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PlanningExceptionWorker> _log;

    public PlanningExceptionWorker(IServiceScopeFactory scopes, ILogger<PlanningExceptionWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API finish starting (migrations) before the first check.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "The production exception check failed; retrying in {Interval}.", Interval); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        var factories = await db.DailyProductionPlans.AsNoTracking().Where(p => p.Status == "PUBLISHED" && p.PlanDate >= since).Select(p => p.FactoryId).Distinct().ToListAsync(ct);
        foreach (var factoryId in factories)
        {
            // A fresh scope per factory keeps one factory's failure and tracked entities away from the next.
            await using var inner = _scopes.CreateAsyncScope();
            var svc = inner.ServiceProvider.GetRequiredService<ExceptionService>();
            try { await svc.EvaluateFactoryAsync(factoryId, IotSourceTypes.Real, DateTimeOffset.UtcNow, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "The production exception check failed for factory {FactoryId}.", factoryId); }
        }
    }
}
