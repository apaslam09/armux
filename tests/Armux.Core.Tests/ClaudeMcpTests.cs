using System.Text.Json.Nodes;
using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public sealed class ClaudeMcpTests
{
    static string TempConfig(string? json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "armux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".claude.json");
        if (json is not null) File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void RegisterAddsOnlyTheArmuxEntryAndKeepsTheRest()
    {
        var path = TempConfig("""
            { "numStartups": 5, "C:/a": 1, "c:/A": 2, "tip": "café ✳",
              "mcpServers": { "wmux": { "command": "node", "args": ["index.js"] } } }
            """);
        Assert.IsTrue(ClaudeMcp.Register(path, @"C:\armux\armux.com"));
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.AreEqual(5, (int)root["numStartups"]!);
        Assert.AreEqual(2, (int)root["c:/A"]!);                        // keys that differ only by case survive
        Assert.AreEqual("café ✳", (string?)root["tip"]);
        Assert.Contains("café ✳", File.ReadAllText(path));               // written as text, not \u escapes
        Assert.AreEqual("node", (string?)root["mcpServers"]!["wmux"]!["command"]);
        Assert.AreEqual(@"C:\armux\armux.com", (string?)root["mcpServers"]!["armux"]!["command"]);
        Assert.AreEqual("mcp", (string?)root["mcpServers"]!["armux"]!["args"]![0]);

        // Already right: nothing is written. A moved armux repoints the entry.
        Assert.IsFalse(ClaudeMcp.Register(path, @"c:\ARMUX\armux.com"));
        Assert.IsTrue(ClaudeMcp.Register(path, @"D:\new\armux.com"));
        Assert.AreEqual(@"D:\new\armux.com", (string?)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["armux"]!["command"]);
        Assert.IsFalse(File.Exists(path + ".armux.tmp"));

        Assert.IsTrue(ClaudeMcp.Unregister(path));
        var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();
        Assert.IsFalse(servers.ContainsKey("armux"));
        Assert.IsTrue(servers.ContainsKey("wmux"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void MissingBrokenOrForeignConfigsAreLeftAlone()
    {
        var missing = TempConfig(null);
        Assert.IsFalse(ClaudeMcp.Register(missing, @"C:\armux\armux.com"));
        Assert.IsFalse(File.Exists(missing));                           // Claude Code not installed: never create its file

        var broken = TempConfig("{ not json");
        Assert.IsFalse(ClaudeMcp.Register(broken, @"C:\armux\armux.com"));
        Assert.AreEqual("{ not json", File.ReadAllText(broken));

        var noServers = TempConfig("""{ "numStartups": 1 }""");
        Assert.IsTrue(ClaudeMcp.Register(noServers, @"C:\armux\armux.com"));
        Assert.IsNotNull(JsonNode.Parse(File.ReadAllText(noServers))!["mcpServers"]!["armux"]);

        // An "armux" entry the user pointed at something else is not removed.
        var foreign = TempConfig("""{ "mcpServers": { "armux": { "command": "node", "args": ["x.js"] } } }""");
        Assert.IsFalse(ClaudeMcp.Unregister(foreign));
        foreach (var p in new[] { missing, broken, noServers, foreign }) Directory.Delete(Path.GetDirectoryName(p)!, true);
    }
}
