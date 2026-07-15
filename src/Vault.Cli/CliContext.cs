using Vault.Core;

namespace Vault.Cli;

/// <summary>
/// Resolves the project's <c>vault/</c> directory (the folder holding <c>manifest.json</c>), the active
/// profile, the manifest, and the decrypted vault map — lazily, so commands that don't need the key (like
/// <c>keygen</c> or <c>--help</c>) never touch it.
/// </summary>
public sealed class CliContext
{
    public string VaultDir { get; }
    public string Profile { get; }

    private CliContext(string vaultDir, string profile)
    {
        VaultDir = vaultDir;
        Profile = profile;
    }

    public string ManifestPath => Path.Combine(VaultDir, "manifest.json");

    /// <summary>Find the nearest <c>vault/manifest.json</c> walking up from cwd (or <c>$VAULT_DIR</c>).</summary>
    public static CliContext Discover(string profile)
    {
        var explicitDir = Environment.GetEnvironmentVariable("VAULT_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return new CliContext(Path.GetFullPath(explicitDir), profile);

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "vault", "manifest.json");
            if (File.Exists(candidate))
                return new CliContext(Path.Combine(dir.FullName, "vault"), profile);
            dir = dir.Parent;
        }
        throw new CliError("No `vault/manifest.json` found in this directory or any parent. "
            + "Set $VAULT_DIR or run from within a project that has one.");
    }

    private Manifest? _manifest;
    public Manifest Manifest => _manifest ??= Manifest.Load(ManifestPath);

    private byte[]? _key;
    /// <summary>The key for this vault's identity (from the shared file's header), via the keyring.</summary>
    public byte[] Key => _key ??= KeyStore.LoadFor(SharedFile.ReadId());

    /// <summary>This vault's identity (from <c>&lt;profile&gt;.enc</c>), or null if it has none yet (legacy).</summary>
    public string? VaultId => SharedFile.ReadId();

    /// <summary>Shared, committed values for the active profile (<c>&lt;profile&gt;.enc</c>).</summary>
    public VaultFile SharedFile => new(VaultDir, Profile);

    /// <summary>Per-developer overrides (<c>personal.enc</c>, gitignored — never committed, layered on top).</summary>
    public VaultFile PersonalFile => new(VaultDir, "personal");

    /// <summary>Effective values: shared overlaid with personal (personal wins).</summary>
    public SortedDictionary<string, string> ReadVault()
    {
        var map = SharedFile.Read(Key);
        foreach (var (k, v) in PersonalFile.Read(Key)) map[k] = v;
        return map;
    }

    /// <summary>Keys that came from the personal file (for provenance in <c>list</c>).</summary>
    public HashSet<string> PersonalKeys() => new(PersonalFile.Read(Key).Keys, StringComparer.Ordinal);

    /// <summary>Which keys are stored encrypted, per the manifest (unknown keys default to non-secret).</summary>
    public bool IsSecret(string name) => Manifest.Find(name)?.Secret ?? false;
}

/// <summary>A user-facing error: printed as a clean message, exit code 1, no stack trace.</summary>
public sealed class CliError : Exception
{
    public CliError(string message) : base(message) { }
}
