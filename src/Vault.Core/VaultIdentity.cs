using System.Security.Cryptography;

namespace Vault.Core;

/// <summary>Vault identities: short opaque ids, and a keyless read of the plaintext (for the reset/wipe path).</summary>
public static class VaultIdentity
{
    /// <summary>A fresh opaque id — 16 lowercase hex chars (8 CSPRNG bytes). Stable once written, safe to commit.</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>
    /// Read only the values a reader can recover <b>without the key</b>: plaintext (non-<c>enc:</c>) v2 entries.
    /// Encrypted entries are reported in <paramref name="droppedSecrets"/>. Used when re-initialising a vault
    /// whose old key you don't hold — the secrets are unrecoverable and get wiped, the config survives.
    /// </summary>
    public static SortedDictionary<string, string> ReadPlaintextOnly(VaultFile file, out List<string> droppedSecrets)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        droppedSecrets = new List<string>();
        if (!file.Exists()) return map;
        var text = File.ReadAllText(file.Path);
        if (!text.TrimStart().StartsWith("#vault:2", StringComparison.Ordinal))
            return map; // legacy v1 is an opaque blob — nothing recoverable without the key
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq];
            var val = line[(eq + 1)..];
            if (Crypto.IsEncrypted(val)) droppedSecrets.Add(name);
            else map[name] = val;
        }
        return map;
    }
}
