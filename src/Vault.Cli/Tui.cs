using Spectre.Console;
using Spectre.Console.Rendering;
using Vault.Core;

namespace Vault.Cli;

/// <summary>
/// Full-screen two-pane master/detail secrets browser. Categories on the left, their vars on the right; color
/// status, masked values, inline edit. Not a scrolling log — it paints in the alternate screen buffer and
/// repaints on each keystroke.
/// </summary>
public sealed class Tui
{
    private readonly string[] _profiles;
    private int _profileIdx;
    private CliContext _ctx = null!;
    private Manifest _manifest = null!;
    private List<VarStatus> _statuses = new();
    private List<string> _categories = new();

    private enum Pane { Categories, Vars }
    private Pane _focus = Pane.Categories;
    private int _catIdx;
    private int _varIdx;
    private bool _reveal;
    private string _search = "";
    private string _status = "";

    private Tui(string[] profiles, int profileIdx)
    {
        _profiles = profiles;
        _profileIdx = profileIdx;
    }

    /// <summary>Entry point for `vault` with no args (interactive terminals only).</summary>
    public static int Launch(string initialProfile)
    {
        // Discover the manifest up front so a missing project fails before we grab the terminal.
        var probe = CliContext.Discover(initialProfile);
        _ = probe.Manifest;
        var profiles = DiscoverProfiles(probe.Manifest);
        int idx = Math.Max(0, Array.IndexOf(profiles, initialProfile));

        var tui = new Tui(profiles, idx);
        AltScreen(true);
        try { tui.Run(); }
        finally { AltScreen(false); }
        return 0;
    }

    private static string[] DiscoverProfiles(Manifest m)
    {
        var seen = new List<string>();
        foreach (var v in m.Vars)
            foreach (var p in v.Profiles)
                if (!seen.Contains(p)) seen.Add(p);
        if (seen.Count == 0) seen.Add("local");
        return seen.ToArray();
    }

    /// <summary>Render a single frame to a forced-color console (for verification in a non-TTY).</summary>
    public static int Snapshot(string profile, int width = 100, int height = 40)
    {
        var probe = CliContext.Discover(profile);
        _ = probe.Manifest;
        var tui = new Tui(DiscoverProfiles(probe.Manifest), 0) { _profileIdx = Math.Max(0, Array.IndexOf(DiscoverProfiles(probe.Manifest), profile)) };
        tui.Reload();
        var layout = tui.BuildLayout();
        tui.Paint(layout);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(Console.Out),
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Write(layout);
        return 0;
    }

    private void Reload()
    {
        _ctx = CliContext.Discover(_profiles[_profileIdx]);
        _manifest = _ctx.Manifest;
        _statuses = Resolve.StatusFor(_manifest, ReadVaultSafe(), _ctx.Profile);
        _categories = _statuses.Select(s => s.Var.Category).Distinct().ToList();
        _catIdx = Math.Clamp(_catIdx, 0, Math.Max(0, _categories.Count - 1));
        _varIdx = 0;
    }

    private SortedDictionary<string, string> ReadVaultSafe()
    {
        try { return _ctx.ReadVault(); }
        catch (Exception e) { _status = "[red]" + Markup.Escape(e.Message) + "[/]"; return new(StringComparer.Ordinal); }
    }

    private void Run()
    {
        Reload();
        bool running = true;
        while (running)
        {
            Action? modal = null;
            AnsiConsole.Clear();
            var layout = BuildLayout();
            AnsiConsole.Live(layout).AutoClear(false).Overflow(VerticalOverflow.Crop).Start(ctx =>
            {
                while (true)
                {
                    Paint(layout);
                    ctx.Refresh();
                    var key = Console.ReadKey(intercept: true);
                    var action = Handle(key);
                    if (action == Act.Quit) { running = false; return; }
                    if (action == Act.EditModal) { modal = EditSelected; return; }
                    if (action == Act.SearchModal) { modal = SearchPrompt; return; }
                }
            });
            modal?.Invoke();
        }
    }

    private enum Act { Continue, Quit, EditModal, SearchModal }

