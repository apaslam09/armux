using Armux.Core.Vt;

namespace Armux.Core.Tests;

[TestClass]
public sealed class KeyEncoderTests
{
    [TestMethod]
    public void CursorKeysFollowApplicationModeAndModifiers()
    {
        Assert.AreEqual("\u001b[A", KeyEncoder.Encode(TermKey.Up, false, false, false, appCursorKeys: false));
        Assert.AreEqual("\u001bOA", KeyEncoder.Encode(TermKey.Up, false, false, false, appCursorKeys: true));
        Assert.AreEqual("\u001b[1;5C", KeyEncoder.Encode(TermKey.Right, false, false, ctrl: true, appCursorKeys: true));   // Ctrl+Right: word jump
        Assert.AreEqual("\u001b[1;2H", KeyEncoder.Encode(TermKey.Home, shift: true, false, false, false));
    }

    [TestMethod]
    public void EditingAndFunctionKeys()
    {
        Assert.AreEqual("\u001b[3~", KeyEncoder.Encode(TermKey.Delete, false, false, false, false));
        Assert.AreEqual("\u001b[5;5~", KeyEncoder.Encode(TermKey.PageUp, false, false, true, false));
        Assert.AreEqual("\u001bOP", KeyEncoder.Encode(TermKey.F1, false, false, false, false));
        Assert.AreEqual("\u001b[24~", KeyEncoder.Encode(TermKey.F12, false, false, false, false));
        Assert.AreEqual("\u001b[15;3~", KeyEncoder.Encode(TermKey.F5, false, alt: true, false, false));
    }

    [TestMethod]
    public void TabBackspaceAndText()
    {
        Assert.AreEqual("\t", KeyEncoder.Encode(TermKey.Tab, false, false, false, false));
        Assert.AreEqual("\u001b[Z", KeyEncoder.Encode(TermKey.Tab, shift: true, false, false, false));
        Assert.AreEqual("\u007f", KeyEncoder.Encode(TermKey.Backspace, false, false, false, false));
        Assert.AreEqual("\b", KeyEncoder.Encode(TermKey.Backspace, false, false, ctrl: true, false));
        Assert.AreEqual("\u001b\u007f", KeyEncoder.Encode(TermKey.Backspace, false, alt: true, false, false));
        Assert.AreEqual("\u001bb", KeyEncoder.Text("b", alt: true, ctrl: false));   // Alt+B: word back in readline
        Assert.AreEqual("@", KeyEncoder.Text("@", alt: true, ctrl: true));        // AltGr+Q on a German layout
    }

    [TestMethod]
    public void ShiftEnterIsDistinctOnlyInFullScreenPrograms()
    {
        Assert.IsNull(KeyEncoder.ShiftEnter(altScreen: false, MouseTracking.None));
        Assert.AreEqual("\u001b[13;2u", KeyEncoder.ShiftEnter(altScreen: true, MouseTracking.None));
        Assert.AreEqual("\u001b[13;2u", KeyEncoder.ShiftEnter(altScreen: false, MouseTracking.Any));
    }
}
