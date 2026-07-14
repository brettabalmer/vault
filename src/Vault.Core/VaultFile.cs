using System.Text;

namespace Vault.Core;

/// <summary>
/// A profile's encrypted values on disk: <c>&lt;dir&gt;/&lt;profile&gt;.enc</c>. Read-modify-write with the
/// crypto envelope; the committed file is base64 line-wrapped at 76 cols for readable diffs.
/// </summary>
public sealed class VaultFile
{
    private const int WrapCols = 76;

    public string Dir { get; }
    public string Profile { get; }
    public string Path => System.IO.Path.Combine(Dir, Profile + ".enc");

    public VaultFile(string dir, string profile)
    {
        Dir = dir;
        Profile = profile;
    }

    public bool Exists() => File.Exists(Path);

    /// <summary>Decrypt to a map. A missing file is an empty map (a not-yet-populated profile).</summary>
    public SortedDictionary<string, string> Read(byte[] key)
    {
        if (!Exists()) return new SortedDictionary<string, string>(StringComparer.Ordinal);
        var plaintext = Crypto.Decrypt(File.ReadAllText(Path), key);
        return EnvText.Parse(plaintext);
    }

    /// <summary>Encrypt and persist a full map (sorted, wrapped).</summary>
    public void Write(byte[] key, IReadOnlyDictionary<string, string> map)
    {
        Directory.CreateDirectory(Dir);
        var b64 = Crypto.Encrypt(EnvText.Serialize(map), key);
        File.WriteAllText(Path, Wrap(b64) + "\n");
    }

    /// <summary>Set one key; returns the prior value (null if new).</summary>
    public string? Set(byte[] key, string name, string value)
    {
        if (!EnvText.IsValidKey(name))
            throw new ArgumentException($"'{name}' is not a valid key (must be ^[A-Za-z_][A-Za-z0-9_]*$).");
        var map = Read(key);
        map.TryGetValue(name, out var prior);
        map[name] = value;
        Write(key, map);
        return prior;
    }

    /// <summary>Remove one key; returns true if it was present.</summary>
    public bool Unset(byte[] key, string name)
    {
        var map = Read(key);
        if (!map.Remove(name)) return false;
        Write(key, map);
        return true;
    }

    private static string Wrap(string s)
    {
        var sb = new StringBuilder(s.Length + s.Length / WrapCols + 1);
        for (int i = 0; i < s.Length; i += WrapCols)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(s.AsSpan(i, Math.Min(WrapCols, s.Length - i)));
        }
        return sb.ToString();
    }
}
