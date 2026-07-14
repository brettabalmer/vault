using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vault.Core;

/// <summary>
/// Resolves and creates the 32-byte AES key (FORMAT.md §1): <c>$VAULT_KEY</c> → <c>$VAULT_KEY_FILE</c> →
/// <c>~/.config/vault/key</c> (<c>%APPDATA%\vault\key</c> on Windows).
/// </summary>
public static class KeyStore
{
    private const int KeyBytes = 32;

    /// <summary>The default key file path for the current OS/user.</summary>
    public static string DefaultKeyPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "vault", "key");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "vault", "key");
    }

    /// <summary>Load the key bytes, or throw <see cref="VaultKeyNotFoundException"/> with the paths tried.</summary>
    public static byte[] Load()
    {
        var inline = Environment.GetEnvironmentVariable("VAULT_KEY");
        if (!string.IsNullOrWhiteSpace(inline)) return Decode(inline, "$VAULT_KEY");

        var fileEnv = Environment.GetEnvironmentVariable("VAULT_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(fileEnv) && File.Exists(fileEnv))
            return Decode(File.ReadAllText(fileEnv), fileEnv);

        var path = DefaultKeyPath();
        if (File.Exists(path)) return Decode(File.ReadAllText(path), path);

        throw new VaultKeyNotFoundException(
            $"No vault key found. Set $VAULT_KEY, or run `vault keygen` to create {path}.");
    }

    /// <summary>True if a key is resolvable without throwing.</summary>
    public static bool Exists()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_KEY"))) return true;
        var fileEnv = Environment.GetEnvironmentVariable("VAULT_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(fileEnv) && File.Exists(fileEnv)) return true;
        return File.Exists(DefaultKeyPath());
    }

    /// <summary>Generate a new key at the default path. Refuses to overwrite unless <paramref name="force"/>.</summary>
    public static string Generate(bool force = false)
    {
        var path = DefaultKeyPath();
        if (File.Exists(path) && !force)
            throw new InvalidOperationException($"A key already exists at {path}. Pass --force to overwrite (this invalidates every existing vault file).");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static byte[] Decode(string base64, string source)
    {
        byte[] key;
        try { key = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException e) { throw new VaultKeyNotFoundException($"Key from {source} is not valid base64.", e); }
        if (key.Length != KeyBytes)
            throw new VaultKeyNotFoundException($"Key from {source} must decode to {KeyBytes} bytes (got {key.Length}).");
        return key;
    }
}

/// <summary>Thrown when no usable key can be resolved.</summary>
public sealed class VaultKeyNotFoundException : Exception
{
    public VaultKeyNotFoundException(string message, Exception? inner = null) : base(message, inner) { }
}
