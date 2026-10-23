namespace FactoryBrain.Domain.Entities;

/// <summary>
/// Authentication principal for <c>POST /api/auth/login</c>,
/// <c>POST /api/auth/refresh</c>, and the <c>AdminOrLegacyToken</c>
/// authorization policy. The hash is <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>
/// output (PBKDF2 + salt + format marker); the refresh-token hash is
/// <c>SHA-256(raw 32 random bytes)</c> base64url. Email is stored
/// lowercased so login can do a case-insensitive lookup without ILIKE.
/// </summary>
public class User
{
    public Guid     Id                    { get; set; } = Guid.NewGuid();
    public string   Email                 { get; set; } = default!;
    public string   PasswordHash          { get; set; } = default!;
    /// <summary>"Admin" or "Viewer". Compared case-sensitively by the policy handler.</summary>
    public string   Role                  { get; set; } = "Viewer";
    public DateTimeOffset CreatedAt       { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Base64url SHA-256 of the currently-active refresh token. Null when no refresh is active.</summary>
    public string?  RefreshTokenHash      { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
}
