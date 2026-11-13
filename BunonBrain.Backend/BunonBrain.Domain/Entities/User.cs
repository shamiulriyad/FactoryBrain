namespace BunonBrain.Domain.Entities;

/// <summary>
/// A login account. Authorization comes from <see cref="Roles"/> (system
/// roles → permissions, see <c>AccessCatalog</c>) plus the factory and
/// production-line scope in <see cref="Factories"/> / <see cref="Lines"/>.
/// A login account is not a worker: a <see cref="WorkerProfile"/> may link
/// to at most one account, and most workers have none.
/// <para>
/// The password hash is <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>
/// output (PBKDF2 + salt + format marker); the refresh-token hash is
/// <c>SHA-256(raw 32 random bytes)</c> base64url. Email is stored lowercased.
/// </para>
/// </summary>
public class User
{
    public Guid     Id                    { get; set; } = Guid.NewGuid();
    public string   Email                 { get; set; } = default!;
    public string   PasswordHash          { get; set; } = default!;
    public string?  FullName              { get; set; }
    public string?  Phone                 { get; set; }

    /// <summary>False blocks sign-in and token refresh, and ends active sessions.</summary>
    public bool     IsActive              { get; set; } = true;
    public DateTimeOffset? DeactivatedAt  { get; set; }

    /// <summary>Set when an administrator chose the password; cleared when the user changes it.</summary>
    public bool     MustChangePassword    { get; set; }

    /// <summary>
    /// Random value rotated whenever roles, scope, status or password change.
    /// Access tokens carry it; a token with an old stamp is rejected.
    /// </summary>
    public string   SecurityStamp         { get; set; } = NewStamp();

    /// <summary>
    /// Role from the pre-RBAC model (Admin / Operator / Viewer). Mapped to
    /// system roles once at startup, then cleared. Never used for authorization.
    /// </summary>
    public string?  LegacyRole            { get; set; }

    public DateTimeOffset CreatedAt       { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt      { get; set; }
    public DateTimeOffset? LastLoginAt    { get; set; }
    public Guid?    CreatedByUserId       { get; set; }

    /// <summary>Base64url SHA-256 of the currently-active refresh token. Null when no refresh is active.</summary>
    public string?  RefreshTokenHash      { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }

    public List<UserRole> Roles { get; set; } = new();
    public List<UserFactoryAssignment> Factories { get; set; } = new();
    public List<UserProductionLineAssignment> Lines { get; set; } = new();

    public static string NewStamp() => Guid.NewGuid().ToString("N");
}
