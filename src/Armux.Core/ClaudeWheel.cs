using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armux.Core;

/// <summary>
/// Claude Code's own wheel acceleration setting (D-45). With <c>wheelScrollAccelerationEnabled</c> false in its
/// settings.json, every wheel event scrolls a constant number of rows (<c>CLAUDE_CODE_SCROLL_SPEED</c>, default 3), so
/// armux can pass wheel notches straight through instead of pacing them (<see cref="WheelPacer"/>).
/// </summary>
public static class ClaudeWheel
{
    public const string Key = "wheelScrollAccelerationEnabled";

    static string? cachedPath;
    static DateTime cachedStamp;
    static bool cachedOff;
    static long checkedAt;

    /// <summary>~/.claude/settings.json, or the one in CLAUDE_CONFIG_DIR.</summary>
    public static string SettingsPath()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, "settings.json");
    }

    /// <summary>True when the user level settings turn Claude Code's wheel acceleration off. Re-read at most every 2 s, and only when the file changed.</summary>
    public static bool AccelerationOff(string? path = null)
    {
        path ??= SettingsPath();
        long now = Environment.TickCount64;
        if (path == cachedPath && now - checkedAt < 2000) return cachedOff;
        checkedAt = now;
        try
        {
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path == cachedPath && stamp == cachedStamp) return cachedOff;
            cachedPath = path;
            cachedStamp = stamp;
            cachedOff = stamp != DateTime.MinValue && Read(path) is { } root && root[Key] is JsonValue v && v.TryGetValue(out bool on) && !on;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            cachedOff = false;
        }
        return cachedOff;
    }

    /// <summary>
    /// Turns Claude Code's wheel acceleration off (<paramref name="off"/>) or removes the key again. Keeps the other keys, writes
    /// the file indented (comments are lost) and leaves a one-time backup next to it. Throws if the file is not valid JSON, so it is never overwritten.
    /// </summary>
    public static void SetAccelerationOff(bool off, string? path = null)
    {
        path ??= SettingsPath();
        var root = File.Exists(path) ? Read(path) ?? throw new JsonException("settings.json is not a JSON object") : new JsonObject();
        if (off) root[Key] = false; else root.Remove(Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".armux-bak";
        if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
        using (var stream = File.Create(path))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            root.WriteTo(writer);
        checkedAt = 0;
    }

    static JsonObject? Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
}
