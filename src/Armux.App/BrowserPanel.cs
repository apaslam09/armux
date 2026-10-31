using System.Runtime.InteropServices;
using System.Text.Json;
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
        Ui.StyleTextBox(url.Resources);
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
        consoleOn = false;
        console.Clear();
        ShowPlaceholder(reason);
    }

    // ---- agent control (armux mcp, MainWindow.Browser.cs) ---------------------------------------

    readonly List<string> console = [];
    bool consoleOn;

    /// <summary>The page, once its WebView2 is up (a tab that was just opened takes a moment); resumed if it was suspended.</summary>
    internal async Task<CoreWebView2> ReadyAsync()
    {
        for (int i = 0; i < 150 && Web?.CoreWebView2 is null && (IsLoaded || Web is not null); i++) await Task.Delay(100);
        if (Web?.CoreWebView2 is not { } core)
            throw new InvalidOperationException($"browser tab {Surface.Id} is not loaded: show it in armux first");
        if (core.IsSuspended) core.Resume();
        return core;
    }

    /// <summary>After an agent command on a hidden tab: suspend it again.</summary>
    internal void Settle() { if (!IsLoaded) host.Hidden(this); }

    /// <summary>A Chrome DevTools Protocol call on this page. Input goes to the page only, never to Windows.</summary>
    internal async Task<JsonDocument> CdpAsync(string method, string parameters = "{}")
    {
        var core = await ReadyAsync();
        return JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync(method, parameters));
    }

    /// <summary>Evaluates JavaScript (awaiting a promise) and returns its JSON value; a page exception becomes an error.</summary>
    internal async Task<JsonElement?> EvalAsync(string expression)
    {
        using var doc = await CdpAsync("Runtime.evaluate", $$"""{"expression":{{Json(expression)}},"returnByValue":true,"awaitPromise":true}""");
        var root = doc.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var ex))
        {
            string text = ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ex.GetProperty("text").GetString() ?? "";
            throw new InvalidOperationException("page error: " + text);
        }
        return root.GetProperty("result").TryGetProperty("value", out var v) ? v.Clone() : null;
    }

    /// <summary>Navigates (address or search) and waits up to 20 s for the page to finish loading.</summary>
    internal async Task<string> NavigateAsync(string input)
    {
        var target = AddressOrSearch(input) ?? throw new InvalidOperationException("give an address");
        var core = await ReadyAsync();
        url.Text = target;
        Surface.Url = target;
        return await WaitForLoad(core, () => core.Navigate(target), TimeSpan.FromSeconds(20));
    }

    /// <summary>Back, forward or reload; same-page history steps finish without a load, so the wait is short.</summary>
    internal async Task<string> HistoryAsync(string what)
    {
        var core = await ReadyAsync();
        Action go = what switch
        {
            "back" when core.CanGoBack => core.GoBack,
            "forward" when core.CanGoForward => core.GoForward,
            "reload" => core.Reload,
            _ => throw new InvalidOperationException($"can't go {what}"),
        };
        return await WaitForLoad(core, go, TimeSpan.FromSeconds(10));
    }

    static async Task<string> WaitForLoad(CoreWebView2 core, Action start, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<bool>();
        void Completed(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            start();
            var first = await Task.WhenAny(done.Task, Task.Delay(timeout));
            if (first != done.Task) return $"still loading after {timeout.TotalSeconds:0} s: {core.Source}";
            return (done.Task.Result ? "loaded " : "failed to load ") + core.Source;
        }
        finally { core.NavigationCompleted -= Completed; }
    }

    /// <summary>Console messages and page errors since capture started (the first call starts it; the last 200 are kept).</summary>
    internal async Task<(bool Started, List<string> Lines)> ConsoleAsync()
    {
        var core = await ReadyAsync();
        if (consoleOn) return (false, [.. console]);
        consoleOn = true;
        core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var p = doc.RootElement;
            var parts = p.GetProperty("args").EnumerateArray().Select(a =>
                a.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
                : a.TryGetProperty("description", out var d) ? d.GetString() : a.GetProperty("type").GetString());
            AddConsole($"[{p.GetProperty("type").GetString()}] {string.Join(' ', parts)}");
        };
        core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var x = doc.RootElement.GetProperty("exceptionDetails");
            AddConsole("[exception] " + (x.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d) ? d.GetString() : x.GetProperty("text").GetString()));
        };
        (await CdpAsync("Runtime.enable")).Dispose();
        return (true, [.. console]);
    }

    void AddConsole(string line)
    {
        console.Add(line.Length > 2000 ? line[..2000] + "…" : line);
        if (console.Count > 200) console.RemoveAt(0);
    }

    /// <summary>A JSON (and JavaScript) string literal.</summary>
    internal static string Json(string s) => "\"" + JsonEncodedText.Encode(s) + "\"";

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

    /// <summary>The browser panel shown most recently that still has a page.</summary>
    public BrowserPanel? MostRecent => live.First?.Value;

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
