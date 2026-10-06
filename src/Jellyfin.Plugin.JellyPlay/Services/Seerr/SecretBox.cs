using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Seerr;

/// <summary>
/// AES-GCM at-rest encryption for Seerr session cookies. The 256-bit key lives
/// in a file next to the plugin database (created with user-only permissions);
/// without it, stored sessions are undecryptable and are treated as absent
/// (fail closed — the user just re-links Seerr).
///
/// Stored payloads carry a version prefix byte: 0x01 = AES-GCM
/// ([0x01][12-byte nonce][ciphertext][16-byte tag]). Legacy rows hold bare
/// UTF-8 plaintext and lack the prefix; they decrypt as-is on read and are
/// re-encrypted on the session's next write (lazy migration).
/// </summary>
public sealed class SecretBox
{
    public const string KeyFileName = "seerr_secret.key";

    /// <summary>Version byte marking an AES-GCM-encrypted payload.</summary>
    public const byte PayloadVersion = 0x01;

    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int HeaderSizeBytes = 1 + NonceSizeBytes + TagSizeBytes;

    private readonly byte[]? _key;

    /// <summary>
    /// A box without a key is inert: Protect/TryUnprotect return null instead
    /// of touching ciphertext (missing or unreadable key file).
    /// </summary>
    public SecretBox(byte[]? key)
    {
        if (key is not null && key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Key must be {KeySizeBytes} bytes", nameof(key));
        }

        _key = key;
    }

    public bool IsAvailable => _key is not null;

    /// <summary>Encrypts to [0x01][nonce][ciphertext][tag]; null when the box has no key.</summary>
    public byte[]? Protect(string plaintext)
    {
        if (_key is null)
        {
            return null;
        }

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagSizeBytes];
        using var gcm = new AesGcm(_key, TagSizeBytes);
        gcm.Encrypt(nonce, plain, ciphertext, tag);

        var payload = new byte[HeaderSizeBytes + ciphertext.Length];
        payload[0] = PayloadVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, payload, 1 + NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSizeBytes + ciphertext.Length, TagSizeBytes);
        return payload;
    }

    /// <summary>
    /// Decrypts a versioned payload. Returns null for plaintext/legacy input
    /// (no 0x01 prefix — caller falls back to the raw bytes), a box without a
    /// key, tampered payloads, or payloads sealed with a different key.
    /// </summary>
    public string? TryUnprotect(byte[]? payload)
    {
        if (_key is null
            || payload is null
            || payload.Length < HeaderSizeBytes
            || payload[0] != PayloadVersion)
        {
            return null;
        }

        var nonce = new byte[NonceSizeBytes];
        Buffer.BlockCopy(payload, 1, nonce, 0, NonceSizeBytes);
        var ciphertext = new byte[payload.Length - HeaderSizeBytes];
        Buffer.BlockCopy(payload, 1 + NonceSizeBytes, ciphertext, 0, ciphertext.Length);
        var tag = new byte[TagSizeBytes];
        Buffer.BlockCopy(payload, payload.Length - TagSizeBytes, tag, 0, TagSizeBytes);

        try
        {
            var plain = new byte[ciphertext.Length];
            using var gcm = new AesGcm(_key, TagSizeBytes);
            gcm.Decrypt(nonce, ciphertext, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered payload: the session is unrecoverable.
            return null;
        }
    }

    /// <summary>True when the payload carries the encrypted-payload version byte.</summary>
    public static bool IsEncryptedPayload(byte[]? payload)
        => payload is not null && payload.Length > 0 && payload[0] == PayloadVersion;

    /// <summary>
    /// Loads (or on first use creates) the key file in the plugin data
    /// directory. Any failure yields an inert box: sessions read back as absent
    /// instead of crashing the plugin.
    /// </summary>
    public static SecretBox LoadOrCreate(string directory, ILogger? logger = null)
    {
        var path = Path.Combine(directory, KeyFileName);
        try
        {
            if (File.Exists(path))
            {
                var key = File.ReadAllBytes(path);
                if (key.Length != KeySizeBytes)
                {
                    logger?.LogError(
                        "Seerr session key file {Path} has {Length} bytes (expected {Expected}); stored Seerr sessions cannot be decrypted and will be treated as unlinked. Delete the file to re-key.",
                        path, key.Length, KeySizeBytes);
                    return new SecretBox(null);
                }

                return new SecretBox(key);
            }

            var fresh = RandomNumberGenerator.GetBytes(KeySizeBytes);
            File.WriteAllBytes(path, fresh);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            logger?.LogInformation("Generated Seerr session encryption key at {Path}", path);
            return new SecretBox(fresh);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not load or create the Seerr session key file {Path}; Seerr sessions are treated as absent", path);
            return new SecretBox(null);
        }
    }
}
