namespace FactoryBrain.Application.Abstractions;

/// <summary>Who made a change, taken from the validated JWT (never from the request body).</summary>
public sealed record AuditActor(Guid? UserId, string Email, string Role);

public enum SettingsWriteOutcome
{
    Ok,
    /// <summary>Validation failed; <see cref="SettingsWriteResult{T}.Errors"/> says why.</summary>
    Invalid,
    /// <summary><c>expectedVersion</c> no longer matches, or a reindex is already running.</summary>
    Conflict,
    /// <summary>An API key was submitted but the server has no encryption key.</summary>
    EncryptionKeyMissing,
    /// <summary>The change re-embeds vectors and no (or a stale) confirmation was given.</summary>
    ConfirmationRequired,
    PlanChanged,
    /// <summary>The provider's output didn't match the requested dimensions.</summary>
    DimensionMismatch,
    /// <summary>The embedding provider failed; nothing was written.</summary>
    ProviderFailed,
}

public sealed record SettingsWriteResult<T>(
    SettingsWriteOutcome Outcome,
    T? Value,
    IReadOnlyDictionary<string, string>? Errors = null,
    object? Detail = null)
{
    public static SettingsWriteResult<T> Ok(T value) => new(SettingsWriteOutcome.Ok, value);
    public static SettingsWriteResult<T> Fail(SettingsWriteOutcome outcome, IReadOnlyDictionary<string, string>? errors = null, object? detail = null)
        => new(outcome, default, errors, detail);
}
