using Armux.Core.Vt;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using TextDecorations = Windows.UI.Text.TextDecorations;

namespace Armux.App;

/// <summary>
/// Draws a <see cref="TerminalSession"/> and sends keyboard input to it. Each screen row is one TextBlock of
/// colored runs, with rectangles behind it for background colors and the selection. Only rows whose content
/// changed are rebuilt, and nothing runs while the terminal is idle.
/// </summary>
public sealed partial class TerminalView : UserControl
{
    const double PadX = 10, PadY = 6;
    /// <summary>Width kept for the scrollbar even while it is hidden, so the column count never depends on it.</summary>
    const double BarWidth = 14;

    readonly TerminalSession session;
    readonly Grid content = new() { Margin = new Thickness(PadX, PadY, PadX, PadY) };
    readonly Canvas back = new() { IsHitTestVisible = false };
    readonly StackPanel rowsPanel = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly Canvas front = new() { IsHitTestVisible = false };
    readonly Rectangle cursor = new() { IsHitTestVisible = false };
    readonly ScrollBar bar = new() { Orientation = Orientation.Vertical, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, Visibility = Visibility.Collapsed };
    readonly DebounceTimer resize;
    readonly Blinker blink;
    readonly List<Rectangle> selectionRects = [];
    TextBlock[] rowBlocks = [];
    List<Rectangle>[] rowRects = [];
    int[] rowHash = [];
    Cell[] line = [];
    double cellW, cellH;
    int viewTop, lastViewTop = -1;
    long lastVersion = -1;
    bool follow = true, hasFocus, selecting;
    (int Line, int Col)? selA, selB;
    char highSurrogate;
    bool keyDownSeen;
    int wheelAccum, reportButton = -1;
    (int Col, int Row) lastReported = (-1, -1);

    public TerminalSession Session => session;
    /// <summary>Ctrl+wheel: +1 or -1 zoom step for the window.</summary>
    public Action<int>? ZoomRequested { get; set; }
    /// <summary>Text for a file dropped on the terminal (the window turns paths into relative, quoted text).</summary>
    public Func<IReadOnlyList<string>, string>? DropText { get; set; }

