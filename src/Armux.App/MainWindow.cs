using System.Collections.Specialized;
using Armux.Core;
using Layout = Armux.Core.Layout;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Armux.App;

/// <summary>
/// The armux window: title bar (sidebar toggle, explorer toggle, notifications bell, new workspace, workspace title),
/// the vertical workspace sidebar, the optional file explorer beside it, and the split-pane area of the current workspace.
/// </summary>
public sealed partial class MainWindow : Window, IPaneHost, IExplorerHost
{
    const double SidebarWidth = 280;

    readonly LaunchOptions opts;
    readonly AppState state;
    readonly BrowserHost browsers;
    readonly Grid root = new();
    readonly Grid titleRow = new();
    readonly ColumnDefinition titleLeft = new();
    readonly ColumnDefinition sidebarCol = new() { Width = new GridLength(SidebarWidth) };
    readonly ColumnDefinition explorerCol = new() { Width = new GridLength(0) };
    readonly StackPanel sidebarList = new() { Padding = new Thickness(0, 4, 0, 12) };
    readonly Grid content = new();
    readonly Grid dragRegion = new();
    readonly TextBlock titleText = new();
    /// <summary>The title bar's buttons (sidebar, explorer, bell, new workspace, settings); hidden while Settings is open.</summary>
    StackPanel titleButtons = new();
    FontIcon titleIcon = new();
    Grid? bellIcon;
    Grid? titlePanel;
    readonly Border bellBadge = new();
    readonly TextBlock bellCount = new();
    readonly CommandPalette palette = new();
    /// <summary>
    /// Holds keyboard focus for the pane area, so shortcuts keep working after re-renders destroy the focused
    /// element. Phase 3's terminal control takes this role.
    /// </summary>
    readonly ContentControl keySink = new() { IsTabStop = true, UseSystemFocusVisuals = false, Width = 0, Height = 0 };
    Button bellButton = null!;
    Flyout bellFlyout = null!;
    readonly Dictionary<Pane, PaneView> paneViews = [];
    readonly Dictionary<Surface, FrameworkElement> panels = [];
    bool sidebarVisible = true;
    PipeServer? pipe;

    /// <summary>True while this window is the foreground window; toasts are only shown when it is not.</summary>
    public bool IsActiveWindow { get; private set; } = true;

    readonly Viewbox zoomHost = new() { Stretch = Stretch.Fill };
    /// <summary>
    /// The window's own presenter, kept so its minimum size can be set: under NativeAOT, casting
    /// <c>AppWindow.Presenter</c> to <see cref="OverlappedPresenter"/> fails. Full screen returns to this one.
    /// </summary>
    readonly OverlappedPresenter presenter = OverlappedPresenter.Create();
    readonly Windows.UI.ViewManagement.UISettings uiSettings = new();

    void ApplyZoom(Windows.Foundation.Size size)
    {
        double k = Theme.Settings.Zoom;
        if (size.Width <= 0 || size.Height <= 0) return;
        root.Width = size.Width / k;
        root.Height = size.Height / k;
        UpdateMinSize();
    }

    /// <summary>Title space kept beside the title bar's buttons: folder icon plus a few characters of the title.</summary>
    const double MinTitleWidth = 140;
    /// <summary>Title bar, a pane's tab bar and a few terminal rows.</summary>
    const double MinContentHeight = 220;

    /// <summary>Keeps the title clear of the caption buttons, in the zoomed layout's units.</summary>
    void UpdateTitleMargin()
    {
        if (titlePanel is null) return;
        double scale = root.XamlRoot?.RasterizationScale ?? 1;
        double caption = AppWindow.TitleBar.RightInset > 0 ? AppWindow.TitleBar.RightInset / scale : 138;
        var m = new Thickness(14, 0, caption / Theme.Settings.Zoom + 8, 0);
        if (titlePanel.Margin != m) titlePanel.Margin = m;
    }

