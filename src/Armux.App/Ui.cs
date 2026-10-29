using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Armux.App;

/// <summary>Small factory helpers for code-built UI, plus the icons cmux uses that have no Fluent glyph.</summary>
public static class Ui
{
    public const string Bell = "", Add = "", Back = "", Forward = "", Refresh = "",
        Close = "", Folder = "", Terminal = "", Globe = "", Pin = "", Search = "";

    public const string Explorer = "\uEC50", FolderOpen = "\uE838", ChevronRight = "\uE76C", ChevronDown = "\uE70D",
        ChevronUp = "\uE70E", Document = "\uE8A5", Code = "\uE943", Photo = "\uE91B", Gear = "\uE713", Page = "\uE7C3",
        Up = "\uE74A", Drive = "\uEDA2", Home = "\uE80F", Places = "\uE81D";

    public static FontIcon Glyph(string glyph, double size, Brush fg) =>
        new() { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size, Foreground = fg };

    public static TextBlock Text(string text, double size, Brush fg, bool semibold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = fg,
        FontFamily = new FontFamily(semibold ? Theme.UiFontSemibold : Theme.UiFont),
        FontWeight = semibold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    public static Button IconButton(UIElement icon, string tip, Action click, double size = 28)
    {
        var b = new Button
        {
            Content = icon,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            // Clicking an icon button must not take keyboard focus from the terminal (Tab still reaches it).
            AllowFocusOnInteraction = false,
        };
        ToolTipService.SetToolTip(b, tip);
        // Icon-only buttons need a name for screen readers (and UI Automation tests).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Solid themed popup background. The default acrylic does not survive window capture.</summary>
    public static Style SolidPresenter(Type target)
    {
        var s = new Style(target);
        s.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Brush("Palette")));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Brush("Border")));
        return s;
    }

    /// <summary>Outlined rectangle with an inner divider: the split-right / split-down / sidebar icons.</summary>
    static Canvas Framed(Brush fg, Action<Canvas> inner)
    {
        var c = new Canvas { Width = 14, Height = 12 };
        c.Children.Add(new Rectangle { Width = 14, Height = 12, Stroke = fg, StrokeThickness = 1.2, RadiusX = 2, RadiusY = 2 });
        inner(c);
        return c;
    }

    static Line Ln(double x1, double y1, double x2, double y2, Brush fg) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = fg, StrokeThickness = 1.2 };

    public static UIElement SplitRightIcon(Brush fg) => Framed(fg, c => c.Children.Add(Ln(7, 0, 7, 12, fg)));
    public static UIElement SplitDownIcon(Brush fg) => Framed(fg, c => c.Children.Add(Ln(0, 6, 14, 6, fg)));
    public static UIElement SidebarIcon(Brush fg) => Framed(fg, c => c.Children.Add(Ln(5, 0, 5, 12, fg)));
}