    public TerminalView(TerminalSession session)
    {
        this.session = session;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = Theme.Brush("Pane");

        // UserControl does not draw its Background, and the rows are not hit-testable, so this brush is what makes
        // the terminal clickable (focus, selection, wheel). Without it clicks fell through to the pane.
        var grid = new Grid { Background = Theme.Brush("Pane") };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarWidth) });
        content.Children.Add(back);
        content.Children.Add(rowsPanel);
        content.Children.Add(front);
        front.Children.Add(cursor);
        grid.Children.Add(content);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        Content = grid;

        MeasureCell();
        resize = new DebounceTimer(DispatcherQueue, TimeSpan.FromMilliseconds(60), ApplySize);
        blink = new Blinker(this);
        bar.Scroll += (_, e) => { viewTop = (int)Math.Round(e.NewValue); follow = viewTop >= ScrollbackCount(); Render(force: false); };

        SizeChanged += (_, _) => { if (!session.Started) ApplySize(); else resize.Restart(); };
        Loaded += (_, _) => { session.Changed -= OnChanged; session.Changed += OnChanged; Render(force: true); };
        Unloaded += (_, _) => { session.Changed -= OnChanged; blink.Stop(); };
        GotFocus += (_, _) => { hasFocus = true; if (session.Terminal.FocusReporting) session.Send("\u001b[I"); DrawCursor(); };
        LostFocus += (_, _) => { hasFocus = false; keyDownSeen = false; if (session.Terminal.FocusReporting) session.Send("\u001b[O"); DrawCursor(); };
        CharacterReceived += OnCharacter;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnWheel;
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    void OnChanged() => Render(force: false);

    // ---- layout ------------------------------------------------------------------------------

    /// <summary>Cell size of the terminal font: the advance of "M" and the line height from Settings.</summary>
    void MeasureCell()
    {
        var probe = new TextBlock { Text = new string('M', 64), FontFamily = Theme.TerminalFont, FontSize = Theme.TerminalFontSize, IsTextScaleFactorEnabled = false };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        cellW = Math.Max(1, probe.DesiredSize.Width / 64);
        cellH = Math.Round(Theme.TerminalFontSize * Theme.Settings.LineHeight);
    }

    (int Cols, int Rows) GridSize()
    {
        double w = ActualWidth - PadX * 2 - BarWidth;
        double h = ActualHeight - PadY * 2;
        return (Math.Max(2, (int)(w / cellW)), Math.Max(1, (int)(h / cellH)));
    }

    void ApplySize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var (cols, rows) = GridSize();
        if (!session.Started) session.Start(cols, rows);
        else if (cols != session.Terminal.Cols || rows != session.Terminal.Rows) session.Resize(cols, rows);
        Render(force: true);
    }

    /// <summary>
    /// After the window settles at a new size: what a workspace switch did when a grown window left the terminal
    /// half blank. Re-measures and redraws every row, and toggles focus for programs that report it (Claude Code
    /// redraws on focus) in case they missed the resize.
    /// </summary>
    public void Refit()
    {
        if (!session.Started || !IsLoaded) return;
        ApplySize();
        if (session.Terminal.FocusReporting) session.Send(hasFocus ? "\u001b[O\u001b[I" : "\u001b[I\u001b[O");
    }

    int ScrollbackCount() { lock (session.Terminal.Sync) return session.Terminal.ScrollbackCount; }

    // ---- rendering ---------------------------------------------------------------------------

    void Render(bool force)
    {
        var term = session.Terminal;
        lock (term.Sync)
        {
            int rows = term.Rows, cols = term.Cols, sb = term.ScrollbackCount;
            if (rowBlocks.Length != rows) { BuildRows(rows); force = true; }
            if (line.Length != cols) { line = new Cell[cols]; force = true; }
            if (follow) viewTop = sb;
            viewTop = Math.Clamp(viewTop, 0, sb);
            if (!force && term.Version == lastVersion && viewTop == lastViewTop) return;
            bool moved = viewTop != lastViewTop;
            lastVersion = term.Version;
            lastViewTop = viewTop;

            for (int r = 0; r < rows; r++)
            {
                term.ReadLine(viewTop + r, line);
                int hash = Hash(line);
                if (!force && !moved && hash == rowHash[r]) continue;
                rowHash[r] = hash;
                DrawRow(r);
            }

            bar.Maximum = sb;
            bar.ViewportSize = rows;
            bar.LargeChange = rows;
            bar.SmallChange = 1;
            bar.Value = viewTop;
            bar.Visibility = sb > 0 ? Visibility.Visible : Visibility.Collapsed;
            DrawSelection(term);
            DrawCursor();
        }
    }

    void BuildRows(int rows)
    {
        rowsPanel.Children.Clear();
        back.Children.Clear();
        selectionRects.Clear();
        rowBlocks = new TextBlock[rows];
        rowRects = new List<Rectangle>[rows];
        rowHash = new int[rows];
        for (int r = 0; r < rows; r++)
        {
            rowBlocks[r] = new TextBlock
            {
                FontFamily = Theme.TerminalFont,
                FontSize = Theme.TerminalFontSize,
                LineHeight = cellH,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Height = cellH,
                TextWrapping = TextWrapping.NoWrap,
                IsTextScaleFactorEnabled = false,
                Foreground = TerminalColors.DefaultForeground,
            };
            if (!Theme.Settings.Ligatures)
            {
                Microsoft.UI.Xaml.Documents.Typography.SetStandardLigatures(rowBlocks[r], false);
                Microsoft.UI.Xaml.Documents.Typography.SetContextualAlternates(rowBlocks[r], false);
            }
            rowsPanel.Children.Add(rowBlocks[r]);
            rowRects[r] = [];
        }
    }

    /// <summary>Text, background and attributes of a row; equal hashes skip redrawing it.</summary>
    static int Hash(Cell[] cells)
    {
        var h = new HashCode();
        foreach (var c in cells) { h.Add(c.Rune); h.Add(c.Attr); }
        return h.ToHashCode();
    }

    /// <summary>Rebuilds row <paramref name="r"/> from <see cref="line"/>: runs of equal style, and background rectangles.</summary>
    void DrawRow(int r)
    {
        var tb = rowBlocks[r];
        tb.Inlines.Clear();
        foreach (var rect in rowRects[r]) back.Children.Remove(rect);
        rowRects[r].Clear();

        int last = line.Length - 1;
        while (last >= 0 && line[last] == Cell.Blank) last--;
        var text = new System.Text.StringBuilder();
        int x = 0;
        while (x <= last)
        {
            var a = line[x].Attr;
            int start = x;
            text.Clear();
            while (x <= last && line[x].Attr == a)
            {
                int rune = line[x].Rune;
                if (rune != Cell.WideTail) text.Append(rune == 0 ? " " : char.ConvertFromUtf32(rune));
                x++;
            }
            var (fg, bg) = TerminalColors.Resolve(a);
            if (bg is not null)
            {
                var rect = new Rectangle { Width = (x - start) * cellW, Height = cellH, Fill = bg };
                Canvas.SetLeft(rect, start * cellW);
                Canvas.SetTop(rect, r * cellH);
                back.Children.Insert(0, rect);
                rowRects[r].Add(rect);
            }
            var run = new Run { Text = text.ToString(), Foreground = fg };
            if (a.Flags.HasFlag(CellFlags.Bold)) run.FontWeight = FontWeights.Bold;
            if (a.Flags.HasFlag(CellFlags.Italic)) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            var deco = (a.Flags.HasFlag(CellFlags.Underline) ? TextDecorations.Underline : TextDecorations.None)
                | (a.Flags.HasFlag(CellFlags.Strike) ? TextDecorations.Strikethrough : TextDecorations.None);
            if (deco != TextDecorations.None) run.TextDecorations = deco;
            tb.Inlines.Add(run);
        }
    }

    void DrawCursor()
    {
        var term = session.Terminal;
        int y = term.ScrollbackCount + term.CursorY - viewTop;
        bool visible = term.CursorVisible && session.Live && session.Started && !session.Exited && y >= 0 && y < term.Rows;
        cursor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        // Shape from Settings. Unfocused, a block becomes an outline, as in most terminals.
        string style = Theme.Settings.CursorStyle;
        cursor.Width = style == "bar" ? 2 : cellW;
        cursor.Height = style == "underline" ? 2 : cellH;
        Canvas.SetLeft(cursor, term.CursorX * cellW);
        Canvas.SetTop(cursor, y * cellH + (style == "underline" ? cellH - 2 : 0));
        bool outline = !hasFocus && style == "block";
        cursor.Fill = outline ? null : hasFocus ? TerminalColors.Cursor : TerminalColors.CursorOutline;
        cursor.Stroke = outline ? TerminalColors.CursorOutline : null;
        cursor.StrokeThickness = 1;
        // A blinking cursor restarts visible after each change; it only blinks while the terminal has focus.
        cursor.Opacity = 1;
        if (Theme.Settings.CursorBlink && hasFocus) blink.Restart(); else blink.Stop();
    }

    /// <summary>Blinks the cursor (Settings > Terminal), stopping after a minute idle so an idle window does no work.</summary>
    sealed class Blinker(TerminalView view)
    {
        readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer = Create(view);
        int ticks;

        static Microsoft.UI.Dispatching.DispatcherQueueTimer Create(TerminalView view)
        {
            var t = view.DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(530);
            return t;
        }

        public void Restart()
        {
            ticks = 0;
            timer.Tick -= OnTick;
            timer.Tick += OnTick;
            timer.Start();
        }

        public void Stop() => timer.Stop();

        void OnTick(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object o)
        {
            if (++ticks > 113) { t.Stop(); view.cursor.Opacity = 1; return; }   // about a minute
            view.cursor.Opacity = view.cursor.Opacity > 0 ? 0 : 1;
        }
    }

    // ---- selection and clipboard -------------------------------------------------------------

    ((int Line, int Col) From, (int Line, int Col) To)? Ordered()
    {
        if (selA is not { } a || selB is not { } b || a == b) return null;
        return a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col) ? (a, b) : (b, a);
    }

    void DrawSelection(Terminal term)
    {
        foreach (var r in selectionRects) back.Children.Remove(r);
        selectionRects.Clear();
        if (Ordered() is not { } s) return;
        for (int row = 0; row < term.Rows; row++)
        {
            int l = viewTop + row;
            if (l < s.From.Line || l > s.To.Line) continue;
            int c1 = l == s.From.Line ? s.From.Col : 0, c2 = l == s.To.Line ? s.To.Col : term.Cols;
            if (c2 <= c1) continue;
            var rect = new Rectangle { Width = (c2 - c1) * cellW, Height = cellH, Fill = TerminalColors.Selection };
            Canvas.SetLeft(rect, c1 * cellW);
            Canvas.SetTop(rect, row * cellH);
            back.Children.Add(rect);
            selectionRects.Add(rect);
        }
    }

    (int Line, int Col) CellAt(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(content).Position;
        int col = Math.Clamp((int)Math.Round(p.X / cellW), 0, session.Terminal.Cols);
        int row = Math.Clamp((int)(p.Y / cellH), 0, session.Terminal.Rows - 1);
        return (viewTop + row, col);
    }

    /// <summary>
    /// The program gets the mouse when it turned tracking on (Claude Code, vim, htop); Shift+click still selects,
    /// as in other terminals.
    /// </summary>
    bool Reporting(PointerRoutedEventArgs e) => session.Terminal.Mouse != MouseTracking.None && !Down(VirtualKey.Shift);

    (int Col, int Row) ScreenCell(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(content).Position;
        return (Math.Clamp((int)(p.X / cellW), 0, session.Terminal.Cols - 1), Math.Clamp((int)(p.Y / cellH), 0, session.Terminal.Rows - 1));
    }

    bool Report(MouseAction action, int button, PointerRoutedEventArgs e)
    {
        var (col, row) = ScreenCell(e);
        if (action == MouseAction.Move && (col, row) == lastReported) return true;
        lastReported = (col, row);
        var t = session.Terminal;
        if (MouseEncoder.Encode(t.Mouse, t.SgrMouse, action, button, col, row, Down(VirtualKey.Shift), Down(VirtualKey.Menu), Down(VirtualKey.Control)) is { } seq)
            session.Send(seq);
        return true;
    }

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var props = e.GetCurrentPoint(this).Properties;
        int button = props.IsLeftButtonPressed ? 0 : props.IsMiddleButtonPressed ? 1 : props.IsRightButtonPressed ? 2 : -1;
        if (button >= 0 && Reporting(e))
        {
            reportButton = button;
            CapturePointer(e.Pointer);
            Report(MouseAction.Press, button, e);
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed) { ShowMenu(e); e.Handled = true; return; }
        if (!props.IsLeftButtonPressed) return;
        selA = selB = CellAt(e);
        selecting = true;
        CapturePointer(e.Pointer);
        lock (session.Terminal.Sync) DrawSelection(session.Terminal);
        // Handled, so no ancestor acts on the press; the pane still sees it (it listens with handledEventsToo).
        e.Handled = true;
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (reportButton >= 0 || (Reporting(e) && session.Terminal.Mouse == MouseTracking.Any))
        {
            Report(MouseAction.Move, reportButton, e);
            return;
        }
        if (!selecting) return;
        selB = CellAt(e);
        lock (session.Terminal.Sync) DrawSelection(session.Terminal);
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        if (reportButton >= 0)
        {
            Report(MouseAction.Release, reportButton, e);
            reportButton = -1;
        }
        else if (selecting)
        {
            selecting = false;
            // Warp and Windows Terminal: a mouse selection is copied as soon as it is made.
            if (Theme.Settings.CopyOnSelect) Copy(keepSelection: true);
        }
        // Something after the press moved focus away (reported: typing died after every click, and worked only
        // while the button was held). Take it back once the click has finished.
        DispatcherQueue.TryEnqueue(() => { if (!hasFocus) Focus(FocusState.Pointer); });
    }

    // ---- drag and drop -----------------------------------------------------------------------

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!session.Started || session.Exited) return;
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Insert path";
        }
    }

    /// <summary>Files from the armux explorer or Windows Explorer are typed as paths; other text is pasted.</summary>
    async void OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            List<string> paths = [];
            if (e.DataView.Properties.TryGetValue(ExplorerPanel.DragPathKey, out var p) && p is string armuxPath) paths.Add(armuxPath);
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
                foreach (var item in await e.DataView.GetStorageItemsAsync()) paths.Add(item.Path);
            if (paths.Count > 0 && DropText is { } format) session.Paste(format(paths));
            else if (e.DataView.Contains(StandardDataFormats.Text)) session.Paste(await e.DataView.GetTextAsync());
            Focus(FocusState.Pointer);
        }
        catch (Exception ex) { App.Log(nameof(OnDrop), ex); }
        finally { deferral.Complete(); }
    }

    void ShowMenu(PointerRoutedEventArgs e)
    {
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = Ui.SolidPresenter(typeof(MenuFlyoutPresenter)) };
        var copy = new MenuFlyoutItem { Text = "Copy", IsEnabled = Ordered() is not null };
        copy.Click += (_, _) => Copy();
        var paste = new MenuFlyoutItem { Text = "Paste" };
        paste.Click += (_, _) => _ = PasteAsync();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.ShowAt(this, e.GetCurrentPoint(this).Position);
    }

    /// <summary>Copies the selection (Ctrl+Shift+C) and clears it, unless it is the automatic copy of a fresh selection. Returns false when nothing is selected.</summary>
    public bool Copy(bool keepSelection = false)
    {
        if (Ordered() is not { } s) return false;
        string text;
        lock (session.Terminal.Sync) text = session.Terminal.GetText(s.From.Line, s.From.Col, s.To.Line, s.To.Col - 1);
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        if (keepSelection) return true;
        selA = selB = null;
        lock (session.Terminal.Sync) DrawSelection(session.Terminal);
        return true;
    }

    /// <summary>Pastes clipboard text (Ctrl+Shift+V, Shift+Insert), bracketed when the program asked for it.</summary>
    public async Task PasteAsync()
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text)) return;
        var text = await view.GetTextAsync();
        if (!follow) { follow = true; Render(force: false); }
        session.Paste(text);
    }

    /// <summary>
    /// Ctrl+Shift+Up / Down / PageUp / PageDown / Home / End (Windows Terminal's defaults): moves the view through
    /// the scrollback by <paramref name="lines"/> (negative is up); int.MinValue / MaxValue go to the top / bottom.
    /// </summary>
    public void ScrollBy(int lines)
    {
        int sb = ScrollbackCount();
        if (lines == int.MinValue) viewTop = 0;
        else if (lines == int.MaxValue) viewTop = sb;
        else viewTop = Math.Clamp(viewTop + lines, 0, sb);
        follow = viewTop >= sb;
        Render(force: false);
    }

    /// <summary>Rows on screen, for page scrolling.</summary>
    public int PageRows { get { lock (session.Terminal.Sync) return Math.Max(1, session.Terminal.Rows - 1); } }

    // ---- input -------------------------------------------------------------------------------

    void SendInput(string text)
    {
        if (!follow) { follow = true; Render(force: false); }
        if (selA is not null && !selecting) { selA = selB = null; lock (session.Terminal.Sync) DrawSelection(session.Terminal); }
        session.Input(text);
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        // Precision touchpads send deltas below one notch (120); add them up so slow scrolls still move.
        wheelAccum += e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        int notches = wheelAccum / 120;
        if (Down(VirtualKey.Control))
        {
            if (notches != 0) { wheelAccum -= notches * 120; ZoomRequested?.Invoke(Math.Sign(notches)); }
            return;
        }
        var term = session.Terminal;
        if (Reporting(e))
        {
            if (notches == 0) return;
            wheelAccum -= notches * 120;
            for (int i = 0; i < Math.Abs(notches); i++) Report(notches > 0 ? MouseAction.WheelUp : MouseAction.WheelDown, -1, e);
            return;
        }
        // Scrollback moves a line per 40 units: 3 lines per notch, and smooth on touchpads.
        int lines = wheelAccum / 40;
        if (lines == 0) return;
        wheelAccum -= lines * 40;
        notches = lines;
        if (term.AltScreen)
        {
            // Full-screen programs without mouse support scroll with the arrow keys.
            string key = notches > 0 ? (term.AppCursorKeys ? "\u001bOA" : "\u001b[A") : (term.AppCursorKeys ? "\u001bOB" : "\u001b[B");
            session.Send(string.Concat(Enumerable.Repeat(key, Math.Abs(notches))));
            return;
        }
        int sb = ScrollbackCount();
        viewTop = Math.Clamp(viewTop - notches, 0, sb);
        follow = viewTop >= sb;
        Render(force: false);
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        bool shift = Down(VirtualKey.Shift), ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu);
        if (e.Key == VirtualKey.Insert && shift && !ctrl) { _ = PasteAsync(); e.Handled = true; return; }
        TermKey? key = e.Key switch
        {
            VirtualKey.Up => TermKey.Up, VirtualKey.Down => TermKey.Down, VirtualKey.Right => TermKey.Right, VirtualKey.Left => TermKey.Left,
            VirtualKey.Home => TermKey.Home, VirtualKey.End => TermKey.End, VirtualKey.Insert => TermKey.Insert, VirtualKey.Delete => TermKey.Delete,
            VirtualKey.PageUp => TermKey.PageUp, VirtualKey.PageDown => TermKey.PageDown,
            >= VirtualKey.F1 and <= VirtualKey.F12 => TermKey.F1 + (e.Key - VirtualKey.F1),
            VirtualKey.Tab => TermKey.Tab, VirtualKey.Back => TermKey.Backspace,
            _ => null,
        };
        if (key is null)
        {
            // This key's character (if any) may follow; modifiers alone produce none.
            keyDownSeen = e.Key is not (VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu);
            base.OnKeyDown(e);
            return;
        }
        e.Handled = true;
        SendInput(KeyEncoder.Encode(key.Value, shift, alt, ctrl, session.Terminal.AppCursorKeys));
    }

    void OnCharacter(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        char c = e.Character;
        e.Handled = true;
        // A control character whose key went down elsewhere (Enter in a closing dialog, Esc closing
        // the palette) arrives here after focus returns. Drop it so it doesn't reach the shell.
        // LostFocus clears the flag, so a key pressed before a dialog opened can't vouch for it.
        bool seen = keyDownSeen;
        keyDownSeen = false;
        if (!seen && c < ' ') return;
        // Backspace and Tab are sent from OnKeyDown.
        if (c is '\b' or '\t' or '\u007f') return;
        if (char.IsHighSurrogate(c)) { highSurrogate = c; return; }
        string text = char.IsLowSurrogate(c) && highSurrogate != 0 ? new string([highSurrogate, c]) : c.ToString();
        highSurrogate = '\0';
        SendInput(KeyEncoder.Text(text, alt: Down(VirtualKey.Menu), ctrl: Down(VirtualKey.Control)));
    }
}
/// <summary>A one-shot timer that restarts on each call; used to debounce resizes.</summary>
sealed class DebounceTimer
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;

    public DebounceTimer(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
    }

    public void Restart() { timer.Stop(); timer.Start(); }
}

