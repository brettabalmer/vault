using System.Text.Json.Serialization;

namespace Vault.Cli;

/// <summary>Source-gen context for <c>export --format json</c> (a flat string map).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class JsonMapContext : JsonSerializerContext { }
