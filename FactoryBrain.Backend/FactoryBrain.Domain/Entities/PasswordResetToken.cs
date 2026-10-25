namespace FactoryBrain.Domain.Entities;

/// <summary>
/// A one-time password reset link. Only the SHA-256 of the random token is
/// stored; the raw token exists only in the email that was sent. Valid until
/// <see cref="ExpiresAt"/> and for a single use (<see cref="UsedAt"/>).
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
