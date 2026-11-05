namespace Armux.Core;

/// <summary>
/// Paces wheel reports to programs that track the mouse (D-42). Some programs speed up with the rate of wheel events:
/// Claude Code on Windows scrolls up to 36 rows per event when events come 5-80 ms apart, so a slightly fast wheel or a
/// touchpad flick ran to the top of the conversation. Here a notch goes out at once, faster notches are sent together
/// at most once per <see cref="MinGapMs"/>, and whatever does not fit in a batch is dropped: scrolling stops when the
/// fingers stop, because nothing queues up.
/// </summary>
public sealed class WheelPacer
{
    /// <summary>One wheel notch in Windows' delta units. Touchpads send fractions of it.</summary>
    public const int Notch = 120;
    /// <summary>Above the 80 ms where Claude Code stops accelerating, with room for timer and pipe jitter.</summary>
    public const int MinGapMs = 100;
    /// <summary>The most notches in a batch, which sets the top scroll speed (about 80 rows a second in Claude Code).</summary>
    public const int MaxBatch = 6;

    int pending, lastDirection;
    long lastSent;

    /// <summary>Adds a wheel delta (positive is up). Returns the notches to report now, signed; 0 for none yet.</summary>
    public int Add(int delta, long nowMs)
    {
        if (pending != 0 && Math.Sign(delta) != Math.Sign(pending)) pending = 0;
        pending = Math.Clamp(pending + delta, -MaxBatch * Notch, MaxBatch * Notch);
        return Take(nowMs);
    }

    /// <summary>The notches that are due now, signed. A change of direction does not wait for the gap.</summary>
    public int Take(long nowMs)
    {
        int notches = pending / Notch;
        if (notches == 0) return 0;
        int direction = Math.Sign(notches);
        if (direction == lastDirection && nowMs - lastSent < MinGapMs) return 0;
        pending -= notches * Notch;
        lastDirection = direction;
        lastSent = nowMs;
        return notches;
    }

    /// <summary>Milliseconds until a waiting batch is due, or -1 when nothing is waiting.</summary>
    public int WaitMs(long nowMs) => Math.Abs(pending) < Notch ? -1 : (int)Math.Max(0, lastSent + MinGapMs - nowMs);
}
