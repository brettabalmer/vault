namespace Vault.Core;

/// <summary>
/// The local-dev environment reader: decrypt the vault and seed values into the process environment, <b>only
/// for keys not already set</b> (so computed/per-worktree overrides and real cloud settings always win).
/// This is the library an app calls at boot — e.g. <c>EnvSeeder.SeedEnvironment("worker")</c>.
///
/// <para><b>Dev-only + silent.</b> It no-ops in Azure (<c>WEBSITE_INSTANCE_ID</c> present) so deployed apps keep
/// reading their App Service settings, and it <b>never throws and never writes to stdout</b> — a missing
/// key/vault degrades to "env unchanged". (The last part matters for isolated Azure Functions workers, where a
/// stray stdout write crash-loops the host channel.)</para>
/// </summary>
public static class EnvSeeder
{
    /// <summary>
    /// Seed env vars that apply to <paramref name="platform"/> for the active profile. Returns the keys it set
    /// (empty if it no-oped). Never throws.
    /// </summary>
    public static IReadOnlyList<string> SeedEnvironment(string platform, string? profileOverride = null)
    {
        try { return SeedCore(platform, profileOverride); }
        catch { return Array.Empty<string>(); } // silent by design — see class remarks
    }

    private static IReadOnlyList<string> SeedCore(string platform, string? profileOverride)
    {
        // In Azure: leave the App Service application settings untouched.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
            return Array.Empty<string>();

        var vaultDir = FindVaultDir();
        if (vaultDir is null) return Array.Empty<string>();

        var profile = profileOverride ?? Environment.GetEnvironmentVariable("VAULT_PROFILE") ?? "local";
        var shared = new VaultFile(vaultDir, profile);
        var manifestPath = Path.Combine(vaultDir, "manifest.json");
        if (!shared.Exists() || !File.Exists(manifestPath)) return Array.Empty<string>();

        // Resolve the key for THIS vault's identity from the keyring (id::key pairings + legacy bare fallback).
        byte[] key;
        try { key = KeyStore.LoadFor(shared.ReadId()); }
        catch (VaultKeyNotFoundException) { return Array.Empty<string>(); }

        SortedDictionary<string, string> values;
        try
        {
            values = shared.Read(key);
            var personal = new VaultFile(vaultDir, "personal"); // per-developer overrides, layered on top
            if (personal.Exists())
                foreach (var (k, v) in personal.Read(key)) values[k] = v;
        }
        catch (VaultCryptoException) { return Array.Empty<string>(); } // wrong key → leave env unchanged

        var manifest = Manifest.Load(manifestPath);
        var seeded = new List<string>();
        foreach (var v in manifest.Vars)
        {
            if (!v.AppliesTo(platform, profile)) continue;
            string? value = values.TryGetValue(v.Key, out var fromVault) ? fromVault : v.Default;
            if (value is null) continue;
            if (Environment.GetEnvironmentVariable(v.Key) is null)
            {
                Environment.SetEnvironmentVariable(v.Key, value);
                seeded.Add(v.Key);
            }
        }
        return seeded;
    }

    /// <summary>Find the nearest <c>vault/manifest.json</c> from <c>$VAULT_DIR</c>, the cwd, or the app base dir.</summary>
    public static string? FindVaultDir()
    {
        var explicitDir = Environment.GetEnvironmentVariable("VAULT_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir) && File.Exists(Path.Combine(explicitDir, "manifest.json")))
            return explicitDir;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "vault", "manifest.json");
                if (File.Exists(candidate)) return Path.Combine(dir.FullName, "vault");
                dir = dir.Parent;
            }
        }
        return null;
    }
}
