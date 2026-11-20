using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;

namespace BunonBrain.Application.Demo;

/// <summary>
/// Interactive demo settings, read once at startup from the environment.
/// The demo runs inside the normal deployment (same website, same API) on its
/// OWN database: requests that belong to a demo session are routed to
/// <c>DEMO_BUNONBRAIN_DB</c> (see <see cref="DemoRoute"/>), everything else
/// stays on the production database.
/// <list type="bullet">
///   <item><c>DEMO_MODE</c> — true to offer the interactive demo (default false).</item>
///   <item><c>DEMO_BUNONBRAIN_DB</c> — the demo database (required when on; must not be the production database).</item>
///   <item><c>DEMO_LOGIN_EMAIL</c> / <c>DEMO_LOGIN_PASSWORD</c> — the one credential reviewers use (required when on; password ≥ 8 chars).</item>
///   <item><c>DEMO_SESSION_HOURS</c> — absolute session lifetime, 1–24 (default 4).</item>
///   <item><c>DEMO_MAX_SESSIONS</c> — active reviewer sessions at once, 1–5000 (default 300).</item>
///   <item><c>DEMO_RESET_TIME</c> — local time of the daily reset, HH:mm (default 03:00).</item>
///   <item><c>DEMO_TIME_ZONE</c> — zone of the reset time and the demo dataset's dates (default Asia/Dhaka).</item>
///   <item><c>DEMO_ADMIN_KEY</c> — optional operator key (≥ 24 chars) for listing / revoking sessions and forcing a reset.</item>
/// </list>
/// </summary>
public sealed class DemoOptions
{
    public const int MinPasswordLength = 8;
    public const int MinAdminKeyLength = 24;

    public bool Enabled { get; init; }
    /// <summary>Npgsql connection string of the demo database.</summary>
    public string ConnectionString { get; init; } = "";
    public string LoginEmail { get; init; } = "";
    public string LoginPassword { get; init; } = "";
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(4);
    public int MaxActiveSessions { get; init; } = 300;
    public TimeOnly ResetTime { get; init; } = new(3, 0);
    public TimeZoneInfo TimeZone { get; init; } = AttendanceRules.ResolveTimeZone("Asia/Dhaka")!;
    public string? AdminKey { get; init; }

    /// <summary>
    /// This deployment's public URL (Render sets RENDER_EXTERNAL_URL; DEMO_KEEP_AWAKE_URL overrides it).
    /// While the demo data is being built the API requests its own /health every few minutes, so a
    /// free instance does not go to sleep half-way through.
    /// </summary>
    public string? KeepAwakeUrl { get; init; }

    /// <summary>Why the demo can't run with this configuration; null when it can (or is off).</summary>
    public string? Problem { get; init; }

    public static DemoOptions Disabled { get; } = new();

    public static DemoOptions FromEnvironment(Func<string, string?> env)
    {
        if (!bool.TryParse(env("DEMO_MODE")?.Trim(), out var on) || !on) return Disabled;

        var problems = new List<string>();
        var connection = (env("DEMO_BUNONBRAIN_DB") ?? "").Trim();
        if (connection.Length == 0) problems.Add("DEMO_BUNONBRAIN_DB (the demo database) must be set");
        var email = (env("DEMO_LOGIN_EMAIL") ?? "").Trim().ToLowerInvariant();
        var password = env("DEMO_LOGIN_PASSWORD") ?? "";
        if (email.Length == 0 || !email.Contains('@')) problems.Add("DEMO_LOGIN_EMAIL must be an email address");
        if (password.Length < MinPasswordLength) problems.Add($"DEMO_LOGIN_PASSWORD must be at least {MinPasswordLength} characters");

        var hours = Int("DEMO_SESSION_HOURS", 4, 1, 24);
        var max = Int("DEMO_MAX_SESSIONS", 300, 1, 5000);

        var resetRaw = env("DEMO_RESET_TIME")?.Trim();
        var reset = new TimeOnly(3, 0);
        if (!string.IsNullOrEmpty(resetRaw) && !TimeOnly.TryParseExact(resetRaw, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out reset))
            problems.Add("DEMO_RESET_TIME must be HH:mm");

        var zoneId = env("DEMO_TIME_ZONE")?.Trim();
        var zone = AttendanceRules.ResolveTimeZone(string.IsNullOrEmpty(zoneId) ? "Asia/Dhaka" : zoneId);
        if (zone is null) problems.Add($"DEMO_TIME_ZONE '{zoneId}' is not a known time zone");

        var adminKey = env("DEMO_ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey)) adminKey = null;
        else if (adminKey.Length < MinAdminKeyLength) problems.Add($"DEMO_ADMIN_KEY must be at least {MinAdminKeyLength} characters (or unset)");

        return new DemoOptions
        {
            Enabled = true,
            ConnectionString = connection,
            LoginEmail = email,
            LoginPassword = password,
            SessionLifetime = TimeSpan.FromHours(hours),
            MaxActiveSessions = max,
            ResetTime = reset,
            TimeZone = zone ?? AttendanceRules.ResolveTimeZone("Asia/Dhaka")!,
            AdminKey = adminKey,
            KeepAwakeUrl = (env("DEMO_KEEP_AWAKE_URL") ?? env("RENDER_EXTERNAL_URL"))?.Trim().TrimEnd('/') is { Length: > 0 } url
                && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? url : null,
            Problem = problems.Count == 0 ? null : string.Join("; ", problems),
        };

        int Int(string name, int fallback, int min, int max2)
        {
            var raw = env(name)?.Trim();
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max2) return v;
            problems.Add($"{name} must be a whole number from {min} to {max2}");
            return fallback;
        }
    }

