using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactoryBrain.Application.Settings;

/// <summary>
/// A decrypted API key. <see cref="ToString"/> never returns the value and the
/// JSON converter refuses to serialise it, so a key can't reach a log line,
/// an exception message or a response body by accident (including through a
/// record's generated <c>ToString</c>). Call <see cref="Reveal"/> only at the
/// point where the key is put on an outbound request.
/// </summary>
[JsonConverter(typeof(SecretValueJsonConverter))]
public sealed class SecretValue : IEquatable<SecretValue>
{
    public const string Redacted = "[redacted]";

    private readonly string _value;

    public SecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A secret value cannot be empty.", nameof(value));
        _value = value.Trim();
    }

    /// <summary>Wrap a possibly empty string; null when there is nothing to wrap.</summary>
    public static SecretValue? From(string? value) => string.IsNullOrWhiteSpace(value) ? null : new SecretValue(value);

    public string Reveal() => _value;

    public override string ToString() => Redacted;

    public bool Equals(SecretValue? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is SecretValue s && Equals(s);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);
}

internal sealed class SecretValueJsonConverter : JsonConverter<SecretValue>
{
    public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("Secret values are never deserialised from JSON.");

    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options)
        => throw new JsonException("Secret values are never serialised to JSON.");
}