/// <summary>Terminal colors: the 16-color palette per theme, the xterm 256-color cube and 24-bit colors, with brush caching.</summary>
public static class TerminalColors
{
    // ANSI 0-15. Dark follows armux's Phase 2 terminal tokens; light is darkened for a white background.
    static readonly uint[] Dark =
    [
        0x0C0C0C, 0xE05561, 0x4EC97B, 0xDCC77A, 0x6CA6E8, 0xC792EA, 0x56C8E8, 0xCCCCCC,
        0x8A8A8A, 0xF16A6A, 0x5FE08E, 0xF9F1A5, 0x8AB8F0, 0xD7A8F0, 0x7AD8F0, 0xF2F2F2,
    ];
    static readonly uint[] Light =
    [
        0x1F1F1F, 0xC91B1B, 0x16823C, 0x8F6A00, 0x1D4ED8, 0x9333EA, 0x0E7490, 0x6B6B6B,
        0x6E6E6E, 0xDC2626, 0x15A34A, 0xA16207, 0x2563EB, 0xA855F7, 0x0891B2, 0x3A3A3A,
    ];
    static readonly Dictionary<(uint, ThemeKind, byte), SolidColorBrush> cache = [];

    public static SolidColorBrush DefaultForeground => Theme.Brush("Term.Default");
    public static SolidColorBrush Selection => Get(Theme.Dark ? 0x3B82F6u : 0x93C5FDu, 0x66);
    public static SolidColorBrush Cursor => Get(Theme.Dark ? 0xE8E8E8u : 0x1F1F1Fu, 0x99);
    public static SolidColorBrush CursorOutline => Get(Theme.Dark ? 0xE8E8E8u : 0x1F1F1Fu, 0xFF);

