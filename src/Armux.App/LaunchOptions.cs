using Armux.Core;

namespace Armux.App;

/// <summary>
/// Command-line switches. "live" (the default) starts real shells; the mock scenarios make screenshots and memory
/// measurements reproducible.
///   --scenario live|demo|idle|panes|browser   (default live)
///   --panes N            terminal panes for the "live" and "panes" scenarios
///   --close-after MS     close browser panels and the explorer after MS
///   --theme dark|light   --select N   --palette   --notifications   --zoom   --menu   --colors   --size WxH
///   --explorer           open the file explorer      --settings [section]   open Settings (General, Appearance, …)
///   --prompt-editor      open the prompt editor under the focused terminal
///   --rename             open the rename dialog for the first workspace after 3 s (tests that Enter stays out of the terminal)
///   --no-restore         start fresh instead of reopening the saved session (also implied by --panes)
/// </summary>
public sealed record LaunchOptions(
    string Scenario, int Panes, int CloseAfterMs, string? Theme, int Select,
    bool Palette, bool Notifications, bool Zoom, bool Menu, bool Colors, int Width, int Height,
    bool Explorer = false, bool Settings = false, bool PromptEditor = false, string? SettingsSection = null, bool Rename = false,
    bool NoRestore = false)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? Value(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Contains(name);
        var size = (Value("--size") ?? "1440x900").Split('x');
        return new LaunchOptions(
            Value("--scenario") ?? "live",
            int.TryParse(Value("--panes"), out var p) ? p : 1,
            int.TryParse(Value("--close-after"), out var c) ? c : -1,
            Value("--theme"),
            int.TryParse(Value("--select"), out var s) ? s : 0,
            Flag("--palette"), Flag("--notifications"), Flag("--zoom"), Flag("--menu"), Flag("--colors"),
            int.Parse(size[0]), int.Parse(size[1]),
            Flag("--explorer"), Flag("--settings"), Flag("--prompt-editor"),
            Value("--settings") is { } section && !section.StartsWith("--") ? section : null,
            Flag("--rename"),
            Flag("--no-restore") || Flag("--panes"));
    }

    /// <summary>The real window: the one that saves and reopens the session (not mock scenarios or New Window).</summary>
    public bool Persistent => Scenario == "live" && !NoRestore;

    public AppState BuildState() => Scenario switch
    {
        "idle" => MockData.Idle(),
        "panes" => MockData.Panes(Math.Max(1, Panes)),
        "browser" => WithBrowser(MockData.Idle()),
        "demo" => MockData.Demo(),
        _ => Restored() ?? Live(Math.Max(1, Panes)),
    };

    /// <summary>The workspaces open when armux last closed (Settings > General > Reopen workspaces), if any.</summary>
    AppState? Restored()
    {
        if (!Persistent || !Armux.App.Theme.Settings.RestoreSession) return null;
        try { return SessionStore.Load(MainWindow.SessionPath) is { } file ? SessionStore.Restore(file, Armux.App.Theme.Settings.Shell) : null; }
        catch (Exception e) { App.Log("RestoreSession", e); return null; }
    }

    /// <summary>One workspace in the start directory from Settings with <paramref name="panes"/> live terminals running the default shell.</summary>
    static AppState Live(int panes)
    {
        static Surface Shell() => new() { Kind = PanelKind.Terminal, Title = Armux.App.Theme.Settings.Shell, Shell = Armux.App.Theme.Settings.Shell };
        var state = new AppState();
        var ws = MockData.NewWorkspace("workspace 1", "", "", MainWindow.StartDirectory(), Shell());
        for (int i = 1; i < panes; i++)
        {
            // Split the largest pane along its longer side, like the mock "panes" layout.
            var rects = Layout.Rects(ws.Root);
            var (pane, r) = rects.OrderByDescending(kv => kv.Value.W * kv.Value.H).Select(kv => (kv.Key, kv.Value)).First();
            Layout.Split(ws, pane, r.W >= r.H ? SplitDirection.Right : SplitDirection.Down, Shell());
        }
        ws.Focused = ws.Panes.First();
        state.Workspaces.Add(ws);
        state.Current = ws;
        return state;
    }

    static AppState WithBrowser(AppState state)
    {
        var ws = state.Workspaces[0];
        Layout.Split(ws, ws.Focused!, SplitDirection.Right, MockData.Browser());
        return state;
    }
}
