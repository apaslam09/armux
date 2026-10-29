using System.Text;
using Armux.Core;
using Armux.Core.Pty;
using Armux.Core.Vt;
using Microsoft.UI.Dispatching;

namespace Armux.App;

/// <summary>
/// The terminal behind one terminal surface: an emulator plus either a live shell in a pseudoconsole or, for the
/// mock scenarios, the surface's canned lines. Lives as long as the surface, independent of its view, so theme or
/// font changes rebuild the view without restarting the shell.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    readonly Surface surface;
    readonly DispatcherQueue ui;
    readonly IReadOnlyDictionary<string, string> environment;
    readonly string? cwd;
    PtySession? pty;
    FileStream? ptyLog;
    int renderQueued;

    /// <summary>The emulator; scrollback length comes from Settings when the terminal is created.</summary>
    public Terminal Terminal { get; } = new(80, 24, Theme.Settings.ScrollbackLines);
    public bool Started { get; private set; }
    public bool Exited { get; private set; }
    /// <summary>A real shell, not a mock terminal replaying canned lines.</summary>
    public bool Live => surface.Live;
    /// <summary>The shell's working directory (OSC 7), or the directory it started in.</summary>
    public string? Cwd { get; private set; }
    /// <summary>Working or idle, from OSC 9;4 progress and Claude Code's title (see <see cref="AgentStatus"/>).</summary>
    public AgentState State { get; private set; }
    /// <summary>The program's own tab title, before armux shortens it.</summary>
    public string RawTitle { get; private set; } = "";

    /// <summary>Raised on the UI thread when output arrived (coalesced: at most one pending call).</summary>
    public event Action? Changed;
    public event Action<TermNotification>? Notified;
    public event Action<string>? CwdChanged;
    public event Action<int>? ProcessExited;
    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;
    /// <summary>Raised on the UI thread when the program sets a new title.</summary>
    public event Action? TitleUpdated;
    /// <summary>Raised on the UI thread for BEL. Claude Code rings it when it needs input.</summary>
    public event Action? Bell;

    public TerminalSession(Surface surface, DispatcherQueue ui, string? cwd, IReadOnlyDictionary<string, string> environment)
    {
        this.surface = surface;
        this.ui = ui;
        this.cwd = cwd;
        Cwd = cwd;
        this.environment = environment;
        Terminal.TitleChanged += t => ui.TryEnqueue(() =>
        {
            RawTitle = t;
            if (t.Length > 0) surface.Title = TabTitle(t);
            UpdateState();
            TitleUpdated?.Invoke();
        });
        Terminal.ProgressChanged += () => ui.TryEnqueue(UpdateState);
        Terminal.Bell += () => ui.TryEnqueue(() => Bell?.Invoke());
        Terminal.Notified += n => ui.TryEnqueue(() => Notified?.Invoke(n));
        Terminal.CwdChanged += p => ui.TryEnqueue(() => { Cwd = p; CwdChanged?.Invoke(p); });
        Terminal.Reply += r => pty?.Write(r);
    }

    /// <summary>Starts the shell (or replays mock lines) at the size of the first layout.</summary>
    public void Start(int cols, int rows)
    {
        if (Started) return;
        Started = true;
        Terminal.Resize(cols, rows);
        if (!surface.Live)
        {
            Terminal.Feed(MockToVt(surface.Lines));
            return;
        }
        try
        {
            var kind = Shells.Parse(surface.Shell);
            // Diagnostics: ARMUX_PTY_LOG=<file> records the raw ConPTY output and every resize.
            var log = System.Environment.GetEnvironmentVariable("ARMUX_PTY_LOG") is { Length: > 0 } path ? File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read) : null;
            ptyLog = log;
            Log($"[start {cols}x{rows}]");
            pty = PtySession.Start(ShellIntegration.CommandLine(kind), cwd, ShellIntegration.Environment(kind, environment), cols, rows,
                data => { if (log is not null) lock (log) { log.Write(data.Span); log.Flush(); } FeedSafely(data.Span); QueueRender(); },
                code => ui.TryEnqueue(() => { Exited = true; surface.Waiting = false; ProcessExited?.Invoke(code); }));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            App.Log(nameof(TerminalSession), e);
            Terminal.Feed($"\x1b[31marmux: could not start {surface.Shell}: {e.Message}\x1b[0m\r\n");
        }
    }

    /// <summary>
    /// An exception on the reader thread would end the whole process (a fail-fast that Windows reports as a "stack
    /// buffer overrun"). An emulator bug must cost at most a garbled screen: log it and keep reading.
    /// </summary>
    void FeedSafely(ReadOnlySpan<byte> data)
    {
        try { Terminal.Feed(data); }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref feedErrors) <= 3) App.Log(nameof(TerminalSession) + ".Feed", e);
        }
    }

    int feedErrors;

    /// <summary>
    /// The program asked the user something (a question, a permission prompt): set by the bell from a program
    /// (a bell at a bare shell prompt is ignored) or an OSC notification, cleared by the next key or paste. While set,
    /// <see cref="State"/> is NeedsInput, shown orange.
    /// </summary>
    public bool Waiting => surface.Waiting;

    public void MarkWaiting(bool fromBell)
    {
        if (Exited || fromBell && AgentStatus.ProgramName(RawTitle) is null) return;
        surface.Waiting = true;
        UpdateState();
    }

    void Answered()
    {
        if (!surface.Waiting) return;
        surface.Waiting = false;
        UpdateState();
    }

    void UpdateState()
    {
        var state = AgentStatus.FromProgress(Terminal.ProgressState);
        if (state == AgentState.Idle && AgentStatus.TitleShowsWork(RawTitle)) state = AgentState.Working;
        if (surface.Waiting) state = AgentState.NeedsInput;
        if (state == State) return;
        State = state;
        StateChanged?.Invoke();
    }

    void QueueRender()
    {
        if (Interlocked.Exchange(ref renderQueued, 1) == 1) return;
        ui.TryEnqueue(DispatcherQueuePriority.Low, () => { Volatile.Write(ref renderQueued, 0); Changed?.Invoke(); });
    }

    void Log(string marker)
    {
        if (ptyLog is not { } log) return;
        lock (log) { log.Write(Encoding.UTF8.GetBytes(marker)); log.Flush(); }
    }

    public void Resize(int cols, int rows)
    {
        Log($"[resize {cols}x{rows}]");
        Terminal.Resize(cols, rows);
        pty?.Resize(cols, rows);
    }

    public void Send(string text) => pty?.Write(text);

    /// <summary>Typed by the user: also answers a waiting program. (Mouse reports and focus events use <see cref="Send"/>.)</summary>
    public void Input(string text)
    {
        Answered();
        Send(text);
    }

    /// <summary>The shell's process id, or 0 before it starts.</summary>
    public int ShellProcessId => pty?.ProcessId ?? 0;

    /// <summary>Pastes text the way Ctrl+Shift+V does: bracketed when the program asked for it.</summary>
    public void Paste(string text)
    {
        Answered();
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        Send(Terminal.BracketedPaste ? "\u001b[200~" + text.Replace("\u001b[201~", "") + "\u001b[201~" : text);
    }

    public void Dispose()
    {
        pty?.Dispose();
        pty = null;
        ptyLog?.Dispose();
    }

    /// <summary>ConPTY sets the title to the shell's full path ("C:\...\pwsh.exe"); a tab shows just "pwsh".</summary>
    static string TabTitle(string title) =>
        title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(title) ? Path.GetFileNameWithoutExtension(title) : title;

    /// <summary>Phase 2 mock lines as VT text, so mock and live terminals share one renderer.</summary>
    static string MockToVt(IReadOnlyList<TermLine> lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append("\r\n");
            foreach (var span in lines[i].Spans)
            {
                sb.Append(span.Color switch
                {
                    TermColor.Dim => "\x1b[90m",
                    TermColor.Green => "\x1b[32m",
                    TermColor.Blue => "\x1b[34m",
                    TermColor.Yellow => "\x1b[33m",
                    TermColor.Red => "\x1b[31m",
                    TermColor.Magenta => "\x1b[35m",
                    TermColor.Cyan => "\x1b[36m",
                    _ => "\x1b[39m",
                });
                sb.Append(span.Text);
            }
            sb.Append("\x1b[0m");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Shell integration: the shells report their working directory with OSC 7 after every prompt, so the sidebar and
/// the file explorer follow <c>cd</c>. The user's own prompt is kept and wrapped.
/// </summary>
public static class ShellIntegration
{
    public static string CommandLine(ShellKind kind)
    {
        var shell = Shells.CommandLine(kind);
        if (kind != ShellKind.PowerShell) return shell;
        // Runs after the profile, wrapping whatever prompt it defined. The directory is also reported on every
        // location change (PowerShell 7.1+), so `cd x; claude` updates armux before the prompt returns.
        // [char]27 works in Windows PowerShell 5.1 too.
        const string script =
            "function global:__armuxCwd { $l = $executionContext.SessionState.Path.CurrentLocation; " +
            "if ($l.Provider.Name -eq 'FileSystem') { [Console]::Write([char]27 + ']7;file://localhost/' + ($l.ProviderPath -replace '\\\\', '/') + [char]7) } }; " +
            "$global:__armuxPrompt = $function:prompt; " +
            "function global:prompt { __armuxCwd; & $global:__armuxPrompt }; " +
            "if ($ExecutionContext.InvokeCommand.PSObject.Properties['LocationChangedAction']) { $ExecutionContext.InvokeCommand.LocationChangedAction = { __armuxCwd } }";
        return $"{shell} -NoExit -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}";
    }

    public static IReadOnlyDictionary<string, string> Environment(ShellKind kind, IReadOnlyDictionary<string, string> extra)
    {
        var env = new Dictionary<string, string>(extra)
        {
            ["TERM_PROGRAM"] = "armux",
            ["COLORTERM"] = "truecolor",
        };
        if (kind == ShellKind.Cmd)
        {
            // cmd has no prompt hook; PROMPT codes can still emit OSC 7 with the raw path ($P).
            var prompt = System.Environment.GetEnvironmentVariable("PROMPT") is { Length: > 0 } p ? p : "$P$G";
            env["PROMPT"] = "$E]7;file://localhost/$P$E\\" + prompt;
        }
        return env;
    }
}
