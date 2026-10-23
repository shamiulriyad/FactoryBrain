using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// Thin wrapper over
/// <see cref="PasswordHasher{TUser}"/> so the auth pipeline doesn't
/// need to take a dependency on <c>Microsoft.Extensions.Identity</c>
/// from the Application project. The underlying hasher uses PBKDF2
/// (HMAC-SHA256, 100k iterations by default) with a 128-bit salt and a
/// self-describing format marker — output is one string that contains
/// both the salt and the hash.
/// </summary>
public sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password)
        => _hasher.HashPassword(user, password);

    public bool Verify(User user, string hashed, string provided)
        => _hasher.VerifyHashedPassword(user, hashed, provided)
            == PasswordVerificationResult.Success;

    /// <summary>
    /// A pre-computed PBKDF2 hash (same format as <see cref="Hash"/>).
    /// Used by <c>AuthService.LoginAsync</c> to keep the work done for
    /// "unknown email" identical to the work done for "wrong password":
    /// without it, an attacker can enumerate accounts by measuring the
    /// response time. The hash is generated once at class-load from
    /// <see cref="DummyPassword"/> via the same <see cref="PasswordHasher{TUser}"/>
    /// the adapter uses for real users, so the iteration count, salt
    /// size and marker format all match. The plaintext
    /// <see cref="DummyPassword"/> is irrelevant — no caller ever
    /// checks it, the dummy only exists to give
    /// <see cref="PasswordHasher{TUser}.VerifyHashedPassword"/> a real
    /// hash to chew on.
    /// </summary>
    public const string DummyPassword = "factorybrain-login-timing-dummy";

    public static readonly string DummyHash = new PasswordHasher<User>()
        .HashPassword(new User(), DummyPassword);
}
