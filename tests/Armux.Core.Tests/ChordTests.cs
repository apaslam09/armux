using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public sealed class ChordTests
{
    [TestMethod]
    public void TextEditingKeysStayWithTextBoxesAndPages()
    {
        foreach (var c in new[] { "Ctrl+V", "Ctrl+C", "Ctrl+Left", "Ctrl+Shift+Right", "Ctrl+Shift+Home", "Shift+Insert", "Ctrl+Insert", "Ctrl+Z", "Ctrl+Shift+Z", "Shift+Enter" })
            Assert.IsTrue(Chord.Parse(c).IsTextEditing, c);
        foreach (var c in new[] { "Ctrl+Shift+V", "Ctrl+Shift+C", "Ctrl+Tab", "Ctrl+Shift+T", "Ctrl+Alt+Left", "Alt+Shift+Down", "Ctrl+Alt+Shift+Enter", "Ctrl+PageUp", "F6", "Ctrl+," })
            Assert.IsFalse(Chord.Parse(c).IsTextEditing, c);
    }

    [TestMethod]
    public void PageCodesRoundTrip()
    {
        Assert.AreEqual("101T", Chord.Parse("Ctrl+Shift+T").Code);
        Assert.AreEqual("000F11", Chord.Parse("F11").Code);
        foreach (var c in new[] { "Ctrl+Shift+Tab", "Ctrl+=", "Alt+Shift+]", "Ctrl+Alt+Shift+Enter", "Ctrl+," })
            Assert.AreEqual(Chord.Parse(c), Chord.FromCode(Chord.Parse(c).Code));
        Assert.IsNull(Chord.FromCode("hello"));
        Assert.IsNull(Chord.FromCode("10"));
    }
}
