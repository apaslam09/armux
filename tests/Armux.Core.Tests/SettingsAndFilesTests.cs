using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public sealed class SettingsAndFilesTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "armux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void SettingsRoundTripAndDefaults()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        Assert.AreEqual(new Settings(), Settings.Load(path));   // missing file gives defaults

        var s = new Settings { TerminalFont = "Cascadia Code", TerminalFontSize = 15.5, UiFont = "Segoe UI" };
        s.Save(path);
        Assert.AreEqual(s, Settings.Load(path));

        // A file with only some settings keeps the defaults for the rest.
        File.WriteAllText(path, """{ "terminalFont": "Cascadia Mono" }""");
        Assert.AreEqual(new Settings { TerminalFont = "Cascadia Mono" }, Settings.Load(path));

        File.WriteAllText(path, "{ not json");
        Assert.AreEqual(new Settings(), Settings.Load(path));
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void TryParseReportsErrorsAndAcceptsHandEditedJson()
    {
        Assert.IsFalse(Settings.TryParse("{\n  \"theme\": \"dark\",\n  oops\n}", out _, out var error));
        StringAssert.StartsWith(error, "Line 3");
        // Comments, trailing commas and any casing are fine in a file typed by hand.
        Assert.IsTrue(Settings.TryParse("{\n  // dark please\n  \"Theme\": \"dark\",\n  \"terminalFontSize\": 15,\n}", out var s, out error));
        Assert.IsNull(error);
        Assert.AreEqual(new Settings { Theme = "dark", TerminalFontSize = 15 }, s);
    }

    [TestMethod]
    public void DropPathIsRelativeUnlessItClimbsFar()
    {
        var cwd = @"C:\Users\me\dev\armux";
        Assert.AreEqual(@"src\app.cs", FileTree.DropPath(cwd, @"C:\Users\me\dev\armux\src\app.cs"));
        Assert.AreEqual(@"..\notes.md", FileTree.DropPath(cwd, @"C:\Users\me\dev\notes.md"));
        Assert.AreEqual(@"..\..\Downloads\a.png", FileTree.DropPath(cwd, @"C:\Users\me\Downloads\a.png"));
        Assert.AreEqual(@"C:\Windows\notepad.exe", FileTree.DropPath(cwd, @"C:\Windows\notepad.exe"));   // four levels up: absolute
        Assert.AreEqual(@"D:\data\x.csv", FileTree.DropPath(cwd, @"D:\data\x.csv"));                      // another drive
        Assert.AreEqual(@"D:\data\x.csv", FileTree.DropPath(null, @"D:\data\x.csv"));
    }

    [TestMethod]
    public void SettingsNormalizeBlankNamesAndSizes()
    {
        var s = new Settings { TerminalFont = "  ", UiFont = "", TerminalFontSize = 99 }.Normalized();
        Assert.AreEqual(Settings.DefaultTerminalFont, s.TerminalFont);
        Assert.AreEqual(Settings.DefaultUiFont, s.UiFont);
        Assert.AreEqual(Settings.MaxFontSize, s.TerminalFontSize);
        Assert.AreEqual(13.5, new Settings().WithTerminalFontSize(13.4).TerminalFontSize);
        Assert.AreEqual(Settings.MinFontSize, new Settings().WithTerminalFontSize(1).TerminalFontSize);
    }

    [TestMethod]
    public void TreeListsFoldersFirstSkipsGitAndExpands()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "");
        File.WriteAllText(Path.Combine(root, "README.md"), "");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "");

        var tree = new FileTree(root);
        CollectionAssert.AreEqual(new[] { "src", ".gitignore", "README.md" }, tree.Rows().Select(r => r.Entry!.Name).ToArray());

        tree.Toggle(Path.Combine(root, "src"));
        var rows = tree.Rows();
        Assert.AreEqual("a.cs", rows[1].Entry!.Name);
        Assert.AreEqual(1, rows[1].Depth);

        // Cached until invalidated.
        File.WriteAllText(Path.Combine(root, "src", "b.cs"), "");
        Assert.HasCount(4, tree.Rows());
        tree.Invalidate(Path.Combine(root, "src"));
        Assert.HasCount(5, tree.Rows());

        tree.CollapseAll();
        Assert.HasCount(3, tree.Rows());
        Directory.Delete(root, true);
    }

    [TestMethod]
    public void TreeTruncatesLargeFolders()
    {
        var root = TempDir();
        for (int i = 0; i < FileTree.MaxEntries + 3; i++) File.WriteAllText(Path.Combine(root, $"f{i:D4}.txt"), "");
        var rows = new FileTree(root).Rows();
        Assert.HasCount(FileTree.MaxEntries + 1, rows);
        Assert.IsNull(rows[^1].Entry);
        Assert.AreEqual(3, rows[^1].More);
        Directory.Delete(root, true);
    }

    [TestMethod]
    public void ResolveExpandsHomeAndFallsBackToExistingParent()
    {
        var home = TempDir();
        Directory.CreateDirectory(Path.Combine(home, "dev", "armux"));

        Assert.AreEqual(Path.Combine(home, "dev", "armux"), FileTree.Resolve("~\\dev\\armux", home, out var exact));
        Assert.IsTrue(exact);
        Assert.AreEqual(Path.Combine(home, "dev", "armux"), FileTree.Resolve("~/dev/armux/web", home, out exact));
        Assert.IsFalse(exact);
        Assert.AreEqual(home, FileTree.Resolve("~", home, out exact));
        Assert.IsTrue(exact);
        Directory.Delete(home, true);
    }
}
