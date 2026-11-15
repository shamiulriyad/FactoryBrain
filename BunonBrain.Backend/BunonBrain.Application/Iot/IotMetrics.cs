using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BunonBrain.Application.Iot;

/// <summary>Time a machine spent in each state over a period, computed from its transitions.</summary>
public sealed record StateTotals(IReadOnlyDictionary<string, double> Seconds, double CoveredSeconds)
{
    public double Of(string state) => Seconds.TryGetValue(state, out var v) ? v : 0;
    public double Running => Of(MachineStates.Running);
    public double Idle => Of(MachineStates.Idle);
    public double Downtime => MachineStates.Downtime.Sum(Of);
}

public static class IotMetrics
{
    /// <summary>
    /// Seconds per state in [from, to). <paramref name="stateAtStart"/> is the
    /// state in force at <paramref name="from"/> (null: no data yet, counted as
    /// UNKNOWN); <paramref name="transitions"/> are the changes after it, in any
    /// order. Time after <paramref name="to"/> (or in the future) is ignored.
    /// </summary>
    public static StateTotals Durations(string? stateAtStart, IEnumerable<(DateTimeOffset At, string State)> transitions, DateTimeOffset from, DateTimeOffset to)
    {
        var totals = MachineStates.All.ToDictionary(s => s, _ => 0.0);
        if (to <= from) return new StateTotals(totals, 0);
        var current = stateAtStart ?? MachineStates.Unknown;
        var cursor = from;
        foreach (var (at, state) in transitions.Where(t => t.At > from && t.At < to).OrderBy(t => t.At))
        {
            totals[current] += (at - cursor).TotalSeconds;
            current = state;
            cursor = at;
        }
        totals[current] += (to - cursor).TotalSeconds;
        return new StateTotals(totals, (to - from).TotalSeconds);
    }

    /// <summary>Pieces per running hour; null when the machine didn't run.</summary>
    public static double? RatePerRunningHour(long pieces, double runningSeconds) =>
        runningSeconds < 60 ? null : Math.Round(pieces / (runningSeconds / 3600.0), 1);

    /// <summary>Pieces per hour of the period.</summary>
    public static double? RatePerHour(long pieces, double seconds) => seconds < 60 ? null : Math.Round(pieces / (seconds / 3600.0), 1);
}

/// <summary>
/// Device keys for HTTP device authentication: <c>dk_</c> + 40 random
/// characters, shown once; only the SHA-256 hash is stored.
/// </summary>
public static class DeviceKeys
{
    public const string Prefix = "dk_";
    public const string Header = "X-Device-Key";
    public const string DeviceHeader = "X-Device-Id";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[30];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Convert.ToBase64String(bytes).Replace('+', 'A').Replace('/', 'b').TrimEnd('=');
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))).ToLowerInvariant();

    public static string Display(string key) => key.Length <= 9 ? key : key[..9] + "…";

    /// <summary>Constant-time comparison of a presented key with a stored hash.</summary>
    public static bool Matches(string? presented, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrEmpty(storedHash) || !presented.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var a = Encoding.ASCII.GetBytes(Hash(presented));
        var b = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Per-device message budget (sliding one-second and one-minute windows), so
/// a faulty sensor or a replay storm can't flood the pipeline. In memory, per
/// API instance.
/// </summary>
public sealed class DeviceRateLimiter
{
    private readonly int _perSecond;
    private readonly int _perMinute;
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _windows = new();

    public DeviceRateLimiter(int perSecond, int perMinute) { _perSecond = Math.Max(1, perSecond); _perMinute = Math.Max(_perSecond, perMinute); }

    public bool TryAcquire(Guid deviceId, DateTimeOffset now)
    {
        var q = _windows.GetOrAdd(deviceId, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= _perMinute) return false;
            var lastSecond = q.Count(t => now - t < TimeSpan.FromSeconds(1));
            if (lastSecond >= _perSecond) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