    private Act Handle(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q or ConsoleKey.Escape when _search.Length == 0:
                return Act.Quit;
            case ConsoleKey.Escape:
                _search = ""; _varIdx = 0; return Act.Continue;
            case ConsoleKey.UpArrow: Move(-1); return Act.Continue;
            case ConsoleKey.DownArrow: Move(+1); return Act.Continue;
            case ConsoleKey.LeftArrow: _focus = Pane.Categories; return Act.Continue;
            case ConsoleKey.RightArrow: _focus = Pane.Vars; _varIdx = 0; return Act.Continue;
            case ConsoleKey.Tab: _focus = _focus == Pane.Categories ? Pane.Vars : Pane.Categories; return Act.Continue;
            case ConsoleKey.Enter:
                if (_focus == Pane.Categories) { _focus = Pane.Vars; _varIdx = 0; return Act.Continue; }
                return Act.EditModal;
        }
        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'e': _focus = Pane.Vars; return Act.EditModal;
            case 'r': _reveal = !_reveal; return Act.Continue;
            case '/': return Act.SearchModal;
            case 'p': _profileIdx = (_profileIdx + 1) % _profiles.Length; Reload(); _status = $"profile → [bold]{_profiles[_profileIdx]}[/]"; return Act.Continue;
            case 'c': RunCheck(); return Act.Continue;
        }
        return Act.Continue;
    }

    private void Move(int delta)
    {
        if (_focus == Pane.Categories)
        {
            if (_categories.Count == 0) return;
            _catIdx = (_catIdx + delta + _categories.Count) % _categories.Count;
            _varIdx = 0;
        }
        else
        {
            var vars = VisibleVars();
            if (vars.Count == 0) return;
            _varIdx = (_varIdx + delta + vars.Count) % vars.Count;
        }
    }

    private List<VarStatus> VisibleVars()
    {
        var cat = _categories.Count > 0 ? _categories[_catIdx] : null;
        return _statuses.Where(s =>
            (cat is null || s.Var.Category == cat) &&
            (_search.Length == 0 || s.Var.Key.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ---- rendering ---------------------------------------------------------

    private Layout BuildLayout() =>
        new Layout("root").SplitRows(
            new Layout("body").SplitColumns(
                new Layout("cats").Size(30),
                new Layout("vars")),
            new Layout("foot").Size(4));

    private void Paint(Layout layout)
    {
        layout["cats"].Update(CategoriesPanel());
        layout["vars"].Update(VarsPanel());
        layout["foot"].Update(FooterPanel());
    }

    private IRenderable CategoriesPanel()
    {
        var rows = new List<IRenderable>();
        for (int i = 0; i < _categories.Count; i++)
        {
            var cat = _categories[i];
            var inCat = _statuses.Where(s => s.Var.Category == cat).ToList();
            int set = inCat.Count(s => s.State == VarState.Set);
            bool bad = inCat.Any(s => s.State is VarState.MissingRequired or VarState.Invalid);
            var name = Markup.Escape(cat);
            var count = $"[grey]{set}/{inCat.Count}[/]";
            var dot = bad ? "[red]●[/]" : (set == inCat.Count ? "[green]●[/]" : "[grey]○[/]");
            var label = (i == _catIdx && _focus == Pane.Categories)
                ? $"[black on white] {dot} {name} {count} [/]"
                : $" {dot} {name} {count}";
            rows.Add(new Markup(label));
        }
        return new Panel(new Rows(rows)).Header("[bold]Categories[/]").Expand().Border(BoxBorder.Rounded)
            .BorderColor(_focus == Pane.Categories ? Color.Aqua : Color.Grey);
    }

    private IRenderable VarsPanel()
    {
        var vars = VisibleVars();
        _varIdx = Math.Clamp(_varIdx, 0, Math.Max(0, vars.Count - 1));
        var rows = new List<IRenderable>();
        for (int i = 0; i < vars.Count; i++)
        {
            var s = vars[i];
            bool sel = i == _varIdx && _focus == Pane.Vars;
            var glyph = s.State switch
            {
                VarState.Set => "[green]●[/]",
                VarState.MissingRequired => "[red]●[/]",
                VarState.Invalid => "[yellow]▲[/]",
                _ => "[grey]○[/]",
            };
            var badge = s.Var.Required ? "[red]REQ[/]" : "[grey]opt[/]";
            var key = sel ? $"[black on white] {Markup.Escape(s.Var.Key)} [/]" : $"[bold]{Markup.Escape(s.Var.Key)}[/]";
            rows.Add(new Markup($"{glyph} {badge} {key}"));
            rows.Add(new Markup($"    {ValueLine(s)}"));
            rows.Add(new Markup($"    [grey]{Markup.Escape(Truncate(s.Var.Description, 70))}[/]"));
        }
        if (vars.Count == 0) rows.Add(new Markup("[grey]  (no matching vars)[/]"));
        var title = _search.Length > 0 ? $"Vars — search: [yellow]{Markup.Escape(_search)}[/]"
            : (_categories.Count > 0 ? $"Vars — {Markup.Escape(_categories[_catIdx])}" : "Vars");
        return new Panel(new Rows(rows)).Header($"[bold]{title}[/]").Expand().Border(BoxBorder.Rounded)
            .BorderColor(_focus == Pane.Vars ? Color.Aqua : Color.Grey);
    }

    private string ValueLine(VarStatus s)
    {
        if (s.State == VarState.MissingRequired) return "[red]required — not set[/]";
        if (s.State == VarState.MissingOptional) return "[grey]—[/]";
        if (s.State == VarState.Invalid) return "[yellow]invalid value[/]";
        var raw = s.Value ?? "";
        string shown = (!s.Var.Secret || _reveal) ? Markup.Escape(raw) : MaskMarkup(raw);
        return shown + (s.FromDefault ? " [grey](default)[/]" : "");
    }

    private IRenderable FooterPanel()
    {
        var keys = "[grey]↑↓[/] move  [grey]←→/Tab[/] pane  [grey]Enter/e[/] edit  [grey]r[/] "
            + (_reveal ? "[green]hide[/]" : "reveal") + "  [grey]/[/] search  [grey]p[/] profile  [grey]c[/] check  [grey]q[/] quit";
        var line1 = new Markup($"profile [bold]{Markup.Escape(_ctx.Profile)}[/]   " + (_status.Length > 0 ? _status : "[grey]ready[/]"));
        return new Panel(new Rows(line1, new Markup(keys))).Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string MaskMarkup(string v)
    {
        if (v.Length == 0) return "[grey](empty)[/]";
        if (v.Length <= 4) return "[grey]••••[/]";
        return $"[grey]{Markup.Escape(v[..2])}…{Markup.Escape(v[^2..])} ({v.Length})[/]";
    }

    // ---- modals (run outside the live view) --------------------------------

    private void EditSelected()
    {
        var vars = VisibleVars();
        if (_varIdx >= vars.Count) return;
        var v = vars[_varIdx].Var;
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(v.Key)}[/] [grey]({Markup.Escape(v.Category)})[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(v.Description)}[/]");
        if (v.Example is not null) AnsiConsole.MarkupLine($"[grey]example: {Markup.Escape(v.Example)}[/]");
        AnsiConsole.MarkupLine("[grey]Enter a new value (blank to cancel):[/]");
        var input = AnsiConsole.Prompt(new TextPrompt<string>(">").AllowEmpty());
        if (!string.IsNullOrEmpty(input))
        {
            if (!Manifest.PassesValidation(v, input)) { _status = $"[red]value rejected: fails {Markup.Escape(v.Validate ?? "")}[/]"; }
            else { _ctx.ProfileFile.Set(_ctx.Key, v.Key, input); _status = $"[green]set {Markup.Escape(v.Key)}[/]"; Reload(); RestoreSelection(v.Key); }
        }
    }

    private void SearchPrompt()
    {
        AnsiConsole.Clear();
        _search = AnsiConsole.Prompt(new TextPrompt<string>("[grey]search keys:[/]").AllowEmpty());
        _focus = Pane.Vars;
        _varIdx = 0;
    }

    private void RunCheck()
    {
        var failures = Resolve.Failures(_manifest, ReadVaultSafe(), _ctx.Profile);
        _status = failures.Count == 0
            ? "[green]✓ check passed[/]"
            : $"[red]✗ {failures.Count} required/invalid[/]";
    }

    private void RestoreSelection(string key)
    {
        var vars = VisibleVars();
        var i = vars.FindIndex(s => s.Var.Key == key);
        if (i >= 0) _varIdx = i;
    }

    private static void AltScreen(bool on)
    {
        // Enter/leave the alternate buffer and hide/show the cursor.
        Console.Write(on ? "\x1b[?1049h\x1b[?25l" : "\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
    }
}
