using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public class ScrollShiftTests
{
    [TestMethod]
    public void WholeScreenScrollsUp()
    {
        int[] old = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        int[] now = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var r = ScrollShift.Find(old, now)!.Value;
        Assert.AreEqual(2, r.Shift);
        Assert.AreEqual(0, r.First);
        Assert.AreEqual(9, r.Last);
        Assert.AreEqual(7, r.MovedLast);
    }

    [TestMethod]
    public void WholeScreenScrollsDown()
    {
        int[] old = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        int[] now = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var r = ScrollShift.Find(old, now)!.Value;
        Assert.AreEqual(-2, r.Shift);
        Assert.AreEqual(0, r.First);
        Assert.AreEqual(2, r.MovedFirst);
        Assert.AreEqual(9, r.Last);
    }

    [TestMethod]
    public void FixedFooterStaysOutOfTheBand()
    {
        int[] old = [1, 2, 3, 4, 5, 6, 7, 8, 90, 91];
        int[] now = [2, 3, 4, 5, 6, 7, 8, 9, 90, 91];
        var r = ScrollShift.Find(old, now)!.Value;
        Assert.AreEqual(1, r.Shift);
        Assert.AreEqual(0, r.First);
        Assert.AreEqual(7, r.Last);
    }

    [TestMethod]
    public void BlankRowsAndSmallChangesAreNotScrolls()
    {
        int[] blank = [0, 0, 0, 0, 0, 0, 0, 0];
        Assert.IsNull(ScrollShift.Find(blank, blank));
        int[] old = [1, 2, 3, 4, 5, 6, 7, 8];
        int[] spinner = [1, 2, 3, 4, 5, 6, 7, 99];
        Assert.IsNull(ScrollShift.Find(old, spinner));
        Assert.IsNull(ScrollShift.Find(old, old));
    }
}
