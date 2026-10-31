using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armux.Core;

/// <summary>
/// User preferences, saved as JSON in %LOCALAPPDATA%\armux\settings.json. Every property has a default, a file with
/// only some of them keeps the defaults for the rest, and <see cref="Normalized"/> repairs out-of-range values.
/// </summary>
public sealed record Settings
{
    /// <summary>Zed's default buffer font. armux bundles it (SIL OFL-1.1, ARCHITECTURE.md section 10).</summary>
    public const string DefaultTerminalFont = "Lilex";
    /// <summary>Zed's default interface font (".ZedSans"). armux bundles it (SIL OFL-1.1).</summary>
    public const string DefaultUiFont = "IBM Plex Sans";
    public const double DefaultTerminalFontSize = 13, MinFontSize = 8, MaxFontSize = 32;
    public const double MinZoom = 0.7, MaxZoom = 2.0;
    public const int DefaultScrollback = 5000, MinScrollback = 500, MaxScrollback = 100_000;

    public static readonly string[] Themes = ["system", "dark", "light", "high-contrast"];
    public static readonly string[] CursorStyles = ["block", "bar", "underline"];
    public static readonly string[] SearchEngines = ["google", "bing", "duckduckgo"];
    public static readonly string[] Indicators = ["accent", "underline", "none"];
    public static readonly string[] ConfirmCloseModes = ["running", "always", "never"];

    // Settable, not init: the JSON source generator fills init-only properties missing from the file with
    // default values (0, null), which would turn a partial settings file into font size 0.

    // ---- General
    /// <summary>Shell for new terminals: "pwsh" (PowerShell) or "cmd".</summary>
    public string Shell { get; set; } = "pwsh";
    /// <summary>Folder for the first terminal and new workspaces; empty means the user profile. "~" is allowed.</summary>
    public string StartDirectory { get; set; } = "";
    /// <summary>Where new tabs and splits open: "workspace" (the workspace's current folder) or "start" (<see cref="StartDirectory"/>).</summary>
    public string NewTabDirectory { get; set; } = "workspace";
    /// <summary>When the last terminal's shell exits, close the window (true) or open a new shell (false).</summary>
    public bool CloseOnLastExit { get; set; } = true;
    /// <summary>Ask before closing the window: "running" (only while a terminal runs a program), "always" or "never".</summary>
    public string ConfirmClose { get; set; } = "running";
    /// <summary>Selecting text with the mouse copies it (Warp, Windows Terminal's copyOnSelect).</summary>
    public bool CopyOnSelect { get; set; } = true;
    /// <summary>Save the workspaces, splits and tabs when armux closes and reopen them on the next start.</summary>
    public bool RestoreSession { get; set; } = true;

    // ---- Appearance
    /// <summary>"system", "dark", "light" or "high-contrast".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Scale of the whole window (Ctrl+= / Ctrl+- / Ctrl+0), in steps of 0.1.</summary>
    public double Zoom { get; set; } = 1.0;
    public string UiFont { get; set; } = DefaultUiFont;
    /// <summary>Workspace color shown as "accent" (left rail), "underline" or "none".</summary>
    public string WorkspaceIndicator { get; set; } = "accent";

    // ---- Terminal
    public string TerminalFont { get; set; } = DefaultTerminalFont;
    public double TerminalFontSize { get; set; } = DefaultTerminalFontSize;
    /// <summary>Line height as a multiple of the font size.</summary>
    public double LineHeight { get; set; } = 1.4;
    public bool Ligatures { get; set; } = true;
    /// <summary>"block", "bar" or "underline".</summary>
    public string CursorStyle { get; set; } = "block";
    public bool CursorBlink { get; set; }
    public int ScrollbackLines { get; set; } = DefaultScrollback;

    // ---- Notifications
    /// <summary>Windows toasts for notifications that arrive while armux is in the background.</summary>
    public bool Toasts { get; set; } = true;
    /// <summary>Workspace status on the taskbar button.</summary>
    public bool TaskbarStatus { get; set; } = true;
    /// <summary>A terminal bell (BEL) counts as a request for attention.</summary>
    public bool BellNotifies { get; set; } = true;

