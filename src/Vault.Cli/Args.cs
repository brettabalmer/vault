namespace Vault.Cli;

/// <summary>Tiny hand-rolled flag parser (repo convention is manual argv parsing).</summary>
public sealed class Args
{
    private readonly List<string> _positional = new();
    private readonly Dictionary<string, string?> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bools = new(StringComparer.Ordinal);

    /// <summary>Args after a literal <c>--</c> separator (for <c>run -- cmd…</c>). Null if no separator.</summary>
    public string[]? PassThrough { get; }

    /// <param name="valueFlags">Flag names (without <c>--</c>) that consume the next token as a value.</param>
    public Args(IReadOnlyList<string> argv, params string[] valueFlags)
    {
        var valued = new HashSet<string>(valueFlags, StringComparer.Ordinal);
        for (int i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            if (a == "--")
            {
                PassThrough = argv.Skip(i + 1).ToArray();
                break;
            }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                int eq = name.IndexOf('=');
                if (eq >= 0) { _flags[name[..eq]] = name[(eq + 1)..]; continue; }
                if (valued.Contains(name) && i + 1 < argv.Count) { _flags[name] = argv[++i]; }
                else { _bools.Add(name); }
            }
            else _positional.Add(a);
        }
    }

    public IReadOnlyList<string> Positional => _positional;
    public string? Positional0 => _positional.Count > 0 ? _positional[0] : null;
    public string? Positional1 => _positional.Count > 1 ? _positional[1] : null;
    public bool Has(string flag) => _bools.Contains(flag) || _flags.ContainsKey(flag);
    public string? Value(string flag) => _flags.GetValueOrDefault(flag);
    public string Value(string flag, string fallback) => _flags.GetValueOrDefault(flag) ?? fallback;
}
