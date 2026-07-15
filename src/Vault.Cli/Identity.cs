using System.Diagnostics;
using Spectre.Console;
using Vault.Core;

namespace Vault.Cli;

/// <summary>
/// Vault identity commands: <c>init</c> (assign/replace a vault's identity), <c>rekey</c> (rotate the key,
/// preserving values), <c>share-key</c> (copy the <c>id :: key</c> pairing to the clipboard), and
/// <c>add-key</c> (paste a shared pairing into the keyring).
/// </summary>
internal static class IdentityCommands
{
    // ---- init -------------------------------------------------------------

    public static int Init(string[] a)
    {
        var args = new Args(a, "profile");
        var (ctx, createdManifest) = CliContext.DiscoverOrCreate(args.Value("profile", "local"));
        if (createdManifest)
            AnsiConsole.MarkupLine($"[green]✓[/] Created a new manifest at [bold]{Markup.Escape(ctx.ManifestPath)}[/] "
                + "[grey](define vars with `vault manifest add`).[/]");
        var existingId = ctx.VaultId;

        if (existingId is null)
        {
            // Fresh or legacy (identity-less) vault: assign an id and KEEP the current key — non-destructive.
            var oldKey = TryResolveKey(existingId);
            var newKey = oldKey ?? KeyStore.NewKey();
            var newId = VaultIdentity.NewId();
            var dropped = Reidentify(ctx, newId, newKey, oldKey);
            KeyStore.AddPair(newId, newKey);
            AnsiConsole.MarkupLine($"[green]✓[/] Initialised this vault with identity [bold]{newId}[/] "
                + $"[grey]({(oldKey is null ? "new key generated" : "existing key kept")})[/].");
            ReportDropped(dropped);
            ShareHint(newId);
            return 0;
        }

        // Already identified: this is the "cloned a repo, want different values" reset.
        AnsiConsole.MarkupLine($"[yellow]This vault already has identity [bold]{existingId}[/].[/]");
        if (!args.Has("force") && !Confirm(
                "Re-initialise with a NEW identity + key? Existing secrets are re-encrypted if you hold the "
                + "current key, otherwise WIPED (plaintext config is kept)."))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled — nothing changed.[/]");
            return 1;
        }
        var reKey = KeyStore.NewKey();
        var reId = VaultIdentity.NewId();
        var current = TryResolveKey(existingId);
        var dropped2 = Reidentify(ctx, reId, reKey, current);
        KeyStore.AddPair(reId, reKey);
        AnsiConsole.MarkupLine($"[green]✓[/] Re-initialised: identity [bold]{existingId}[/] → [bold]{reId}[/], new key.");
        if (current is null)
            AnsiConsole.MarkupLine("[yellow]![/] You didn't hold the old key — encrypted secrets were wiped; re-enter them with `vault set`.");
        ReportDropped(dropped2);
        ShareHint(reId);
        return 0;
    }

    // ---- rekey (safe rotation) --------------------------------------------

    public static int Rekey(string[] a)
    {
        var args = new Args(a, "profile");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var existingId = ctx.VaultId;

        var oldKey = TryResolveKey(existingId);
        if (oldKey is null)
            throw new CliError("Can't rekey — the current key isn't in your keyring, so values can't be preserved. "
                + "Use `vault init --force` to reset (this wipes unreadable secrets).");

        // Prove the key actually decrypts before we rotate (a stale bare-key fallback would silently corrupt).
        try { ctx.SharedFile.Read(oldKey); }
        catch (VaultCryptoException) { throw new CliError("The resolved key does not decrypt this vault — refusing to rekey. Use `vault init --force` to reset."); }

        var newId = VaultIdentity.NewId();
        var newKey = KeyStore.NewKey();
        Reidentify(ctx, newId, newKey, oldKey);
        KeyStore.AddPair(newId, newKey);
        AnsiConsole.MarkupLine($"[green]✓[/] Rekeyed: {(existingId is null ? "identity assigned" : $"identity [bold]{existingId}[/] →")} [bold]{newId}[/], values preserved.");
        AnsiConsole.MarkupLine("[grey]Commit the changed .enc files, and re-share the key with teammates (`vault share-key`).[/]");
        ShareHint(newId);
        return 0;
    }

    // ---- share-key --------------------------------------------------------

    public static int ShareKey(string[] a)
    {
        var args = new Args(a, "profile");
        var ctx = CliContext.Discover(args.Value("profile", "local"));
        var id = ctx.VaultId ?? throw new CliError("This vault has no identity yet. Run `vault init` first.");
        var key = KeyStore.LoadFor(id);
        var pairing = $"{id} :: {Convert.ToBase64String(key)}";

        if (args.Has("stdout"))
        {
            Console.WriteLine(pairing);
            return 0;
        }
        if (Clipboard.TryCopy(pairing))
            AnsiConsole.MarkupLine($"[green]✓[/] Copied the key pairing for [bold]{id}[/] to your clipboard.\n"
                + "[grey]Send it to a teammate over a secure channel (1Password/Signal — never plaintext chat). "
                + "They run `vault add-key` and paste it.[/]");
        else
            AnsiConsole.MarkupLine($"[yellow]![/] Couldn't reach a clipboard tool. Here it is — copy it manually:\n[bold]{Markup.Escape(pairing)}[/]");
        return 0;
    }

    // ---- add-key ----------------------------------------------------------

    public static int AddKey(string[] a)
    {
        var args = new Args(a);
        string pairing;
        if (args.Has("stdin")) pairing = Console.In.ReadToEnd().Trim();
        else if (args.Has("clipboard")) pairing = Clipboard.TryPaste() ?? throw new CliError("Couldn't read the clipboard. Pass the pairing as an argument or via --stdin.");
        else pairing = args.Positional0 ?? throw new CliError("Usage: vault add-key \"<id> :: <base64 key>\"   |   vault add-key --stdin   |   vault add-key --clipboard");

        var ring = KeyStore.Parse(pairing, "input");
        if (ring.ById.Count != 1)
            throw new CliError("Expected exactly one `<id> :: <key>` pairing.");
        var (id, key) = ring.ById.First();
        var path = KeyStore.AddPair(id, key);
        AnsiConsole.MarkupLine($"[green]✓[/] Added the key for vault [bold]{id}[/] to [grey]{Markup.Escape(path)}[/]. `vault check` should work now.");
        return 0;
    }

    // ---- core: re-write every profile under a new identity/key -------------

    /// <summary>
    /// Re-stamp+re-encrypt every <c>*.enc</c> in the vault dir under <paramref name="newId"/>/<paramref name="newKey"/>.
    /// With <paramref name="oldKey"/> the encrypted values are preserved; without it only plaintext config survives
    /// (unreadable secrets are dropped and returned per profile).
    /// </summary>
    private static Dictionary<string, List<string>> Reidentify(CliContext ctx, string newId, byte[] newKey, byte[]? oldKey)
    {
        var dropped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var files = Directory.Exists(ctx.VaultDir)
            ? Directory.GetFiles(ctx.VaultDir, "*.enc").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();

        foreach (var path in files)
        {
            var profile = Path.GetFileNameWithoutExtension(path);
            var file = new VaultFile(ctx.VaultDir, profile);
            SortedDictionary<string, string> map;
            if (oldKey is not null)
            {
                try { map = file.Read(oldKey); }
                catch (VaultCryptoException) { map = VaultIdentity.ReadPlaintextOnly(file, out var d); dropped[profile] = d; }
            }
            else
            {
                map = VaultIdentity.ReadPlaintextOnly(file, out var d);
                if (d.Count > 0) dropped[profile] = d;
            }
            file.Write(newKey, map, ctx.IsSecret, newId);
        }

        // Fresh vault (no .enc yet): create an empty, identified local.enc so the identity has an anchor.
        if (files.Count == 0)
            ctx.SharedFile.Write(newKey, new SortedDictionary<string, string>(StringComparer.Ordinal), ctx.IsSecret, newId);

        return dropped;
    }

    private static byte[]? TryResolveKey(string? id)
    {
        try { return KeyStore.LoadFor(id); }
        catch (VaultKeyNotFoundException) { return null; }
    }

    private static void ReportDropped(Dictionary<string, List<string>> dropped)
    {
        foreach (var (profile, keys) in dropped)
            if (keys.Count > 0)
                AnsiConsole.MarkupLine($"[yellow]![/] {keys.Count} secret(s) in [bold]{Markup.Escape(profile)}[/] couldn't be decrypted and were dropped: [grey]{Markup.Escape(string.Join(", ", keys))}[/]");
    }

    private static void ShareHint(string id) =>
        AnsiConsole.MarkupLine($"[grey]Share it with teammates: [/]vault share-key[grey] (copies `{id} :: <key>` to your clipboard).[/]");

    private static bool Confirm(string prompt)
    {
        if (Console.IsInputRedirected) return false; // non-interactive → treat as No; use --force to proceed
        return AnsiConsole.Confirm($"[yellow]{Markup.Escape(prompt)}[/]", defaultValue: false);
    }
}

