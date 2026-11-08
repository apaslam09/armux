using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public class WheelPacerTests
{
    const long Start = 1_000_000;

    [TestMethod]
    public void FirstNotchGoesOutAtOnce()
    {
        Assert.AreEqual(1, new WheelPacer().Add(120, Start));
        Assert.AreEqual(-1, new WheelPacer().Add(-120, Start));
    }

    [TestMethod]
    public void SlowNotchesEachGoOutAtOnce()
    {
        var p = new WheelPacer();
        for (int i = 0; i < 5; i++) Assert.AreEqual(1, p.Add(120, Start + i * WheelPacer.MinGapMs));
    }

    [TestMethod]
    public void FastNotchesWaitForTheGapAndGoTogether()
    {
        var p = new WheelPacer();
        Assert.AreEqual(1, p.Add(120, Start));
        Assert.AreEqual(0, p.Add(120, Start + 20));
        Assert.AreEqual(0, p.Add(120, Start + 40));
        Assert.AreEqual(WheelPacer.MinGapMs - 40, p.WaitMs(Start + 40));
        Assert.AreEqual(0, p.Take(Start + 99));
        Assert.AreEqual(2, p.Take(Start + 100));
        Assert.AreEqual(-1, p.WaitMs(Start + 100));
    }

    /// <summary>A flick at 120 events a second: batches never come closer than the gap, and never exceed the cap.</summary>
    [TestMethod]
    public void AFlickIsCappedAndNeverBackToBack()
    {
        var p = new WheelPacer();
        long last = -1;
        int total = 0;
        for (long t = 0; t < 1000; t += 8)
        {
            int n = p.Add(480, Start + t);
            if (n == 0) continue;
            Assert.IsTrue(n <= WheelPacer.MaxBatch);
            if (last >= 0) Assert.IsTrue(t - last >= WheelPacer.MinGapMs, $"{t - last} ms apart");
            last = t;
            total += n;
        }
        Assert.IsTrue(total <= 11 * WheelPacer.MaxBatch, $"{total} notches in a second");
    }

    /// <summary>What did not fit is gone: at most one batch follows the last wheel event.</summary>
    [TestMethod]
    public void NothingQueuesAfterTheWheelStops()
    {
        var p = new WheelPacer();
        p.Add(120, Start);
        for (int i = 1; i <= 50; i++) p.Add(120, Start + i);
        Assert.AreEqual(WheelPacer.MaxBatch, p.Take(Start + 150));
        Assert.AreEqual(-1, p.WaitMs(Start + 150));
        Assert.AreEqual(0, p.Take(Start + 1000));
    }

    [TestMethod]
    public void TouchpadFractionsAddUp()
    {
        var p = new WheelPacer();
        for (int i = 0; i < 3; i++) Assert.AreEqual(0, p.Add(30, Start + i));
        Assert.AreEqual(-1, p.WaitMs(Start + 3));
        Assert.AreEqual(1, p.Add(30, Start + 3));
    }

    [TestMethod]
    public void ChangingDirectionDropsWhatWaitedAndGoesAtOnce()
    {
        var p = new WheelPacer();
        Assert.AreEqual(1, p.Add(120, Start));
        Assert.AreEqual(0, p.Add(240, Start + 10));
        Assert.AreEqual(-1, p.Add(-120, Start + 20));
        Assert.AreEqual(-1, p.WaitMs(Start + 20));
        Assert.AreEqual(0, p.Take(Start + 500));
    }
}
