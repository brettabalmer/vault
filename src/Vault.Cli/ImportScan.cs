using System.Text.Json;
using Vault.Core;

namespace Vault.Cli;

/// <summary>
/// One-time migration: scan a project directory for the scattered env-ish files that feed a profile, union
/// their keys, and write those that exist in the manifest into the profile's vault. Keys not in the manifest
/// are reported as unmapped (e.g. host-only Functions settings, which intentionally stay out of the vault).
/// </summary>
public static class ImportScan
{
    public static (int imported, List<string> unmapped) Run(CliContext ctx, string fromDir)
    {
        var root = Path.GetFullPath(fromDir);
        if (!Directory.Exists(root)) throw new CliError($"--from directory does not exist: {root}");

        var collected = new Dictionary<string, string>(StringComparer.Ordinal); // later files win
        foreach (var file in FilesForProfile(root, ctx.Profile))
            foreach (var (k, v) in ReadPairs(file))
                collected[k] = v;

        var vault = ctx.SharedFile.Read(ctx.Key);
        var unmapped = new List<string>();
        int imported = 0;
        foreach (var (k, v) in collected)
        {
            if (ctx.Manifest.Find(k) is null) { unmapped.Add(k); continue; }
            vault[k] = v;
            imported++;
        }
        ctx.SharedFile.Write(ctx.Key, vault, ctx.IsSecret);
        return (imported, unmapped);
    }

    /// <summary>The candidate source files for a profile, in precedence order (later wins).</summary>
    private static IEnumerable<string> FilesForProfile(string root, string profile)
    {
        if (profile == "local")
        {
            foreach (var name in new[] { ".env", ".env.local", ".env.integration" })
            {
                var p = Path.Combine(root, name);
                if (File.Exists(p)) yield return p;
            }
            // Every app's local.settings.json (skip build output).
            foreach (var p in SafeEnumerate(root, "local.settings.json"))
                yield return p;
        }
        else if (profile.StartsWith("azure-", StringComparison.Ordinal))
        {
            var suffix = profile["azure-".Length..]; // dev | prod
            foreach (var name in new[] { $".env.azure.{suffix}", $".env.azure.worker.{suffix}" })
            {
                var p = Path.Combine(root, name);
                if (File.Exists(p)) yield return p;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string fileName)
    {
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var p in all)
        {
            var norm = p.Replace('\\', '/');
            if (norm.Contains("/bin/") || norm.Contains("/obj/") || norm.Contains("/node_modules/") ||
                norm.Contains("/.svelte-kit/") || norm.Contains("/build/"))
                continue;
            yield return p;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadPairs(string file)
    {
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return ReadLocalSettings(file);
        return EnvText.Parse(File.ReadAllText(file));
    }

    /// <summary>Extract the <c>Values</c> object from an Azure Functions <c>local.settings.json</c>.</summary>
    private static IEnumerable<KeyValuePair<string, string>> ReadLocalSettings(string file)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
        catch (JsonException) { return pairs; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Object)
                return pairs;
            foreach (var prop in values.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var key = prop.Name;
                if (key.StartsWith("_", StringComparison.Ordinal) || key.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!EnvText.IsValidKey(key)) continue;
                pairs.Add(new(key, prop.Value.GetString() ?? ""));
            }
        }
        return pairs;
    }
}
