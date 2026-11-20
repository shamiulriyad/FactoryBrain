using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Demo;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BunonBrain.Infrastructure.Demo;

/// <summary>
/// Owns the demo database's life cycle: claiming an empty database as a demo
/// database (<see cref="EnsureClaimedAsync"/>) and rebuilding it to the
/// baseline (<see cref="ResetAsync"/>). Both refuse to touch a database that
/// is not marked as a demo database, so a demo deployment pointed at a real
/// database by mistake stops at startup instead of changing anything.
/// </summary>
public sealed class DemoDatabase : IDemoResetService
{
    public const string StatusReady = "ready";
    /// <summary>The database is about to be emptied (an interruption here means: empty it again).</summary>
    public const string StatusResetting = "resetting";
    /// <summary>Emptied; the baseline is being written (an interruption here means: continue where it stopped).</summary>
    public const string StatusSeeding = "seeding";
    private const string MarkerTable = "demo_environment";

    /// <summary>pg advisory-lock key, so only one instance resets at a time.</summary>
    private const long ResetLockKey = 0x426F6E6F_44656D6F; // "BonoDemo"

    private readonly IServiceProvider _root;
    private readonly DemoOptions _opts;
    private readonly ISessionValidator _sessions;
    private readonly ILogger<DemoDatabase> _log;

    public DemoDatabase(IServiceProvider root, DemoOptions opts, ISessionValidator sessions, ILogger<DemoDatabase> log)
    {
        _root = root; _opts = opts; _sessions = sessions; _log = log;
    }

