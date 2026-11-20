namespace BunonBrain.Domain.Entities;

// Interactive demo (DEMO_MODE=true only). A demo deployment runs the same
// code against its own database; these two tables stay empty everywhere else.

/// <summary>
/// The marker that makes a database a demo database. It is written only when
/// a demo deployment starts against a database with no user accounts, and the
/// daily reset refuses to run without it, so a production database (which has
/// accounts) can never be claimed or wiped by a mis-configured demo.
/// Exactly one row (<see cref="Id"/> = 1).
/// </summary>
public class DemoEnvironment
{
    public int Id { get; set; } = 1;
    public DateTimeOffset ClaimedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Version of the baseline dataset last seeded (see DemoSeeder.BaselineVersion).</summary>
    public int BaselineVersion { get; set; }

    /// <summary>"ready" when the baseline is in place, "resetting" while it is rebuilt.</summary>
    public string Status { get; set; } = "resetting";

    public DateTimeOffset? LastResetAt { get; set; }
    public DateTimeOffset? NextResetAt { get; set; }
}

/// <summary>
/// One reviewer's sign-in with the shared demo credential. The session owns
/// its refresh token (users share demo identities, so the per-user refresh
/// slot cannot be used) and remembers which demo identity is active; access
/// tokens carry the session id and are refused once it is revoked or expired.
/// </summary>
public class DemoSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Base64url SHA-256 of the current refresh token (rotated on every refresh and role switch).</summary>
    public string RefreshTokenHash { get; set; } = default!;

    /// <summary>The demo identity (a user row) the reviewer is acting as; null until a role is chosen.</summary>
    public Guid? CurrentUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Absolute end of the session; refresh never extends it.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public int RoleSwitches { get; set; }
}
