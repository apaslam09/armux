namespace Armux.Core;

/// <summary>What a terminal tab is doing, for the taskbar indicator. Ordered from least to most urgent.</summary>
public enum AgentState { Idle, Working, Error, NeedsInput }

/// <summary>
/// Reads a terminal's state from what programs already emit:
/// <list type="bullet">
/// <item>OSC 9;4 progress (any program): 1 or 3 working, 2 error, 4 paused (waiting).</item>
/// <item>Claude Code's tab title: a spinner glyph while it works (<c>◐◓◑◒</c>, braille dots), <c>✳</c> when idle.
/// Claude Code emits OSC 9;4 only for ConEmu, Ghostty and iTerm, so the title is the signal in armux.</item>
/// </list>
/// "Needs input" also comes from notifications (OSC 9 / 99 / 777, the bell): the terminal session marks itself
/// waiting until the user types or pastes.
/// </summary>
public static class AgentStatus
{
    public static AgentState FromProgress(int oscProgressState) => oscProgressState switch
    {
        1 or 3 => AgentState.Working,
        2 => AgentState.Error,
        4 => AgentState.NeedsInput,
        _ => AgentState.Idle,
    };

    public static bool TitleShowsWork(string title)
    {
        if (title.Length == 0) return false;
        char c = title[0];
        // Only the glyphs Claude Code puts in the title while working; its idle title starts with '✳'.
        return c is '◐' or '◓' or '◑' or '◒' || c is >= '⠀' and <= '⣿';
    }

    /// <summary>The most urgent of the states.</summary>
    public static AgentState Max(IEnumerable<AgentState> states) => states.DefaultIfEmpty(AgentState.Idle).Max();

    static readonly string[] Shells = ["pwsh", "powershell", "windows powershell", "cmd", "command prompt"];

    /// <summary>
    /// The program a tab title names, without Claude Code's status glyph ("◑ armux" → "armux"), or null for a
    /// plain shell title ("pwsh", "C:\...\cmd.exe").
    /// </summary>
    public static string? ProgramName(string title)
    {
        title = title.Trim();
        if (title.Length > 1 && !char.IsLetterOrDigit(title[0]) && title[1] == ' ') title = title[2..].Trim();
        if (title.Length == 0) return null;
        var bare = title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(title) : title;
        return Shells.Contains(bare, StringComparer.OrdinalIgnoreCase) ? null : title;
    }

    /// <summary>
    /// The sidebar status line of a workspace with live terminals (cmux shows the latest notification there):
    /// an unread notification first, then a program waiting for an answer, then what is working, then the latest
    /// notification, then the program that is open, then "Idle".
    /// </summary>
    public static (string Text, AgentState Activity) Describe(
        IReadOnlyList<(string Title, AgentState State)> tabs, string? unreadNotification, string? latestNotification)
    {
        if (unreadNotification is not null) return (unreadNotification, AgentState.NeedsInput);
        if (tabs.FirstOrDefault(t => t.State == AgentState.NeedsInput) is { Title: { } waitingTitle })
            return ("Waiting for you · " + (ProgramName(waitingTitle) ?? "terminal"), AgentState.NeedsInput);
        var working = tabs.Where(t => t.State == AgentState.Working).ToList();
        if (working.Count == 1) return ("Working · " + (ProgramName(working[0].Title) ?? "terminal"), AgentState.Working);
        if (working.Count > 1) return ($"{working.Count} tabs working", AgentState.Working);
        if (tabs.Any(t => t.State == AgentState.Error)) return ("Error reported", AgentState.Error);
        if (latestNotification is not null) return (latestNotification, AgentState.Idle);
        var program = tabs.Select(t => ProgramName(t.Title)).FirstOrDefault(n => n is not null);
        return (program is null ? "Idle" : "Idle · " + program, AgentState.Idle);
    }
}
