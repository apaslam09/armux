using Armux.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Armux.App;

public enum ThemeKind { Dark, Light, HighContrast }

/// <summary>
/// Dark, light and high-contrast design tokens. All armux-drawn surfaces read colors from here, so a theme switch
/// is one call to <see cref="Use"/> followed by a re-render.
/// </summary>
public static class Theme
{
    public static ThemeKind Kind { get; private set; } = ThemeKind.Dark;
    /// <summary>High contrast is drawn on black, so it counts as dark for built-in controls and terminal colors.</summary>
    public static bool Dark => Kind != ThemeKind.Light;
    public static bool HighContrast => Kind == ThemeKind.HighContrast;

    /// <summary>The theme a setting asks for. "system" follows Windows: its high-contrast mode, else its app mode.</summary>
    public static ThemeKind Resolve(string setting) => setting switch
    {
        "dark" => ThemeKind.Dark,
        "light" => ThemeKind.Light,
        "high-contrast" => ThemeKind.HighContrast,
        _ => new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? ThemeKind.HighContrast
            : new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128
                ? ThemeKind.Dark : ThemeKind.Light,
    };

    /// <summary>Saved fonts and sizes. Change them with <see cref="UseSettings"/>, then re-render.</summary>
    public static Settings Settings { get; private set; } = new();

    /// <summary>The interface font as a FontFamily source (the bundled file for IBM Plex Sans), with Segoe UI as fallback.</summary>
    public static string UiFont => FontSource(Settings.UiFont) + ", Segoe UI Variable Text, Segoe UI";
    /// <summary>The semibold interface font. Plex ships weights as separate files, so semibold needs its own source.</summary>
    public static string UiFontSemibold =>
        (Settings.UiFont.Equals(Settings.DefaultUiFont, StringComparison.OrdinalIgnoreCase) ? BundledPlexSemibold : Settings.UiFont)
        + ", Segoe UI Variable Text, Segoe UI";
    public static double TerminalFontSize => Settings.TerminalFontSize;

    /// <summary>Bundled copies of Zed's default fonts (.ZedMono = Lilex, .ZedSans = IBM Plex Sans), loaded from the app folder.</summary>
    const string BundledLilex = "ms-appx:///Assets/Fonts/Lilex-VF.ttf#Lilex";
    const string BundledPlex = "ms-appx:///Assets/Fonts/IBMPlexSans-Regular.ttf#IBM Plex Sans";
    const string BundledPlexSemibold = "ms-appx:///Assets/Fonts/IBMPlexSans-SemiBold.ttf#IBM Plex Sans";

    /// <summary>The terminal font with fallbacks, so a missing font or glyph still renders monospaced.</summary>
    public static FontFamily TerminalFont => new(FontSource(Settings.TerminalFont) + ", Cascadia Mono, Consolas");

    /// <summary>A family name, or the bundled file for Lilex and IBM Plex Sans.</summary>
    public static string FontSource(string family) =>
        family.Equals(Settings.DefaultTerminalFont, StringComparison.OrdinalIgnoreCase) ? BundledLilex
        : family.Equals(Settings.DefaultUiFont, StringComparison.OrdinalIgnoreCase) ? BundledPlex
        : family;

    public static void UseSettings(Settings s) => Settings = s.Normalized();

