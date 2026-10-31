using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Armux.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Armux.App;

/// <summary>
/// The Settings page (cmux ⌘,; Ctrl+, here), laid out like Zed's settings editor (deviation D-31): a search box
/// and the sections on the left, flat rows of settings on the right, and "Edit in settings.json" for the file itself.
/// It covers the whole window and hides the title bar's workspace buttons until it closes. Every control applies and
/// saves at once. The page is built when opened and dropped when closed.
/// </summary>
public sealed partial class MainWindow
{
    static readonly string[] SettingsSections = ["General", "Appearance", "Terminal", "Keyboard", "Notifications", "Browser", "About"];

    Grid? settingsPage;
    ScrollViewer? settingsScroll;
    StackPanel? settingsNav;
    TextBox? settingsSearch;
    string settingsSection = "General";
    /// <summary>Text in the search box: while it is not empty the page lists matching settings from every section.</summary>
    string settingsQuery = "";
    string keyFilter = "";
    /// <summary>The shortcut being recorded: the command and the button showing "Press a shortcut…".</summary>
    (string Id, Button Button, TextBlock Note)? recordingShortcut;

    const string Sample = "PS ~\\dev\\armux> git status  -> => != 0O 1lI {}[]";

    void ToggleSettingsPage()
    {
        if (settingsPage is null) ShowSettingsPage(settingsSection);
        else CloseSettingsPage();
    }

    /// <summary>Shows (or rebuilds) the page at <paramref name="section"/>, keeping the scroll position when it is the same section.</summary>
    void ShowSettingsPage(string section)
    {
        double offset = section == settingsSection && settingsScroll is not null ? settingsScroll.VerticalOffset : 0;
        if (settingsPage is not null) root.Children.Remove(settingsPage);
        settingsSection = section;
        recordingShortcut = null;
        settingsPage = BuildSettingsPage();
        Grid.SetRow(settingsPage, 1);
        Canvas.SetZIndex(settingsPage, 50);
        root.Children.Add(settingsPage);
        UpdateTitle();
        if (offset > 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => settingsScroll?.ChangeView(null, offset, null, true));
    }

    void CloseSettingsPage()
    {
        if (settingsPage is null) return;
        root.Children.Remove(settingsPage);
        settingsPage = null;
        settingsScroll = null;
        settingsNav = null;
        settingsSearch = null;
        settingsQuery = "";
        recordingShortcut = null;
        UpdateTitle();
        FocusKeys();
    }

    /// <summary>Esc on the page: clear the search first, then close.</summary>
    void EscapeSettingsPage()
    {
        if (settingsSearch is { Text.Length: > 0 } s) s.Text = "";
        else CloseSettingsPage();
    }

    /// <summary>Zed's "Edit in settings.json": the file opens as an editable tab; saving it applies the settings.</summary>
    void OpenSettingsJson()
    {
        if (!File.Exists(SettingsPath))
            try { Theme.Settings.Save(SettingsPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(nameof(OpenSettingsJson), e); }
        CloseSettingsPage();
        OpenFile(SettingsPath, newPane: false);
    }

    Grid BuildSettingsPage()
    {
        var page = new Grid { Background = Theme.Brush("Pane") };
        Ui.StyleControls(page.Resources);
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        page.ColumnDefinitions.Add(new ColumnDefinition());
        page.PreviewKeyDown += OnRecordKey;

        var nav = new Grid { Background = Theme.Brush("Sidebar"), BorderBrush = Theme.Brush("Divider"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, 12, 10, 12), RowSpacing = 10 };
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nav.RowDefinitions.Add(new RowDefinition());
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsSearch = Field(settingsQuery, "Search settings…", double.NaN);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsSearch, "Search settings");
        settingsSearch.TextChanged += (_, _) => { if (settingsSearch is { } s && s.Text != settingsQuery) { settingsQuery = s.Text; FillSettingsBody(); } };
        nav.Children.Add(settingsSearch);
        settingsNav = new StackPanel { Spacing = 1 };
        Grid.SetRow(settingsNav, 1);
        nav.Children.Add(settingsNav);
        var edit = SettingsButton(IconLabel(Ui.Document, "Edit in settings.json"), OpenSettingsJson);
        edit.HorizontalAlignment = HorizontalAlignment.Stretch;
        edit.HorizontalContentAlignment = HorizontalAlignment.Left;
        ToolTipService.SetToolTip(edit, SettingsPath);
        Grid.SetRow(edit, 2);
        nav.Children.Add(edit);
        page.Children.Add(nav);

        var right = new Grid();
        settingsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        right.Children.Add(settingsScroll);
        var close = Ui.IconButton(Ui.Glyph(Ui.Close, 11, Theme.Brush("TextDim")), "Close settings (Esc)", CloseSettingsPage, 30);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 10, 14, 0);
        right.Children.Add(close);
        Grid.SetColumn(right, 1);
        page.Children.Add(right);