    /// <summary>
    /// Startup check in demo mode (after migrations). A database that already
    /// carries the marker is fine. One with no user accounts is claimed. Any
    /// other database is refused: it holds real accounts and must never be
    /// seeded or reset by a demo deployment.
    /// </summary>
    public static async Task EnsureClaimedAsync(BunonBrainDbContext db, ILogger log, CancellationToken ct)
    {
        if (await db.DemoEnvironment.AnyAsync(ct)) return;
        var accounts = await db.Users.CountAsync(ct);
        if (accounts > 0)
            throw new InvalidOperationException(
                $"DEMO_MODE=true, but this database is not a demo database: it has {accounts} user account(s) and no demo marker. " +
                "Point BUNONBRAIN_DB at a new, empty database for the demo. Nothing was changed.");
        db.DemoEnvironment.Add(new DemoEnvironment { Id = 1, Status = StatusResetting, ClaimedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        log.LogWarning("Claimed this empty database as the interactive demo database; it will be seeded and reset daily.");
    }

    public async Task<bool> ResetAsync(string reason, CancellationToken ct, bool resume = false)
    {
        if (!_opts.Enabled) return false;
        // Everything below runs on the demo database, never on production.
        using var route = DemoRoute.Enter();
        // One connection held open for the whole reset: it owns the advisory lock and runs the
        // truncation. It is the DbContext's own connection, opened by EF (a connection string read
        // back from an opened connection no longer carries the password).
        await using var lockScope = _root.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        if (!await lockDb.DemoEnvironment.AnyAsync(ct))
            throw new InvalidOperationException("Refusing to reset: this database has no demo marker.");
        await lockDb.Database.OpenConnectionAsync(ct);
        var lockConn = (NpgsqlConnection)lockDb.Database.GetDbConnection();
        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", lockConn))
        {
            cmd.Parameters.AddWithValue("k", ResetLockKey);
            if (await cmd.ExecuteScalarAsync(ct) is not true)
            {
                _log.LogInformation("Demo reset skipped: another instance is resetting.");
                return false;
            }
        }

        var started = DateTimeOffset.UtcNow;
        try
        {
            _log.LogWarning("Demo {Kind} started ({Reason}).", resume ? "seed (resumed)" : "reset", reason);
            if (!resume)
            {
                await SetStatusAsync(StatusResetting, null, ct);
                await TruncateAsync(lockConn, ct);
            }
            await SetStatusAsync(StatusSeeding, null, ct);
            await DbInitializer.SeedAsync(_root, ct, seedAccounts: false);
            await DemoSeeder.SeedAsync(_root, _opts, _log, DateTimeOffset.UtcNow, ct);
            await SetStatusAsync(StatusReady, started, ct);
            foreach (var identity in DemoIdentities.All) _sessions.Invalidate(identity.UserId);
            _log.LogWarning("Demo reset finished in {Seconds:0} s.", (DateTimeOffset.UtcNow - started).TotalSeconds);
            return true;
        }
        finally
        {
            await using (var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", lockConn))
            {
                unlock.Parameters.AddWithValue("k", ResetLockKey);
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
            await lockDb.Database.CloseConnectionAsync();
        }
    }

    private async Task SetStatusAsync(string status, DateTimeOffset? resetAt, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var env = await db.DemoEnvironment.FirstAsync(ct);
        env.Status = status;
        // A seed belongs to its day: it may be resumed until the next reset time, not after.
        if (status == StatusSeeding) env.NextResetAt = _opts.NextResetAfter(DateTimeOffset.UtcNow);
        if (resetAt is { } at)
        {
            env.LastResetAt = at;
            env.BaselineVersion = DemoSeeder.BaselineVersion;
            env.NextResetAt = _opts.NextResetAfter(DateTimeOffset.UtcNow);
            db.AuditLog.Add(new AuditLogEntry
            {
                OccurredAt = DateTime.UtcNow, ActorEmail = "system:demo-reset", ActorRole = "System", Action = "demo.reset",
                EntityType = MarkerTable, EntityId = "1", FromState = StatusResetting, ToState = StatusReady,
                Reason = $"demo data restored to baseline v{DemoSeeder.BaselineVersion}; every demo session ended",
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Empty every application table except the marker, in one transaction.
    /// Append-only tables refuse TRUNCATE through their own triggers, so user
    /// triggers are switched off for the statement and back on before commit.
    /// Sequences (worker numbers, alert numbers) start again from 1.
    /// </summary>
    private async Task TruncateAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        List<string> tables;
        List<string> sequences;
        await using (var scope = _root.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
            tables = db.Model.GetEntityTypes()
                .Where(e => e.GetTableName() is not null && e.GetViewName() is null)
                .Select(e => Quote(e.GetSchema(), e.GetTableName()!))
                .Where(t => t != Quote(null, MarkerTable))
                .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
            sequences = db.Model.GetSequences().Select(s => Quote(s.Schema, s.Name)).ToList();
        }
        if (tables.Count == 0) return;

        await using var tx = await conn.BeginTransactionAsync(ct);
        async Task Exec(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in tables) await Exec($"ALTER TABLE {t} DISABLE TRIGGER USER");
        await Exec($"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE");
        foreach (var t in tables) await Exec($"ALTER TABLE {t} ENABLE TRIGGER USER");
        foreach (var s in sequences) await Exec($"ALTER SEQUENCE {s} RESTART WITH 1");
        await tx.CommitAsync(ct);
        _log.LogInformation("Demo reset emptied {Tables} tables.", tables.Count);
    }

    private static string Quote(string? schema, string name) =>
        (schema is null ? "" : $"\"{schema.Replace("\"", "\"\"")}\".") + $"\"{name.Replace("\"", "\"\"")}\"";
}

/// <summary>
/// Demo mode background work, every minute: the first baseline (and a new one
/// after a dataset change), the daily reset at <see cref="DemoOptions.ResetTime"/>,
/// today's live data (gate punches, hourly output, the monitoring simulation)
/// and the clean-up of long-ended sessions. A failed reset is retried after
/// ten minutes; meanwhile sign-in answers "being prepared".
/// </summary>
public sealed class DemoResetWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LiveEvery = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _root;
    private readonly DemoOptions _opts;
    private readonly IDemoResetService _reset;
    private readonly ILogger<DemoResetWorker> _log;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLive = DateTimeOffset.MinValue;

    public DemoResetWorker(IServiceProvider root, DemoOptions opts, IDemoResetService reset, ILogger<DemoResetWorker> log)
    {
        _root = root; _opts = opts; _reset = reset; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled) return;
        // This worker only ever touches the demo database.
        using var route = DemoRoute.Enter();
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        var nextMaintenance = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextMaintenance)
            {
                nextMaintenance = DateTimeOffset.UtcNow + Tick;
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Demo maintenance failed; retrying in {Minutes} minutes.", RetryAfterFailure.TotalMinutes);
                    _nextAttempt = DateTimeOffset.UtcNow + RetryAfterFailure;
                }
            }
            // The production workers run on the production database; the demo's own
            // monitoring simulations advance here.
            try { await TickSimulationsAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Demo simulation tick failed.");
            }
            await Task.Delay(SimulationTick, stoppingToken);
        }
    }

    private static readonly TimeSpan SimulationTick = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan KeepAwakeEvery = TimeSpan.FromMinutes(4);

    /// <summary>
    /// While the baseline is built, request this deployment's own /health every few minutes so a
    /// free instance (asleep after 15 idle minutes) keeps running until the demo is ready.
    /// </summary>
    private IDisposable KeepAwake(CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (_opts.KeepAwakeUrl is { } url)
        {
            var http = _root.GetRequiredService<IHttpClientFactory>().CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(KeepAwakeEvery, cts.Token);
                        using var res = await http.GetAsync(url + "/health", cts.Token);
                        _log.LogInformation("Demo seed in progress; keep-awake ping {Status}.", (int)res.StatusCode);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _log.LogDebug("Keep-awake ping failed: {Error}", ex.GetType().Name); }
                }
            }, CancellationToken.None);
        }
        return cts;
    }

    private async Task TickSimulationsAsync(CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        if (!await db.DemoEnvironment.AnyAsync(e => e.Status == DemoDatabase.StatusReady, ct)) return;
        await scope.ServiceProvider.GetRequiredService<IMonitoringSimulatorService>().TickAsync(DateTimeOffset.UtcNow, ct);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        DemoEnvironment? env;
        await using (var scope = _root.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
            env = await db.DemoEnvironment.AsNoTracking().FirstOrDefaultAsync(ct);
            // Sessions that ended over a day ago are no longer useful (their audit entries stay).
            var cutoff = now.AddDays(-1);
            await db.DemoSessions.Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff)).ExecuteDeleteAsync(ct);
        }
        if (env is null) return;

        // An interrupted seed (instance restarted or put to sleep) continues where it stopped;
        // anything else that is due empties the database and builds the baseline again.
        // Only on the day it started: past the next reset time the dates would no longer match.
        var resume = env.Status == DemoDatabase.StatusSeeding && env.NextResetAt is { } seedUntil && seedUntil > now;
        var due = env.Status != DemoDatabase.StatusReady
                  || env.BaselineVersion < DemoSeeder.BaselineVersion
                  || env.NextResetAt is null || env.NextResetAt <= now;
        if (due)
        {
            if (now < _nextAttempt) return;
            using var awake = KeepAwake(ct);
            await _reset.ResetAsync(resume ? "continuing an interrupted seed" : env.Status != DemoDatabase.StatusReady ? "baseline not in place" : "scheduled daily reset",
                ct, resume);
            _nextLive = DateTimeOffset.UtcNow + LiveEvery;
            return;
        }

        if (now >= _nextLive)
        {
            _nextLive = now + LiveEvery;
            await DemoSeeder.CatchUpTodayAsync(_root, _opts, _log, now, ct);
            // Close sessions with a missing check-out, as the production review worker does there.
            await using var scope = _root.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAttendanceService>().ReviewAsync(null, ct);
        }
    }
}
