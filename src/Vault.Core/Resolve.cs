namespace Vault.Core;

/// <summary>Per-variable state for a given profile, used by check/list/TUI.</summary>
public enum VarState
{
    /// <summary>Has a value (from the vault or a default).</summary>
    Set,
    /// <summary>Required but absent from both vault and default.</summary>
    MissingRequired,
    /// <summary>Optional and unset.</summary>
    MissingOptional,
    /// <summary>Has a value that fails the manifest <c>validate</c> regex.</summary>
    Invalid,
}

public sealed record VarStatus(ManifestVar Var, VarState State, string? Value, bool FromDefault);

/// <summary>
/// Assembles values from manifest + a decrypted vault map (FORMAT.md §5) and reports per-var status.
/// </summary>
public static class Resolve
{
    /// <summary>The env map a reader for <paramref name="platform"/>/<paramref name="profile"/> should surface.</summary>
    public static Dictionary<string, string> ForPlatform(
        Manifest manifest, IReadOnlyDictionary<string, string> vault, string platform, string profile)
    {
        var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in manifest.Vars)
        {
            if (!v.AppliesTo(platform, profile)) continue;
            if (vault.TryGetValue(v.Key, out var val)) outMap[v.Key] = val;
            else if (v.Default is not null) outMap[v.Key] = v.Default;
        }
        return outMap;
    }

    /// <summary>Status of every manifest var that belongs to <paramref name="profile"/> (all platforms).</summary>
    public static List<VarStatus> StatusFor(
        Manifest manifest, IReadOnlyDictionary<string, string> vault, string profile)
    {
        var list = new List<VarStatus>(manifest.Vars.Count);
        foreach (var v in manifest.Vars)
        {
            if (v.Profiles.Count > 0 && !v.Profiles.Contains(profile)) continue;

            if (vault.TryGetValue(v.Key, out var val))
            {
                var state = Manifest.PassesValidation(v, val) ? VarState.Set : VarState.Invalid;
                list.Add(new VarStatus(v, state, val, FromDefault: false));
            }
            else if (v.Default is not null)
            {
                list.Add(new VarStatus(v, VarState.Set, v.Default, FromDefault: true));
            }
            else
            {
                list.Add(new VarStatus(v, v.Required ? VarState.MissingRequired : VarState.MissingOptional, null, false));
            }
        }
        return list;
    }

    /// <summary>Vars that fail a <c>check</c> (required-missing or invalid).</summary>
    public static List<VarStatus> Failures(
        Manifest manifest, IReadOnlyDictionary<string, string> vault, string profile) =>
        StatusFor(manifest, vault, profile)
            .Where(s => s.State is VarState.MissingRequired or VarState.Invalid)
            .ToList();
}
