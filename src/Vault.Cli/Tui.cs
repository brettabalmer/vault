using System.Diagnostics;
using System.Text;
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
            case 'c': CopySelected(); return Act.Continue;
            case '/': return Act.SearchModal;
            // Switching profiles re-runs verify automatically (Reload recomputes the statuses the footer reads).
            case 'p': _profileIdx = (_profileIdx + 1) % _profiles.Length; Reload(); _status = $"profile → [bold]{Markup.Escape(_profiles[_profileIdx])}[/]"; return Act.Continue;
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
            // Green when every REQUIRED var is satisfied (optional-unset doesn't count against it).
            bool requiredUnmet = inCat.Any(s => s.State is VarState.MissingRequired or VarState.Invalid);
            var name = Markup.Escape(cat);
            var count = $"[grey]{inCat.Count}[/]";
            var dot = requiredUnmet ? "[red]●[/]" : "[green]●[/]";
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
        // Verify runs continuously: this summary is recomputed from _statuses every render, so it's always
        // current after a load, profile switch, or value save/clear — no manual step.
        int required = _statuses.Count(s => s.Var.Required);
        int attention = _statuses.Count(s => s.State is VarState.MissingRequired or VarState.Invalid);
        var verify = attention == 0
            ? $"[green]✓ verify: all {required} required present & valid[/]"
            : $"[red]✗ verify: {attention} need attention (red ●)[/]";
        var line1 = new Markup($"[grey]profile[/] [bold]{Markup.Escape(_ctx.Profile)}[/]   {verify}"
            + (_status.Length > 0 ? $"   [grey]·[/] {_status}" : ""));
        var keys = "[grey]↑↓[/] move  [grey]←→/Tab[/] pane  [grey]Enter/e[/] edit  [grey]c[/] copy  [grey]r[/] "
            + (_reveal ? "[green]hide[/]" : "reveal") + "  [grey]/[/] search  [grey]p[/] profile  [grey]q[/] quit";
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
        var current = vars[_varIdx].Value ?? "";
        string? error = null;

        // Verify the single value BEFORE saving: on a format failure we re-prompt (prefilled with what was
        // typed) so it can be corrected in place. On save/clear we Reload — which re-verifies the whole
        // profile, refreshing the footer summary and the category dots.
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(v.Key)}[/] [grey]({Markup.Escape(v.Category)})[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(v.Description)}[/]");
            if (v.Example is not null) AnsiConsole.MarkupLine($"[grey]example: {Markup.Escape(v.Example)}[/]");
            if (v.Validate is not null) AnsiConsole.MarkupLine($"[grey]must match: {Markup.Escape(v.Validate)}[/]");
            if (error is not null) AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            AnsiConsole.MarkupLine("[grey]←→ move · Home/End · Del · [/]Enter[grey]=save · empty=clear · [/]Esc[grey]=cancel[/]");
            AnsiConsole.Markup("[grey]›[/] ");

            var input = ReadLineEditable(current);
            if (input is null) { _status = "[grey]edit cancelled[/]"; return; }

            if (input.Length == 0)
            {
                if (!_ctx.PersonalFile.Unset(_ctx.Key, v.Key)) _ctx.SharedFile.Unset(_ctx.Key, v.Key);
                _status = $"[green]cleared {Markup.Escape(v.Key)}[/]";
                Reload(); RestoreSelection(v.Key); return;
            }
            if (!Manifest.PassesValidation(v, input))
            {
                error = "that value doesn't match the required format — correct it:";
                current = input; // keep the typed value so it can be fixed
                continue;
            }
            // Per-developer vars go in the gitignored personal.enc; shared vars in the committed profile file.
            (v.Personal ? _ctx.PersonalFile : _ctx.SharedFile).Set(_ctx.Key, v.Key, input);
            _status = $"[green]saved {Markup.Escape(v.Key)} ({(v.Personal ? "personal" : "shared")})[/]";
            Reload(); RestoreSelection(v.Key); return;
        }
    }

    /// <summary>
    /// A single-line editor prefilled with <paramref name="initial"/>: ←/→ move the caret, Home/End jump,
    /// Backspace/Delete remove, printable keys insert at the caret. Horizontally scrolls for values wider than
    /// the terminal. Esc → null (cancel); Enter → the buffer (empty string = clear).
    /// </summary>
    private static string? ReadLineEditable(string initial)
    {
        var buf = new StringBuilder(initial);
        int cursor = buf.Length;   // caret index within buf
        int offset = 0;            // first visible char (horizontal scroll)
        int baseLeft = Console.CursorLeft;
        int baseTop = Console.CursorTop;
        int Width() => Math.Max(8, Console.WindowWidth - baseLeft - 1);

        void Render()
        {
            int width = Width();
            if (cursor < offset) offset = cursor;
            else if (cursor - offset > width) offset = cursor - width;
            int len = Math.Max(0, Math.Min(width, buf.Length - offset));
            try
            {
                Console.SetCursorPosition(baseLeft, baseTop);
                Console.Write("\x1b[K");                    // clear to end of line
                Console.Write(buf.ToString(offset, len));
                Console.SetCursorPosition(baseLeft + (cursor - offset), baseTop);
            }
            catch (ArgumentOutOfRangeException) { /* window shrank mid-edit; skip this frame */ }
        }

        Console.Write("\x1b[?25h"); // show the caret while editing
        try
        {
            Render();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter: Console.WriteLine(); return buf.ToString();
                    case ConsoleKey.Escape: Console.WriteLine(); return null;
                    case ConsoleKey.LeftArrow: if (cursor > 0) cursor--; break;
                    case ConsoleKey.RightArrow: if (cursor < buf.Length) cursor++; break;
                    case ConsoleKey.Home: cursor = 0; break;
                    case ConsoleKey.End: cursor = buf.Length; break;
                    case ConsoleKey.Backspace: if (cursor > 0) buf.Remove(--cursor, 1); break;
                    case ConsoleKey.Delete: if (cursor < buf.Length) buf.Remove(cursor, 1); break;
                    default:
                        if (!char.IsControl(key.KeyChar)) { buf.Insert(cursor, key.KeyChar); cursor++; }
                        break;
                }
                Render();
            }
        }
        finally { Console.Write("\x1b[?25l"); } // re-hide for the TUI
    }

    private void SearchPrompt()
    {
        AnsiConsole.Clear();
        AnsiConsole.Markup("[grey]search ([/]Esc[grey]=cancel, empty=clear):[/] ");
        var input = ReadLineEditable("");
        if (input is null) return; // cancelled — leave the current filter as-is
        _search = input;
        _focus = Pane.Vars;
        _varIdx = 0;
    }

    private void CopySelected()
    {
        var vars = VisibleVars();
        if (_varIdx >= vars.Count) return;
        var s = vars[_varIdx];
        if (s.Value is null) { _status = $"[grey]{Markup.Escape(s.Var.Key)} is unset — nothing to copy[/]"; return; }
        _status = CopyToClipboard(s.Value)
            ? $"[green]copied {Markup.Escape(s.Var.Key)} to clipboard[/]"
            : "[red]clipboard unavailable[/]";
    }

    /// <summary>Best-effort clipboard copy via the platform tool (pbcopy / clip / xclip|wl-copy).</summary>
    private static bool CopyToClipboard(string text)
    {
        (string cmd, string args)[] candidates = OperatingSystem.IsMacOS()
            ? new[] { ("pbcopy", "") }
            : OperatingSystem.IsWindows()
                ? new[] { ("clip", "") }
                : new[] { ("wl-copy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") };

        foreach (var (cmd, args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, args) { RedirectStandardInput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                p.WaitForExit();
                if (p.ExitCode == 0) return true;
            }
            catch { /* try the next tool */ }
        }
        return false;
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