/// <summary>Best-effort clipboard access via the platform tool (pbcopy/clip/wl-copy/xclip).</summary>
internal static class Clipboard
{
    public static bool TryCopy(string text)
    {
        foreach (var (exe, argv) in CopyCandidates())
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, UseShellExecute = false };
                foreach (var arg in argv) psi.ArgumentList.Add(arg);
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                p.WaitForExit();
                if (p.ExitCode == 0) return true;
            }
            catch { /* try the next candidate */ }
        }
        return false;
    }

    public static string? TryPaste()
    {
        foreach (var (exe, argv) in PasteCandidates())
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var arg in argv) psi.ArgumentList.Add(arg);
                using var p = Process.Start(psi);
                if (p is null) continue;
                var outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) return outp.Trim();
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    private static IEnumerable<(string, string[])> CopyCandidates()
    {
        if (OperatingSystem.IsMacOS()) { yield return ("pbcopy", Array.Empty<string>()); yield break; }
        if (OperatingSystem.IsWindows()) { yield return ("clip", Array.Empty<string>()); yield break; }
        yield return ("wl-copy", Array.Empty<string>());
        yield return ("xclip", new[] { "-selection", "clipboard" });
        yield return ("xsel", new[] { "--clipboard", "--input" });
    }

    private static IEnumerable<(string, string[])> PasteCandidates()
    {
        if (OperatingSystem.IsMacOS()) { yield return ("pbpaste", Array.Empty<string>()); yield break; }
        if (OperatingSystem.IsWindows()) { yield return ("powershell", new[] { "-NoProfile", "-Command", "Get-Clipboard" }); yield break; }
        yield return ("wl-paste", Array.Empty<string>());
        yield return ("xclip", new[] { "-selection", "clipboard", "-o" });
        yield return ("xsel", new[] { "--clipboard", "--output" });
    }
}
