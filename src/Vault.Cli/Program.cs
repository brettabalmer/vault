using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;
using Vault.Core;

namespace Vault.Cli;

public static class Program
{
    public static int Main(string[] argv)
    {
        try
        {
            if (argv.Length == 0)
            {
                if (Console.IsInputRedirected || Console.IsOutputRedirected) { Help(); return 0; }
                return Tui.Launch("local");
            }
            var command = argv[0];
            var rest = argv.Skip(1).ToArray();
            return command switch
            {
                "keygen" => Keygen(rest),
                "manifest" => ManifestCommands.Run(rest),
                "init" => IdentityCommands.Init(rest),
                "rekey" => IdentityCommands.Rekey(rest),
                "share-key" => IdentityCommands.ShareKey(rest),
                "add-key" => IdentityCommands.AddKey(rest),
                "check" or "verify" => Check(rest),
                "list" => List(rest),
                "get" => Get(rest),
                "set" => Set(rest),
                "unset" => Unset(rest),
                "describe" => Describe(rest),
                "missing" => Missing(rest),
                "export" => Export(rest),
                "run" => Run(rest),
                "import" => Import(rest),
                "snapshot" => Tui.Snapshot(new Args(rest, "profile").Value("profile", "local")),
                "-h" or "--help" or "help" => Ret(Help),
                _ => Unknown(command),
            };
        }
        catch (CliError e) { Error(e.Message); return 1; }
        catch (VaultKeyNotFoundException e) { Error(e.Message); return 1; }
        catch (VaultCryptoException e) { Error(e.Message); return 1; }
        catch (FileNotFoundException e) { Error(e.Message); return 1; }
        catch (InvalidOperationException e) { Error(e.Message); return 1; }
        catch (ArgumentException e) { Error(e.Message); return 1; }
    }

    // ---- commands ----------------------------------------------------------

    private static int Keygen(string[] a)
    {
        var args = new Args(a);
        // Legacy: write a bare (identity-less) key. Prefer `vault init`, which gives the vault an identity.
        var path = KeyStore.SetLegacyBare(KeyStore.NewKey(), force: args.Has("force"));
        AnsiConsole.MarkupLine($"[green]✓[/] Wrote a legacy bare key to [bold]{Markup.Escape(path)}[/]");
        AnsiConsole.MarkupLine("[grey]Tip: `vault init` gives this vault its own identity + keyring entry (recommended for multi-repo use).[/]");
        return 0;
    }

    private static int Check(string[] a)
    {
        var args = new Args(a, "profile");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var failures = Resolve.Failures(ctx.Manifest, ctx.ReadVault(), ctx.Profile);
        if (failures.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓ verify passed[/] [grey]— every required value is present and matches its format (profile {Markup.Escape(ctx.Profile)})[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"[red]✗ verify failed[/] [grey](profile {Markup.Escape(ctx.Profile)})[/] — {failures.Count} required var(s) missing or failing validation:\n");
        foreach (var f in failures)
        {
            var why = f.State == VarState.Invalid ? "value fails validation" : "required but not set";
            AnsiConsole.MarkupLine($"  [red]•[/] [bold]{Markup.Escape(f.Var.Key)}[/] [grey]({Markup.Escape(f.Var.Category)})[/] — {why}");
            AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(f.Var.Description)}[/]");
            AnsiConsole.MarkupLine($"    [grey]fix:[/] vault set {Markup.Escape(f.Var.Key)} --stdin");
        }
        return 1;
    }

