using Armux.Core;
using Layout = Armux.Core.Layout;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Armux.App;

public sealed partial class MainWindow
{
    /// <summary>A palette command: its group in Settings > Keyboard, and what it runs (false: the key was not used).</summary>
    sealed record CommandEntry(string Id, string Group, Func<bool> Run);

    readonly List<CommandEntry> entries = [];
    readonly List<(string Chord, string Description, Action Run)> fixedKeys = [];
    List<Command> commands = [];
    readonly Dictionary<Chord, (string Id, Func<bool> Run)> keymap = [];
    /// <summary>The last chord the window's key handler saw, and when (see <see cref="OnPageKey"/>).</summary>
    (Chord Chord, long At) lastChord;
    Keymap keys = new(new Dictionary<string, string>());
    string group = "";
    int simulated;

    /// <summary>Adds a palette command with its default shortcut (ARCHITECTURE.md section 8); Settings can rebind it.</summary>
    void Cmd(string title, string shortcut, Action run) => CmdIf(title, shortcut, () => { run(); return true; });

    /// <summary>
    /// A command that can decline its key: when it returns false the key goes on to the focused control
    /// (Ctrl+V in a text box, Ctrl+Left to the shell when no pane is on the left).
    /// </summary>
    void CmdIf(string title, string shortcut, Func<bool> run)
    {
        entries.Add(new CommandEntry(title, group, run));
        keys.AddDefault(title, Keymap.Normalize(shortcut));
    }

    /// <summary>A fixed shortcut without a palette entry (numbered jumps, aliases); Settings lists it read-only.</summary>
    void Key(string shortcut, Action run, string description = "") => fixedKeys.Add((Keymap.Normalize(shortcut), description, run));

    /// <summary>Applies <see cref="Settings.Keybindings"/>: rebuilds the chord table and the palette's shortcut column.</summary>
    void RebuildKeymap()
    {
        var fresh = new Keymap(Theme.Settings.Keybindings);
        foreach (var e in entries) fresh.AddDefault(e.Id, keys.Default(e.Id));
        keys = fresh;
        keymap.Clear();
        foreach (var (chord, description, run) in fixedKeys) keymap[Chord.Parse(chord)] = (description, () => { run(); return true; });
        // Rebindable commands win over fixed keys on the same chord.
        foreach (var e in entries)
            if (keys.Effective(e.Id) is { Length: > 0 } c) keymap[Chord.Parse(c)] = (e.Id, e.Run);
        commands = [.. entries.Select(e => new Command(e.Id, e.Id, Chord.Display(keys.Effective(e.Id)), () => e.Run()))];
        // Browser pages hand these chords back to armux instead of using them (Ctrl+L focuses the address bar).
        browsers.SetKeys([.. keymap.Keys.Where(c => !c.IsTextEditing).Append(new Chord(true, false, false, "L")).Select(c => c.Code).Distinct()]);
    }

    Workspace? Ws => state.Current;
    Pane? Focused => state.Current?.Focused;

