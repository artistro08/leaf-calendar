namespace LeafCalendar.Core.Sunsama;

/// <summary>How much of each block the striped tracked-time overlay covers (spec 2026-10-16, section 6.3.1).</summary>
public static class SunsamaTracked
{
    /// <summary>
    /// Spreads a task's tracked minutes over its blocks in time order: each block takes up to its own length and the rest
    /// carries to the next, so tracked time past the blocks' total leaves every block full. Sunsama gives no per-day split,
    /// so each day's blocks are filled from the task's whole total. A block that hasn't started by <paramref name="now"/>
    /// holds none of it: time can't have been tracked there yet (a task worked on today and planned again tomorrow).
    /// </summary>
    /// <param name="blocksOfOneTaskOnOneDay">One task's blocks on one day, in any order; they share one tracked total.</param>
    /// <param name="now">The current time, or null to fill every block.</param>
    /// <returns>A fraction from 0 to 1 for each block, in the input order.</returns>
    public static IReadOnlyList<double> Fill(IReadOnlyList<SunsamaBlock> blocksOfOneTaskOnOneDay, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(blocksOfOneTaskOnOneDay);

        var fills = new double[blocksOfOneTaskOnOneDay.Count];
        double remaining = blocksOfOneTaskOnOneDay.Count > 0 ? blocksOfOneTaskOnOneDay[0].TrackedMinutes ?? 0 : 0;
        foreach (var i in Enumerable.Range(0, fills.Length).OrderBy(i => blocksOfOneTaskOnOneDay[i].Start))
        {
            if (blocksOfOneTaskOnOneDay[i].Start > now)
            {
                continue;
            }

            var length = (blocksOfOneTaskOnOneDay[i].End - blocksOfOneTaskOnOneDay[i].Start).TotalMinutes;
            var take = Math.Max(0, Math.Min(remaining, length));
            fills[i] = length > 0 ? take / length : 0;
            remaining -= take;
        }

        return fills;
    }
}
