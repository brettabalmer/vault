using Spectre.Console;
using Vault.Core;

namespace Vault.Cli;

/// <summary>
/// Define the manifest from the CLI: <c>vault manifest add|set|rm KEY [flags]</c>. <c>add</c> creates a new var
/// (bootstrapping <c>vault/manifest.json</c> if absent), <c>set</c> edits fields of an existing one, <c>rm</c>
/// removes it. Field flags: <c>--category --description --platforms a,b --profiles a,b --default --validate
/// --source --example --required yes|devOnly|no</c>; booleans <c>--required/--no-required (sugar for yes/no)
/// --secret/--no-secret --personal/--no-personal</c>.
/// </summary>
internal static class ManifestCommands
{
    private static readonly string[] ValueFlags =
        { "profile", "category", "description", "platforms", "profiles", "default", "validate", "source", "example", "required" };

    public static int Run(string[] a)
    {
        var sub = a.Length > 0 ? a[0] : null;
        var rest = a.Skip(1).ToArray();
        return sub switch
        {
            "add" => AddOrSet(rest, isAdd: true),
            "set" or "edit" => AddOrSet(rest, isAdd: false),
            "rm" or "remove" => Remove(rest),
            _ => throw new CliError("Usage: vault manifest <add|set|rm> KEY [flags]"),
        };
    }

    private static int AddOrSet(string[] a, bool isAdd)
    {
        var args = new Args(a, ValueFlags);
        var key = args.Positional0 ?? throw new CliError($"Usage: vault manifest {(isAdd ? "add" : "set")} KEY [flags]");
        if (!EnvText.IsValidKey(key))
            throw new CliError($"'{key}' is not a valid key (must be ^[A-Za-z_][A-Za-z0-9_]*$).");

        var (ctx, createdManifest) = CliContext.DiscoverOrCreate(args.Value("profile", "local"));
        if (createdManifest)
            AnsiConsole.MarkupLine($"[green]✓[/] Created a new manifest at [bold]{Markup.Escape(ctx.ManifestPath)}[/].");

        var doc = Manifest.LoadDoc(ctx.ManifestPath);
        var existing = doc.Vars.FirstOrDefault(v => v.Key == key);

        if (isAdd && existing is not null)
            throw new CliError($"{key} is already defined. Use `vault manifest set {key} …` to edit it.");
        if (!isAdd && existing is null)
            throw new CliError($"{key} is not defined. Use `vault manifest add {key} …` to create it.");

        var v = existing ?? new ManifestVar { Key = key, Secret = true };
        ApplyFlags(v, args);
        if (existing is null) doc.Vars.Add(v);
        Manifest.SaveDoc(ctx.ManifestPath, doc);

        AnsiConsole.MarkupLine($"[green]✓[/] {(existing is null ? "added" : "updated")} [bold]{Markup.Escape(key)}[/] "
            + $"[grey]({Markup.Escape(v.Category)} · {(v.Secret ? "secret" : "config")}{v.Required switch { RequiredLevel.Yes => " · required", RequiredLevel.DevOnly => " · dev-only", _ => "" }}{(v.Personal ? " · personal" : "")})[/]");
        return 0;
    }

    private static int Remove(string[] a)
    {
        var args = new Args(a, "profile");
        var key = args.Positional0 ?? throw new CliError("Usage: vault manifest rm KEY");
        var (ctx, _) = CliContext.DiscoverOrCreate(args.Value("profile", "local"));
        var doc = Manifest.LoadDoc(ctx.ManifestPath);
        int removed = doc.Vars.RemoveAll(v => v.Key == key);
        if (removed == 0) throw new CliError($"{key} is not in the manifest.");
        Manifest.SaveDoc(ctx.ManifestPath, doc);
        AnsiConsole.MarkupLine($"[green]✓[/] removed [bold]{Markup.Escape(key)}[/] from the manifest. "
            + "[grey](Its stored value, if any, is left in the vault — `vault unset` to drop it.)[/]");
        return 0;
    }

    /// <summary>Apply only the flags the user actually passed, so `set` is a partial edit.</summary>
    private static void ApplyFlags(ManifestVar v, Args args)
    {
        if (args.Value("category") is { } cat) v.Category = cat;
        if (args.Value("description") is { } desc) v.Description = desc;
        if (args.Value("default") is { } def) v.Default = def;
        if (args.Value("validate") is { } val) v.Validate = val;
        if (args.Value("source") is { } src) v.Source = src;
        if (args.Value("example") is { } ex) v.Example = ex;
        if (args.Value("platforms") is { } p) v.Platforms = SplitList(p);
        if (args.Value("profiles") is { } pr) v.Profiles = SplitList(pr);

        if (args.Has("no-required")) v.Required = RequiredLevel.No;
        if (args.Has("required"))
        {
            var lvl = args.Value("required"); // valued form (`--required devOnly`); bare `--required` → yes
            if (lvl is null) v.Required = RequiredLevel.Yes;
            else if (RequiredLevelConverter.TryParse(lvl, out var parsed)) v.Required = parsed;
            else throw new CliError($"--required must be yes|devOnly|no (got '{lvl}').");
        }
        if (args.Has("secret")) v.Secret = true;
        if (args.Has("no-secret")) v.Secret = false;
        if (args.Has("personal")) v.Personal = true;
        if (args.Has("no-personal")) v.Personal = false;
    }

    private static List<string> SplitList(string s) =>
        s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
