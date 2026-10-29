using Armux.Core;
using Armux.Core.Pty;
using Armux.Core.Vt;
using Layout = Armux.Core.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Armux.App;

/// <summary>Terminal sessions: one per terminal surface, started when its view first gets a size.</summary>
public sealed partial class MainWindow
{
    readonly Dictionary<Surface, TerminalSession> sessions = [];

    /// <summary>A new live terminal surface running the default shell from Settings.</summary>
    static Surface LiveTerminal(string? shell = null)
    {
        var kind = Shells.Parse(shell ?? Theme.Settings.Shell);
        return new Surface { Kind = PanelKind.Terminal, Title = Shells.Name(kind), Shell = Shells.Name(kind) };
    }

    /// <summary>A browser tab on the new-tab page from Settings, or blank with the address bar focused.</summary>
    static Surface NewBrowserSurface()
    {
        var home = Theme.Settings.BrowserHome.Length > 0 ? BrowserPanel.AddressOrSearch(Theme.Settings.BrowserHome) ?? "" : "";
        return new Surface { Kind = PanelKind.Browser, Title = home.Length > 0 ? home : "New tab", Url = home };
    }

    (Workspace Ws, Pane Pane)? Locate(Surface surface)
    {
        foreach (var ws in state.Workspaces)
            foreach (var pane in ws.Panes)
                if (pane.Surfaces.Contains(surface)) return (ws, pane);
        return null;
    }

    TerminalSession SessionFor(Surface surface)
    {
        if (sessions.TryGetValue(surface, out var s)) return s;
        var ws = Locate(surface)?.Ws;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // New tabs follow the workspace's directory, or start in the start directory (Settings > General).
        // A restored terminal starts where it was when armux closed.
        string cwd = surface.StartDirectory.Length > 0 && Directory.Exists(surface.StartDirectory) ? surface.StartDirectory
            : FileTree.Resolve(Theme.Settings.NewTabDirectory == "start" ? StartDirectory() : ws?.Cwd ?? "~", home, out _);
        var env = new Dictionary<string, string>
        {
            ["ARMUX_SURFACE_ID"] = surface.Id.ToString(),
            ["ARMUX_WORKSPACE_ID"] = (ws?.Id ?? 0).ToString(),
            ["ARMUX_PIPE"] = PipeServer.PipeName,
            // armux.com (the CLI) ships next to armux.exe, so `armux notify` works in every armux terminal.
            ["PATH"] = AppContext.BaseDirectory.TrimEnd('\\') + ";" + Environment.GetEnvironmentVariable("PATH"),
        };
        s = new TerminalSession(surface, DispatcherQueue, cwd, env);
        // Programs that ask for the colors (OSC 10 / 11; Claude Code does) pick a matching light or dark theme.
        s.Terminal.ReportedBackground = Theme.Dark ? 0x262626u : 0xFFFFFFu;
        s.Terminal.ReportedForeground = Theme.Dark ? 0xD4D4D4u : 0x1F1F1Fu;
        // A program's notification or bell also means it is waiting for an answer: orange until the user types.
        s.Notified += n => { s.MarkWaiting(fromBell: false); OnTerminalNotification(surface, n); };
        s.CwdChanged += path => OnTerminalCwd(surface, path);
        s.ProcessExited += _ => OnProcessExited(surface);
        s.StateChanged += () => { UpdateTaskbar(); UpdateStatus(surface); };
        s.TitleUpdated += () => UpdateStatus(surface);
        // The bell is how Claude Code (and many programs) ask for attention when no OSC notification is configured.
        s.Bell += () =>
        {
            if (!Theme.Settings.BellNotifies) return;
            s.MarkWaiting(fromBell: true);
            OnTerminalNotification(surface, new TermNotification("", "Needs your attention"));
        };
        sessions[surface] = s;
        return s;
    }

    void DisposeSession(Surface surface)
    {
        if (sessions.Remove(surface, out var s)) s.Dispose();
        editors.Remove(surface);
    }

    /// <summary>One prompt editor per terminal, kept while closed so its draft survives.</summary>
    readonly Dictionary<Surface, PromptEditor> editors = [];

    /// <summary>Ctrl+G: open or close the prompt editor under the focused terminal.</summary>
    void TogglePromptEditor()
    {
        if (Focused is not { Selected: { Live: true } surface } pane) return;
        var view = ViewOf(pane);
        if (view.EditorOpen) { CloseEditor(surface); return; }
        if (!editors.TryGetValue(surface, out var editor))
        {
            editors[surface] = editor = new PromptEditor();
            editor.Submitted += (text, enter) =>
            {
                var session = SessionFor(surface);
                session.Paste(text);
                if (enter) session.Send("\r");
                CloseEditor(surface);
            };
            editor.CloseRequested += () => CloseEditor(surface);
        }
        view.ShowEditor(editor);
        DispatcherQueue.TryEnqueue(editor.FocusText);
    }

    void CloseEditor(Surface surface)
    {
        if (Locate(surface) is { } at && paneViews.TryGetValue(at.Pane, out var v)) v.ShowEditor(null);
        FocusKeys();
    }

