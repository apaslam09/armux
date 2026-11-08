namespace Armux.Core;

/// <summary>
/// Finds out whether a redraw was a scroll (D-45): the rows of the new screen that equal other rows of the old screen
/// moved by a fixed number of rows. The view animates those rows from their old place, so scrolling looks smooth even
/// when a program (Claude Code) redraws the screen in jumps. Blank rows (hash 0) never count as a match.
/// </summary>
public static class ScrollShift
{
    /// <summary>Fewest moved rows that count as a scroll.</summary>
    public const int MinRows = 3;

    /// <summary>
    /// <c>Shift</c> is how many rows content moved up (positive) or down (negative). <c>First</c>..<c>Last</c> is the
    /// scrolled band, including the rows that scrolled in; <c>MovedFirst</c>..<c>MovedLast</c> are the rows that have
    /// an old position (the others in the band are new content).
    /// </summary>
    public readonly record struct Result(int Shift, int First, int Last, int MovedFirst, int MovedLast);

    public static Result? Find(int[] oldHash, int[] newHash)
    {
        int rows = newHash.Length;
        if (oldHash.Length != rows || rows < 2 * MinRows) return null;
        int bestK = 0, bestCount = 0;
        for (int k = -(rows / 2); k <= rows / 2; k++)
        {
            if (k == 0) continue;
            int count = 0;
            for (int r = 0; r < rows; r++)
            {
                int o = r + k;
                if (o < 0 || o >= rows) continue;
                if (newHash[r] != 0 && newHash[r] == oldHash[o] && newHash[r] != oldHash[r]) count++;
            }
            if (count > bestCount) { bestCount = count; bestK = k; }
        }
        if (bestCount < MinRows) return null;

        int first = -1, last = -1;
        for (int r = 0; r < rows; r++)
        {
            int o = r + bestK;
            if (o < 0 || o >= rows) continue;
            if (newHash[r] != 0 && newHash[r] == oldHash[o] && newHash[r] != oldHash[r]) { if (first < 0) first = r; last = r; }
        }
        // Rows that scrolled in sit at the end the content moved away from.
        int from = first, to = last;
        if (bestK > 0) to = Math.Min(rows - 1, last + bestK);
        else from = Math.Max(0, first + bestK);
        return new Result(bestK, from, to, first, last);
    }
}