    static readonly Dictionary<string, (uint Dark, uint Light)> Tokens = new()
    {
        ["TitleBar"] = (0xFF1F1F1F, 0xFFF0F0F0),
        ["Sidebar"] = (0xFF1F1F1F, 0xFFF0F0F0),
        ["SidebarHover"] = (0xFF292929, 0xFFE6E6E6),
        ["Pane"] = (0xFF262626, 0xFFFFFFFF),
        ["TabBar"] = (0xFF2C2C2C, 0xFFF5F5F5),
        ["TabActive"] = (0xFF262626, 0xFFFFFFFF),
        ["Divider"] = (0xFF161616, 0xFFD9D9D9),
        ["Border"] = (0xFF3A3A3A, 0xFFD4D4D4),
        ["Text"] = (0xFFE8E8E8, 0xFF1F1F1F),
        ["TextDim"] = (0xFFA3A3A3, 0xFF5C5C5C),
        ["TextFaint"] = (0xFF7A7A7A, 0xFF8A8A8A),
        ["Accent"] = (0xFF3B82F6, 0xFF2563EB),
        // Selected workspace: a lighter gray than hover, not cmux's blue, so workspace colors stay visible (D-12).
        ["SelectedRow"] = (0xFF363636, 0xFFD9D9D9),
        ["SelectedText"] = (0xFFFFFFFF, 0xFFFFFFFF),
        ["SelectedDim"] = (0xFFDBE7FE, 0xFFDBE7FE),
        ["Link"] = (0xFF60A5FA, 0xFF2563EB),
        ["Overlay"] = (0x66000000, 0x33000000),
        ["Palette"] = (0xFF2B2B2B, 0xFFFFFFFF),
        ["PaletteSelected"] = (0xFF3B82F6, 0xFF2563EB),
        ["Input"] = (0xFF1F1F1F, 0xFFF3F3F3),
        // Settings page (Zed style): row separators and the flat buttons, dropdowns and fields.
        ["Line"] = (0xFF323232, 0xFFE5E5E5),
        // A program waiting for an answer (question or permission prompt): orange, apart from the blue of "unread".
        ["Waiting"] = (0xFFF97316, 0xFFEA580C),
        ["Control"] = (0xFF2E2E2E, 0xFFFFFFFF),
        ["ControlHover"] = (0xFF393939, 0xFFF0F0F0),
        ["Term.Default"] = (0xFFD4D4D4, 0xFF1F1F1F),
        ["Term.Dim"] = (0xFF8A8A8A, 0xFF6E6E6E),
        ["Term.Green"] = (0xFF4EC97B, 0xFF16A34A),
        ["Term.Blue"] = (0xFF6CA6E8, 0xFF2563EB),
        ["Term.Yellow"] = (0xFFDCC77A, 0xFFA16207),
        ["Term.Red"] = (0xFFF16A6A, 0xFFDC2626),
        ["Term.Magenta"] = (0xFFC792EA, 0xFF9333EA),
        ["Term.Cyan"] = (0xFF56C8E8, 0xFF0E7490),
    };

    /// <summary>
    /// High contrast: white on black, yellow links and a cyan accent, as in Windows' "Night sky" theme. Keys not
    /// listed fall back to the dark tokens.
    /// </summary>
    static readonly Dictionary<string, uint> HighContrastTokens = new()
    {
        ["TitleBar"] = 0xFF000000, ["Sidebar"] = 0xFF000000, ["SidebarHover"] = 0xFF1A1A1A, ["Pane"] = 0xFF000000,
        ["TabBar"] = 0xFF000000, ["TabActive"] = 0xFF000000, ["Divider"] = 0xFFFFFFFF, ["Border"] = 0xFFFFFFFF,
        ["Text"] = 0xFFFFFFFF, ["TextDim"] = 0xFFFFFFFF, ["TextFaint"] = 0xFFD0D0D0, ["Accent"] = 0xFF1AEBFF,
        ["SelectedRow"] = 0xFF00405C, ["SelectedText"] = 0xFFFFFFFF, ["SelectedDim"] = 0xFFFFFFFF, ["Link"] = 0xFFFFFF00,
        ["Overlay"] = 0xCC000000, ["Palette"] = 0xFF000000, ["PaletteSelected"] = 0xFF00405C, ["Input"] = 0xFF000000,
        ["Line"] = 0xFFFFFFFF, ["Waiting"] = 0xFFFF9F1A, ["Control"] = 0xFF000000, ["ControlHover"] = 0xFF1A1A1A,
        ["Term.Default"] = 0xFFFFFFFF, ["Term.Dim"] = 0xFFD0D0D0, ["Term.Green"] = 0xFF3FF23F, ["Term.Blue"] = 0xFF5CB8FF,
        ["Term.Yellow"] = 0xFFFFFF00, ["Term.Red"] = 0xFFFF6B6B, ["Term.Magenta"] = 0xFFFF7BFF, ["Term.Cyan"] = 0xFF1AEBFF,
    };

    static readonly Dictionary<(string, ThemeKind), SolidColorBrush> cache = [];

    public static void Use(ThemeKind kind) => Kind = kind;

    public static Color Color(string key)
    {
        uint v = HighContrast && HighContrastTokens.TryGetValue(key, out var hc) ? hc : Dark ? Tokens[key].Dark : Tokens[key].Light;
        return ColorHelper.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static SolidColorBrush Brush(string key)
    {
        if (!cache.TryGetValue((key, Kind), out var b)) cache[(key, Kind)] = b = new SolidColorBrush(Color(key));
        return b;
    }

    public static SolidColorBrush Term(TermColor c) => Brush("Term." + c);

    public static SolidColorBrush Hex(string hex)
    {
        uint v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v));
    }
}
