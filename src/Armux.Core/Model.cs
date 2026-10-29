using System.Collections.ObjectModel;

namespace Armux.Core;

public enum PanelKind { Terminal, Browser, File }

/// <summary>Split direction. Right = side by side (cmux "split right"), Down = stacked ("split down").</summary>
public enum SplitDirection { Right, Down }

public enum Direction { Left, Right, Up, Down }

public enum IndicatorStyle { Accent, Underline, None }

/// <summary>A tab inside a pane (cmux "surface"). Its content is a terminal, a browser or a read-only file view.</summary>
public sealed class Surface : Observable
{
    static int next;
    public int Id { get; } = Interlocked.Increment(ref next);
    public PanelKind Kind { get; init; }

    string title = "";
    public string Title { get => title; set => Set(ref title, value); }

    bool unread;
    public bool Unread { get => unread; set => Set(ref unread, value); }

    bool waiting;
    /// <summary>
    /// The program in this terminal asked for the user (a question or a permission prompt: the bell, an OSC
    /// notification, OSC 9;4 paused) and nothing has been typed or pasted since. Shown orange.
    /// </summary>
    public bool Waiting { get => waiting; set => Set(ref waiting, value); }

    /// <summary>Folder a live terminal starts in (a restored session); empty means the workspace's folder.</summary>
    public string StartDirectory { get; init; } = "";

    string url = "";
    public string Url { get => url; set => Set(ref url, value); }

    /// <summary>A browser surface that shows a built-in mock page instead of loading <see cref="Url"/> (screenshots, measurements).</summary>
    public bool MockPage { get; init; }

    /// <summary>Full path of the file shown by a <see cref="PanelKind.File"/> surface.</summary>
    public string FilePath { get; init; } = "";

    /// <summary>Shell of a live terminal ("pwsh" or "cmd"). Empty for a mock terminal, which shows <see cref="Lines"/>.</summary>
    public string Shell { get; init; } = "";
    public bool Live => Kind == PanelKind.Terminal && Shell.Length > 0;

    /// <summary>Canned output of a mock terminal (screenshots and measurements).</summary>
    public List<TermLine> Lines { get; init; } = [];
}

/// <summary>One mock terminal line made of colored spans.</summary>
public sealed record TermLine(IReadOnlyList<TermSpan> Spans)
{
    public static TermLine Plain(string text) => new([new TermSpan(text, TermColor.Default)]);
}

public sealed record TermSpan(string Text, TermColor Color);

public enum TermColor { Default, Dim, Green, Blue, Yellow, Red, Magenta, Cyan }

public abstract class PaneNode
{
    public SplitNode? Parent { get; internal set; }
}

/// <summary>A split region holding one or more surfaces (cmux "pane").</summary>
public sealed class Pane : PaneNode
{
    static int next;
    public int Id { get; } = Interlocked.Increment(ref next);
    public ObservableCollection<Surface> Surfaces { get; } = [];
    public Surface? Selected { get; set; }
    public bool NeedsAttention => Surfaces.Any(s => s.Unread);
    public bool Waiting => Surfaces.Any(s => s.Waiting);
}

public sealed class SplitNode : PaneNode
{
    public SplitDirection Direction { get; init; }
    PaneNode first = null!, second = null!;
    public PaneNode First { get => first; set { first = value; value.Parent = this; } }
    public PaneNode Second { get => second; set { second = value; value.Parent = this; } }

    /// <summary>Share of space given to <see cref="First"/>, 0..1.</summary>
    public double Ratio { get; set; } = 0.5;
}

public sealed class Workspace : Observable
{
    static int next;
    public int Id { get; } = Interlocked.Increment(ref next);

    string title = "";
    public string Title { get => title; set => Set(ref title, value); }

    string status = "";
    /// <summary>Status line or latest notification text shown under the title.</summary>
    public string Status { get => status; set => Set(ref status, value); }

    string branch = "", cwd = "", pr = "", ports = "";
    public string Branch { get => branch; set { if (Set(ref branch, value)) Raise(nameof(Location)); } }
    public string Cwd { get => cwd; set { if (Set(ref cwd, value)) Raise(nameof(Location)); } }
    public string Pr { get => pr; set { if (Set(ref pr, value)) Raise(nameof(HasPr)); } }
    public string Ports { get => ports; set => Set(ref ports, value); }
    public string Location => string.IsNullOrEmpty(Branch) ? Cwd : $"{Branch} · {Cwd}";
    public bool HasPr => !string.IsNullOrEmpty(Pr);

    string? color;
    /// <summary>Workspace color as "#RRGGBB", or null for none.</summary>
    public string? Color { get => color; set => Set(ref color, value); }

    bool pinned;
    public bool Pinned { get => pinned; set => Set(ref pinned, value); }

    int unreadCount;
    public int UnreadCount { get => unreadCount; set { if (Set(ref unreadCount, value)) Raise(nameof(HasUnread)); } }
    public bool HasUnread => UnreadCount > 0;

    bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }

    AgentState activity;
    /// <summary>What the workspace's terminals are doing, shown as a dot beside the status line.</summary>
    public AgentState Activity { get => activity; set => Set(ref activity, value); }

    public PaneNode Root { get; set; } = new Pane();
    public Pane? Focused { get; set; }
    public Pane? Zoomed { get; set; }

    public IEnumerable<Pane> Panes => Layout.Leaves(Root);
}

public sealed record Notification(int Id, Workspace Workspace, Pane Pane, Surface Surface, string Title, string Body, DateTime Time)
{
    public bool Read { get; set; }
}

/// <summary>17 workspace color presets (cmux offers 17; values here are armux's own).</summary>
public static class WorkspaceColors
{
    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Red", "#EF4444"), ("Orange", "#F97316"), ("Amber", "#F59E0B"), ("Yellow", "#EAB308"),
        ("Lime", "#84CC16"), ("Green", "#22C55E"), ("Emerald", "#10B981"), ("Teal", "#14B8A6"),
        ("Cyan", "#06B6D4"), ("Sky", "#0EA5E9"), ("Blue", "#3B82F6"), ("Indigo", "#6366F1"),
        ("Violet", "#8B5CF6"), ("Purple", "#A855F7"), ("Fuchsia", "#D946EF"), ("Pink", "#EC4899"),
        ("Slate", "#64748B"),
    ];
}
