using BunonBrain.Application.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// Runs the attendance review every few minutes: an open session that is
/// long past its shift (or its maximum length) is closed as MissingCheckOut
/// and flagged for a correction, so no session stays active indefinitely;
/// approved overtime is brought up to date with attendance. Several API
/// instances may run it: every change happens under the worker's row lock.
/// </summary>
public sealed class AttendanceReviewWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AttendanceReviewWorker> _log;

    public AttendanceReviewWorker(IServiceScopeFactory scopes, ILogger<AttendanceReviewWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAttendanceService>().ReviewAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Attendance review failed; retrying in {Interval}.", Interval);
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
