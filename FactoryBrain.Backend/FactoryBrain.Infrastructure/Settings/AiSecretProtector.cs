using System.Security.Cryptography;
using System.Text;

namespace FactoryBrain.Infrastructure.Settings;

/// <summary>
/// Encrypts provider API keys for storage in <c>ai_settings</c> with
/// AES-256-GCM (authenticated: a wrong key, a tampered blob or a blob moved to
/// another column fails the tag check instead of decrypting to garbage).
/// <para>
/// The key comes from <c>AI_SETTINGS_ENCRYPTION_KEY</c> (base64, 32 bytes) or
/// a file named by <c>AI_SETTINGS_ENCRYPTION_KEY_FILE</c> — never from the
/// database. <c>AI_SETTINGS_ENCRYPTION_KEY_PREVIOUS</c> is accepted for
/// decryption only, so a key can be rotated; the next save re-encrypts with the
/// primary key.
/// </para>
/// Blob layout: <c>0x01 | keyId[4] | nonce[12] | tag[16] | ciphertext</c>.
/// </summary>
public sealed class AiSecretProtector
{
    public const string EnvKey         = "AI_SETTINGS_ENCRYPTION_KEY";
    public const string EnvKeyFile     = "AI_SETTINGS_ENCRYPTION_KEY_FILE";
    public const string EnvPreviousKey = "AI_SETTINGS_ENCRYPTION_KEY_PREVIOUS";

    /// <summary>Associated data per column, so ciphertexts can't be swapped between columns.</summary>
    public const string LlmApiKeyPurpose       = "factorybrain/ai_settings/v1/llm_api_key";
    public const string EmbeddingApiKeyPurpose = "factorybrain/ai_settings/v1/embedding_api_key";

    private const byte FormatVersion = 1;
    private const int KeyIdSize = 4, NonceSize = 12, TagSize = 16;
    private const int HeaderSize = 1 + KeyIdSize + NonceSize + TagSize;

    private readonly byte[]? _primary;
    private readonly byte[]? _primaryId;
    private readonly byte[]? _previous;
    private readonly byte[]? _previousId;

    public AiSecretProtector(byte[]? primaryKey, byte[]? previousKey = null)
    {
        if (primaryKey is not null && primaryKey.Length != 32)
            throw new ArgumentException("The AI settings encryption key must be 32 bytes.", nameof(primaryKey));
        if (previousKey is not null && previousKey.Length != 32)
            throw new ArgumentException("The previous AI settings encryption key must be 32 bytes.", nameof(previousKey));
        _primary = primaryKey;
        _primaryId = primaryKey is null ? null : KeyId(primaryKey);
        _previous = previousKey;
        _previousId = previousKey is null ? null : KeyId(previousKey);
    }

    /// <summary>
    /// Read the key(s) from the environment. Throws (without echoing any value)
    /// when a key is present but malformed, so a typo fails at startup rather
    /// than silently disabling key storage.
    /// </summary>
    public static AiSecretProtector FromEnvironment(Func<string, string?> read)
    {
        var raw = read(EnvKey);
        var file = read(EnvKeyFile);
        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(file))
        {
            try { raw = File.ReadAllText(file.Trim()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"{EnvKeyFile} is set but the file could not be read.", ex);
            }
        }
        return new AiSecretProtector(Decode(raw, EnvKey), Decode(read(EnvPreviousKey), EnvPreviousKey));
    }

    private static byte[]? Decode(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(raw.Trim()); }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{name} must be base64 for exactly 32 random bytes (e.g. `openssl rand -base64 32`).");
        }
        if (bytes.Length != 32)
            throw new InvalidOperationException($"{name} must decode to exactly 32 bytes; it decodes to {bytes.Length}.");
        return bytes;
    }

    /// <summary>True when a primary key is configured, i.e. API keys can be saved.</summary>
    public bool CanEncrypt => _primary is not null;

    public byte[] Protect(string plaintext, string purpose)
    {
        if (_primary is null) throw new AiEncryptionKeyMissingException();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[HeaderSize + pt.Length];
        blob[0] = FormatVersion;
        _primaryId!.CopyTo(blob, 1);
        nonce.CopyTo(blob, 1 + KeyIdSize);
        using var aes = new AesGcm(_primary, TagSize);
        aes.Encrypt(nonce, pt, blob.AsSpan(HeaderSize), blob.AsSpan(1 + KeyIdSize + NonceSize, TagSize), Encoding.UTF8.GetBytes(purpose));
        CryptographicOperations.ZeroMemory(pt);
        return blob;
    }

    /// <summary>
    /// Decrypt a stored blob. Returns false — never throws, never logs — when
    /// no configured key matches, the blob is malformed, or authentication fails.
    /// </summary>
    public bool TryUnprotect(byte[]? blob, string purpose, out string? plaintext)
    {
        plaintext = null;
        if (blob is null || blob.Length < HeaderSize || blob[0] != FormatVersion) return false;
        var id = blob.AsSpan(1, KeyIdSize);
        byte[]? key = _primaryId is not null && id.SequenceEqual(_primaryId) ? _primary
                    : _previousId is not null && id.SequenceEqual(_previousId) ? _previous
                    : null;
        if (key is null) return false;

        var pt = new byte[blob.Length - HeaderSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(1 + KeyIdSize, NonceSize), blob.AsSpan(HeaderSize),
                blob.AsSpan(1 + KeyIdSize + NonceSize, TagSize), pt, Encoding.UTF8.GetBytes(purpose));
            plaintext = Encoding.UTF8.GetString(pt);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pt);
        }
    }

    /// <summary>True when the blob was written with the previous (rotation) key and should be re-encrypted.</summary>
    public bool UsesPreviousKey(byte[]? blob) =>
        blob is { Length: > KeyIdSize } && _previousId is not null && blob.AsSpan(1, KeyIdSize).SequenceEqual(_previousId);

    private static byte[] KeyId(byte[] key)
    {
        var material = Encoding.UTF8.GetBytes("factorybrain-ai-settings-key-id").Concat(key).ToArray();
        return SHA256.HashData(material)[..KeyIdSize];
    }
}

/// <summary>An API key was submitted but no encryption key is configured, so it can't be stored.</summary>
public sealed class AiEncryptionKeyMissingException : Exception
{
    public AiEncryptionKeyMissingException()
        : base($"API keys can't be saved: {AiSecretProtector.EnvKey} (or {AiSecretProtector.EnvKeyFile}) is not configured on the API server.") { }
}
