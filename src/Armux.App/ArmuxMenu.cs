using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Armux.App;

/// <summary>
/// armux's own popup (deviation D-43), in place of Flyout, for the same reason as <see cref="ArmuxDialog"/>: WinUI's
/// flyouts made in code get the old square look. A rounded card with a thin border, as in Zed, placed next to an element
/// and kept inside the window. A click outside or Esc closes it. It is built when opened and dropped when closed.
/// </summary>
public sealed class ArmuxPopup
{
    const double Gap = 4, Edge = 6;

    readonly FrameworkElement anchor;
    readonly Popup popup;
    readonly ContentControl holder;
    Point? at;
    bool beside, restored = true;
    UIElement? focusedBefore;

    /// <summary>A key pressed in the popup; Esc is already taken care of.</summary>
    public event KeyEventHandler? KeyDown;

    /// <summary>After it closes, however it closed. Whoever kept the popup lets go of it here.</summary>
    public event Action? Closed;

    public ArmuxPopup(FrameworkElement anchor, UIElement body, Thickness padding)
    {
        this.anchor = anchor;
        var card = new Border
        {
            Child = body,
            Background = Theme.Brush("Palette"),
            BorderBrush = Theme.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding,
        };
        // The element that holds the keyboard while nothing in the card has it, so Esc works and typing goes nowhere else.
        holder = new ContentControl
        {
            Content = card,
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            TabFocusNavigation = KeyboardNavigationMode.Cycle,
            RequestedTheme = Theme.Dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        // Popups sit outside the window's zoom (Ctrl+=), so the card scales itself to match.
        double k = Theme.Settings.Zoom;
        if (k != 1) holder.RenderTransform = new ScaleTransform { ScaleX = k, ScaleY = k };
        holder.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Escape) { e.Handled = true; Hide(); }
            else KeyDown?.Invoke(s, e);
        };
        holder.SizeChanged += (_, _) => Place();
        popup = new Popup { XamlRoot = anchor.XamlRoot, Child = holder, IsLightDismissEnabled = true };
        popup.Closed += (_, _) => Finish();
    }

    /// <summary>
    /// Opens the popup at <paramref name="at"/> (a point in the anchor), else below the anchor, or
    /// <paramref name="beside"/> it. It moves or flips to stay inside the window once its size is known.
    /// </summary>
    public void Show(Point? at = null, bool beside = false)
    {
        this.at = at;
        this.beside = beside;
        focusedBefore = FocusManager.GetFocusedElement(anchor.XamlRoot) as UIElement;
        restored = false;
        holder.Loaded += (_, _) => holder.Focus(FocusState.Programmatic);
        popup.IsOpen = true;
    }

    public void Hide()
    {
        popup.IsOpen = false;
        Finish();
    }

    void Finish()
    {
        if (restored) return;
        restored = true;
        focusedBefore?.Focus(FocusState.Programmatic);
        Closed?.Invoke();
        Ui.FreeSoon(holder.DispatcherQueue);
    }

    void Place()
    {
        if (!anchor.IsLoaded || anchor.XamlRoot is not { } root) return;
        double k = Theme.Settings.Zoom, w = holder.ActualWidth * k, h = holder.ActualHeight * k;
        var t = anchor.TransformToVisual(null);
        Point topLeft = t.TransformPoint(default), bottomRight = t.TransformPoint(new Point(anchor.ActualWidth, anchor.ActualHeight));
        double x = at is { } p ? t.TransformPoint(p).X : beside ? bottomRight.X + Gap : topLeft.X;
        double y = at is { } q ? t.TransformPoint(q).Y : beside ? topLeft.Y : bottomRight.Y + Gap;
        // No room below an element: above it, as a dropdown does.
        if (at is null && !beside && y + h > root.Size.Height - Edge && topLeft.Y - Gap - h >= Edge) y = topLeft.Y - Gap - h;
        popup.HorizontalOffset = Math.Max(Edge, Math.Min(x, root.Size.Width - Edge - w));
        popup.VerticalOffset = Math.Max(Edge, Math.Min(y, root.Size.Height - Edge - h));
    }
}

/// <summary>
/// A context menu or dropdown on an <see cref="ArmuxPopup"/>, in place of MenuFlyout: compact rows with an optional icon
/// or check mark and the shortcut on the right, like Zed's. Up, Down, Home and End move, Enter runs a row.
/// </summary>
public sealed class ArmuxMenu
{
    const int MaxRowsWithoutScrolling = 14;

    readonly FrameworkElement anchor;
    readonly StackPanel rows = new() { MinWidth = 168 };
    readonly List<Button> items = [];
    ArmuxPopup? popup;

    public ArmuxMenu(FrameworkElement anchor)
    {
        this.anchor = anchor;
        Ui.StyleRowButtons(rows.Resources);
    }

    /// <param name="glyph">An icon in front of the text.</param>
    /// <param name="check">For a dropdown: true draws a check mark in front of the text, false keeps its space empty.</param>
    /// <param name="hint">The shortcut, on the right.</param>
    public void Item(string text, Action run, bool enabled = true, string? glyph = null, bool? check = null, string? hint = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = glyph is null && check is null ? new GridLength(0) : new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if ((glyph ?? (check == true ? Ui.Check : null)) is { } icon)
        {
            var g = Ui.Glyph(icon, check is null ? 13 : 11, Theme.Brush(check is null ? "TextDim" : "Text"));
            g.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(g);
        }
        var label = Ui.Text(text, 13, Theme.Brush(enabled ? "Text" : "TextFaint"));
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (hint is { Length: > 0 })
        {
            var keys = Ui.Text(hint, 12, Theme.Brush("TextFaint"));
            keys.Margin = new Thickness(24, 0, 0, 0);
            keys.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(keys, 2);
            row.Children.Add(keys);
        }
        var b = Ui.RowButton(row, () => { popup?.Hide(); run(); });
        b.IsEnabled = enabled;
        AutomationProperties.SetName(b, text);
        b.GotFocus += (_, _) => b.Background = Theme.Brush("ControlHover");
        b.LostFocus += (_, _) => b.ClearValue(Control.BackgroundProperty);
        items.Add(b);
        rows.Children.Add(b);
    }

    public void Separator() =>
        rows.Children.Add(new Border { Height = 1, Background = Theme.Brush("Line"), Margin = new Thickness(-4, 4, -4, 4) });

    /// <param name="at">A point in the anchor, for a context menu; without it the menu opens below the anchor.</param>
    /// <param name="minWidth">A dropdown is at least as wide as its button.</param>
    public void Show(Point? at = null, double minWidth = 0)
    {
        rows.MinWidth = Math.Max(rows.MinWidth, minWidth);
        UIElement body = rows.Children.Count <= MaxRowsWithoutScrolling ? rows
            : new ScrollViewer { Content = rows, MaxHeight = anchor.XamlRoot.Size.Height * 0.8 / Theme.Settings.Zoom };
        popup = new ArmuxPopup(anchor, body, new Thickness(4));
        popup.KeyDown += OnKey;
        popup.Show(at);
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
        e.Handled = true;
        var enabled = items.Where(b => b.IsEnabled).ToList();
        if (enabled.Count == 0) return;
        var focused = FocusManager.GetFocusedElement(anchor.XamlRoot);
        int i = enabled.FindIndex(b => ReferenceEquals(b, focused));
        int next = e.Key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => enabled.Count - 1,
            VirtualKey.Down => (i + 1) % enabled.Count,
            _ => i < 0 ? enabled.Count - 1 : (i - 1 + enabled.Count) % enabled.Count,
        };
        enabled[next].Focus(FocusState.Keyboard);
    }
}
