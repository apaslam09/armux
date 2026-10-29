using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armux.Core;

/// <summary>
/// What armux reopens on the next start (setting <see cref="Settings.RestoreSession"/>): the workspaces with their
/// titles, colors and pins, the split tree with its ratios, and each pane's tabs. Terminals come back as new shells
/// in the folder they were last in; the programs that ran in them and their scrollback do not.
/// Saved as session.json beside settings.json.
/// </summary>
public sealed class SessionFile
{
    public int Version { get; set; } = 1;
    /// <summary>Index of the workspace that was shown.</summary>
    public int Current { get; set; }
    public List<SessionWorkspace> Workspaces { get; set; } = [];
}

public sealed class SessionWorkspace
{
    public string Title { get; set; } = "";
    public string? Color { get; set; }
    public bool Pinned { get; set; }
    public string Cwd { get; set; } = "";
    /// <summary>Index of the focused pane, in layout order.</summary>
    public int Focused { get; set; }
    public SessionNode Root { get; set; } = new();
}

/// <summary>A split (<see cref="Split"/> is "right" or "down", with <see cref="First"/> and <see cref="Second"/>) or a pane (<see cref="Tabs"/>).</summary>
public sealed class SessionNode
{
    public string? Split { get; set; }
    public double Ratio { get; set; } = 0.5;
    public SessionNode? First { get; set; }
    public SessionNode? Second { get; set; }
    public List<SessionTab>? Tabs { get; set; }
    public int Selected { get; set; }
}

public sealed class SessionTab
{
    /// <summary>"terminal", "browser" or "file".</summary>
    public string Kind { get; set; } = "terminal";
    public string Title { get; set; } = "";
    public string Shell { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class SessionStore
{
    /// <summary>The window's state as a session. <paramref name="terminalCwd"/> gives a terminal's current folder, if known.</summary>
    public static SessionFile Capture(AppState state, Func<Surface, string?> terminalCwd)
    {
        var file = new SessionFile { Current = Math.Max(0, state.Current is { } c ? state.Workspaces.IndexOf(c) : 0) };
        foreach (var ws in state.Workspaces)
        {
            var panes = ws.Panes.ToList();
            file.Workspaces.Add(new SessionWorkspace
            {
                Title = ws.Title,
                Color = ws.Color,
                Pinned = ws.Pinned,
                Cwd = ws.Cwd,
                Focused = Math.Max(0, ws.Focused is { } f ? panes.IndexOf(f) : 0),
                Root = Node(ws.Root),
            });
        }
        return file;

        SessionNode Node(PaneNode node)
        {
            if (node is SplitNode s)
                return new SessionNode { Split = s.Direction == SplitDirection.Right ? "right" : "down", Ratio = s.Ratio, First = Node(s.First), Second = Node(s.Second) };
            var pane = (Pane)node;
            return new SessionNode
            {
                Tabs = [.. pane.Surfaces.Select(Tab)],
                Selected = Math.Max(0, pane.Selected is { } sel ? pane.Surfaces.IndexOf(sel) : 0),
            };
        }

        SessionTab Tab(Surface s) => s.Kind switch
        {
            PanelKind.Browser => new SessionTab { Kind = "browser", Title = s.Title, Url = s.MockPage ? "" : s.Url },
            PanelKind.File => new SessionTab { Kind = "file", Title = s.Title, Path = s.FilePath },
            _ => new SessionTab { Kind = "terminal", Title = s.Title, Shell = s.Shell, Cwd = terminalCwd(s) ?? "" },
        };
    }

    /// <summary>
    /// The window state a session describes, or null when it has no workspaces. Files that are gone are left out,
    /// a pane left with no tabs gets a terminal, and terminals without a shell use <paramref name="defaultShell"/>.
    /// </summary>
    public static AppState? Restore(SessionFile file, string defaultShell)
    {
        if (file.Workspaces.Count == 0) return null;
        var state = new AppState();
        foreach (var w in file.Workspaces)
        {
            var ws = new Workspace { Title = w.Title.Length > 0 ? w.Title : $"workspace {state.Workspaces.Count + 1}", Color = w.Color, Pinned = w.Pinned, Cwd = w.Cwd };
            ws.Root = Node(w.Root ?? new SessionNode(), 0);
            var panes = ws.Panes.ToList();
            ws.Focused = panes[Math.Clamp(w.Focused, 0, panes.Count - 1)];
            state.Workspaces.Add(ws);
        }
        state.Current = state.Workspaces[Math.Clamp(file.Current, 0, state.Workspaces.Count - 1)];
        return state;

        PaneNode Node(SessionNode n, int depth)
        {
            if (n.Split is "right" or "down" && n.First is { } first && n.Second is { } second && depth < 32)
                return new SplitNode
                {
                    Direction = n.Split == "right" ? SplitDirection.Right : SplitDirection.Down,
                    Ratio = double.IsFinite(n.Ratio) ? Math.Clamp(n.Ratio, 0.05, 0.95) : 0.5,
                    First = Node(first, depth + 1),
                    Second = Node(second, depth + 1),
                };
            var pane = new Pane();
            foreach (var t in n.Tabs ?? []) if (FromTab(t) is { } s) pane.Surfaces.Add(s);
            if (pane.Surfaces.Count == 0) pane.Surfaces.Add(Terminal(defaultShell, ""));
            pane.Selected = pane.Surfaces[Math.Clamp(n.Selected, 0, pane.Surfaces.Count - 1)];
            return pane;
        }

        Surface? FromTab(SessionTab t) => t.Kind switch
        {
            "browser" => new Surface { Kind = PanelKind.Browser, Title = t.Title.Length > 0 ? t.Title : t.Url.Length > 0 ? t.Url : "New tab", Url = t.Url },
            "file" => File.Exists(t.Path) ? new Surface { Kind = PanelKind.File, Title = Path.GetFileName(t.Path), FilePath = t.Path } : null,
            _ => Terminal(t.Shell.Length > 0 ? t.Shell : defaultShell, t.Cwd),
        };

        static Surface Terminal(string shell, string cwd)
        {
            var kind = Pty.Shells.Parse(shell);
            return new Surface
            {
                Kind = PanelKind.Terminal,
                Title = Pty.Shells.Name(kind),
                Shell = Pty.Shells.Name(kind),
                StartDirectory = cwd.Length > 0 && Directory.Exists(cwd) ? cwd : "",
            };
        }
    }

    public static string ToJson(SessionFile file) => JsonSerializer.Serialize(file, SessionJson.Default.SessionFile);

    /// <summary>The saved session, or null when there is none or it can't be read.</summary>
    public static SessionFile? Load(string path)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), SessionJson.Default.SessionFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Writes <paramref name="json"/> to a temporary file first, so a crash mid-write never leaves half a session.</summary>
    public static void Save(string path, string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SessionJson : JsonSerializerContext;
