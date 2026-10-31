using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Armux.Core;

/// <summary>
/// Registers <c>armux mcp</c> with Claude Code (D-40), as wmux does for its own server: an <c>"armux"</c> entry under
/// <c>mcpServers</c> in Claude Code's user config (<c>~/.claude.json</c>), which every Claude Code session loads.
/// Only that one entry is touched; the file is written only when the entry is missing or points elsewhere.
/// </summary>
public static class ClaudeMcp
{
    public const string Name = "armux";

    /// <summary>
    /// Claude Code's user config: <c>ARMUX_CLAUDE_CONFIG</c> (tests), else <c>$CLAUDE_CONFIG_DIR/.claude.json</c>,
    /// else <c>~/.claude.json</c>.
    /// </summary>
    public static string ConfigPath()
    {
        if (Environment.GetEnvironmentVariable("ARMUX_CLAUDE_CONFIG") is { Length: > 0 } test) return test;
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir) return Path.Combine(dir, ".claude.json");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    }

    /// <summary>
    /// Adds or repairs the entry so it runs <paramref name="cliPath"/> <c>mcp</c>. Returns true when the file changed.
    /// Does nothing when the config does not exist (Claude Code not installed) or does not parse.
    /// </summary>
    public static bool Register(string configPath, string cliPath) => Edit(configPath, servers =>
    {
        if (servers[Name] is JsonObject e && e["command"]?.GetValueKind() == JsonValueKind.String
            && string.Equals((string?)e["command"], cliPath, StringComparison.OrdinalIgnoreCase)
            && e["args"] is JsonArray { Count: 1 } a && a[0]?.GetValueKind() == JsonValueKind.String && (string?)a[0] == "mcp")
            return false;
        servers[Name] = new JsonObject { ["command"] = cliPath, ["args"] = new JsonArray("mcp") };
        return true;
    });

    /// <summary>Removes the entry if armux wrote it (its command is an <c>armux.com</c>). Returns true when the file changed.</summary>
    public static bool Unregister(string configPath) => Edit(configPath, servers =>
    {
        if (servers[Name] is not JsonObject e || e["command"]?.GetValueKind() != JsonValueKind.String
            || !string.Equals(Path.GetFileName((string?)e["command"]), "armux.com", StringComparison.OrdinalIgnoreCase))
            return false;
        servers.Remove(Name);
        return true;
    });

    /// <summary>
    /// Claude Code rewrites this file often, so the edit is redone if the file changed while it was being made, and
    /// the result is written beside it and swapped in, so a reader never sees half a file.
    /// </summary>
    static bool Edit(string configPath, Func<JsonObject, bool> change)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!File.Exists(configPath)) return false;
            var stamp = File.GetLastWriteTimeUtc(configPath);
            JsonObject root;
            try { if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject o) return false; root = o; }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
            catch (IOException) { Thread.Sleep(50); continue; }

            if (root["mcpServers"] is not JsonObject servers)
            {
                if (root["mcpServers"] is not null) return false;
                servers = [];
                root["mcpServers"] = servers;
            }
            if (!change(servers)) return false;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                root.WriteTo(writer);
            var temp = configPath + ".armux.tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            try
            {
                if (File.GetLastWriteTimeUtc(configPath) != stamp) continue;
                File.Move(temp, configPath, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return false;
    }
}
