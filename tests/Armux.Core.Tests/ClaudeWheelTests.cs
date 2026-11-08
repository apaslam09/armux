using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public class ClaudeWheelTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "armux-cw-" + Guid.NewGuid().ToString("N"), "settings.json");

    [TestMethod]
    public void MissingFileMeansAccelerationOn()
    {
        Assert.IsFalse(ClaudeWheel.AccelerationOff(TempFile()));
    }

    [TestMethod]
    public void SetKeepsOtherKeysAndBacksUpOnce()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"model\": \"opus\", // comment\n \"env\": { \"A\": \"1\" } }");
        ClaudeWheel.SetAccelerationOff(true, path);
        Assert.IsTrue(ClaudeWheel.AccelerationOff(path));
        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "\"model\": \"opus\"");
        StringAssert.Contains(text, "\"A\": \"1\"");
        Assert.IsTrue(File.Exists(path + ".armux-bak"));
        StringAssert.Contains(File.ReadAllText(path + ".armux-bak"), "// comment");

        ClaudeWheel.SetAccelerationOff(false, path);
        Assert.IsFalse(ClaudeWheel.AccelerationOff(path));
        Assert.IsFalse(File.ReadAllText(path).Contains(ClaudeWheel.Key));
        StringAssert.Contains(File.ReadAllText(path + ".armux-bak"), "// comment");
    }

    [TestMethod]
    public void CreatesTheFileWhenMissingAndRefusesInvalidJson()
    {
        var path = TempFile();
        ClaudeWheel.SetAccelerationOff(true, path);
        Assert.IsTrue(ClaudeWheel.AccelerationOff(path));

        var bad = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "{ not json");
        Assert.Throws<System.Text.Json.JsonException>(() => ClaudeWheel.SetAccelerationOff(true, bad));
        Assert.AreEqual("{ not json", File.ReadAllText(bad));
    }
}