    void RegisterCommands()
    {
        group = "Workspaces";
        Cmd("New Workspace", "Ctrl+Shift+N", NewWorkspace);
        // Ctrl+Shift+Tab moves between workspaces, Ctrl+Tab between the tabs inside one (requested).
        Cmd("Next Workspace", "Ctrl+Shift+Tab", () => CycleWorkspace(1));
        Cmd("Previous Workspace", "Ctrl+PageUp", () => CycleWorkspace(-1));
        Key("Ctrl+PageDown", () => CycleWorkspace(1), "Next workspace");
        Key("Alt+Shift+]", () => CycleWorkspace(1), "Next workspace (cmux mapping)");
        Key("Alt+Shift+[", () => CycleWorkspace(-1), "Previous workspace (cmux mapping)");
        Cmd("Close Workspace", "Ctrl+Alt+Shift+W", () => { if (Ws is { } w) CloseWorkspace(w); });
        Cmd("Rename Workspace…", "Ctrl+Alt+Shift+R", () => { if (Ws is { } w) _ = RenameAsync(w); });
        Cmd("Pin or Unpin Workspace", "", () => { if (Ws is { } w) { w.Pinned = !w.Pinned; state.SortPinned(); } });
        Cmd("Workspace Color: Custom…", "", () => { if (Ws is { } w) _ = CustomColorAsync(w); });
        Cmd("Workspace Color: None", "", () => { if (Ws is { } w) w.Color = null; });
        foreach (var (name, hex) in WorkspaceColors.Presets)
            Cmd($"Workspace Color: {name}", "", () => { if (Ws is { } w) w.Color = hex; });
        Cmd("Workspace Indicator: Accent (left rail)", "", () => ChangeSettings(s => s with { WorkspaceIndicator = "accent" }));
        Cmd("Workspace Indicator: Underline", "", () => ChangeSettings(s => s with { WorkspaceIndicator = "underline" }));
        Cmd("Workspace Indicator: None", "", () => ChangeSettings(s => s with { WorkspaceIndicator = "none" }));
        group = "Window";
        Cmd("Toggle Sidebar", "Ctrl+Shift+B", ToggleSidebar);
        Cmd("Toggle File Explorer", "Ctrl+Shift+E", ToggleExplorer);
        // Ctrl+1-9 (requested in review, D-14) and cmux's mapped Ctrl+Shift+1-9 both jump to a workspace.
        foreach (var mods in new[] { "Ctrl", "Ctrl+Shift" })
        {
            for (int i = 1; i <= 8; i++) { int n = i; Key($"{mods}+{n}", () => JumpWorkspace(n - 1), $"Go to workspace {n}"); }
            Key($"{mods}+9", () => JumpWorkspace(state.Workspaces.Count - 1), "Go to the last workspace");
        }

        group = "Tabs";
        Cmd("New Terminal Tab", "Ctrl+Shift+T", () => { if (Focused is { } p) NewSurface(p, PanelKind.Terminal); });
        Cmd("New Browser Tab", "", () => { if (Focused is { } p) NewSurface(p, PanelKind.Browser); });
        Cmd("New PowerShell Tab", "", () => { if (Focused is { } p) { var s = LiveTerminal("pwsh"); p.Surfaces.Add(s); SelectSurface(p, s); } });
        Cmd("New Command Prompt Tab", "", () => { if (Focused is { } p) { var s = LiveTerminal("cmd"); p.Surfaces.Add(s); SelectSurface(p, s); } });
        Cmd("Close Tab", "Ctrl+Shift+W", () => { if (Focused is { Selected: { } s } p) CloseSurface(p, s); });
        Cmd("Next Tab", "Ctrl+Tab", () => CycleSurface(1));
        Cmd("Previous Tab", "", () => CycleSurface(-1));
        for (int i = 1; i <= 8; i++) { int n = i; Key($"Alt+{n}", () => JumpSurface(n - 1), $"Go to tab {n} in the pane"); }
        Key("Alt+9", () => JumpSurface(int.MaxValue), "Go to the last tab in the pane");

        group = "Panes";
        Cmd("Split Right", "Ctrl+Shift+D", () => { if (Focused is { } p) SplitPane(p, SplitDirection.Right); });
        Cmd("Split Down", "Ctrl+Alt+Shift+D", () => { if (Focused is { } p) SplitPane(p, SplitDirection.Down); });
        Cmd("Focus Next Pane", "F6", () => CyclePane(1));
        Cmd("Focus Previous Pane", "Shift+F6", () => CyclePane(-1));
        // Ctrl+arrows (requested): with no pane that way the key goes on, so the shell still moves by word.
        CmdIf("Focus Pane Left", "Ctrl+Left", () => FocusDir(Direction.Left));
        CmdIf("Focus Pane Right", "Ctrl+Right", () => FocusDir(Direction.Right));
        CmdIf("Focus Pane Up", "Ctrl+Up", () => FocusDir(Direction.Up));
        CmdIf("Focus Pane Down", "Ctrl+Down", () => FocusDir(Direction.Down));
        foreach (var (name, d) in new[] { ("Left", Direction.Left), ("Right", Direction.Right), ("Up", Direction.Up), ("Down", Direction.Down) })
            Key($"Ctrl+Alt+{name}", () => FocusDir(d), $"Focus pane {name.ToLowerInvariant()}");
        Cmd("Resize Pane Left", "Alt+Shift+Left", () => ResizeDir(Direction.Left));
        Cmd("Resize Pane Right", "Alt+Shift+Right", () => ResizeDir(Direction.Right));
        Cmd("Resize Pane Up", "Alt+Shift+Up", () => ResizeDir(Direction.Up));
        Cmd("Resize Pane Down", "Alt+Shift+Down", () => ResizeDir(Direction.Down));
        Cmd("Toggle Pane Zoom", "Ctrl+Alt+Shift+Enter", ToggleZoom);
        Cmd("Equalize Splits", "Ctrl+Alt+=", () => { if (Ws is { } w) { Layout.Equalize(w.Root); RenderContent(); } });
        Cmd("Flash Focused Panel", "Ctrl+Alt+Shift+H", () => { if (Focused is { } p) ViewOf(p).Flash(); });
        Cmd("Open Browser in Split", "Ctrl+Alt+Shift+L", () =>
        {
            if (Ws is not { } w || Focused is not { } p) return;
            Layout.Split(w, p, SplitDirection.Right, NewBrowserSurface());
            RenderContent();
        });

        group = "Notifications";
        Cmd("Show Notifications", "Ctrl+Shift+I", () => bellFlyout.ShowAt(bellButton));
        Cmd("Jump to Latest Unread", "Ctrl+Alt+Shift+U", () => { if (state.LatestUnread() is { } n) JumpTo(n); });
        Cmd("Toggle Unread", "Ctrl+Alt+U", () => { if (Ws is { } w && Focused?.Selected is { } s) state.ToggleUnread(w, s); });
        Cmd("Mark All Notifications Read", "", state.MarkAllRead);
        Cmd("Clear All Notifications", "", state.Clear);
        Cmd("Simulate Agent Notification (mock)", "", SimulateNotification);

        group = "Terminal";
        Cmd("Clear Scrollback", "Ctrl+Shift+K", () =>
        {
            if (Focused?.Selected is not { Kind: PanelKind.Terminal } s) return;
            if (s.Live) { SessionFor(s).Terminal.ClearScrollback(); return; }
            // Mock terminal: replace its canned output with a prompt and start it again.
            s.Lines.Clear();
            s.Lines.AddRange(MockData.Prompt());
            DisposeSession(s);
            panels.Remove(s);
            ViewOf(Focused).Refresh();
        });
        Cmd("Copy", "Ctrl+Shift+C", () => FocusedTerminal()?.Copy());
        // Ctrl+V pastes in a terminal (requested, as in Windows Terminal); in text boxes it stays their own paste.
        CmdIf("Paste", "Ctrl+V", () =>
        {
            if (FocusManager.GetFocusedElement(root.XamlRoot) is not TerminalView t) return false;
            _ = t.PasteAsync();
            return true;
        });
        Key("Ctrl+Shift+V", () => { if (FocusedTerminal() is { } t) _ = t.PasteAsync(); }, "Paste");
        Cmd("Increase Terminal Font Size", "Ctrl+Shift+=", () => ChangeFontSize(1));
        Cmd("Decrease Terminal Font Size", "Ctrl+Shift+-", () => ChangeFontSize(-1));
        Cmd("Reset Terminal Font Size", "Ctrl+Shift+0", () => ChangeFontSize(0));
        Cmd("Prompt Editor", "Ctrl+G", TogglePromptEditor);
        // Windows Terminal's scrollback keys.
        Cmd("Scroll Up a Line", "Ctrl+Shift+Up", () => FocusedTerminal()?.ScrollBy(-1));
        Cmd("Scroll Down a Line", "Ctrl+Shift+Down", () => FocusedTerminal()?.ScrollBy(1));
        Cmd("Scroll Up a Page", "Ctrl+Shift+PageUp", () => { if (FocusedTerminal() is { } t) t.ScrollBy(-t.PageRows); });
        Cmd("Scroll Down a Page", "Ctrl+Shift+PageDown", () => { if (FocusedTerminal() is { } t) t.ScrollBy(t.PageRows); });
        Cmd("Scroll to Top", "Ctrl+Shift+Home", () => FocusedTerminal()?.ScrollBy(int.MinValue));
        Cmd("Scroll to Bottom", "Ctrl+Shift+End", () => FocusedTerminal()?.ScrollBy(int.MaxValue));
        // Windows Terminal's classic clipboard keys (Shift+Insert also pastes inside the terminal itself).
        Key("Ctrl+Insert", () => FocusedTerminal()?.Copy(), "Copy");
        Key("Shift+Insert", () => { if (FocusedTerminal() is { } t) _ = t.PasteAsync(); }, "Paste");
        // Windows Terminal's split keys: Alt+Shift+Plus right, Alt+Shift+Minus down, Alt+Shift+D along the longer side.
        Key("Alt+Shift+=", () => { if (Focused is { } p) SplitPane(p, SplitDirection.Right); }, "Split right (Windows Terminal)");
        Key("Alt+Shift+-", () => { if (Focused is { } p) SplitPane(p, SplitDirection.Down); }, "Split down (Windows Terminal)");
        Key("Alt+Shift+D", () =>
        {
            if (Focused is not { } p) return;
            var v = ViewOf(p);
            SplitPane(p, v.ActualWidth >= v.ActualHeight ? SplitDirection.Right : SplitDirection.Down);
        }, "Split along the longer side (Windows Terminal)");

        group = "Window";
        // Ctrl+, opens settings and Ctrl+Shift+, the settings file, as in Windows Terminal and VS Code.
        Cmd("Settings", "Ctrl+,", ToggleSettingsPage);
        Cmd("Open settings.json", "Ctrl+Shift+,", OpenSettingsJson);
        Cmd("Toggle Full Screen", "F11", ToggleFullScreen);
        // Ctrl+= / Ctrl+- / Ctrl+0 zoom the whole window, as in browsers (requested). Terminal text alone: Ctrl+Shift.
        Cmd("Zoom In", "Ctrl+=", () => ChangeZoom(1));
        Cmd("Zoom Out", "Ctrl+-", () => ChangeZoom(-1));
        Cmd("Reset Zoom", "Ctrl+0", () => ChangeZoom(0));
        Cmd("Theme: Dark", "", () => ChangeSettings(s => s with { Theme = "dark" }));
        Cmd("Theme: Light", "", () => ChangeSettings(s => s with { Theme = "light" }));
        Cmd("Theme: High Contrast", "", () => ChangeSettings(s => s with { Theme = "high-contrast" }));
        Cmd("Theme: Match Windows", "", () => ChangeSettings(s => s with { Theme = "system" }));
        Cmd("New Window", "Ctrl+Alt+Shift+N", () => new MainWindow(opts with { Scenario = "idle", Palette = false, Notifications = false, Zoom = false, Menu = false, Colors = false, Explorer = false, Settings = false, PromptEditor = false, Rename = false, Select = 0 }).Activate());
        Cmd("Command Palette", "Ctrl+Shift+P", OpenPalette);

        RebuildKeymap();
        root.PreviewKeyDown += OnKey;
    }

