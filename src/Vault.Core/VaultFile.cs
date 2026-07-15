using System.Text;

namespace Vault.Core;

/// <summary>
/// A profile's values on disk (FORMAT.md). <b>v2</b> is a text file — one <c>KEY=VALUE</c> line per var, where
/// non-secret values are plaintext and secret values are <c>enc:&lt;base64&gt;</c> tokens (per-value, deterministic
/// — so git can line-merge changes across worktrees and a blank secret stays blank). The header carries the
/// vault <b>identity</b> (<c>#vault:2 id=&lt;id&gt;</c>) so the reader knows which keyring key to use. Reads still
/// accept the legacy <b>v1</b> whole-file blob and identity-less v2, so old vaults migrate on the next write.
/// </summary>
public sealed class VaultFile
{
    private const string V2Header = "#vault:2";

    public string Dir { get; }
    public string Profile { get; }
    public string Path => System.IO.Path.Combine(Dir, Profile + ".enc");

    public VaultFile(string dir, string profile)
    {
        Dir = dir;
        Profile = profile;
    }

    public bool Exists() => File.Exists(Path);

    /// <summary>The vault identity stamped in the header, or null (legacy/identity-less). Needs no key.</summary>
    public string? ReadId()
    {
        if (!Exists()) return null;
        using var reader = new StreamReader(Path);
        var first = reader.ReadLine();
        return ParseId(first);
    }

    /// <summary>Parse <c>id=…</c> from a <c>#vault:2 id=…</c> header line, or null.</summary>
    public static string? ParseId(string? headerLine)
    {
        if (headerLine is null || !headerLine.TrimStart().StartsWith(V2Header, StringComparison.Ordinal)) return null;
        foreach (var tok in headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (tok.StartsWith("id=", StringComparison.Ordinal))
            {
                var id = tok[3..].Trim();
                return id.Length == 0 ? null : id;
            }
        return null;
    }

    /// <summary>Decrypt to a map. Missing file → empty. Handles both v2 (per-value) and legacy v1 (whole blob).</summary>
    public SortedDictionary<string, string> Read(byte[] key)
    {
        if (!Exists()) return new SortedDictionary<string, string>(StringComparer.Ordinal);
        var text = File.ReadAllText(Path);
        return text.TrimStart().StartsWith(V2Header, StringComparison.Ordinal)
            ? ReadV2(text, key)
            : EnvText.Parse(Crypto.Decrypt(text, key)); // v1: whole-file AES-GCM blob
    }

    private static SortedDictionary<string, string> ReadV2(string text, byte[] key)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq];
            var val = line[(eq + 1)..];
            map[name] = Crypto.IsEncrypted(val) ? Crypto.DecryptValue(name, val, key) : val;
        }
        return map;
    }

    /// <summary>
    /// Encrypt+persist a full map in v2, stamping <paramref name="id"/> in the header (null = identity-less).
    /// <paramref name="isSecret"/> decides which keys are stored as <c>enc:…</c> (a blank secret stays blank).
    /// </summary>
    public void Write(byte[] key, IReadOnlyDictionary<string, string> map, Func<string, bool> isSecret, string? id)
    {
        Directory.CreateDirectory(Dir);
        var sb = new StringBuilder();
        sb.Append(V2Header);
        if (id is not null) sb.Append(" id=").Append(id);
        sb.Append('\n');
        sb.Append("# Managed by `vault` — do not hand-edit. Non-secret values are plaintext; secrets are enc:<base64>.\n");
        foreach (var name in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = map[name];
            var stored = isSecret(name) && value.Length > 0 ? Crypto.EncryptValue(name, value, key) : value;
            sb.Append(name).Append('=').Append(stored).Append('\n');
        }
        File.WriteAllText(Path, sb.ToString());
    }

    /// <summary>Set one key (preserving the current identity); returns the prior value (null if new).</summary>
    public string? Set(byte[] key, string name, string value, Func<string, bool> isSecret)
    {
        if (!EnvText.IsValidKey(name))
            throw new ArgumentException($"'{name}' is not a valid key (must be ^[A-Za-z_][A-Za-z0-9_]*$).");
        var id = ReadId();
        var map = Read(key);
        map.TryGetValue(name, out var prior);
        map[name] = value;
        Write(key, map, isSecret, id);
        return prior;
    }

    /// <summary>Remove one key (preserving the current identity); returns true if it was present.</summary>
    public bool Unset(byte[] key, string name, Func<string, bool> isSecret)
    {
        var id = ReadId();
        var map = Read(key);
        if (!map.Remove(name)) return false;
        Write(key, map, isSecret, id);
        return true;
    }
}
