using System.Runtime.InteropServices;
using Armux.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Armux.App;

/// <summary>
/// Browser panel: address bar plus a WebView2 that is created only when the panel is first shown.
/// <see cref="BrowserHost"/> owns the lifecycle rules from ARCHITECTURE.md (lazy, suspend, cap, teardown).
/// </summary>
public sealed partial class BrowserPanel : Grid
{
    public Surface Surface { get; }
    public WebView2? Web { get; private set; }
    readonly Grid webHost = new();
    readonly TextBlock placeholder;
    readonly BrowserHost host;
    readonly TextBox url;

    public BrowserPanel(Surface surface, BrowserHost host)
    {
        Surface = surface;
        this.host = host;
        Background = Theme.Brush("Pane");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var bar = new Grid { Padding = new Thickness(6, 4, 8, 4), ColumnSpacing = 2, Background = Theme.Brush("Pane") };
        for (int i = 0; i < 3; i++) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        var dim = Theme.Brush("TextDim");
        var back = Ui.IconButton(Ui.Glyph(Ui.Back, 12, dim), "Back", () => { if (Web?.CanGoBack == true) Web.GoBack(); });
        var fwd = Ui.IconButton(Ui.Glyph(Ui.Forward, 12, dim), "Forward", () => { if (Web?.CanGoForward == true) Web.GoForward(); });
        var reload = Ui.IconButton(Ui.Glyph(Ui.Refresh, 12, dim), "Reload", () => Web?.Reload());
        url = new TextBox
        {
            Text = surface.Url,
            PlaceholderText = "Search or enter address",
            FontFamily = new FontFamily(Theme.UiFont),
            FontSize = 13,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // Compact, text centered: the template's 32 px minimum top-aligns the text, so set the height outright.
            MinHeight = 0,
            Height = 28,
            Padding = new Thickness(10, 4, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            IsSpellCheckEnabled = false,
            Background = Theme.Brush("Input"),
            Foreground = Theme.Brush("Text"),
            BorderBrush = Theme.Brush("Border"),
            // A set PlaceholderForeground keeps its gray when focused (the template's focus binding ignores resources).
            PlaceholderForeground = Theme.Brush("TextFaint"),
        };
        // The default text box turns white with dark text when focused; keep the bar's colors in every state.
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            url.Resources[key] = Theme.Brush("Input");
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            url.Resources[key] = Theme.Brush("Text");
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver" })
            url.Resources[key] = Theme.Brush("Border");
        url.Resources["TextControlBorderBrushFocused"] = Theme.Brush("Accent");
        url.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Go(url.Text);
        };
        url.GotFocus += (_, _) => url.SelectAll();
        SetColumn(fwd, 1); SetColumn(reload, 2); SetColumn(url, 3);
        bar.Children.Add(back); bar.Children.Add(fwd); bar.Children.Add(reload); bar.Children.Add(url);
        Children.Add(bar);

        placeholder = Ui.Text("", 12.5, Theme.Brush("TextFaint"));
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        webHost.Children.Add(placeholder);
        SetRow(webHost, 1);
        Children.Add(webHost);

        Loaded += (_, _) => host.Shown(this);
        Unloaded += (_, _) => host.Hidden(this);
    }

    public void ShowPlaceholder(string text) { placeholder.Text = text; placeholder.Visibility = Visibility.Visible; }

