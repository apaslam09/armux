namespace Armux.Core;

public sealed record FileEntry(string Path, string Name, bool IsDirectory);

/// <summary>A visible row of the explorer: an entry, or a "N more" row when a folder is truncated.</summary>
public readonly record struct FileRow(FileEntry? Entry, int Depth, int More = 0);

/// <summary>
/// The file explorer's model (Warp's file tree): one root folder, lazily listed folders, and the set of
/// expanded folders. Listings are cached until <see cref="Invalidate"/> is called for that folder.
/// </summary>
public sealed class FileTree(string root)
{
    /// <summary>Folders with more entries than this show the first ones and a "N more" row.</summary>
    public const int MaxEntries = 500;

    static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    readonly HashSet<string> expanded = new(Cmp);
    readonly Dictionary<string, IReadOnlyList<FileEntry>> listings = new(Cmp);

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public bool IsExpanded(string dir) => expanded.Contains(dir);
    public void Expand(string dir) => expanded.Add(dir);
    public void Collapse(string dir) => expanded.Remove(dir);
    public void Toggle(string dir) { if (!expanded.Remove(dir)) expanded.Add(dir); }

    /// <summary>Collapses every folder and drops cached listings.</summary>
    public void CollapseAll() { expanded.Clear(); listings.Clear(); }

    /// <summary>Forgets the cached listing of <paramref name="dir"/>, so the next render re-reads it.</summary>
    public void Invalidate(string dir) => listings.Remove(Path.TrimEndingDirectorySeparator(dir));

    public void InvalidateAll() => listings.Clear();

    /// <summary>Folders first, then files, each by name. Skips .git and hidden system files; unreadable folders are empty.</summary>
    public IReadOnlyList<FileEntry> List(string dir)
    {
        if (listings.TryGetValue(dir, out var cached)) return cached;
        var list = new List<FileEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (info.Name == ".git") continue;
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) continue;
                list.Add(new FileEntry(info.FullName, info.Name, info is DirectoryInfo));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        list.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : Cmp.Compare(a.Name, b.Name));
        listings[dir] = list;
        return list;
    }

    /// <summary>The rows to draw: a depth-first walk of the root and every expanded folder.</summary>
    public List<FileRow> Rows()
    {
        var rows = new List<FileRow>();
        void Walk(string dir, int depth)
        {
            var entries = List(dir);
            foreach (var e in entries.Take(MaxEntries))
            {
                rows.Add(new FileRow(e, depth));
                if (e.IsDirectory && expanded.Contains(e.Path)) Walk(e.Path, depth + 1);
            }
            if (entries.Count > MaxEntries) rows.Add(new FileRow(null, depth, entries.Count - MaxEntries));
        }
        Walk(Root, 0);
        return rows;
    }

    /// <summary>
    /// The path typed when a file is dropped on a terminal in <paramref name="cwd"/>: relative to it (climbing at most
    /// <paramref name="maxUp"/> folders with ..\), otherwise absolute, as for a file on another drive.
    /// </summary>
    public static string DropPath(string? cwd, string path, int maxUp = 3)
    {
        if (cwd is null) return path;
        var rel = Path.GetRelativePath(cwd, path);
        if (Path.IsPathRooted(rel)) return path;
        int up = rel.Split(Path.DirectorySeparatorChar).TakeWhile(s => s == "..").Count();
        return up > maxUp ? path : rel;
    }

    /// <summary>
    /// Maps a workspace directory such as "~\src\app" to a folder that exists: "~" becomes the user profile,
    /// and a missing folder falls back to its nearest existing parent. <paramref name="exact"/> is false after a fallback.
    /// </summary>
    public static string Resolve(string cwd, string home, out bool exact)
    {
        string path = cwd.Trim();
        if (path.Length == 0 || path == "~") path = home;
        else if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Join(home, path[2..]);
        try { path = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { path = home; }

        exact = true;
        for (var p = path; p is not null; p = Path.GetDirectoryName(p))
        {
            if (Directory.Exists(p)) return Path.TrimEndingDirectorySeparator(p);
            exact = false;
        }
        exact = false;
        return home;
    }
}