    /// <summary>OSC 9 / 99 / 777: the ring, badge and notification list, plus a Windows toast when armux is in the background.</summary>
    void OnTerminalNotification(Surface surface, TermNotification n)
    {
        if (Locate(surface) is not { } at) return;
        string title = n.Title.Length > 0 ? n.Title : surface.Title;
        state.Notify(at.Ws, at.Pane, surface, title, n.Body);
        Toasts.ShowIfInactive(this, surface.Id, at.Ws.Title, title, n.Body);
    }

    /// <summary>OSC 7 from the shell: the workspace directory and git branch follow it, and so does the explorer.</summary>
    void OnTerminalCwd(Surface surface, string path)
    {
        if (Locate(surface) is not { } at) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        at.Ws.Cwd = path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
        at.Ws.Branch = Git.Branch(path) ?? "";
        if (ReferenceEquals(at.Ws, state.Current)) RenderExplorer(rebuild: false);
    }

    /// <summary>The terminal view of the focused pane's selected tab, if that tab is a terminal.</summary>
    TerminalView? FocusedTerminal() =>
        Focused?.Selected is { Kind: PanelKind.Terminal } s && panels.TryGetValue(s, out var p) ? p as TerminalView : null;

    /// <summary>A shell exited: its tab closes. When it was the last tab of the window, the window closes, as in other terminals.</summary>
    void OnProcessExited(Surface surface)
    {
        if (Locate(surface) is not { } at) return;
        if (state.Workspaces.Sum(w => w.Panes.Sum(p => p.Surfaces.Count)) == 1)
        {
            if (Theme.Settings.CloseOnLastExit) { Close(); return; }
            // Settings > General: keep the window and start a new shell in the same pane.
            DisposeSession(surface);
            panels.Remove(surface);
            var fresh = LiveTerminal();
            at.Pane.Surfaces.Add(fresh);
            at.Pane.Surfaces.Remove(surface);
            SelectSurface(at.Pane, fresh);
            return;
        }
        CloseSurface(at.Pane, surface);
    }

    /// <summary>Clicking a toast: show the window and jump to the surface.</summary>
    void JumpToSurface(int surfaceId)
    {
        foreach (var n in state.Notifications)
            if (n.Surface.Id == surfaceId) { JumpTo(n); break; }
        Activate();
    }