    internal async Task CreateWebAsync()
    {
        if (Web is not null) return;
        ShowPlaceholder("Loading…");
        Web = new WebView2 { DefaultBackgroundColor = Theme.Color("Pane") };
        webHost.Children.Add(Web);
        await Web.EnsureCoreWebView2Async();
        placeholder.Visibility = Visibility.Collapsed;
        var core = Web.CoreWebView2;
        core.SourceChanged += (_, _) =>
        {
            if (Surface.MockPage || core.Source.StartsWith("data:", StringComparison.Ordinal)) return;
            Surface.Url = core.Source;
            if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), url)) url.Text = core.Source;
        };
        core.DocumentTitleChanged += (_, _) => { if (!Surface.MockPage && core.DocumentTitle.Length > 0) Surface.Title = core.DocumentTitle; };
        // Links that open a new window load in this panel instead of a separate browser window.
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
        if (Surface.MockPage) Web.NavigateToString(MockPage(Surface.Url));
        else if (Surface.Url.Length > 0) core.Navigate(Surface.Url);
        else url.Focus(FocusState.Programmatic);
    }

    /// <summary>Navigates to an address, or searches with the engine from Settings when the text does not look like one.</summary>
    void Go(string input)
    {
        var target = AddressOrSearch(input);
        if (target is null) return;
        url.Text = target;
        Surface.Url = target;
        if (Web?.CoreWebView2 is { } core) core.Navigate(target);
        Web?.Focus(FocusState.Programmatic);
    }

    public static string? AddressOrSearch(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;
        if (Uri.TryCreate(input, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "file") return abs.ToString();
        bool local = input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || input.StartsWith("127.0.0.1", StringComparison.Ordinal);
        if (local) return "http://" + input;
        if (!input.Contains(' ') && input.Contains('.') && Uri.TryCreate("https://" + input, UriKind.Absolute, out var guess)) return guess.ToString();
        string q = Uri.EscapeDataString(input);
        return Theme.Settings.SearchEngine switch
        {
            "bing" => "https://www.bing.com/search?q=" + q,
            "duckduckgo" => "https://duckduckgo.com/?q=" + q,
            _ => "https://www.google.com/search?q=" + q,
        };
    }

    /// <summary>Ctrl+L (Edge default): focus the address bar.</summary>
    public void FocusAddress() => url.Focus(FocusState.Keyboard);

    internal void DestroyWeb(string reason)
    {
        if (Web is null) return;
        webHost.Children.Remove(Web);
        Web.Close();
        Web = null;
        ShowPlaceholder(reason);
    }

    static string MockPage(string url) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        body{margin:0;font:15px "Segoe UI Variable Text",system-ui;background:#0b0b0c;color:#e8e8e8}
        main{max-width:640px;margin:72px auto;padding:0 32px}
        h1{font-size:30px;margin:0 0 8px}p{color:#a3a3a3;line-height:1.6}
        code{background:#1f1f1f;padding:2px 6px;border-radius:4px;color:#60a5fa}
        .row{display:flex;gap:12px;margin-top:28px}.btn{padding:10px 18px;border-radius:999px;background:#e8e8e8;color:#111;font-weight:600}
        .ghost{background:transparent;border:1px solid #3a3a3a;color:#e8e8e8}
        </style></head><body><main>
        <h1>armux web</h1>
        <p>Mock page for <code>{{System.Net.WebUtility.HtmlEncode(url)}}</code>, used by the screenshot and
        measurement scenarios. Browser tabs you open load real pages.</p>
        <div class="row"><span class="btn">Download for Windows on ARM</span><span class="btn ghost">View on GitHub</span></div>
        </main></body></html>
        """;
}

/// <summary>Enforces the browser policy: lazy creation, suspend when hidden, at most <see cref="Cap"/> live, trim on teardown.</summary>
public sealed class BrowserHost
{
    public const int Cap = 2;
    readonly LinkedList<BrowserPanel> live = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueue queue;

    public BrowserHost(Microsoft.UI.Dispatching.DispatcherQueue queue) => this.queue = queue;

    public int LiveCount => live.Count;

    public async void Shown(BrowserPanel p)
    {
        live.Remove(p);
        live.AddFirst(p);
        // Over the cap: unload the least recently shown panel. It reloads when shown again.
        while (live.Count > Cap)
        {
            var victim = live.Last!.Value;
            live.RemoveLast();
            victim.DestroyWeb("Unloaded to save memory. Click the tab to reload.");
        }
        try
        {
            if (p.Web is null) await p.CreateWebAsync();
            else if (p.Web.CoreWebView2 is { } core)
            {
                if (core.IsSuspended) core.Resume();
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        }
        catch (Exception ex)
        {
            p.ShowPlaceholder("Browser failed to start: " + ex.Message);
        }
    }

    public void Hidden(BrowserPanel p)
    {
        // Re-layout detaches and re-attaches panels, so wait before deciding the panel is really hidden.
        var t = queue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.IsRepeating = false;
        t.Tick += async (_, _) =>
        {
            if (p.IsLoaded || p.Web?.CoreWebView2 is not { } core) return;
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await core.TrySuspendAsync();
            }
            catch (Exception)
            {
                // Suspend is best effort; it fails if the controller is still visible.
            }
        };
        t.Start();
    }

    public void Close(BrowserPanel p)
    {
        live.Remove(p);
        p.DestroyWeb("");
        if (live.Count == 0)
        {
            // All WebView2 processes exit once the last controller closes; give back pages they left mapped.
            var t = queue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(5);
            t.IsRepeating = false;
            t.Tick += (_, _) =>
            {
                if (live.Count > 0) return;
                var before = GC.GetGCMemoryInfo();
                // WebView2 setup leaves a large managed heap behind; Aggressive returns the committed memory to the OS.
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                var after = GC.GetGCMemoryInfo();
                if (Environment.GetEnvironmentVariable("ARMUX_DIAG") is not null)
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "armux-diag.txt"),
                        $"gc heap {before.HeapSizeBytes / 1048576.0:F1} -> {after.HeapSizeBytes / 1048576.0:F1} MB, committed {before.TotalCommittedBytes / 1048576.0:F1} -> {after.TotalCommittedBytes / 1048576.0:F1} MB\n");
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            };
            t.Start();
        }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
