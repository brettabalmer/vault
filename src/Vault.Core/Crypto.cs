using System.Security.Cryptography;
using System.Text;

namespace Vault.Core;

/// <summary>
/// The AES-256-GCM envelope from FORMAT.md §2: base64( version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16) ),
/// AAD = the version byte. Built-in crypto only — no external dependency.
/// </summary>
public static class Crypto
{
    public const byte Version = 0x01;
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // GCM standard
    private const int TagSize = 16;

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/> to the base64 envelope. Nonce comes from the CSPRNG.</summary>
    public static string Encrypt(string plaintext, byte[] key)
    {
        ValidateKey(key);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        var aad = new[] { Version };

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag, aad);

        var envelope = new byte[1 + NonceSize + cipher.Length + TagSize];
        envelope[0] = Version;
        Buffer.BlockCopy(nonce, 0, envelope, 1, NonceSize);
        Buffer.BlockCopy(cipher, 0, envelope, 1 + NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, envelope, 1 + NonceSize + cipher.Length, TagSize);
        return Convert.ToBase64String(envelope);
    }

    /// <summary>Decrypt a base64 envelope (whitespace tolerated) back to the UTF-8 plaintext.</summary>
    public static string Decrypt(string base64Envelope, byte[] key)
    {
        ValidateKey(key);
        var stripped = StripWhitespace(base64Envelope);
        byte[] envelope;
        try { envelope = Convert.FromBase64String(stripped); }
        catch (FormatException e) { throw new VaultCryptoException("Vault file is not valid base64.", e); }

        if (envelope.Length < 1 + NonceSize + TagSize)
            throw new VaultCryptoException("Vault file is too short to be a valid envelope.");
        if (envelope[0] != Version)
            throw new VaultCryptoException($"Unsupported vault format version 0x{envelope[0]:X2} (this reader supports 0x{Version:X2}).");

        int cipherLen = envelope.Length - 1 - NonceSize - TagSize;
        var nonce = new byte[NonceSize];
        var cipher = new byte[cipherLen];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(envelope, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, 1 + NonceSize, cipher, 0, cipherLen);
        Buffer.BlockCopy(envelope, 1 + NonceSize + cipherLen, tag, 0, TagSize);

        var plain = new byte[cipherLen];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain, new[] { Version });
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new VaultCryptoException("Decryption failed: wrong key or the vault file was tampered with.", e);
        }
        return Encoding.UTF8.GetString(plain);
    }

    public const string EncPrefix = "enc:";

    /// <summary>True if a stored value is an encrypted token (vs plaintext).</summary>
    public static bool IsEncrypted(string value) => value.StartsWith(EncPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypt ONE value → <c>enc:base64(nonce ‖ ct ‖ tag)</c> (vault format v2, per-value). The nonce is
    /// derived deterministically from HMAC(key, name ‖ 0x00 ‖ value) so an unchanged secret produces the same
    /// token every write — clean git diffs, no churn. A nonce only repeats for an identical (name,value) under
    /// the same key (→ identical token, which is safe). AAD = the var name, binding a token to its key.
    /// </summary>
    public static string EncryptValue(string name, string plaintext, byte[] key)
    {
        ValidateKey(key);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = DeriveNonce(key, name, plain);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        var token = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, token, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, token, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, token, NonceSize + cipher.Length, TagSize);
        return EncPrefix + Convert.ToBase64String(token);
    }

    /// <summary>Decrypt an <c>enc:…</c> token back to plaintext (AAD = <paramref name="name"/>).</summary>
    public static string DecryptValue(string name, string token, byte[] key)
    {
        ValidateKey(key);
        byte[] raw;
        try { raw = Convert.FromBase64String(token[EncPrefix.Length..]); }
        catch (FormatException e) { throw new VaultCryptoException($"'{name}' has a malformed encrypted value.", e); }
        if (raw.Length < NonceSize + TagSize) throw new VaultCryptoException($"'{name}' encrypted value is too short.");
        int ctLen = raw.Length - NonceSize - TagSize;
        var nonce = new byte[NonceSize];
        var cipher = new byte[ctLen];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(raw, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(raw, NonceSize, cipher, 0, ctLen);
        Buffer.BlockCopy(raw, NonceSize + ctLen, tag, 0, TagSize);
        var plain = new byte[ctLen];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(name));
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new VaultCryptoException($"Failed to decrypt '{name}': wrong key or the value was tampered with.", e);
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveNonce(byte[] key, string name, byte[] plain)
    {
        using var h = new HMACSHA256(key);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var buf = new byte[nameBytes.Length + 1 + plain.Length];
        Buffer.BlockCopy(nameBytes, 0, buf, 0, nameBytes.Length);
        buf[nameBytes.Length] = 0;
        Buffer.BlockCopy(plain, 0, buf, nameBytes.Length + 1, plain.Length);
        var mac = h.ComputeHash(buf);
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(mac, 0, nonce, 0, NonceSize);
        return nonce;
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeySize)
            throw new VaultCryptoException($"Key must be {KeySize} bytes (got {key.Length}).");
    }

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>Thrown for any crypto/envelope failure (bad key, bad base64, tampering, unsupported version).</summary>
public sealed class VaultCryptoException : Exception
{
    public VaultCryptoException(string message, Exception? inner = null) : base(message, inner) { }
}