        FillSettingsBody();
        return page;
    }

    /// <summary>The right side: the current section, or search results from every section.</summary>
    void FillSettingsBody()
    {
        if (settingsScroll is null) return;
        recordingShortcut = null;
        var body = new StackPanel { MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(40, 22, 40, 48) };
        var q = settingsQuery.Trim();
        if (q.Length == 0)
        {
            body.Children.Add(PageTitle(settingsSection));
            BuildSection(settingsSection, body);
        }
        else SearchSettings(q, body);
        settingsScroll.Content = body;

        settingsNav?.Children.Clear();
        foreach (var name in SettingsSections) settingsNav?.Children.Add(NavItem(name, q.Length == 0 && name == settingsSection));
    }

    void BuildSection(string section, StackPanel body)
    {
        switch (section)
        {
            case "General": BuildGeneral(body); break;
            case "Appearance": BuildAppearance(body); break;
            case "Terminal": BuildTerminal(body); break;
            case "Keyboard": BuildKeyboard(body); break;
            case "Notifications": BuildNotifications(body); break;
            case "Browser": BuildBrowser(body); break;
            default: BuildAbout(body); break;
        }
    }

    /// <summary>Zed's settings search: the rows whose title or description match, under the name of their section.</summary>
    void SearchSettings(string q, StackPanel body)
    {
        body.Children.Add(PageTitle("Search results"));
        int found = 0;
        foreach (var section in SettingsSections.Where(s => s is not ("Keyboard" or "About")))
        {
            var all = new StackPanel();
            BuildSection(section, all);
            var hits = all.Children.Where(e => e is FrameworkElement { Tag: string text } && text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            all.Children.Clear();
            if (hits.Count == 0) continue;
            body.Children.Add(GroupHeader(section));
            foreach (var h in hits) body.Children.Add(h);
            found += hits.Count;
        }
        int shortcuts = entries.Count(e => e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Chord.Display(keys.Effective(e.Id)).Contains(q, StringComparison.OrdinalIgnoreCase));
        if (shortcuts > 0)
        {
            body.Children.Add(GroupHeader("Keyboard"));
            body.Children.Add(Card(shortcuts == 1 ? "1 shortcut matches" : $"{shortcuts} shortcuts match", "Commands and keys in the Keyboard section.",
                ActionButton("Show", () => { keyFilter = q; settingsQuery = ""; ShowSettingsPage("Keyboard"); })));
            found++;
        }
        if (found == 0) body.Children.Add(Ui.Text($"No settings match \"{q}\".", 13.5, Theme.Brush("TextDim")));
    }

    FrameworkElement NavItem(string name, bool active)
    {
        var g = new Grid { Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(5) };
        g.Children.Add(Ui.Text(name, 13.5, Theme.Brush(active ? "Text" : "TextDim"), semibold: active));
        g.Background = active ? Theme.Brush("SelectedRow") : null;
        if (!active)
        {
            g.PointerEntered += (_, _) => g.Background = Theme.Brush("SidebarHover");
            g.PointerExited += (_, _) => g.Background = null;
        }
        g.Tapped += (_, _) => { settingsQuery = ""; ShowSettingsPage(name); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(g, name);
        return g;
    }

    // ---- building blocks ---------------------------------------------------------------------

    static TextBlock PageTitle(string text)
    {
        var t = Ui.Text(text, 22, Theme.Brush("Text"), semibold: true);
        t.Margin = new Thickness(0, 0, 0, 6);
        return t;
    }

    static TextBlock GroupHeader(string text)
    {
        var t = Ui.Text(text, 13, Theme.Brush("TextDim"), semibold: true);
        t.Margin = new Thickness(0, 26, 0, 2);
        return t;
    }

    /// <summary>A setting (Zed's row): title and description on the left, its control on the right, a line underneath.</summary>
    static Grid Card(string title, string? description, UIElement? control, TextBlock? note = null)
    {
        var g = new Grid
        {
            Padding = new Thickness(0, 12, 0, 12), ColumnSpacing = 24, MinHeight = 54,
            BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1),
            Tag = title + "\n" + description,   // what the settings search matches
        };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(title, 14, Theme.Brush("Text")));
        if (description is not null)
        {
            var d = Ui.Text(description, 12.5, Theme.Brush("TextDim"));
            d.TextWrapping = TextWrapping.Wrap;
            d.TextTrimming = TextTrimming.None;
            text.Children.Add(d);
        }
        if (note is not null) text.Children.Add(note);
        g.Children.Add(text);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 1);
            g.Children.Add(fe);
        }
        return g;
    }

    static TextBlock Note(string text = "")
    {
        var t = Ui.Text(text, 12.5, Theme.Brush("Term.Red"));
        t.TextWrapping = TextWrapping.Wrap;
        t.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        return t;
    }

    static void SetNote(TextBlock note, string text)
    {
        note.Text = text;
        note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A flat button (colors come from <see cref="Ui.StyleControls"/>).</summary>
    static Button SettingsButton(object content, Action? click)
    {
        var b = new Button { Content = content, Padding = new Thickness(10, 4, 10, 5), MinHeight = 28, FontSize = 13, CornerRadius = new CornerRadius(5) };
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    static StackPanel IconLabel(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icon = Ui.Glyph(glyph, 12, Theme.Brush("TextDim"));
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        row.Children.Add(Ui.Text(text, 13, Theme.Brush("Text")));
        return row;
    }

    static TextBox Field(string text, string placeholder, double width) => new()
    {
        Text = text,
        PlaceholderText = placeholder,
        PlaceholderForeground = Theme.Brush("TextFaint"),
        Width = width,
        FontSize = 13,
        MinHeight = 28,
        Padding = new Thickness(8, 4, 8, 5),
        CornerRadius = new CornerRadius(5),
    };

    /// <summary>Zed's dropdown: a button showing the value and a chevron, opening a menu with the current value checked.</summary>
    static Button Choice(string[] labels, int selected, Action<int> changed)
    {
        var text = Ui.Text(selected >= 0 && selected < labels.Length ? labels[selected] : "", 13, Theme.Brush("Text"));
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(text);
        var chevron = Ui.Glyph(Ui.ChevronDown, 9, Theme.Brush("TextDim"));
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 1);
        content.Children.Add(chevron);
        var b = SettingsButton(content, null);
        b.MinWidth = 190;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, ShouldConstrainToRootBounds = true, MenuFlyoutPresenterStyle = Ui.SolidPresenter(typeof(MenuFlyoutPresenter)) };
        for (int i = 0; i < labels.Length; i++)
        {
            int n = i;
            var item = new ToggleMenuFlyoutItem { Text = labels[i], IsChecked = i == selected };
            item.Click += (_, _) =>
            {
                for (int k = 0; k < menu.Items.Count; k++) ((ToggleMenuFlyoutItem)menu.Items[k]).IsChecked = k == n;
                text.Text = labels[n];
                changed(n);
            };
            menu.Items.Add(item);
        }
        b.Flyout = menu;
        return b;
    }

    static ToggleSwitch Switch(bool on, Action<bool> changed)
    {
        // Without On/Off text the template still reserves its content column; the fixed width keeps the switch at the right edge.
        var t = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, Width = 44 };
        t.Toggled += (_, _) => changed(t.IsOn);
        return t;
    }

    /// <summary>− value + buttons (NumberBox crashes NativeAOT builds, deviation D-16).</summary>
    static StackPanel Stepper(Func<string> value, Action<int> step)
    {
        var text = Ui.Text(value(), 13, Theme.Brush("Text"));
        text.Width = 64;
        text.TextAlignment = TextAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        Button Step(string glyph, int d, string tip)
        {
            var b = SettingsButton(Ui.Glyph(glyph, 10, Theme.Brush("Text")), () => { step(d); text.Text = value(); });
            b.Width = 30;
            b.Padding = new Thickness(0);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(Step("", -1, "Decrease"));
        row.Children.Add(text);
        row.Children.Add(Step(Ui.Add, 1, "Increase"));
        return row;
    }

    static Button ActionButton(string text, Action click) => SettingsButton(text, click);

    static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>An editable font list: the bundled font first (when given), then the installed fonts.</summary>
    static ComboBox FontPicker(IEnumerable<string> fonts, string current, string? bundled, Action<string> changed)
    {
        var c = new ComboBox { IsEditable = true, Width = 260, MaxDropDownHeight = 360, IsTextSearchEnabled = true, FontSize = 13, CornerRadius = new CornerRadius(5) };
        var list = new List<string>();
        if (bundled is not null) list.Add(bundled);
        list.AddRange(fonts.Where(f => !list.Contains(f, StringComparer.OrdinalIgnoreCase)));
        if (!list.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Insert(0, current);
        foreach (var f in list) c.Items.Add(f);
        c.SelectedIndex = list.FindIndex(f => f.Equals(current, StringComparison.OrdinalIgnoreCase));
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string f) changed(f); };
        c.TextSubmitted += (_, e) => { if (e.Text.Trim().Length > 0) changed(e.Text.Trim()); };
        return c;
    }

    // ---- sections ----------------------------------------------------------------------------

    void BuildGeneral(StackPanel body)
    {
        var s = Theme.Settings;
        body.Children.Add(GroupHeader("Terminals"));
        var pwsh = Armux.Core.Pty.Shells.FindPwsh();
        body.Children.Add(Card("Shell for new terminals",
            pwsh is null ? "PowerShell 7 is not installed, so PowerShell means Windows PowerShell 5.1." : $"PowerShell runs {pwsh}.",
            Choice(["PowerShell", "Command Prompt"], s.Shell == "cmd" ? 1 : 0, i => ChangeSettings(x => x with { Shell = i == 1 ? "cmd" : "pwsh" }))));

        var startNote = Note();
        var startDir = Field(s.StartDirectory, "~ (your user folder)", 240);
        void ApplyStart()
        {
            var dir = startDir.Text.Trim().Trim('"');
            if (dir.Length > 0 && !Directory.Exists(ExpandHome(dir))) { SetNote(startNote, $"\"{dir}\" does not exist."); return; }
            SetNote(startNote, "");
            ChangeSettings(x => x with { StartDirectory = dir });
        }
        startDir.LostFocus += (_, _) => ApplyStart();
        startDir.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; ApplyStart(); } };
        var browse = SettingsButton("Browse…", null);
        browse.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            try { if (await picker.PickSingleFolderAsync() is { } folder) { startDir.Text = folder.Path; ApplyStart(); } }
            catch (Exception e) { App.Log("FolderPicker", e); }
        };
        var startRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        startRow.Children.Add(startDir);
        startRow.Children.Add(browse);
        body.Children.Add(Card("Start directory", "Where armux's first terminal and every new workspace open.", startRow, startNote));

        body.Children.Add(Card("New tabs and splits open in", "Following the workspace keeps you where you last used cd.",
            Choice(["The workspace's current directory", "The start directory"], s.NewTabDirectory == "start" ? 1 : 0,
                i => ChangeSettings(x => x with { NewTabDirectory = i == 1 ? "start" : "workspace" }))));
        body.Children.Add(Card("When the last shell exits", "Typing exit in the only terminal left.",
            Choice(["Close the window", "Start a new shell"], s.CloseOnLastExit ? 0 : 1, i => ChangeSettings(x => x with { CloseOnLastExit = i == 0 }))));
        body.Children.Add(Card("Ask before closing armux", "Closing the window ends every shell and the programs running in them.",
            Choice(["When a program is running", "Always", "Never"], Array.IndexOf(Settings.ConfirmCloseModes, s.ConfirmClose),
                i => ChangeSettings(x => x with { ConfirmClose = Settings.ConfirmCloseModes[i] }))));

        body.Children.Add(GroupHeader("Startup"));
        body.Children.Add(Card("Reopen workspaces", "When armux starts, the workspaces, splits and tabs from last time come back, and each terminal opens in the folder it was in. Programs that were running (Claude Code, servers) are not restarted; run claude --continue to resume a conversation.",
            Switch(s.RestoreSession, on => ChangeSettings(x => x with { RestoreSession = on }))));

        body.Children.Add(GroupHeader("Mouse"));
        body.Children.Add(Card("Copy on select", "Selecting text with the mouse copies it. Programs that use the mouse (Claude Code, vim) need Shift+drag to select.",
            Switch(s.CopyOnSelect, on => ChangeSettings(x => x with { CopyOnSelect = on }))));

        body.Children.Add(GroupHeader("Settings file"));
        body.Children.Add(Card("settings.json", "Every setting on this page, as JSON. Saving the file applies it at once, also from another editor.", Buttons(
            ActionButton("Edit in armux", OpenSettingsJson),
            ActionButton("Open in default app", () => OpenFile(SettingsPath)),
            ActionButton("Show in folder", () => Reveal(SettingsPath)))));
        body.Children.Add(Card("Reset all settings", "Fonts, theme, shortcuts and everything else go back to their defaults.",
            ActionButton("Reset…", () => _ = ResetAllAsync())));
    }

    void BuildAppearance(StackPanel body)
    {
        var s = Theme.Settings;
        body.Children.Add(GroupHeader("Theme"));
        body.Children.Add(Card("Theme", "Match Windows follows your app mode and Windows' own contrast themes.",
            Choice(["Match Windows", "Dark", "Light", "High contrast"], Array.IndexOf(Settings.Themes, s.Theme),
                i => ChangeSettings(x => x with { Theme = Settings.Themes[i] }))));

        body.Children.Add(GroupHeader("Size"));
        var zoom = Stepper(() => $"{Theme.Settings.Zoom * 100:0}%", d => ChangeZoom(d));
        zoom.Children.Add(ActionButton("Reset", () => { ChangeZoom(0); ((TextBlock)zoom.Children[1]).Text = "100%"; }));
        body.Children.Add(Card("Zoom", "Scales the whole window. Shortcuts: Ctrl+= / Ctrl+- / Ctrl+0, or Ctrl+mouse wheel.", zoom));

        body.Children.Add(GroupHeader("Interface"));
        body.Children.Add(Card("Interface font", "IBM Plex Sans is Zed's interface font and ships with armux.",
            FontPicker(FontList.All, s.UiFont, Settings.DefaultUiFont, f => ChangeSettings(x => x with { UiFont = f }))));
        body.Children.Add(Card("Workspace color", "How a workspace's color shows in the sidebar.",
            Choice(["Left rail", "Underline", "None"], Array.IndexOf(Settings.Indicators, s.WorkspaceIndicator),
                i => ChangeSettings(x => x with { WorkspaceIndicator = Settings.Indicators[i] }))));
    }

    void BuildTerminal(StackPanel body)
    {
        var s = Theme.Settings;
        var preview = new TextBlock { Text = Sample, TextWrapping = TextWrapping.Wrap, Foreground = TerminalColors.DefaultForeground };
        var previewBox = new Border { Child = preview, Background = Theme.Brush("Pane"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 12, 0, 0) };
        void UpdatePreview()
        {
            preview.FontFamily = Theme.TerminalFont;
            preview.FontSize = Theme.TerminalFontSize;
            preview.LineHeight = Math.Round(Theme.TerminalFontSize * Theme.Settings.LineHeight);
            Microsoft.UI.Xaml.Documents.Typography.SetStandardLigatures(preview, Theme.Settings.Ligatures);
            Microsoft.UI.Xaml.Documents.Typography.SetContextualAlternates(preview, Theme.Settings.Ligatures);
        }
        UpdatePreview();

        body.Children.Add(GroupHeader("Font"));
        var fontNote = Ui.Text("", 12.5, Theme.Brush("TextDim"));
        void Font(string f)
        {
            ChangeSettings(x => x with { TerminalFont = f });
            fontNote.Text = f.Equals(Settings.DefaultTerminalFont, StringComparison.OrdinalIgnoreCase) || FontList.IsInstalled(f) ? "" : $"\"{f}\" is not installed; terminals use Cascadia Mono.";
            UpdatePreview();
        }
        var showAll = new CheckBox { Content = "All fonts", FontSize = 13 };
        var fontHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fontHost.Children.Add(FontPicker(FontList.Monospace, s.TerminalFont, Settings.DefaultTerminalFont, Font));
        fontHost.Children.Add(showAll);
        showAll.Checked += (_, _) => fontHost.Children[0] = FontPicker(FontList.All, Theme.Settings.TerminalFont, Settings.DefaultTerminalFont, Font);
        showAll.Unchecked += (_, _) => fontHost.Children[0] = FontPicker(FontList.Monospace, Theme.Settings.TerminalFont, Settings.DefaultTerminalFont, Font);
        body.Children.Add(Card("Font", "Lilex is Zed's default font and ships with armux.", fontHost, fontNote));
        body.Children.Add(Card("Font size", "Also Ctrl+Shift+= / Ctrl+Shift+- / Ctrl+Shift+0.",
            Stepper(() => $"{Theme.TerminalFontSize:0.#} pt", d => { ChangeFontSize(d); UpdatePreview(); })));
        body.Children.Add(Card("Line height", "Space between lines, as a multiple of the font size.",
            Stepper(() => $"{Theme.Settings.LineHeight:0.0#}", d => { ChangeSettings(x => x with { LineHeight = x.LineHeight + d * 0.1 }); UpdatePreview(); })));
        body.Children.Add(Card("Ligatures", "Draw -> => != and similar as single symbols, as Zed does.",
            Switch(s.Ligatures, on => { ChangeSettings(x => x with { Ligatures = on }); UpdatePreview(); })));
        body.Children.Add(previewBox);

        body.Children.Add(GroupHeader("Cursor"));
        body.Children.Add(Card("Cursor shape", null,
            Choice(["Block", "Bar", "Underline"], Array.IndexOf(Settings.CursorStyles, s.CursorStyle), i => ChangeSettings(x => x with { CursorStyle = Settings.CursorStyles[i] }))));
        body.Children.Add(Card("Blinking cursor", "Off saves a little CPU: a steady cursor never redraws.",
            Switch(s.CursorBlink, on => ChangeSettings(x => x with { CursorBlink = on }))));

        body.Children.Add(GroupHeader("History"));
        int[] sizes = [1000, 5000, 10_000, 50_000, 100_000];
        int at = Array.IndexOf(sizes, s.ScrollbackLines);
        body.Children.Add(Card("Scrollback", "Lines kept per terminal, for terminals opened from now on. Lines are stored compactly; 10,000 lines of typical output take about 1 MB.",
            Choice(["1,000 lines", "5,000 lines", "10,000 lines", "50,000 lines", "100,000 lines"], at < 0 ? 1 : at, i => ChangeSettings(x => x with { ScrollbackLines = sizes[i] }))));
    }

    void BuildKeyboard(StackPanel body)
    {
        var intro = Ui.Text("Click a shortcut, then press the new keys. Esc cancels, Backspace removes the shortcut.", 13, Theme.Brush("TextDim"));
        intro.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(intro);
        var search = Field(keyFilter, "Search commands or keys", 340);
        var resetAll = ActionButton("Reset all shortcuts", () => { ChangeSettings(x => x with { Keybindings = [] }); ShowSettingsPage("Keyboard"); });
        resetAll.IsEnabled = Theme.Settings.Keybindings.Count > 0;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        top.Children.Add(search);
        top.Children.Add(resetAll);
        body.Children.Add(top);
        var rows = new StackPanel();
        body.Children.Add(rows);

        void Fill()
        {
            rows.Children.Clear();
            string f = keyFilter.Trim();
            bool Match(string title, string chord) =>
                f.Length == 0 || title.Contains(f, StringComparison.OrdinalIgnoreCase) || Chord.Display(chord).Contains(f, StringComparison.OrdinalIgnoreCase);
            foreach (var g in entries.GroupBy(e => e.Group))
            {
                var items = g.Where(e => Match(e.Id, keys.Effective(e.Id))).ToList();
                if (items.Count == 0) continue;
                rows.Children.Add(GroupHeader(g.Key));
                foreach (var e in items) rows.Children.Add(ShortcutRow(e.Id));
            }
            var fixedMatches = fixedKeys.Where(k => k.Description.Length > 0 && Match(k.Description, k.Chord)).ToList();
            if (fixedMatches.Count > 0)
            {
                rows.Children.Add(GroupHeader("Fixed shortcuts"));
                foreach (var (chord, description, _) in fixedMatches)
                    rows.Children.Add(Card(description, null, Ui.Text(Chord.Display(chord), 13, Theme.Brush("TextDim"))));
            }
        }
        search.TextChanged += (_, _) => { keyFilter = search.Text; Fill(); };
        Fill();
    }

    Grid ShortcutRow(string id)
    {
        string chord = keys.Effective(id);
        var note = Note(keys.Conflicts(id, chord).ToList() is { Count: > 0 } c ? "Also used by " + string.Join(", ", c) : "");
        var button = SettingsButton(chord.Length > 0 ? Chord.Display(chord) : "None", null);
        button.MinWidth = 170;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        if (chord.Length == 0) button.Foreground = Theme.Brush("TextFaint");
        button.Click += (_, _) =>
        {
            recordingShortcut = (id, button, note);
            button.Content = "Press a shortcut…";
            SetNote(note, "");
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        controls.Children.Add(button);
        var reset = Ui.IconButton(Ui.Glyph(Ui.Refresh, 12, Theme.Brush("TextDim")), "Reset to " + (keys.Default(id) is { Length: > 0 } d ? Chord.Display(d) : "none"), () =>
        {
            ChangeSettings(x => x with { Keybindings = keys.With(id, null) });
            ShowSettingsPage("Keyboard");
        }, 28);
        reset.Visibility = keys.IsCustom(id) ? Visibility.Visible : Visibility.Collapsed;
        controls.Children.Add(reset);
        return Card(id, keys.IsCustom(id) ? "Default: " + (keys.Default(id) is { Length: > 0 } dd ? Chord.Display(dd) : "none") : null, controls, note);
    }

    /// <summary>While a shortcut is being recorded, the next key chord becomes the shortcut.</summary>
    void OnRecordKey(object sender, KeyRoutedEventArgs e)
    {
        if (recordingShortcut is not { } rec) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape) { recordingShortcut = null; ShowSettingsPage("Keyboard"); return; }
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;   // wait for the key that goes with the modifiers
        bool ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu), shift = Down(VirtualKey.Shift);
        if (e.Key == VirtualKey.Back && !ctrl && !alt)
        {
            recordingShortcut = null;
            ChangeSettings(x => x with { Keybindings = keys.With(rec.Id, "") });
            ShowSettingsPage("Keyboard");
            return;
        }
        if (KeyName(e.Key) is not { } name) { SetNote(rec.Note, "That key can't be part of a shortcut."); return; }
        bool standalone = name.Length > 1 && name[0] == 'F' && char.IsDigit(name[1]) || name is "PAGEUP" or "PAGEDOWN" or "INSERT";
        if (!ctrl && !alt && !standalone) { SetNote(rec.Note, "Add Ctrl or Alt: plain keys belong to the terminal."); return; }
        if (ctrl && !alt && !shift && name.Length == 1 && char.IsLetter(name[0]))
            SetNote(rec.Note, "Ctrl+letter is also a terminal key (Ctrl+C, Ctrl+R…); armux will take it from the shell.");
        var chord = new Chord(ctrl, alt, shift, name).ToString();
        recordingShortcut = null;
        ChangeSettings(x => x with { Keybindings = keys.With(rec.Id, chord) });
        ShowSettingsPage("Keyboard");
    }

    void BuildNotifications(StackPanel body)
    {
        var s = Theme.Settings;
        body.Children.Add(GroupHeader("When a terminal needs you"));
        body.Children.Add(Card("Windows notifications", "Show a toast for OSC 9 / 99 / 777, armux notify and the bell while armux is in the background. Clicking it jumps to the tab.",
            Switch(s.Toasts, on => ChangeSettings(x => x with { Toasts = on }))));
        body.Children.Add(Card("Taskbar status", "The current workspace on armux's taskbar button: accent bar while a program works, yellow when it needs you, red on error.",
            Switch(s.TaskbarStatus, on => ChangeSettings(x => x with { TaskbarStatus = on }))));
        body.Children.Add(Card("Bell asks for attention", "Claude Code rings the terminal bell when it waits for input. Off: the bell is ignored.",
            Switch(s.BellNotifies, on => ChangeSettings(x => x with { BellNotifies = on }))));
        body.Children.Add(Card("Test", "Shows a toast now, even while armux is in front.",
            ActionButton("Send a test notification", () => Toasts.Show(this, Focused?.Selected?.Id ?? 0, state.Current?.Title ?? "armux", "armux", "Notifications are working."))));
    }

    void BuildBrowser(StackPanel body)
    {
        var s = Theme.Settings;
        body.Children.Add(GroupHeader("Address bar"));
        body.Children.Add(Card("Search engine", "Used when what you type in the address bar is not an address.",
            Choice(["Google", "Bing", "DuckDuckGo"], Array.IndexOf(Settings.SearchEngines, s.SearchEngine), i => ChangeSettings(x => x with { SearchEngine = Settings.SearchEngines[i] }))));
        var home = Field(s.BrowserHome, "Blank: focus the address bar", 280);
        home.LostFocus += (_, _) => ChangeSettings(x => x with { BrowserHome = home.Text });
        home.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) ChangeSettings(x => x with { BrowserHome = home.Text }); };
        body.Children.Add(Card("New tab page", "The page a new browser tab opens.", home));
        body.Children.Add(GroupHeader("Coding agents"));
        body.Children.Add(Card("Let agents control the browser", "Claude Code, through armux mcp, can read pages, click, type and run JavaScript in browser tabs, including sites you are signed in to. Only programs running as you can reach it. Turning this on adds armux to Claude Code's MCP servers (restart Claude Code to load them); turning it off removes it.",
            Switch(s.BrowserControl, on => ChangeSettings(x => x with { BrowserControl = on }))));
        body.Children.Add(GroupHeader("Memory"));
        body.Children.Add(Card("Live browser tabs", "At most two browser tabs keep a page loaded; others reload when you show them. Hidden tabs are suspended after a second.", null));
    }

    void BuildAbout(StackPanel body)
    {
        var info = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var version = info.Split('+')[0];
        var commit = info.Contains('+') ? info[(info.IndexOf('+') + 1)..] : "";
        if (commit.Length > 7) commit = commit[..7];

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 10, 0, 6) };
        var logo = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(10) };
        try { logo.Child = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/armux-logo.png")) }; } catch (Exception e) { App.Log("logo", e); }
        top.Children.Add(logo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        names.Children.Add(Ui.Text("armux", 26, Theme.Brush("Text"), semibold: true));
        names.Children.Add(Ui.Text($"Version {version}" + (commit.Length > 0 ? $" ({commit})" : ""), 14, Theme.Brush("TextDim")));
        names.Children.Add(Ui.Text("A cmux-style terminal workspace for Windows on ARM64", 13, Theme.Brush("TextDim")));
        top.Children.Add(names);
        body.Children.Add(top);

        using var self = Process.GetCurrentProcess();
        bool aot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
        string sdk;
        try { sdk = $"{Microsoft.Windows.ApplicationModel.WindowsAppRuntime.ReleaseInfo.AsString} (runtime {Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString})"; }
        catch (Exception) { sdk = "unknown"; }
        var rows = new (string Name, string Value)[]
        {
            ("Version", version + (commit.Length > 0 ? " · commit " + commit : "")),
            ("Build", (aot ? "NativeAOT" : "JIT (development build)") + " · " + RuntimeInformation.ProcessArchitecture),
            (".NET", RuntimeInformation.FrameworkDescription),
            ("Windows App SDK", sdk),
            ("Windows", $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}"),
            ("Memory (this window)", $"{self.WorkingSet64 / 1048576.0:0.0} MB working set · {self.PrivateMemorySize64 / 1048576.0:0.0} MB private"),
            ("Terminals open", $"{sessions.Count(x => x.Value.Live && !x.Value.Exited)} (each runs a conhost.exe and its shell)"),
            ("Settings", SettingsPath),
            ("Log", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "armux", "armux.log")),
        };
        body.Children.Add(GroupHeader("This build"));
        foreach (var (name, value) in rows)
        {
            var v = Ui.Text(value, 13, Theme.Brush("TextDim"));
            v.IsTextSelectionEnabled = true;
            v.TextWrapping = TextWrapping.Wrap;
            v.MaxWidth = 460;
            body.Children.Add(Card(name, null, v));
        }
        var diagnostics = string.Join(Environment.NewLine, rows.Select(r => $"{r.Name}: {r.Value}"));
        body.Children.Add(GroupHeader("Links"));
        body.Children.Add(Card("Copy these details", "For a bug report.", ActionButton("Copy", () =>
        {
            var data = new DataPackage();
            data.SetText("armux " + diagnostics);
            Clipboard.SetContent(data);
        })));
        body.Children.Add(Card("Source code and issues", "github.com/apaslam09/armux", ActionButton("Open", () => OpenFile("https://github.com/apaslam09/armux"))));
        body.Children.Add(Card("License", "MIT. armux contains no cmux code (cmux is GPL-3.0).", ActionButton("View", () => OpenFile("https://github.com/apaslam09/armux/blob/main/LICENSE"))));
        body.Children.Add(Card("Third-party notices", "Lilex and IBM Plex Sans (SIL Open Font License 1.1), Microsoft WebView2 (BSD-3-Clause), Windows App SDK.",
            ActionButton("Show files", () => Reveal(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "OFL-Lilex.txt")))));
        body.Children.Add(Card("Logs", "Errors are written here; attach it to a bug report.",
            ActionButton("Show in folder", () => Reveal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "armux", "armux.log")))));
    }

    // ---- helpers -----------------------------------------------------------------------------

    static void OpenFile(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception)
        {
            // No app for .json: Notepad is always there.
            try { Process.Start("notepad.exe", $"\"{pathOrUrl}\""); } catch (Exception e2) { App.Log(nameof(OpenFile), e2); }
        }
    }

    static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\""); }
        catch (Exception e) { App.Log(nameof(Reveal), e); }
    }

    async Task ResetAllAsync()
    {
        var dlg = Dialog("Reset all settings?", Ui.Text("Fonts, theme, zoom, shortcuts and every other setting go back to their defaults.", 14, Theme.Brush("Text")), "Reset");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        ChangeSettings(_ => new Settings());
        ShowSettingsPage("General");
    }
}