    /// <summary>
    /// The smallest the window may be dragged: the title bar's buttons and some of the title stay clear of the
    /// minimize, maximize and close buttons. It follows the sidebar (its width sets the buttons' column), zoom and DPI.
    /// </summary>
    void UpdateMinSize()
    {
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped || root.XamlRoot is not { } xr) return;
        double scale = xr.RasterizationScale, k = Theme.Settings.Zoom;
        // RightInset is the caption buttons' width in physical pixels; 0 before the title bar is set up.
        double caption = AppWindow.TitleBar.RightInset > 0 ? AppWindow.TitleBar.RightInset : 138 * scale;
        int w = (int)Math.Ceiling((titleLeft.Width.Value + MinTitleWidth) * k * scale + caption);
        int h = (int)Math.Ceiling(MinContentHeight * k * scale);
        UpdateTitleMargin();
        if (presenter.PreferredMinimumWidth != w) presenter.PreferredMinimumWidth = w;
        if (presenter.PreferredMinimumHeight != h) presenter.PreferredMinimumHeight = h;
        // Zooming in or showing the sidebar can raise the minimum above the current size.
        var now = AppWindow.Size;
        if (presenter.State == OverlappedPresenterState.Restored && (now.Width < w || now.Height < h))
            AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(now.Width, w), Math.Max(now.Height, h)));
    }

    /// <summary>Ctrl+= / Ctrl+- / Ctrl+0 and Ctrl+wheel: scale the whole window by 10% steps.</summary>
    void ChangeZoom(int step)
    {
        double z = step == 0 ? 1.0 : Theme.Settings.Zoom + step * 0.1;
        var s = (Theme.Settings with { Zoom = z }).Normalized();
        if (s.Zoom == Theme.Settings.Zoom) return;
        Theme.UseSettings(s);
        try { Theme.Settings.Save(SettingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(nameof(ChangeZoom), e); }
        ApplyZoom(new Windows.Foundation.Size(zoomHost.ActualWidth, zoomHost.ActualHeight));
    }

    public MainWindow(LaunchOptions opts)
    {
        this.opts = opts;
        state = opts.BuildState();
        browsers = new BrowserHost(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        // --theme (screenshots) wins over the saved theme.
        Theme.Use(Theme.Resolve(opts.Theme ?? Theme.Settings.Theme));
        state.Indicator = Indicator(Theme.Settings.WorkspaceIndicator);
        Title = "armux";
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(opts.Width, opts.Height));
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "armux.ico")); }
        catch (Exception e) { App.Log("SetIcon", e); }

        BuildChrome();
        RegisterCommands();
        // App zoom (Ctrl+= / Ctrl+-): the content is laid out at window size / zoom and the Viewbox scales it back up
        // to fill the window, so everything grows together. WinUI has no layout-affecting scale transform.
        zoomHost.Child = root;
        zoomHost.SizeChanged += (_, e) => ApplyZoom(e.NewSize);
        // Growing the window could leave terminals half blank until a workspace switch; refit them once it settles.
        var refit = new DebounceTimer(DispatcherQueue, TimeSpan.FromMilliseconds(250), () =>
        {
            foreach (var t in panels.Values.OfType<TerminalView>()) t.Refit();
        });
        SizeChanged += (_, _) => refit.Restart();
        Content = zoomHost;
        SetTitleBar(dragRegion);

        state.Workspaces.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(RenderSidebar);
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.UnreadTotal)) DispatcherQueue.TryEnqueue(UpdateBell); };
        ((INotifyCollectionChanged)state.Notifications).CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateBell);

        RenderAll();
        root.Loaded += (_, _) => OnFirstLoad();

        Activated += (_, e) =>
        {
            IsActiveWindow = e.WindowActivationState != WindowActivationState.Deactivated;
            state.WindowActive = IsActiveWindow;
        };
        Toasts.Invoked += JumpToSurface;
        // "Match Windows" follows app-mode and contrast-theme changes while armux runs.
        uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (opts.Theme is null && Theme.Settings.Theme == "system" && Theme.Resolve("system") != Theme.Kind)
            {
                SetTheme(Theme.Resolve("system"));
                if (settingsPage is not null) ShowSettingsPage(settingsSection);
            }
        });
        palette.Closed += FocusKeys;
        // Diagnostics: ARMUX_FOCUS_LOG=<file> records every keyboard focus change.
        if (Environment.GetEnvironmentVariable("ARMUX_FOCUS_LOG") is { Length: > 0 } focusLog)
            Microsoft.UI.Xaml.Input.FocusManager.GotFocus += (_, e) =>
            {
                var el = e.NewFocusedElement as FrameworkElement;
                var parent = el is null ? null : VisualTreeHelper.GetParent(el) as FrameworkElement;
                File.AppendAllText(focusLog, $"{DateTime.Now:HH:mm:ss.fff} {e.NewFocusedElement?.GetType().Name ?? "null"} name='{el?.Name}' parent={parent?.GetType().Name}\n");
            };
        pipe = new PipeServer(DispatcherQueue, HandleRequest);
        pipe.Start();
        WatchSettingsFile();
        StartSessionSaving();
        AppWindow.Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            // Before the shells go: each terminal's folder is saved with the session.
            sessionTimer?.Stop();
            SaveSession();
            pipe?.Dispose();
            settingsWatcher?.Dispose();
            DisposeAllSessions();
            Toasts.Unregister();
        };
    }

    // ---- chrome ---------------------------------------------------------------------------

    void BuildChrome()
    {
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        root.RowDefinitions.Add(new RowDefinition());

        titleRow.ColumnDefinitions.Add(titleLeft);
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(titleRow);

        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(sidebarCol);
        bodyGrid.ColumnDefinitions.Add(explorerCol);
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bodyGrid.Children.Add(new ScrollViewer { Content = sidebarList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(explorerHost, 1);
        bodyGrid.Children.Add(explorerHost);
        Grid.SetColumn(content, 2);
        bodyGrid.Children.Add(content);
        Grid.SetRow(bodyGrid, 1);
        root.Children.Add(bodyGrid);

        Grid.SetRowSpan(palette, 2);
        root.Children.Add(palette);
        root.Children.Add(keySink);
    }

    void BuildTitleRow()
    {
        titleRow.Children.Clear();
        titleRow.Background = Theme.Brush("TitleBar");
        var dim = Theme.Brush("TextDim");

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Ui.IconButton(Ui.SidebarIcon(dim), "Toggle sidebar (Ctrl+Shift+B)", ToggleSidebar));
        explorerButton = Ui.IconButton(Ui.Glyph(Ui.Explorer, 14, dim), "File explorer (Ctrl+Shift+E)", ToggleExplorer);
        left.Children.Add(explorerButton);

        // The badge and title text outlive rebuilds (theme or font change). Their old panels have left the tree, where
        // Parent reads null, so they are detached from the panels remembered here, not through Parent.
        bellIcon?.Children.Remove(bellBadge);
        titlePanel?.Children.Remove(titleText);
        var bell = bellIcon = new Grid();
        bell.Children.Add(Ui.Glyph(Ui.Bell, 14, dim));
        bellBadge.Child = bellCount;
        bellBadge.Background = Theme.Brush("Accent");
        bellBadge.CornerRadius = new CornerRadius(7);
        bellBadge.MinWidth = 14;
        bellBadge.Height = 14;
        bellBadge.Padding = new Thickness(3, 0, 3, 0);
        bellBadge.HorizontalAlignment = HorizontalAlignment.Right;
        bellBadge.VerticalAlignment = VerticalAlignment.Top;
        bellBadge.Margin = new Thickness(0, -7, -9, 0);
        bellCount.FontSize = 9.5;
        bellCount.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        bellCount.Foreground = Theme.Brush("SelectedText");
        bellCount.HorizontalAlignment = HorizontalAlignment.Center;
        bellCount.VerticalAlignment = VerticalAlignment.Center;
        bell.Children.Add(bellBadge);
        bellButton = Ui.IconButton(bell, "Notifications (Ctrl+Shift+I)", () => bellFlyout.ShowAt(bellButton));
        bellFlyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft, ShouldConstrainToRootBounds = true, FlyoutPresenterStyle = Ui.SolidPresenter(typeof(FlyoutPresenter)) };
        bellFlyout.Opening += (_, _) => bellFlyout.Content = NotificationsList();
        left.Children.Add(bellButton);
        left.Children.Add(Ui.IconButton(Ui.Glyph(Ui.Add, 13, dim), "New workspace (Ctrl+Shift+N)", NewWorkspace));
        left.Children.Add(Ui.IconButton(Ui.Glyph(Ui.Gear, 13, dim), "Settings (Ctrl+,)", ToggleSettingsPage));
        titleButtons = left;
        titleRow.Children.Add(left);

        dragRegion.Children.Clear();
        dragRegion.Background = new SolidColorBrush(Colors.Transparent);
        // A grid, not a stack panel, so a long title is trimmed instead of measured at full length.
        var title = titlePanel = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition());
        titleIcon = Ui.Glyph(Ui.Folder, 13, Theme.Brush("Link"));
        title.Children.Add(titleIcon);
        Grid.SetColumn(titleText, 1);
        titleText.FontSize = 13;
        // A long workspace name ends in "…" before the caption buttons instead of running under them.
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        UpdateTitleMargin();
        titleText.FontFamily = new FontFamily(Theme.UiFontSemibold);
        titleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        titleText.Foreground = Theme.Brush("Text");
        title.Children.Add(titleText);
        dragRegion.Children.Add(title);
        Grid.SetColumn(dragRegion, 1);
        titleRow.Children.Add(dragRegion);

        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Theme.Color("Text");
        tb.ButtonInactiveForegroundColor = Theme.Color("TextFaint");
        tb.ButtonHoverBackgroundColor = Theme.Color("SidebarHover");
        tb.ButtonHoverForegroundColor = Theme.Color("Text");
    }

    void UpdateTitle()
    {
        var ws = state.Current;
        // Settings covers the workspace, so its buttons (new workspace, sidebar, explorer...) go until it closes.
        bool settings = settingsPage is not null;
        titleButtons.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        titleIcon.Glyph = settings ? Ui.Gear : Ui.Folder;
        titleIcon.Foreground = Theme.Brush(settings ? "TextDim" : "Link");
        titleText.Text = settings ? "Settings" : ws is null ? "armux" : ws.Zoomed is null ? ws.Title : $"{ws.Title}  ·  zoomed";
        Title = settings ? "Settings - armux" : ws is null ? "armux" : $"{ws.Title} - armux";
        titleLeft.Width = new GridLength(settings ? 0 : sidebarVisible ? SidebarWidth : 180);
        UpdateMinSize();
    }

    void UpdateBell()
    {
        UpdateTaskbar();
        UpdateAllStatuses();
        int n = state.UnreadTotal;
        bellCount.Text = n > 99 ? "99+" : n.ToString();
        bellBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- rendering -----------------------------------------------------------------------

    void RenderAll()
    {
        root.RequestedTheme = Theme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        root.Background = Theme.Brush("Sidebar");
        content.Background = Theme.Brush("Divider");
        BuildTitleRow();
        // Theme brushes are baked into views, so a theme change rebuilds them.
        foreach (var v in paneViews.Values) v.Detach();
        paneViews.Clear();
        foreach (var (s, panel) in panels.ToList())
            if (panel is not BrowserPanel) panels.Remove(s);
        RenderSidebar();
        RenderContent();
        RenderExplorer(rebuild: true);
        UpdateTitle();
        UpdateBell();
        FocusKeys();
    }

    void RenderSidebar()
    {
        sidebarList.Children.Clear();
        foreach (var ws in state.Workspaces)
            sidebarList.Children.Add(new SidebarRow(ws, state, SelectWorkspace, ShowWorkspaceMenu));
    }

    void RenderContent()
    {
        try { RenderContentCore(); }
        catch (Exception ex) { App.Log(nameof(RenderContent), ex); }
    }

    void RenderContentCore()
    {
        foreach (var v in paneViews.Values)
            if (v.Parent is Panel p) p.Children.Remove(v);
        content.Children.Clear();
        if (state.Current is not { } ws) return;
        var tree = ws.Zoomed is { } z ? Mount(z) : Build(ws.Root);
        content.Children.Add(tree);
        foreach (var pane in ws.Panes) ViewOf(pane).Refresh();
        UpdateTitle();
        // Moving pane views out of the tree drops keyboard focus; keep shortcuts working.
        if (!palette.IsOpen) FocusKeys();
    }

    FrameworkElement Build(PaneNode node)
    {
        if (node is Pane pane) return Mount(pane);
        var s = (SplitNode)node;
        var g = new Grid();
        bool right = s.Direction == SplitDirection.Right;
        for (int i = 0; i < 3; i++)
        {
            if (right) g.ColumnDefinitions.Add(new ColumnDefinition());
            else g.RowDefinitions.Add(new RowDefinition());
        }
        var a = Build(s.First);
        var b = Build(s.Second);
        var handle = new SplitterHandle(s, g);
        if (right)
        {
            g.ColumnDefinitions[1].Width = new GridLength(1);
            Grid.SetColumn(handle, 1);
            Grid.SetColumn(b, 2);
        }
        else
        {
            g.RowDefinitions[1].Height = new GridLength(1);
            Grid.SetRow(handle, 1);
            Grid.SetRow(b, 2);
        }
        g.Children.Add(a);
        g.Children.Add(b);
        g.Children.Add(handle);
        handle.Apply();
        return g;
    }

    /// <summary>The cached view for <paramref name="pane"/>, created on first use.</summary>
    PaneView ViewOf(Pane pane)
    {
        if (!paneViews.TryGetValue(pane, out var v)) paneViews[pane] = v = new PaneView(pane, this);
        return v;
    }

    /// <summary>The view for <paramref name="pane"/>, detached so it can be placed in a new layout.</summary>
    PaneView Mount(Pane pane)
    {
        var v = ViewOf(pane);
        if (v.Parent is Panel p) p.Children.Remove(v);
        return v;
    }

    // ---- IPaneHost --------------------------------------------------------------------------

    public bool IsFocused(Pane pane) => ReferenceEquals(state.Current?.Focused, pane);

    /// <summary>Gives keyboard focus to the focused pane's terminal, or to the key sink when it has none.</summary>
    void FocusKeys() => DispatcherQueue.TryEnqueue(() =>
    {
        // A dialog, menu or flyout that is open keeps the keyboard, and so does the Settings page.
        if (palette.IsOpen || settingsPage is not null || root.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0) return;
        if (FocusedTerminal() is { } t) t.Focus(FocusState.Programmatic);
        else if (Focused?.Selected is { } s && panels.TryGetValue(s, out var p) && p is FileView { Editable: true } editor) editor.FocusEditor();
        else keySink.Focus(FocusState.Programmatic);
    });

    /// <summary>After a click or tab switch: focus the terminal, but leave focus alone for browser and file tabs.</summary>
    void FocusTerminal() => DispatcherQueue.TryEnqueue(() =>
    {
        if (palette.IsOpen || settingsPage is not null || root.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count > 0) return;
        FocusedTerminal()?.Focus(FocusState.Programmatic);
    });

    public void FocusPane(Pane pane, bool focusTerminal = true)
    {
        if (state.Current is not { } ws) return;
        // Clicking the pane that is already focused still has to give its terminal the keyboard back.
        if (ReferenceEquals(ws.Focused, pane)) { if (focusTerminal) FocusTerminal(); return; }
        var old = ws.Focused;
        ws.Focused = pane;
        if (pane.Selected is { } s) state.MarkRead(ws, s);
        if (old is not null && paneViews.TryGetValue(old, out var ov)) ov.Refresh();
        ViewOf(pane).Refresh();
        if (focusTerminal) FocusTerminal();
        UpdateTaskbar();
    }

    public void SelectSurface(Pane pane, Surface surface)
    {
        // The prompt editor belongs to one tab; switching tabs closes it (its draft is kept).
        if (!ReferenceEquals(pane.Selected, surface) && paneViews.TryGetValue(pane, out var pv) && pv.EditorOpen) pv.ShowEditor(null);
        pane.Selected = surface;
        FocusPane(pane);
        if (state.Current is { } ws) state.MarkRead(ws, surface);
        if (paneViews.TryGetValue(pane, out var v)) v.Refresh();
        FocusTerminal();
    }

    public void CloseSurface(Pane pane, Surface surface)
    {
        // The surface may be in a background workspace (a shell that exited there).
        if ((Locate(surface)?.Ws ?? state.Current) is not { } ws) return;
        if (panels.Remove(surface, out var panel) && panel is BrowserPanel bp) browsers.Close(bp);
        DisposeSession(surface);
        state.MarkRead(ws, surface);
        int at = pane.Surfaces.IndexOf(surface);
        pane.Surfaces.Remove(surface);
        if (pane.Surfaces.Count > 0)
        {
            pane.Selected = pane.Surfaces[Math.Min(at, pane.Surfaces.Count - 1)];
            ViewOf(pane).Refresh();
            return;
        }
        if (Layout.Close(ws, pane))
        {
            if (paneViews.Remove(pane, out var v)) v.Detach();
            if (ReferenceEquals(ws, state.Current)) RenderContent();
        }
        else if (state.Workspaces.Count > 1) CloseWorkspace(ws);
        else NewSurface(pane, PanelKind.Terminal);
    }

    public void NewSurface(Pane pane, PanelKind kind)
    {
        var s = kind == PanelKind.Browser ? NewBrowserSurface() : LiveTerminal();
        pane.Surfaces.Add(s);
        SelectSurface(pane, s);
    }

    public void SplitPane(Pane pane, SplitDirection direction)
    {
        if (state.Current is not { } ws) return;
        Layout.Split(ws, pane, direction, LiveTerminal());
        RenderContent();
    }

    public FrameworkElement PanelFor(Surface surface)
    {
        if (!panels.TryGetValue(surface, out var p))
            panels[surface] = p = surface.Kind switch
            {
                PanelKind.Browser => new BrowserPanel(surface, browsers),
                PanelKind.File => new FileView(surface, IsSettingsFile(surface.FilePath) ? SaveSettingsText : null),
                _ => new TerminalView(SessionFor(surface))
                {
                    ZoomRequested = step => ChangeZoom(step),
                    DropText = paths => DropText(surface, paths),
                },
            };
        return p;
    }

    // ---- workspaces ---------------------------------------------------------------------------

    void SelectWorkspace(Workspace ws)
    {
        if (ReferenceEquals(state.Current, ws)) { FocusKeys(); return; }
        state.Current = ws;
        RenderContent();
        RenderExplorer(rebuild: false);
        UpdateTaskbar();
    }

    void NewWorkspace()
    {
        var ws = MockData.NewWorkspace($"workspace {state.Workspaces.Count + 1}", "", "", StartDirectory(), LiveTerminal());
        state.Workspaces.Add(ws);
        SelectWorkspace(ws);
    }

    void CloseWorkspace(Workspace ws)
    {
        if (state.Workspaces.Count <= 1) return;
        int at = state.Workspaces.IndexOf(ws);
        foreach (var pane in ws.Panes.ToList())
        {
            foreach (var s in pane.Surfaces)
            {
                if (panels.Remove(s, out var panel) && panel is BrowserPanel bp) browsers.Close(bp);
                DisposeSession(s);
            }
            if (paneViews.Remove(pane, out var v)) v.Detach();
        }
        foreach (var n in state.Notifications.Where(n => ReferenceEquals(n.Workspace, ws)).ToList()) state.Notifications.Remove(n);
        explorerRoots.Remove(ws);
        state.Workspaces.Remove(ws);
        if (ReferenceEquals(state.Current, ws)) SelectWorkspace(state.Workspaces[Math.Min(at, state.Workspaces.Count - 1)]);
        UpdateBell();
    }

    void ToggleSidebar()
    {
        sidebarVisible = !sidebarVisible;
        sidebarCol.Width = new GridLength(sidebarVisible ? SidebarWidth : 0);
        UpdateTitle();
    }

    void ShowWorkspaceMenu(Workspace ws, FrameworkElement anchor)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = true, MenuFlyoutPresenterStyle = Ui.SolidPresenter(typeof(MenuFlyoutPresenter)) };
        void Add(MenuFlyoutItemBase item) => menu.Items.Add(item);
        MenuFlyoutItem Item(string text, Action run) { var i = new MenuFlyoutItem { Text = text }; i.Click += (_, _) => run(); return i; }

        Add(Item("Rename…", () => _ = RenameAsync(ws)));
        Add(Item(ws.Pinned ? "Unpin" : "Pin to top", () => { ws.Pinned = !ws.Pinned; state.SortPinned(); }));
        Add(Item("Color…", () => ShowColorFlyout(ws, anchor)));
        Add(new MenuFlyoutSeparator());
        Add(Item("Close workspace", () => CloseWorkspace(ws)));
        menu.ShowAt(anchor);
    }

    /// <summary>cmux tab colors: 17 presets, a custom color, or none.</summary>
    void ShowColorFlyout(Workspace ws, FrameworkElement anchor)
    {
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = true,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop,
            FlyoutPresenterStyle = Ui.SolidPresenter(typeof(FlyoutPresenter)),
        };
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 8 };
        const int perRow = 6;
        for (int c = 0; c < perRow; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < (WorkspaceColors.Presets.Length + perRow - 1) / perRow; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < WorkspaceColors.Presets.Length; i++)
        {
            var (name, hex) = WorkspaceColors.Presets[i];
            var swatch = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 22, Height = 22, Fill = Theme.Hex(hex),
                Stroke = ws.Color == hex ? Theme.Brush("Text") : null, StrokeThickness = 2,
            };
            ToolTipService.SetToolTip(swatch, name);
            swatch.Tapped += (_, _) => { ws.Color = hex; flyout.Hide(); };
            Grid.SetRow(swatch, i / perRow);
            Grid.SetColumn(swatch, i % perRow);
            grid.Children.Add(swatch);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var custom = new Button { Content = "Custom…" };
        custom.Click += (_, _) => { flyout.Hide(); _ = CustomColorAsync(ws); };
        var none = new Button { Content = "No color" };
        none.Click += (_, _) => { ws.Color = null; flyout.Hide(); };
        buttons.Children.Add(custom);
        buttons.Children.Add(none);
        var body = new StackPanel();
        body.Children.Add(Ui.Text("Workspace color", 13, Theme.Brush("Text"), semibold: true));
        body.Children.Add(new Border { Height = 10 });
        body.Children.Add(grid);
        body.Children.Add(buttons);
        flyout.Content = body;
        flyout.ShowAt(anchor);
    }

    async Task RenameAsync(Workspace ws)
    {
        var box = new TextBox { Text = ws.Title };
        box.SelectAll();
        var dlg = Dialog("Rename workspace", box, "Rename");
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim() is { Length: > 0 } t)
        {
            ws.Title = t;
            UpdateTitle();
        }
    }

    async Task CustomColorAsync(Workspace ws)
    {
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, Color = ws.Color is null ? Theme.Color("Accent") : Theme.Hex(ws.Color).Color };
        if (await Dialog("Workspace color", picker, "Apply").ShowAsync() == ContentDialogResult.Primary)
            ws.Color = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
    }

    ArmuxDialog Dialog(string title, UIElement body, string primary) => Ui.Dialog(root.XamlRoot, title, body, primary);

    // ---- notifications -----------------------------------------------------------------------

    UIElement NotificationsList()
    {
        var panel = new StackPanel { Width = 380, Spacing = 2 };
        var head = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Ui.Text("Notifications", 14, Theme.Brush("Text"), semibold: true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var markAll = new HyperlinkButton { Content = "Mark all read", FontSize = 12, Padding = new Thickness(4, 0, 4, 0) };
        markAll.Click += (_, _) => { state.MarkAllRead(); bellFlyout.Hide(); };
        var clear = new HyperlinkButton { Content = "Clear", FontSize = 12, Padding = new Thickness(4, 0, 4, 0) };
        clear.Click += (_, _) => { state.Clear(); bellFlyout.Hide(); };
        actions.Children.Add(markAll);
        actions.Children.Add(clear);
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);
        panel.Children.Add(head);

        if (state.Notifications.Count == 0)
            panel.Children.Add(Ui.Text("No notifications", 12.5, Theme.Brush("TextFaint")));
        foreach (var n in state.Notifications.Take(30))
        {
            var row = new Grid { Padding = new Thickness(8, 6, 8, 7), CornerRadius = new CornerRadius(6), ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top, Fill = n.Read ? null : Theme.Brush("Accent") });
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(Ui.Text(n.Title, 12.5, Theme.Brush("Text"), semibold: !n.Read));
            var body = Ui.Text(n.Body, 12, Theme.Brush("TextDim"));
            body.TextWrapping = TextWrapping.Wrap;
            body.MaxLines = 2;
            text.Children.Add(body);
            text.Children.Add(Ui.Text($"{n.Workspace.Title} · {n.Time:HH:mm}", 11, Theme.Brush("TextFaint")));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            row.PointerEntered += (_, _) => row.Background = Theme.Brush("SidebarHover");
            row.PointerExited += (_, _) => row.Background = null;
            row.Tapped += (_, _) => { bellFlyout.Hide(); JumpTo(n); };
            panel.Children.Add(row);
        }
        return new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    void JumpTo(Notification n)
    {
        SelectWorkspace(n.Workspace);
        if (n.Workspace.Zoomed is not null && !ReferenceEquals(n.Workspace.Zoomed, n.Pane))
        {
            n.Workspace.Zoomed = null;
            RenderContent();
        }
        SelectSurface(n.Pane, n.Surface);
    }

    // ---- startup ---------------------------------------------------------------------------

    void OnFirstLoad()
    {
        bool recorded = false;
        void OnRender(object? s, object e)
        {
            if (recorded) return;
            recorded = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRender;
            var ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            if (Environment.GetEnvironmentVariable("ARMUX_TIMING_FILE") is { } path) File.WriteAllText(path, $"first_frame_ms={ms:F1}\n");
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRender;

        FocusKeys();
        if (opts.Select > 0 && opts.Select <= state.Workspaces.Count) SelectWorkspace(state.Workspaces[opts.Select - 1]);
        if (opts.Zoom) ToggleZoom();
        if (opts.Explorer) ToggleExplorer();
        if (opts.PromptEditor) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TogglePromptEditor);
        if (opts.Palette) OpenPalette();
        if (opts.Rename && state.Workspaces.Count > 0)
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(3);
            t.IsRepeating = false;
            t.Tick += (_, _) => _ = RenameAsync(state.Workspaces[0]);
            t.Start();
        }
        if (opts.Settings) ShowSettingsPage(opts.SettingsSection ?? "General");
        if (opts.Notifications) bellFlyout.ShowAt(bellButton);
        if (opts.Menu && sidebarList.Children.Count > 1 && sidebarList.Children[1] is SidebarRow row)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowWorkspaceMenu(state.Workspaces[1], row));
        if (opts.Colors && sidebarList.Children.Count > 1 && sidebarList.Children[1] is SidebarRow crow)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowColorFlyout(state.Workspaces[1], crow));
        if (opts.CloseAfterMs >= 0)
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(opts.CloseAfterMs);
            t.IsRepeating = false;
            t.Tick += (_, _) =>
            {
                foreach (var ws in state.Workspaces)
                    foreach (var pane in ws.Panes.ToList())
                        foreach (var s in pane.Surfaces.Where(s => s.Kind == PanelKind.Browser).ToList())
                            CloseSurface(pane, s);
                CloseExplorer();
            };
            t.Start();
        }
    }
}