    private static int List(string[] a)
    {
        var args = new Args(a, "profile", "category", "platform");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var statuses = Resolve.StatusFor(ctx.Manifest, ctx.ReadVault(), ctx.Profile);

        var category = args.Value("category");
        var platform = args.Value("platform");
        var onlyMissing = args.Has("missing");
        var filtered = statuses.Where(s =>
            (category is null || string.Equals(s.Var.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (platform is null || s.Var.Platforms.Contains(platform)) &&
            (!onlyMissing || s.State is VarState.MissingRequired or VarState.MissingOptional)).ToList();

        if (args.Has("json")) { EmitJson(filtered); return 0; }

        var personalKeys = ctx.PersonalKeys();
        foreach (var group in filtered.GroupBy(s => s.Var.Category))
        {
            AnsiConsole.MarkupLine($"\n[bold underline]{Markup.Escape(group.Key)}[/]");
            var table = new Table().Border(TableBorder.None).HideHeaders();
            table.AddColumn("s"); table.AddColumn("k"); table.AddColumn("v");
            foreach (var s in group)
                table.AddRow(StatusGlyph(s.State), $"[bold]{Markup.Escape(s.Var.Key)}[/]", RenderValue(s, personalKeys.Contains(s.Var.Key)));
            AnsiConsole.Write(table);
        }
        Summarize(filtered);
        return 0;
    }

    private static int Get(string[] a)
    {
        var args = new Args(a, "profile");
        var key = args.Positional0 ?? throw new CliError("Usage: vault get KEY [--reveal]");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var vault = ctx.ReadVault();
        if (!vault.TryGetValue(key, out var val))
        {
            var v = ctx.Manifest.Find(key);
            if (v?.Default is not null) { Console.WriteLine(v.Default); return 0; }
            throw new CliError($"{key} has no value in profile '{ctx.Profile}'.");
        }
        var mv = ctx.Manifest.Find(key);
        bool secret = mv?.Secret ?? true;
        Console.WriteLine(secret && !args.Has("reveal") ? MaskPlain(val) : val);
        return 0;
    }

    private static int Set(string[] a)
    {
        var args = new Args(a, "profile");
        var key = args.Positional0 ?? throw new CliError("Usage: vault set KEY VALUE   |   vault set KEY --stdin");
        var ctx = CliContext.Discover(args.Value("profile", "local"));

        string value;
        if (args.Has("stdin")) value = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        else value = args.Positional1 ?? throw new CliError("No value given. Pass it as an argument or use --stdin.");

        var mv = ctx.Manifest.Find(key);
        if (mv is null)
            AnsiConsole.MarkupLine($"[yellow]![/] '{Markup.Escape(key)}' is not in the manifest — setting it anyway (add a manifest entry to describe it).");
        else if (!Manifest.PassesValidation(mv, value))
            throw new CliError($"Value for {key} fails its validation regex ({mv.Validate}).");

        // Personal (per-developer) vars go in the gitignored personal.enc; --personal/--shared force it.
        bool personal = args.Has("personal") || (mv?.Personal ?? false);
        if (args.Has("shared")) personal = false;
        var target = personal ? ctx.PersonalFile : ctx.SharedFile;
        var where = personal ? "personal · not committed" : $"shared · profile {ctx.Profile}";

        var prior = target.Set(ctx.Key, key, value, ctx.IsSecret);
        AnsiConsole.MarkupLine($"[green]✓[/] {(prior is null ? "set" : "updated")} [bold]{Markup.Escape(key)}[/] [grey]({Markup.Escape(where)})[/]");
        return 0;
    }

    private static int Unset(string[] a)
    {
        var args = new Args(a, "profile");
        var key = args.Positional0 ?? throw new CliError("Usage: vault unset KEY");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        // Prefer removing a personal override (revert to shared/default); else remove the shared value.
        string? from = ctx.PersonalFile.Unset(ctx.Key, key, ctx.IsSecret) ? "personal"
            : ctx.SharedFile.Unset(ctx.Key, key, ctx.IsSecret) ? $"shared · profile {ctx.Profile}"
            : null;
        AnsiConsole.MarkupLine(from is not null
            ? $"[green]✓[/] removed [bold]{Markup.Escape(key)}[/] [grey]({Markup.Escape(from)})[/]"
            : $"[grey]{Markup.Escape(key)} was not set — nothing to do.[/]");
        return 0;
    }

    private static int Describe(string[] a)
    {
        var args = new Args(a, "profile");
        var key = args.Positional0 ?? throw new CliError("Usage: vault describe KEY");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var v = ctx.Manifest.Find(key) ?? throw new CliError($"{key} is not in the manifest.");
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]key[/]", $"[bold]{Markup.Escape(v.Key)}[/]");
        grid.AddRow("[grey]category[/]", Markup.Escape(v.Category));
        grid.AddRow("[grey]description[/]", Markup.Escape(v.Description));
        grid.AddRow("[grey]required[/]", v.Required switch
        {
            RequiredLevel.Yes => "[yellow]yes[/]",
            RequiredLevel.DevOnly => "[yellow]devOnly[/] [grey](local dev only — not checked in cloud)[/]",
            _ => "no",
        });
        grid.AddRow("[grey]secret[/]", v.Secret ? "yes" : "no");
        grid.AddRow("[grey]personal[/]", v.Personal ? "[blue]yes — per-developer (personal.enc, not committed)[/]" : "no (shared)");
        grid.AddRow("[grey]platforms[/]", Markup.Escape(string.Join(", ", v.Platforms)));
        grid.AddRow("[grey]profiles[/]", Markup.Escape(string.Join(", ", v.Profiles)));
        if (v.Source is not null) grid.AddRow("[grey]source[/]", Markup.Escape(v.Source));
        if (v.Example is not null) grid.AddRow("[grey]example[/]", Markup.Escape(v.Example));
        if (v.Default is not null) grid.AddRow("[grey]default[/]", Markup.Escape(v.Default));
        AnsiConsole.Write(grid);
        return 0;
    }

    private static int Missing(string[] a)
    {
        var args = new Args(a, "profile");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var missing = Resolve.StatusFor(ctx.Manifest, ctx.ReadVault(), ctx.Profile)
            .Where(s => s.State is VarState.MissingRequired).ToList();
        if (args.Has("json")) { EmitJson(missing); return 0; }
        if (missing.Count == 0) { AnsiConsole.MarkupLine("[green]✓ nothing required is missing.[/]"); return 0; }
        foreach (var s in missing)
            AnsiConsole.MarkupLine($"[red]•[/] [bold]{Markup.Escape(s.Var.Key)}[/] [grey]({Markup.Escape(s.Var.Category)}, source={Markup.Escape(s.Var.Source ?? "?")})[/] — {Markup.Escape(s.Var.Description)}");
        return 0;
    }

    private static int Export(string[] a)
    {
        var args = new Args(a, "profile", "platform", "format");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var platform = args.Value("platform") ?? throw new CliError("Usage: vault export --platform <name> [--format dotenv|json|shell] [--no-defaults]");
        var map = Resolve.ForPlatform(ctx.Manifest, ctx.ReadVault(), platform, ctx.Profile, includeDefaults: !args.Has("no-defaults"));
        var format = args.Value("format", "dotenv");
        switch (format)
        {
            case "dotenv":
                foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Console.WriteLine($"{k}={v}");
                break;
            case "shell":
                foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Console.WriteLine($"export {k}='{v.Replace("'", "'\\''")}'");
                break;
            case "json":
                Console.WriteLine(JsonSerializer.Serialize(map, JsonMapContext.Default.DictionaryStringString));
                break;
            default: throw new CliError($"Unknown --format '{format}' (dotenv|json|shell).");
        }
        return 0;
    }

    private static int Run(string[] a)
    {
        var args = new Args(a, "profile", "platform");
        var cmd = args.PassThrough ?? throw new CliError("Usage: vault run [--profile p] -- <command> [args…]");
        if (cmd.Length == 0) throw new CliError("No command after `--`.");
        var ctx = CliContext.Discover(args.Value("profile", "local"));

        // Inject every value that belongs to this profile (union across platforms), only for keys not set.
        var seeded = Resolve.StatusFor(ctx.Manifest, ctx.ReadVault(), ctx.Profile)
            .Where(s => s.State == VarState.Set && s.Value is not null);
        var psi = new ProcessStartInfo { FileName = cmd[0], UseShellExecute = false };
        for (int i = 1; i < cmd.Length; i++) psi.ArgumentList.Add(cmd[i]);
        foreach (var s in seeded)
            if (Environment.GetEnvironmentVariable(s.Var.Key) is null) psi.Environment[s.Var.Key] = s.Value!;

        using var proc = Process.Start(psi) ?? throw new CliError($"Failed to start '{cmd[0]}'.");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static int Import(string[] a)
    {
        var args = new Args(a, "profile", "from");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var from = args.Value("from", ".");
        var (found, unmapped) = ImportScan.Run(ctx, from);

        AnsiConsole.MarkupLine($"[green]✓[/] imported [bold]{found}[/] value(s) into profile [bold]{Markup.Escape(ctx.Profile)}[/].");
        if (unmapped.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] {unmapped.Count} key(s) found in files but not in the manifest (skipped):");
            foreach (var k in unmapped.OrderBy(x => x, StringComparer.Ordinal))
                AnsiConsole.MarkupLine($"  [grey]•[/] {Markup.Escape(k)}");
        }
        return 0;
    }

    // ---- rendering helpers -------------------------------------------------

    private static string StatusGlyph(VarState s) => s switch
    {
        VarState.Set => "[green]●[/]",
        VarState.MissingRequired => "[red]●[/]",
        VarState.MissingOptional => "[grey]○[/]",
        VarState.Invalid => "[yellow]▲[/]",
        _ => " ",
    };

    private static string RenderValue(VarStatus s, bool isPersonal = false)
    {
        if (s.State is VarState.MissingRequired) return "[red]required, not set[/]";
        if (s.State is VarState.MissingOptional) return "[grey]—[/]";
        if (s.State is VarState.Invalid) return "[yellow]invalid value[/]";
        var shown = s.Var.Secret ? Mask(s.Value!) : Markup.Escape(s.Value!);
        if (s.FromDefault) return $"{shown} [grey](default)[/]";
        if (isPersonal) return $"{shown} [blue](personal)[/]";
        return shown;
    }

    private static void Summarize(IReadOnlyList<VarStatus> items)
    {
        int set = items.Count(i => i.State == VarState.Set);
        int reqMissing = items.Count(i => i.State == VarState.MissingRequired);
        int invalid = items.Count(i => i.State == VarState.Invalid);
        AnsiConsole.MarkupLine($"\n[grey]{set}/{items.Count} set[/]"
            + (reqMissing > 0 ? $" · [red]{reqMissing} required missing[/]" : "")
            + (invalid > 0 ? $" · [yellow]{invalid} invalid[/]" : ""));
    }

    private static string Mask(string v)
    {
        if (v.Length == 0) return "[grey](empty)[/]";
        if (v.Length <= 4) return "[grey]••••[/]";
        return $"[grey]{Markup.Escape(v[..2])}…{Markup.Escape(v[^2..])} ({v.Length})[/]";
    }

    /// <summary>Plain-text (no markup) mask for machine-adjacent output like `get`.</summary>
    private static string MaskPlain(string v)
    {
        if (v.Length == 0) return "(empty)";
        if (v.Length <= 4) return "••••";
        return $"{v[..2]}…{v[^2..]} ({v.Length})";
    }

    private static void EmitJson(IReadOnlyList<VarStatus> items)
    {
        var reports = items.Select(s => new VarReport
        {
            Key = s.Var.Key, Category = s.Var.Category, Description = s.Var.Description,
            Required = RequiredLevelConverter.ToWire(s.Var.Required), Secret = s.Var.Secret, Personal = s.Var.Personal, State = s.State.ToString(),
            Platforms = s.Var.Platforms, Example = s.Var.Example, Source = s.Var.Source,
        }).ToList();
        Console.WriteLine(JsonSerializer.Serialize(reports, JsonOutputContext.Default.ListVarReport));
    }

    private static void Error(string message) => AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");

    private static int Unknown(string cmd)
    {
        Error($"unknown command '{cmd}'. Run `vault --help`.");
        return 1;
    }

    private static int Ret(Action a) { a(); return 0; }

    private static void Help()
    {
        AnsiConsole.MarkupLine("[bold]vault[/] — encrypted secrets for local dev\n");
        var t = new Table().Border(TableBorder.None).HideHeaders();
        t.AddColumn("c"); t.AddColumn("d");
        void Row(string c, string d) => t.AddRow($"[green]{Markup.Escape(c)}[/]", $"[grey]{Markup.Escape(d)}[/]");
        Row("vault", "launch the full-screen TUI (coming soon)");
        Row("vault check | verify", "validate every required var is present + values match their format; nonzero exit on failure");
        Row("vault list [--category X] [--platform Y] [--missing] [--json]", "show status");
        Row("vault missing [--json]", "required-but-unset vars (agent-friendly)");
        Row("vault get KEY [--reveal]", "print one value (masked by default)");
        Row("vault set KEY VALUE | KEY --stdin [--personal]", "set one value (--personal → your gitignored personal.enc, per-developer)");
        Row("vault unset KEY", "remove one value");
        Row("vault describe KEY", "show a var's metadata");
        Row("vault export --platform P [--format dotenv|json|shell] [--no-defaults]", "materialize a platform slice (--no-defaults = vault values only, for cloud pushes)");
        Row("vault run [--profile p] -- CMD", "run CMD with the vault injected into its env");
        Row("vault import --from DIR", "one-time migration from scattered env files");
        Row("vault manifest add KEY [--category C --secret --required --platforms a,b …]", "define a var (creates vault/manifest.json if absent)");
        Row("vault manifest set KEY [--no-secret --default V …]", "edit an existing var's fields");
        Row("vault manifest rm KEY", "remove a var definition");
        Row("vault init [--force]", "give this vault an identity (new id; keeps the key on a legacy vault, new key on reset) — creates a manifest if none exists");
        Row("vault rekey", "rotate to a new id + key, preserving values (needs the current key)");
        Row("vault share-key [--stdout]", "copy this vault's `id :: key` pairing to the clipboard for a teammate");
        Row("vault add-key \"<id> :: <key>\" | --stdin | --clipboard", "add a teammate's shared pairing to your keyring");
        Row("vault keygen [--force]", "write a legacy bare key (prefer `vault init`)");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("\n[grey]Global: --profile <local|azure-dev|azure-prod> (default local).[/]");
    }
}
