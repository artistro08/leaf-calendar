using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.People;

/// <summary>Arithmetic on busy and free stretches (overlays and share availability).</summary>
public static class BusyMath
{
    /// <summary>Sorted, with overlapping and touching ranges joined; empty and backwards ranges dropped.</summary>
    /// <param name="ranges">Any ranges, in any order.</param>
    /// <returns>Disjoint ranges in start order.</returns>
    public static IReadOnlyList<BusyRange> Merge(IEnumerable<BusyRange> ranges)
    {
        var merged = new List<BusyRange>();
        foreach (var r in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            // Overlapping Or Touching: Extend The Last One
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = r.End > merged[^1].End ? r.End : merged[^1].End };
                continue;
            }

            merged.Add(r);
        }

        return merged;
    }

    /// <summary>The parts of <paramref name="wanted"/> not covered by <paramref name="busy"/>.</summary>
    /// <param name="wanted">The stretches to keep the free parts of (merged first).</param>
    /// <param name="busy">The stretches to cut out.</param>
    /// <returns>Disjoint free ranges in start order.</returns>
    public static IReadOnlyList<BusyRange> Subtract(IEnumerable<BusyRange> wanted, IEnumerable<BusyRange> busy)
    {
        var blocks = Merge(busy);
        var free = new List<BusyRange>();

        foreach (var slot in Merge(wanted))
        {
            // Walk The Busy Blocks Inside This Slot, Keeping The Gaps
            var cursor = slot.Start;
            foreach (var b in blocks.Where(b => b.End > slot.Start && b.Start < slot.End))
            {
                if (b.Start > cursor)
                {
                    free.Add(new BusyRange(cursor, b.Start));
                }

                cursor = b.End > cursor ? b.End : cursor;
            }

            if (cursor < slot.End)
            {
                free.Add(new BusyRange(cursor, slot.End));
            }
        }

        return free;
    }
}