    void OpenPalette()
    {
        // Workspace switcher results, like cmux's palette.
        var list = new List<Command>(commands);
        for (int i = 0; i < state.Workspaces.Count; i++)
        {
            var w = state.Workspaces[i];
            list.Add(new Command($"ws:{w.Id}", $"Go to Workspace: {w.Title}", i < 8 ? $"Ctrl+{i + 1}" : i == 8 ? "Ctrl+9" : "", () => SelectWorkspace(w)));
        }
        palette.Open(list);
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>The chord key name for a virtual key, or null for keys that cannot be part of a shortcut.</summary>
    internal static string? KeyName(VirtualKey k) => k switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => k.ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (k - VirtualKey.F1 + 1),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)k - (int)VirtualKey.Number0).ToString(),
        (VirtualKey)187 or VirtualKey.Add => "=",
        (VirtualKey)189 or VirtualKey.Subtract => "-",
        VirtualKey.NumberPad0 => "0",
        (VirtualKey)219 => "[",
        (VirtualKey)221 => "]",
        (VirtualKey)188 => ",",
        (VirtualKey)190 => ".",
        (VirtualKey)191 => "/",
        (VirtualKey)186 => ";",
        (VirtualKey)222 => "'",
        (VirtualKey)192 => "`",
        (VirtualKey)220 => "\\",
        VirtualKey.Space => "SPACE",
        VirtualKey.Home => "HOME",
        VirtualKey.End => "END",
        VirtualKey.Insert => "INSERT",
        VirtualKey.Delete => "DELETE",
        VirtualKey.Enter => "ENTER",
        VirtualKey.Tab => "TAB",
        VirtualKey.Left => "LEFT",
        VirtualKey.Right => "RIGHT",
        VirtualKey.Up => "UP",
        VirtualKey.Down => "DOWN",
        VirtualKey.PageUp => "PAGEUP",
        VirtualKey.PageDown => "PAGEDOWN",
        _ => null,
    };

    /// <summary>
    /// The root's PreviewKeyDown sees every key typed anywhere in the window (terminal, explorer, text boxes) before
    /// the focused control does, so shortcuts work wherever the keyboard is (D-41). The Settings page is the
    /// exception: there only Esc and the Settings shortcut (Ctrl+,) act, to close it.
    /// </summary>
    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (palette.IsOpen || recordingShortcut is not null) return;
        if (settingsPage is not null && e.Key == VirtualKey.Escape) { e.Handled = true; EscapeSettingsPage(); return; }
        if (KeyName(e.Key) is not { } key) return;
        var chord = new Chord(Down(VirtualKey.Control), Down(VirtualKey.Menu), Down(VirtualKey.Shift), key);
        // Caret, selection, clipboard and undo keys stay with a focused text box (settings search, address bar, editors).
        if (chord.IsTextEditing && FocusManager.GetFocusedElement(root.XamlRoot)
            is Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.PasswordBox or Microsoft.UI.Xaml.Controls.RichEditBox) return;
        lastChord = (chord, Environment.TickCount64);
        if (RunChord(chord)) e.Handled = true;
    }

    /// <summary>Runs the shortcut on <paramref name="chord"/>; false when there is none or it declined the key.</summary>
    bool RunChord(Chord chord)
    {
        // Ctrl+L belongs to the shell in a terminal, and focuses the address bar in a browser tab (Edge default, D-03).
        if (settingsPage is null && chord == new Chord(true, false, false, "L") && Focused?.Selected is { Kind: PanelKind.Browser } b
            && panels.TryGetValue(b, out var panel) && panel is BrowserPanel bp)
        {
            bp.FocusAddress();
            return true;
        }
        if (!keymap.TryGetValue(chord, out var cmd)) return false;
        // Over the Settings page every other key belongs to its controls (requested).
        if (settingsPage is not null && cmd.Id != "Settings") return false;
        return cmd.Run();
    }

    /// <summary>
    /// A shortcut typed in a browser page, which WebView2 keeps from the window's key events. Skipped when the
    /// window's own key handler ran the same chord a moment ago, so it never runs twice.
    /// </summary>
    void OnPageKey(Chord chord)
    {
        if (palette.IsOpen || recordingShortcut is not null) return;
        if (lastChord.Chord == chord && Environment.TickCount64 - lastChord.At < 250) return;
        RunChord(chord);
    }

    // ---- actions -------------------------------------------------------------------------------

    void CycleWorkspace(int delta)
    {
        if (Ws is not { } w || state.Workspaces.Count == 0) return;
        int i = (state.Workspaces.IndexOf(w) + delta + state.Workspaces.Count) % state.Workspaces.Count;
        SelectWorkspace(state.Workspaces[i]);
    }

    void JumpWorkspace(int index)
    {
        if (index >= 0 && index < state.Workspaces.Count) SelectWorkspace(state.Workspaces[index]);
    }

    /// <summary>Ctrl+Tab: the next tab in the workspace, pane by pane in layout order, so single-tab panes take turns too.</summary>
    void CycleSurface(int delta)
    {
        if (Ws is not { } w || Focused is not { Selected: { } cur }) return;
        var tabs = (w.Zoomed is { } z ? [z] : w.Panes.ToList()).SelectMany(p => p.Surfaces.Select(s => (Pane: p, Surface: s))).ToList();
        if (tabs.Count < 2) return;
        int i = tabs.FindIndex(t => ReferenceEquals(t.Surface, cur));
        var next = tabs[(i + delta + tabs.Count) % tabs.Count];
        SelectSurface(next.Pane, next.Surface);
    }

    void JumpSurface(int index)
    {
        if (Focused is not { } p || p.Surfaces.Count == 0) return;
        SelectSurface(p, p.Surfaces[Math.Min(index, p.Surfaces.Count - 1)]);
    }

    /// <summary>F6 / Shift+F6: focus the next or previous pane in layout order (left to right, top to bottom).</summary>
    void CyclePane(int delta)
    {
        if (Ws is not { } w || w.Zoomed is not null || Focused is not { } p) return;
        var panes = w.Panes.ToList();
        if (panes.Count < 2) return;
        FocusPane(panes[(panes.IndexOf(p) + delta + panes.Count) % panes.Count]);
    }

    /// <summary>Focuses the pane in direction <paramref name="d"/>; false (focus unchanged) when there is none or a text box has the keyboard.</summary>
    bool FocusDir(Direction d)
    {
        if (Ws is not { } w || w.Zoomed is not null || Focused is not { } p) return false;
        if (FocusManager.GetFocusedElement(root.XamlRoot) is Microsoft.UI.Xaml.Controls.TextBox) return false;
        if (Layout.Neighbor(w.Root, p, d) is not { } n) return false;
        FocusPane(n);
        return true;
    }

    void ResizeDir(Direction d)
    {
        if (Focused is { } p && Layout.Resize(p, d)) RenderContent();
    }

    void ToggleZoom()
    {
        if (Ws is not { } w || Focused is not { } p) return;
        w.Zoomed = w.Zoomed is null && w.Panes.Count() > 1 ? p : null;
        RenderContent();
    }

    /// <summary>F11, as in Windows Terminal, browsers and VS Code: the window fills the screen without its title bar.</summary>
    void ToggleFullScreen()
    {
        bool full = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        // Back to the window's own presenter, which carries the minimum size (D-39).
        if (full) AppWindow.SetPresenter(presenter);
        else AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    void SetTheme(ThemeKind kind)
    {
        Theme.Use(kind);
        foreach (var (s, panel) in panels.ToList())
            if (panel is BrowserPanel bp) { browsers.Close(bp); panels.Remove(s); }
        palette.Background = Theme.Brush("Overlay");
        RenderAll();
    }

    /// <summary>Phase 2 stand-in for OSC 9/99/777 and `armux notify`: notifies a surface the user is not looking at.</summary>
    void SimulateNotification()
    {
        var targets = state.Workspaces
            .SelectMany(w => w.Panes.SelectMany(p => p.Surfaces.Where(s => s.Kind == PanelKind.Terminal).Select(s => (w, p, s))))
            .Where(t => !(ReferenceEquals(t.w, Ws) && ReferenceEquals(t.p, Focused) && ReferenceEquals(t.p.Selected, t.s)))
            .ToList();
        if (targets.Count == 0) return;
        var (ws, pane, surface) = targets[simulated++ % targets.Count];
        string[] bodies = ["Claude is waiting for your input", "Agent turn complete", "Tests finished: 24 passed", "Build failed: 1 error"];
        state.Notify(ws, pane, surface, surface.Title, bodies[simulated % bodies.Length]);
    }
}
