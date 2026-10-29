using Armux.Core;

namespace Armux.App;

/// <summary>Applying settings: every change goes through <see cref="ChangeSettings"/>, which saves and redraws what it affects.</summary>
public sealed partial class MainWindow
{
    public static string SettingsPath =>
        Environment.GetEnvironmentVariable("ARMUX_SETTINGS_FILE") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "armux", "settings.json");

    /// <summary>
    /// Applies a change to the settings, saves them, and updates only what the change touches: theme and interface
    /// font redraw the window, terminal options rebuild terminal views (shells keep running), and so on.
    /// <paramref name="save"/> is false when the change came from the file itself, which keeps its formatting and comments.
    /// </summary>
    void ChangeSettings(Func<Settings, Settings> change, bool save = true)
    {
        var old = Theme.Settings;
        var next = change(old).Normalized();
        if (next.Equals(old)) return;
        Theme.UseSettings(next);
        if (save)
            try { Theme.Settings.Save(SettingsPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(nameof(ChangeSettings), e); }

        bool window = next.Theme != old.Theme || next.UiFont != old.UiFont;
        bool terminals = next.TerminalFont != old.TerminalFont || next.TerminalFontSize != old.TerminalFontSize
            || next.LineHeight != old.LineHeight || next.Ligatures != old.Ligatures
            || next.CursorStyle != old.CursorStyle || next.CursorBlink != old.CursorBlink;
        if (window)
        {
            try { Microsoft.UI.Xaml.Application.Current.Resources["ContentControlThemeFontFamily"] = new Microsoft.UI.Xaml.Media.FontFamily(Theme.UiFont); }
            catch (Exception e) { App.Log("ContentControlThemeFontFamily", e); }
            palette.UpdateFont();
            SetTheme(Theme.Resolve(next.Theme));
        }
        else if (terminals) RefreshTerminalViews();
        if (next.Zoom != old.Zoom) ApplyZoom(new Windows.Foundation.Size(zoomHost.ActualWidth, zoomHost.ActualHeight));
        if (next.WorkspaceIndicator != old.WorkspaceIndicator) state.Indicator = Indicator(next.WorkspaceIndicator);
        if (!next.Keybindings.OrderBy(k => k.Key).SequenceEqual(old.Keybindings.OrderBy(k => k.Key))) RebuildKeymap();
        if (next.TaskbarStatus != old.TaskbarStatus) UpdateTaskbar();
        if (window && settingsPage is not null) ShowSettingsPage(settingsSection);
    }

    static IndicatorStyle Indicator(string setting) => setting switch
    {
        "underline" => IndicatorStyle.Underline,
        "none" => IndicatorStyle.None,
        _ => IndicatorStyle.Accent,
    };

    static bool IsSettingsFile(string path)
    {
        try { return Path.GetFullPath(path).Equals(Path.GetFullPath(SettingsPath), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Ctrl+S in the settings.json tab: invalid JSON is reported and nothing changes; valid JSON is written and applied.</summary>
    string? SaveSettingsText(string text)
    {
        if (!Settings.TryParse(text, out var s, out var error)) return error;
        try { File.WriteAllText(SettingsPath, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
        ChangeSettings(_ => s, save: false);
        return null;
    }

    FileSystemWatcher? settingsWatcher;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? settingsDebounce;

    /// <summary>
    /// settings.json is watched, as Zed watches its settings file: a save from the editor tab or any other program
    /// applies at once. A file that does not parse yet is ignored until it does.
    /// </summary>
    void WatchSettingsFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!;
            Directory.CreateDirectory(dir);
            settingsDebounce = DispatcherQueue.CreateTimer();
            settingsDebounce.Interval = TimeSpan.FromMilliseconds(200);
            settingsDebounce.IsRepeating = false;
            settingsDebounce.Tick += (_, _) => ReloadSettingsFile();
            settingsWatcher = new FileSystemWatcher(dir, Path.GetFileName(SettingsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // Watcher events arrive on thread-pool threads, where the window's DispatcherQueue property can't be read.
            var queue = DispatcherQueue;
            void Changed() => queue.TryEnqueue(() => { settingsDebounce?.Stop(); settingsDebounce?.Start(); });
            settingsWatcher.Changed += (_, _) => Changed();
            settingsWatcher.Created += (_, _) => Changed();
            settingsWatcher.Renamed += (_, _) => Changed();
            settingsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { App.Log(nameof(WatchSettingsFile), e); }
    }

    void ReloadSettingsFile()
    {
        string text;
        try { text = File.ReadAllText(SettingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var view in panels.Values.OfType<FileView>()) if (view.Editable) view.Reload(text);
        if (!Settings.TryParse(text, out var s, out _) || s.Equals(Theme.Settings)) return;
        try
        {
            ChangeSettings(_ => s, save: false);
            if (settingsPage is not null) ShowSettingsPage(settingsSection);
        }
        catch (Exception e) { App.Log(nameof(ReloadSettingsFile), e); }
    }

    /// <summary>Rebuilds terminal and file views with the current terminal settings; the shells keep running.</summary>
    void RefreshTerminalViews()
    {
        // The settings.json editor stays: rebuilding it would drop the caret and unsaved edits.
        foreach (var (surface, panel) in panels.ToList())
            if (panel is TerminalView or FileView { Editable: false }) panels.Remove(surface);
        if (state.Current is { } ws)
            foreach (var pane in ws.Panes) ViewOf(pane).Refresh();
        FocusKeys();
    }

    /// <summary>Ctrl+Shift+= / - / 0: the terminal font size alone.</summary>
    void ChangeFontSize(double delta) =>
        ChangeSettings(s => s.WithTerminalFontSize(delta == 0 ? Settings.DefaultTerminalFontSize : s.TerminalFontSize + delta));

    static string ExpandHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path == "~" ? home : path.StartsWith("~\\") || path.StartsWith("~/") ? Path.Join(home, path[2..]) : path;
    }

    /// <summary>The directory new workspaces start in, as shown in the sidebar ("~" for the user folder).</summary>
    public static string StartDirectory()
    {
        var dir = Theme.Settings.StartDirectory;
        if (dir.Length == 0 || !Directory.Exists(ExpandHome(dir))) return "~";
        var full = Path.GetFullPath(ExpandHome(dir));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return full.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + full[home.Length..] : full;
    }
}
