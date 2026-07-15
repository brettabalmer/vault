using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vault.Core;

/// <summary>The manifest schema (FORMAT.md §4). A single variable's metadata.</summary>
public sealed class ManifestVar
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "Uncategorized";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("secret")] public bool Secret { get; set; } = true;
    [JsonPropertyName("platforms")] public List<string> Platforms { get; set; } = new();
    [JsonPropertyName("profiles")] public List<string> Profiles { get; set; } = new();
    [JsonPropertyName("example")] public string? Example { get; set; }
    [JsonPropertyName("default")] public string? Default { get; set; }
    [JsonPropertyName("validate")] public string? Validate { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    /// <summary>Per-developer value (own LLM key, local model, identity…). `vault set` targets the gitignored
    /// personal.enc by default, and it's never committed to the shared vault.</summary>
    [JsonPropertyName("personal")] public bool Personal { get; set; }

    public bool AppliesTo(string platform, string profile) =>
        (Platforms.Count == 0 || Platforms.Contains(platform)) &&
        (Profiles.Count == 0 || Profiles.Contains(profile));
}

/// <summary>Top-level manifest document.</summary>
public sealed class ManifestDoc
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("vars")] public List<ManifestVar> Vars { get; set; } = new();
}

/// <summary>Source-generated JSON context so the manifest round-trips under NativeAOT (no reflection).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ManifestDoc))]
[JsonSerializable(typeof(ManifestVar))]
public partial class ManifestJsonContext : JsonSerializerContext { }

/// <summary>Loads and indexes a project's <c>manifest.json</c>.</summary>
public sealed class Manifest
{
    private readonly Dictionary<string, ManifestVar> _byKey;
    public IReadOnlyList<ManifestVar> Vars { get; }
    public int FormatVersion { get; }

    private Manifest(ManifestDoc doc)
    {
        FormatVersion = doc.FormatVersion;
        Vars = doc.Vars;
        _byKey = doc.Vars.ToDictionary(v => v.Key, StringComparer.Ordinal);
    }

    public static Manifest Load(string manifestPath) => new(LoadDoc(manifestPath, mustExist: true));

    /// <summary>Load the editable document. <paramref name="mustExist"/> false → a missing file returns an empty doc.</summary>
    public static ManifestDoc LoadDoc(string manifestPath, bool mustExist = false)
    {
        if (!File.Exists(manifestPath))
        {
            if (mustExist)
                throw new FileNotFoundException($"Manifest not found at {manifestPath}. Expected `vault/manifest.json`.");
            return new ManifestDoc();
        }
        var json = File.ReadAllText(manifestPath);
        ManifestDoc? doc;
        try { doc = JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ManifestDoc); }
        catch (JsonException e) { throw new InvalidOperationException($"manifest.json is not valid JSON: {e.Message}", e); }
        if (doc is null) throw new InvalidOperationException("manifest.json deserialized to null.");
        return doc;
    }

    /// <summary>Serialize the document back to <paramref name="manifestPath"/> (indented, sorted by key).</summary>
    public static void SaveDoc(string manifestPath, ManifestDoc doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        doc.Vars.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(doc, ManifestJsonContext.Default.ManifestDoc));
    }

    public ManifestVar? Find(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<string> Categories() =>
        Vars.Select(v => v.Category).Distinct(StringComparer.Ordinal);

    /// <summary>Validate a value against the var's optional <c>validate</c> regex.</summary>
    public static bool PassesValidation(ManifestVar v, string value) =>
        string.IsNullOrEmpty(v.Validate) || Regex.IsMatch(value, v.Validate);
}