    // ---- Browser
    public string SearchEngine { get; set; } = "google";
    /// <summary>Page a new browser tab opens; empty means a blank tab with the address bar focused.</summary>
    public string BrowserHome { get; set; } = "";
    /// <summary>Lets the <c>armux mcp</c> server (Claude Code) read and drive browser tabs: page text, clicks, typing, JavaScript.</summary>
    public bool BrowserControl { get; set; }

    // ---- Keyboard
    /// <summary>Shortcut overrides by command id. An empty value removes the shortcut. Missing ids use the defaults.</summary>
    public Dictionary<string, string> Keybindings { get; set; } = [];

    /// <summary>Repairs blank or out-of-range values to the defaults or the nearest allowed value.</summary>
    public Settings Normalized() => this with
    {
        TerminalFont = string.IsNullOrWhiteSpace(TerminalFont) ? DefaultTerminalFont : TerminalFont.Trim(),
        UiFont = string.IsNullOrWhiteSpace(UiFont) ? DefaultUiFont : UiFont.Trim(),
        Shell = Pty.Shells.Name(Pty.Shells.Parse(Shell)),
        StartDirectory = (StartDirectory ?? "").Trim().Trim('"'),
        NewTabDirectory = NewTabDirectory == "start" ? "start" : "workspace",
        Theme = OneOf(Theme, Themes),
        ConfirmClose = OneOf(ConfirmClose, ConfirmCloseModes),
        CursorStyle = OneOf(CursorStyle, CursorStyles),
        SearchEngine = OneOf(SearchEngine, SearchEngines),
        WorkspaceIndicator = OneOf(WorkspaceIndicator, Indicators),
        BrowserHome = (BrowserHome ?? "").Trim(),
        Zoom = double.IsFinite(Zoom) && Zoom > 0 ? Math.Clamp(Math.Round(Zoom * 10) / 10, MinZoom, MaxZoom) : 1.0,
        TerminalFontSize = double.IsFinite(TerminalFontSize)
            ? Math.Clamp(Math.Round(TerminalFontSize * 2) / 2, MinFontSize, MaxFontSize)
            : DefaultTerminalFontSize,
        LineHeight = double.IsFinite(LineHeight) ? Math.Clamp(Math.Round(LineHeight * 20) / 20, 1.0, 2.0) : 1.4,
        ScrollbackLines = Math.Clamp(ScrollbackLines, MinScrollback, MaxScrollback),
        Keybindings = Keybindings is null ? [] : new Dictionary<string, string>(Keybindings),
    };

    static string OneOf(string? value, string[] allowed) =>
        allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? allowed[0];

    public Settings WithTerminalFontSize(double size) => (this with { TerminalFontSize = size }).Normalized();

    /// <summary>Value equality, including the shortcut overrides (a record compares dictionaries by reference).</summary>
    public bool Equals(Settings? other) =>
        other is not null && Json(this) == Json(other);

    public override int GetHashCode() => Json(this).GetHashCode();

    static string Json(Settings s) =>
        JsonSerializer.Serialize(s with { Keybindings = new((s.Keybindings ?? []).OrderBy(k => k.Key, StringComparer.Ordinal)) }, SettingsJson.Default.Settings);

    /// <summary>Reads settings from <paramref name="path"/>. A missing or unreadable file gives the defaults.</summary>
    public static Settings Load(string path)
    {
        try
        {
            return (JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.Settings) ?? new()).Normalized();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Parses settings typed by hand (the settings.json editor, or the file changed by another program). Unlike
    /// <see cref="Load"/>, invalid JSON is an error, not the defaults, so a half-typed file never resets anything.
    /// </summary>
    public static bool TryParse(string json, out Settings settings, out string? error)
    {
        try
        {
            settings = (JsonSerializer.Deserialize(json, SettingsJson.Default.Settings) ?? new()).Normalized();
            error = null;
            return true;
        }
        catch (JsonException e)
        {
            settings = new();
            error = e.LineNumber is { } line ? $"Line {line + 1}: {e.Message.Split(" Path:")[0]}" : e.Message;
            return false;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, SettingsJson.Default.Settings);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
