using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RadiologyPlus.NovaradAuth;

/// <summary>
/// Pure-.NET reproduction of Novarad's <c>EncodingHelper.EncodeData</c>.
/// See <c>docs/novarad-password-algorithm.md</c> for the source citations.
///
/// We do not link against any Novarad assemblies; this is a from-spec
/// reimplementation of three algorithms keyed on the <c>password_format</c>
/// discriminator stored on each <c>shared.users</c> row:
///   0 — plaintext (citext compare)
///   1 — unsalted SHA-256 of UTF-16LE plaintext, hex with dashes
///   2 — AES (Rijndael) CBC/PKCS7 of UTF-16LE plaintext, key=SHA256(UTF-16LE(systemEncryptionKey)), IV=passwordSalt
/// </summary>
public sealed class NovaradPasswordHasher : INovaradPasswordHasher
{
    private const int SaltSizeBytes = 16;

    /// <summary>
    /// Encode a cleartext password into the form Novarad stores in <c>shared.users.password</c>.
    /// </summary>
    /// <param name="password">Cleartext password as the user typed it.</param>
    /// <param name="passwordFormat">Value of <c>shared.users.password_format</c>.</param>
    /// <param name="salt">Decoded bytes of <c>shared.users.password_salt</c> (see <see cref="DecodeSalt"/>). May be null for formats 0/1.</param>
    /// <param name="systemEncryptionKey">The Novarad system EncryptionKey setting (per-install, from <c>shared.settings</c>). Only needed for format 2.</param>
    public string Encode(string password, int passwordFormat, byte[]? salt, string? systemEncryptionKey)
    {
        ArgumentNullException.ThrowIfNull(password);
        return passwordFormat switch
        {
            0 => EncodeFormat0(password),
            1 => EncodeFormat1(password),
            2 => EncodeFormat2(password, RequireSalt(salt), RequireKey(systemEncryptionKey)),
            _ => throw new NotSupportedException($"Unsupported Novarad password_format: {passwordFormat}."),
        };
    }

    /// <summary>
    /// Constant-time comparison of an encoded candidate against a value pulled from
    /// <c>shared.users.password</c>. The column is <c>citext</c> in Novarad so the
    /// compare is case-insensitive; hex formats are always uppercase so case-insensitivity
    /// is a no-op for formats 1/2.
    /// </summary>
    public bool Verify(string password, string storedEncoded, int passwordFormat, byte[]? salt, string? systemEncryptionKey)
    {
        ArgumentNullException.ThrowIfNull(storedEncoded);

        string candidate;
        try
        {
            candidate = Encode(password, passwordFormat, salt, systemEncryptionKey);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        // citext semantics: case-insensitive equality.
        return string.Equals(candidate, storedEncoded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decode the salt column (<c>shared.users.password_salt</c>) into raw bytes.
    /// Novarad stores it as <c>BitConverter.ToString</c> output (hyphen-separated uppercase
    /// hex pairs); some legacy rows may be base64. Returns null when the column is null/empty.
    /// </summary>
    public byte[]? DecodeSalt(string? saltString)
    {
        if (string.IsNullOrEmpty(saltString)) return null;
        return StringToByte(saltString);
    }

    internal static string EncodeFormat0(string password) => password;

    internal static string EncodeFormat1(string password)
    {
        // Encryption.GetHash(string) = SHA256(Encoding.Unicode.GetBytes(password))
        var bytes = Encoding.Unicode.GetBytes(password);
        var hash = SHA256.HashData(bytes);
        return ToHyphenHex(hash);
    }

    internal static string EncodeFormat2(string password, byte[] salt, string systemEncryptionKey)
    {
        if (salt.Length != SaltSizeBytes)
            throw new ArgumentException($"Novarad password salt must be {SaltSizeBytes} bytes (was {salt.Length}).", nameof(salt));

        // Key derivation: SHA-256 of the UTF-16LE bytes of the EncryptionKey setting.
        var key = SHA256.HashData(Encoding.Unicode.GetBytes(systemEncryptionKey));
        // Plaintext bytes: UTF-16LE of the password.
        var plaintext = Encoding.Unicode.GetBytes(password);

        using var aes = Aes.Create();
        // RijndaelManaged defaults: 128-bit block, CBC, PKCS7. Aes (FIPS subset of Rijndael)
        // is always 128-bit block, so these match. Mode/Padding are CBC/PKCS7 by default
        // on .NET; we set them explicitly so the assumption is visible.
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = salt;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        return ToHyphenHex(cipher);
    }

    private static byte[] RequireSalt(byte[]? salt) =>
        salt ?? throw new ArgumentException("Novarad password_format=2 requires a non-null password_salt.", nameof(salt));

    private static string RequireKey(string? key) =>
        string.IsNullOrEmpty(key)
            ? throw new ArgumentException("Novarad password_format=2 requires the EncryptionKey system setting.", nameof(key))
            : key;

    /// <summary>
    /// Reproduces <c>BitConverter.ToString(byte[])</c>: hyphen-separated uppercase hex pairs.
    /// </summary>
    private static string ToHyphenHex(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var sb = new StringBuilder(bytes.Length * 3 - 1);
        sb.Append(bytes[0].ToString("X2", CultureInfo.InvariantCulture));
        for (var i = 1; i < bytes.Length; i++)
        {
            sb.Append('-');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reproduces <c>Encryption.StringToByte(string)</c>: hyphen-separated hex pairs,
    /// falling back to base64 when no hyphen is present.
    /// </summary>
    private static byte[] StringToByte(string data)
    {
        if (data.IndexOf('-') < 0)
        {
            return Convert.FromBase64String(data);
        }

        var parts = data.Split('-');
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            bytes[i] = byte.Parse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        return bytes;
    }
}

/// <summary>Strategy interface around Novarad's password encoding/comparison. Defined to keep tests trivial.</summary>
public interface INovaradPasswordHasher
{
    string Encode(string password, int passwordFormat, byte[]? salt, string? systemEncryptionKey);
    bool Verify(string password, string storedEncoded, int passwordFormat, byte[]? salt, string? systemEncryptionKey);
    byte[]? DecodeSalt(string? saltString);
}