    /// <summary>The control API behind the <c>armux</c> CLI (runs on the UI thread).</summary>
    Response HandleRequest(Request r)
    {
        switch (r.Command)
        {
            case "ping":
                return new Response { Ok = true, Output = "armux " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3) };
            case "notify":
                if (Target(r) is not { } surface) return new Response { Error = $"no surface {r.Surface}" };
                if (r.Title.Length == 0 && r.Body.Length == 0) return new Response { Error = "nothing to send: give a title or a body" };
                OnTerminalNotification(surface, new TermNotification(r.Title, r.Body.Length > 0 ? r.Body : r.Title));
                return new Response { Ok = true };
            case "send":
                if (Target(r) is not { Live: true } live) return new Response { Error = $"surface {r.Surface} is not a live terminal" };
                SessionFor(live).Send(r.Body);
                return new Response { Ok = true };
            case "open":
                if (Target(r) is not { } from || Locate(from) is not { } at) return new Response { Error = $"no surface {r.Surface}" };
                if (BrowserPanel.AddressOrSearch(r.Body) is not { } address) return new Response { Error = "give a URL" };
                Layout.Split(at.Ws, at.Pane, SplitDirection.Right, new Surface { Kind = PanelKind.Browser, Title = address, Url = address });
                if (ReferenceEquals(at.Ws, state.Current)) RenderContent();
                return new Response { Ok = true };
            case "list":
                var sb = new System.Text.StringBuilder();
                foreach (var w in state.Workspaces)
                {
                    sb.Append(ReferenceEquals(w, state.Current) ? "* " : "  ").Append(w.Id).Append("  ").Append(w.Title)
                      .Append("  ").AppendLine(w.Location);
                    foreach (var s in w.Panes.SelectMany(p => p.Surfaces))
                        sb.Append("      ").Append(s.Id).Append("  ").Append(s.Kind.ToString().ToLowerInvariant()).Append("  ").AppendLine(s.Title);
                }
                return new Response { Ok = true, Output = sb.ToString() };
            default:
                return new Response { Error = $"unknown command \"{r.Command}\"" };
        }
    }

    /// <summary>The surface a request names, or the focused one when it names none.</summary>
    Surface? Target(Request r) => r.Surface > 0
        ? state.Workspaces.SelectMany(w => w.Panes).SelectMany(p => p.Surfaces).FirstOrDefault(s => s.Id == r.Surface)
        : Focused?.Selected;

    /// <summary>
    /// The taskbar shows the current workspace only: yellow when a tab in it needs input (an unread notification),
    /// a green bar while a program in it works, red on an OSC 9;4 error, nothing when idle.
    /// </summary>
    void UpdateTaskbar()
    {
        if (state.Current is not { } ws) return;
        var surfaces = ws.Panes.SelectMany(p => p.Surfaces).ToList();
        var status = surfaces.Any(s => s.Unread) ? AgentState.NeedsInput
            : AgentStatus.Max(surfaces.Select(s => sessions.TryGetValue(s, out var t) ? t.State : AgentState.Idle));
        Taskbar.Show(WinRT.Interop.WindowNative.GetWindowHandle(this), Theme.Settings.TaskbarStatus ? status : AgentState.Idle);
    }

    /// <summary>The sidebar status line and activity dot of the workspace holding <paramref name="surface"/>.</summary>
    void UpdateStatus(Surface surface)
    {
        if (Locate(surface) is { } at) UpdateStatus(at.Ws);
    }

    /// <summary>Only workspaces with live terminals: mock workspaces keep their scripted status.</summary>
    void UpdateStatus(Workspace ws)
    {
        var tabs = ws.Panes.SelectMany(p => p.Surfaces).Where(s => s.Live && sessions.ContainsKey(s))
            .Select(s => (Title: sessions[s].RawTitle.Length > 0 ? sessions[s].RawTitle : s.Title, sessions[s].State)).ToList();
        if (tabs.Count == 0) return;
        var mine = state.Notifications.Where(n => ReferenceEquals(n.Workspace, ws)).ToList();
        var (text, activity) = AgentStatus.Describe(tabs, mine.FirstOrDefault(n => !n.Read)?.Body, mine.FirstOrDefault()?.Body);
        ws.Status = text;
        ws.Activity = activity;
    }

    void UpdateAllStatuses()
    {
        foreach (var ws in state.Workspaces) UpdateStatus(ws);
    }

    /// <summary>
    /// Text typed for files dropped on a terminal: each path relative to the terminal's directory (up to three ..\
    /// levels; further away or on another drive it is absolute), quoted for the shell when needed, separated by spaces.
    /// </summary>
    string DropText(Surface surface, IReadOnlyList<string> paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? cwd = sessions.GetValueOrDefault(surface)?.Cwd
            ?? (Locate(surface)?.Ws.Cwd is { } c ? FileTree.Resolve(c, home, out _) : null);
        bool cmd = Shells.Parse(surface.Shell) == ShellKind.Cmd;
        return string.Join(" ", paths.Select(p => QuotePath(FileTree.DropPath(cwd, p), cmd)));
    }

    /// <summary>The explorer's "Insert path in terminal": types the path into the focused pane's terminal.</summary>
    public void InsertPath(string path)
    {
        if (Focused?.Selected is not { Kind: PanelKind.Terminal, Live: true } s) return;
        SessionFor(s).Paste(DropText(s, [path]));
        FocusKeys();
    }

    static readonly char[] ShellSpecial = [' ', '&', '(', ')', ';', '\'', '$', '`', ','];

    static string QuotePath(string path, bool cmd) =>
        path.IndexOfAny(ShellSpecial) < 0 ? path
        : cmd ? "\"" + path + "\""
        : "'" + path.Replace("'", "''") + "'";

    void DisposeAllSessions()
    {
        foreach (var s in sessions.Values) s.Dispose();
        sessions.Clear();
    }

    bool closeConfirmed, closeDialogOpen;

    /// <summary>Programs the shells in this window have started and that are still running (claude, node, ...).</summary>
    List<string> RunningPrograms()
    {
        var snapshot = ProcessTree.Snapshot();
        return sessions.Values.Where(s => !s.Exited && s.ShellProcessId != 0)
            .SelectMany(s => ProcessTree.ChildNames(snapshot, s.ShellProcessId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The close button, Alt+F4 or the taskbar: ask first when closing would end running programs (setting ConfirmClose).</summary>
    async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        string mode = Theme.Settings.ConfirmClose;
        if (closeConfirmed || mode == "never") return;
        var running = RunningPrograms();
        if (running.Count == 0 && mode != "always") return;
        e.Cancel = true;
        if (closeDialogOpen) return;

        int shells = sessions.Values.Count(s => s.Live && !s.Exited);
        string what = shells == 1 ? "1 terminal" : $"{shells} terminals";
        var body = new StackPanel { Spacing = 12, MaxWidth = 380 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = running.Count > 0
                ? $"{string.Join(", ", running.Take(6))}{(running.Count > 6 ? ", ..." : "")} {(running.Count == 1 ? "is" : "are")} still running. Closing armux ends {(running.Count == 1 ? "it" : "them")} and {what}."
                : $"Closing armux ends {what}.",
        });
        var dontAsk = new CheckBox { Content = "Don't ask again" };
        body.Children.Add(dontAsk);
        var dlg = Dialog("Close armux?", body, "Close");
        closeDialogOpen = true;
        try
        {
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch (Exception ex) { App.Log(nameof(OnWindowClosing), ex); return; }
        finally { closeDialogOpen = false; }
        if (dontAsk.IsChecked == true) ChangeSettings(x => x with { ConfirmClose = "never" });
        closeConfirmed = true;
        Close();
    }
}
