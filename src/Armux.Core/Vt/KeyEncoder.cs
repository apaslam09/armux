namespace Armux.Core.Vt;

/// <summary>Keys that send escape sequences rather than characters.</summary>
public enum TermKey
{
    Up, Down, Right, Left, Home, End, Insert, Delete, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Tab, Backspace,
}

/// <summary>
/// xterm key encoding: cursor keys honor DECCKM (application mode sends SS3), and modifiers use the
/// <c>CSI 1 ; m X</c> / <c>CSI n ; m ~</c> forms with m = 1 + Shift + 2·Alt + 4·Ctrl.
/// </summary>
public static class KeyEncoder
{
    /// <summary>
    /// Shift+Enter for a full-screen program (mouse tracking or the alternate screen), which can't tell a plain CR from
    /// Enter. <c>CSI 13;2u</c> reaches Claude Code through ConPTY as a new line. At a shell prompt ConPTY swallows it, so
    /// shells keep the plain CR and Shift+Enter still runs the line there. Null means send nothing special.
    /// </summary>
    public static string? ShiftEnter(bool altScreen, MouseTracking mouse) => altScreen || mouse != MouseTracking.None ? "\u001b[13;2u" : null;

    public static string Encode(TermKey key, bool shift, bool alt, bool ctrl, bool appCursorKeys)
    {
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Cursor(char c) => mod == 1 ? (appCursorKeys ? $"\u001bO{c}" : $"\u001b[{c}") : $"\u001b[1;{mod}{c}";
        string Tilde(int n) => mod == 1 ? $"\u001b[{n}~" : $"\u001b[{n};{mod}~";
        string Ss3(char c) => mod == 1 ? $"\u001bO{c}" : $"\u001b[1;{mod}{c}";
        return key switch
        {
            TermKey.Up => Cursor('A'),
            TermKey.Down => Cursor('B'),
            TermKey.Right => Cursor('C'),
            TermKey.Left => Cursor('D'),
            TermKey.Home => Cursor('H'),
            TermKey.End => Cursor('F'),
            TermKey.Insert => Tilde(2),
            TermKey.Delete => Tilde(3),
            TermKey.PageUp => Tilde(5),
            TermKey.PageDown => Tilde(6),
            TermKey.F1 => Ss3('P'),
            TermKey.F2 => Ss3('Q'),
            TermKey.F3 => Ss3('R'),
            TermKey.F4 => Ss3('S'),
            TermKey.F5 => Tilde(15),
            TermKey.F6 => Tilde(17),
            TermKey.F7 => Tilde(18),
            TermKey.F8 => Tilde(19),
            TermKey.F9 => Tilde(20),
            TermKey.F10 => Tilde(21),
            TermKey.F11 => Tilde(23),
            TermKey.F12 => Tilde(24),
            TermKey.Tab => shift ? "\u001b[Z" : "\t",
            TermKey.Backspace => (alt ? "\u001b" : "") + (ctrl ? "\b" : "\u007f"),
            _ => "",
        };
    }

    /// <summary>
    /// Text typed with a character key. Alt sends ESC first; Ctrl+Alt is AltGr on many layouts and types the
    /// character unchanged (deviation D-01).
    /// </summary>
    public static string Text(string text, bool alt, bool ctrl) => alt && !ctrl ? "\u001b" + text : text;
}