    /// <summary>Foreground brush and background brush (null when the background is the pane's) for an attribute.</summary>
    public static (Brush Fg, Brush? Bg) Resolve(Attr a)
    {
        Brush? fg = a.Fg == VtColor.Default ? null : Get(Rgb(a.Fg), 0xFF);
        Brush? bg = a.Bg == VtColor.Default ? null : Get(Rgb(a.Bg), 0xFF);
        if (a.Flags.HasFlag(CellFlags.Inverse)) (fg, bg) = (bg ?? Theme.Brush("Pane"), fg ?? DefaultForeground);
        fg ??= DefaultForeground;
        if (a.Flags.HasFlag(CellFlags.Hidden)) fg = bg ?? Theme.Brush("Pane");
        else if (a.Flags.HasFlag(CellFlags.Dim) && fg is SolidColorBrush s)
            fg = Get((uint)(s.Color.R << 16 | s.Color.G << 8 | s.Color.B), 0x99);
        return (fg, bg);
    }

    static uint Rgb(uint c)
    {
        if (VtColor.IsRgb(c)) return VtColor.RgbValue(c);
        int i = VtColor.Index(c);
        return i < 16 ? (Theme.Dark ? Dark : Light)[i] : VtColor.Xterm256(i);
    }

    static SolidColorBrush Get(uint rgb, byte alpha)
    {
        var key = (rgb, Theme.Kind, alpha);
        if (!cache.TryGetValue(key, out var b))
        {
            if (cache.Count > 4096) cache.Clear();   // 24-bit color programs can produce many colors
            cache[key] = b = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        return b;
    }
}
