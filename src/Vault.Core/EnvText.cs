using System.Text;

namespace Vault.Core;

/// <summary>
/// The <c>KEY=VALUE</c> plaintext format (FORMAT.md §3). A deliberately dumb line reader: split on the first
/// <c>=</c>, no quoting/escaping/expansion, single-line values. Mirrors the repo's <c>scripts/lib/env-file.mjs</c>
/// so migration is lossless.
/// </summary>
public static class EnvText
{
    /// <summary>Parse a <c>KEY=VALUE</c> block into an ordered map (later duplicates win).</summary>
    public static SortedDictionary<string, string> Parse(string text)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal)) key = key[7..].Trim();
            if (!IsValidKey(key)) continue;
            map[key] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>Serialize a map to sorted <c>KEY=VALUE</c> lines (stable diffs).</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> map)
    {
        var sb = new StringBuilder();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(key).Append('=').Append(map[key]).Append('\n');
        return sb.ToString();
    }

    /// <summary>Keys are <c>^[A-Za-z_][A-Za-z0-9_]*$</c>.</summary>
    public static bool IsValidKey(string key)
    {
        if (key.Length == 0) return false;
        char c0 = key[0];
        if (!(char.IsAsciiLetter(c0) || c0 == '_')) return false;
        foreach (var c in key)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }
}
