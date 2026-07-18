using System.Text.Json.Serialization;

namespace Vault.Cli;

/// <summary>DTOs for machine-readable output (<c>--json</c>). Source-generated for NativeAOT.</summary>
public sealed class VarReport
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("required")] public string Required { get; set; } = "no";
    [JsonPropertyName("secret")] public bool Secret { get; set; }
    [JsonPropertyName("personal")] public bool Personal { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("platforms")] public List<string> Platforms { get; set; } = new();
    [JsonPropertyName("example")] public string? Example { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<VarReport>))]
[JsonSerializable(typeof(VarReport))]
public partial class JsonOutputContext : JsonSerializerContext { }