    /// <summary>Constant-time check of the shared demo credential.</summary>
    public bool CredentialMatches(string? email, string? password)
    {
        if (!Enabled) return false;
        var emailOk = FixedEquals((email ?? "").Trim().ToLowerInvariant(), LoginEmail);
        var passwordOk = FixedEquals(password ?? "", LoginPassword);
        return emailOk & passwordOk;
    }

    /// <summary>Constant-time check of the operator key; false when no key is configured.</summary>
    public bool AdminKeyMatches(string? presented) =>
        AdminKey is not null && presented is not null && FixedEquals(presented, AdminKey);

    /// <summary>The first reset moment strictly after <paramref name="now"/>.</summary>
    public DateTimeOffset NextResetAfter(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZone);
        var candidate = new DateTimeOffset(local.Date + ResetTime.ToTimeSpan(), local.Offset);
        if (candidate <= local) candidate = candidate.AddDays(1);
        // Re-resolve the offset for the candidate day (zones with daylight saving).
        var offset = TimeZone.GetUtcOffset(candidate.DateTime);
        return new DateTimeOffset(candidate.DateTime, offset).ToUniversalTime();
    }

    /// <summary>Today's date in the demo's time zone (the dataset is built around it).</summary>
    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZone).DateTime);

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

/// <summary>
/// Which database the current work belongs to. Set for a demo request by the
/// API's routing middleware (before authentication touches the database) and
/// around the demo's own background work; it flows through <c>await</c>s and
/// into service scopes created along the way. Unset = production.
/// </summary>
public static class DemoRoute
{
    private static readonly AsyncLocal<bool> Demo = new();

    public static bool IsDemo => Demo.Value;

    /// <summary>Route everything awaited from here on to the demo database (until disposed).</summary>
    public static IDisposable Enter()
    {
        var previous = Demo.Value;
        Demo.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly bool _previous;
        public Restore(bool previous) { _previous = previous; }
        public void Dispose() => Demo.Value = _previous;
    }
}

/// <summary>One synthetic demo account standing for one system role.</summary>
public sealed record DemoIdentity(string RoleId, Guid UserId, string Email, string FullName);

/// <summary>
/// The demo accounts: one per role in <see cref="AccessCatalog.AllRoles"/>, so
/// a role added to the catalog appears in the demo without code changes. Ids
/// are derived from the role id, so they are the same after every reset.
/// Reviewers pick a role; the server maps it to the identity here and never
/// accepts a user id from the browser.
/// </summary>
public static class DemoIdentities
{
    public const string EmailDomain = "bunonbrain-demo.example";

    // Display names for the predefined roles. A role without one gets "Demo <role name>".
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [SystemRoles.SuperAdmin] = "Tanvir Ahmed",
        [SystemRoles.FactoryAdmin] = "Nusrat Jahan",
        [SystemRoles.FactoryManager] = "Mahmudul Hasan",
        [SystemRoles.ProductionSupervisor] = "Rafiqul Islam",
        [SystemRoles.MonitoringOperator] = "Sabbir Hossain",
        [SystemRoles.HrOfficer] = "Farzana Akter",
        [SystemRoles.DeviceAdmin] = "Arif Chowdhury",
        [SystemRoles.Worker] = "Rokeya Begum",
        [SystemRoles.DocumentController] = "Shamima Nasrin",
        [SystemRoles.Merchandiser] = "Imran Kabir",
        [SystemRoles.StoreOfficer] = "Abdul Karim",
        [SystemRoles.QcOfficer] = "Sharmin Sultana",
        [SystemRoles.ComplianceOfficer] = "Kamrul Hasan",
        [SystemRoles.CommercialOfficer] = "Ayesha Siddika",
    };

    public static IReadOnlyList<DemoIdentity> All { get; } = AccessCatalog.AllRoles
        .OrderByDescending(r => r.Rank).ThenBy(r => r.Name, StringComparer.Ordinal)
        .Select(r => new DemoIdentity(r.Id, IdFor(r.Id), $"{Slug(r.Id)}@{EmailDomain}",
            Names.TryGetValue(r.Id, out var n) ? n : $"Demo {r.Name}"))
        .ToArray();

    private static readonly Dictionary<string, DemoIdentity> ByRole = All.ToDictionary(i => i.RoleId, StringComparer.Ordinal);
    private static readonly HashSet<Guid> Ids = All.Select(i => i.UserId).ToHashSet();

    public static DemoIdentity? ForRole(string? roleId) =>
        roleId is not null && ByRole.TryGetValue(roleId, out var i) ? i : null;

    public static bool IsDemoIdentity(Guid userId) => Ids.Contains(userId);

    /// <summary>A stable id per role: the first 16 bytes of SHA-256("bunonbrain-demo:" + role).</summary>
    public static Guid IdFor(string roleId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bunonbrain-demo:" + roleId));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version-5 style marker
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    private static string Slug(string roleId)
    {
        var sb = new StringBuilder();
        foreach (var c in roleId)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('.');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
