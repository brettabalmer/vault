using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Vault.Core;

/// <summary>
/// The on-disk <b>keyring</b> (FORMAT.md §1). One machine holds keys for many vaults, so the key file at
/// <c>~/.config/vault/key</c> (<c>%APPDATA%\vault\key</c> on Windows) is a list of <c>&lt;id&gt; :: &lt;base64 key&gt;</c>
/// pairings — one per vault identity. A line with no <c>::</c> is the <b>legacy bare key</b> (pre-identity
/// vaults, and the migration fallback). Resolution for a vault with identity <paramref name="id"/>:
/// <c>$VAULT_KEY</c> → keyring[id] → legacy bare key → not found. The bare-key fallback is what lets an
/// existing vault gain an id without breaking teammates who haven't updated their keyring (the key is
/// unchanged, only the id is new).
/// </summary>
public static class KeyStore
{
    private const int KeyBytes = 32;
    private const string Sep = "::";

    /// <summary>The default keyring path for the current OS/user.</summary>
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

    /// <summary>A parsed keyring: id→key pairings plus an optional legacy bare key.</summary>
    public sealed class Keyring
    {
        public Dictionary<string, byte[]> ById { get; } = new(StringComparer.Ordinal);
        public byte[]? LegacyBare { get; set; }
    }

    /// <summary>Parse the keyring text. Tolerant: blank lines and <c>#</c> comments are ignored.</summary>
    public static Keyring Parse(string text, string source = "keyring")
    {
        var ring = new Keyring();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int sep = line.IndexOf(Sep, StringComparison.Ordinal);
            if (sep < 0)
            {
                ring.LegacyBare ??= Decode(line, source);
                continue;
            }
            var id = line[..sep].Trim();
            var keyText = line[(sep + Sep.Length)..].Trim();
            if (id.Length == 0) continue;
            ring.ById[id] = Decode(keyText, $"{source} (id {id})");
        }
        return ring;
    }

    /// <summary>The keyring file to read/write: <c>$VAULT_KEY_FILE</c> if set, else the default path.</summary>
    public static string KeyringPath()
    {
        var fileEnv = Environment.GetEnvironmentVariable("VAULT_KEY_FILE");
        return !string.IsNullOrWhiteSpace(fileEnv) ? fileEnv : DefaultKeyPath();
    }

    private static Keyring? ReadRing()
    {
        var path = KeyringPath();
        return File.Exists(path) ? Parse(File.ReadAllText(path), path) : null;
    }

    /// <summary>
    /// Load the key for a vault whose identity is <paramref name="id"/> (null = an identity-less/legacy vault).
    /// Order: <c>$VAULT_KEY</c> → keyring[id] → legacy bare key. Throws <see cref="VaultKeyNotFoundException"/>
    /// if none resolves.
    /// </summary>
    public static byte[] LoadFor(string? id)
    {
        var inline = Environment.GetEnvironmentVariable("VAULT_KEY");
        if (!string.IsNullOrWhiteSpace(inline)) return Decode(inline, "$VAULT_KEY");

        var ring = ReadRing();
        if (ring is not null)
        {
            if (id is not null && ring.ById.TryGetValue(id, out var byId)) return byId;
            if (ring.LegacyBare is not null) return ring.LegacyBare; // migration fallback (key unchanged by id)
        }

        var path = DefaultKeyPath();
        if (id is not null)
            throw new VaultKeyNotFoundException(
                $"No key for vault '{id}' in your keyring ({path}). Ask a teammate to run `vault share-key` "
                + "and paste it with `vault add-key`, or set $VAULT_KEY.");
        throw new VaultKeyNotFoundException(
            $"No vault key found. Set $VAULT_KEY, or run `vault init` (or `vault keygen`) to create {path}.");
    }

    /// <summary>Back-compat shim: the identity-less load (legacy bare key / <c>$VAULT_KEY</c>).</summary>
    public static byte[] Load() => LoadFor(null);

    /// <summary>True if <em>any</em> key is resolvable (env, keyring pairing, or legacy bare).</summary>
    public static bool Exists()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_KEY"))) return true;
        var ring = ReadRing();
        return ring is not null && (ring.LegacyBare is not null || ring.ById.Count > 0);
    }

    /// <summary>True if the keyring holds a pairing for <paramref name="id"/>.</summary>
    public static bool HasId(string id)
    {
        var ring = ReadRing();
        return ring is not null && ring.ById.ContainsKey(id);
    }

    /// <summary>Add or replace a pairing in the keyring, creating the file if needed. Returns the path.</summary>
    public static string AddPair(string id, byte[] key)
    {
        ValidateKey(key);
        var path = KeyringPath();
        var ring = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new Keyring();
        ring.ById[id] = key;
        WriteRing(path, ring);
        return path;
    }

    /// <summary>Write a legacy bare key (the <c>keygen</c> path), preserving any existing pairings.</summary>
    public static string SetLegacyBare(byte[] key, bool force = false)
    {
        ValidateKey(key);
        var path = KeyringPath();
        var ring = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new Keyring();
        if (ring.LegacyBare is not null && !force)
            throw new InvalidOperationException($"A legacy bare key already exists in {path}. Pass --force to overwrite (invalidates identity-less vaults).");
        ring.LegacyBare = key;
        WriteRing(path, ring);
        return path;
    }

    /// <summary>Generate 32 fresh CSPRNG bytes.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    private static void WriteRing(string path, Keyring ring)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.Append("# vault keyring — <id> :: <base64 key>, one per vault. A line with no `::` is the legacy bare key.\n");
        foreach (var (id, key) in ring.ById.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(id).Append(' ').Append(Sep).Append(' ').Append(Convert.ToBase64String(key)).Append('\n');
        if (ring.LegacyBare is not null)
            sb.Append(Convert.ToBase64String(ring.LegacyBare)).Append('\n');
        File.WriteAllText(path, sb.ToString());
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != KeyBytes) throw new ArgumentException($"Key must be {KeyBytes} bytes (got {key.Length}).");
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
