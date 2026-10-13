using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Scheduling;

/// <summary>What fitting a pick to a link's bookable times did, for the messages over the calendar.</summary>
/// <param name="Kept">Bookable times kept.</param>
/// <param name="Minutes">Length of a kept time in minutes (0 when none was kept).</param>
/// <param name="Stretched">Times picked more than half and stretched to their full length.</param>
/// <param name="Removed">Times picked half or less and removed.</param>
/// <param name="NotOpenMinutes">Minutes of the pick with no bookable time at all.</param>
public sealed record FitSummary(int Kept, int Minutes, int Stretched, int Removed, int NotOpenMinutes)
{
    /// <summary>Nothing fitted.</summary>
    public static FitSummary None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Both summaries together (the first nonzero length wins).</summary>
    /// <param name="other">The summary to add to this one.</param>
    /// <returns>The combined summary.</returns>
    public FitSummary Add(FitSummary other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Kept + other.Kept, Minutes != 0 ? Minutes : other.Minutes, Stretched + other.Stretched, Removed + other.Removed, NotOpenMinutes + other.NotOpenMinutes);
    }
}

/// <summary>A pick fitted to bookable times: the ranges to keep (merged, in order) and what happened.</summary>
/// <param name="Ranges">The kept ranges, merged and in start order.</param>
/// <param name="Summary">The counts for the messages over the calendar.</param>
public sealed record FitResult(IReadOnlyList<BusyRange> Ranges, FitSummary Summary);

/// <summary>
/// Fits picked times to a scheduling link's bookable times (spec 3.5), and splits saved ranges into bookable times for
/// the copied links.
/// </summary>
/// <remarks>
/// All math is on instants, so DST days and midnight need no special cases. A bookable time the pick covers fully is
/// kept; one it covers more than half of (strictly) is stretched to its full length; one covered half or less is
/// removed. Bookable times are taken earliest first, and one that overlaps a kept time is skipped, so a link that offers
/// 30-minute times every 15 minutes never doubles up.
/// </remarks>
public static class SlotFitting
{
    /// <summary>Fits one dragged range to <paramref name="open"/> (the link's bookable times, any order).</summary>
    /// <param name="drag">The range the person picked.</param>
    /// <param name="open">The link's bookable times, in any order.</param>
    /// <returns>The ranges to keep and the counts for the messages.</returns>
    public static FitResult Fit(BusyRange drag, IReadOnlyList<BusyRange> open)
    {
        ArgumentNullException.ThrowIfNull(drag);
        ArgumentNullException.ThrowIfNull(open);

        var touching = open.Where(s => s.End > drag.Start && s.Start < drag.End).OrderBy(s => s.Start).ToList();

        // Kept: Whole Or More Than Half, Earliest First, No Overlaps
        var kept = new List<BusyRange>();
        var stretched = 0;
        foreach (var slot in touching)
        {
            if (kept.Count > 0 && slot.Start < kept[^1].End)
            {
                continue;
            }

            var covered = Covered(slot, drag);
            var length = slot.End - slot.Start;
            if (covered >= length)
            {
                kept.Add(slot);
            }
            else if (covered * 2 > length)
            {
                kept.Add(slot);
                stretched++;
            }
        }

        // Removed: Too Little Picked, Counted Once Per Stretch That No Kept Time Covers
        var removed = new List<BusyRange>();
        foreach (var slot in touching)
        {
            var covered = Covered(slot, drag);
            if (covered * 2 > slot.End - slot.Start || kept.Any(k => Overlaps(k, slot)) || removed.Any(r => Overlaps(r, slot)))
            {
                continue;
            }

            removed.Add(slot);
        }

        // Not Open: Parts Of The Pick No Bookable Time Touches
        var openCovered = BusyMath.Merge(touching).Sum(s => Covered(s, drag).TotalMinutes);
        var notOpen = (int)Math.Round((drag.End - drag.Start).TotalMinutes - openCovered);

        var minutes = kept.Count > 0 ? (int)(kept[0].End - kept[0].Start).TotalMinutes : 0;
        return new FitResult(BusyMath.Merge(kept), new FitSummary(kept.Count, minutes, stretched, removed.Count, notOpen));
    }

    /// <summary>Fits every range (a saved group's times after a link change), with one summary for all of them.</summary>
    /// <param name="ranges">The ranges to fit.</param>
    /// <param name="open">The link's bookable times, in any order.</param>
    /// <returns>The merged ranges to keep and one summary for all of them.</returns>
    public static FitResult FitAll(IReadOnlyList<BusyRange> ranges, IReadOnlyList<BusyRange> open)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var all = new List<BusyRange>();
        var summary = FitSummary.None;
        foreach (var range in ranges)
        {
            var fit = Fit(range, open);
            all.AddRange(fit.Ranges);
            summary = summary.Add(fit.Summary);
        }

        return new FitResult(BusyMath.Merge(all), summary);
    }

    /// <summary>
    /// The bookable times inside a saved range, earliest first without overlap: the open times that fit wholly inside it,
    /// or, with <paramref name="open"/> null (Leaf couldn't ask), steps of <paramref name="minutes"/> from its start.
    /// </summary>
    /// <param name="range">The saved range to split.</param>
    /// <param name="open">The link's bookable times, or null when they couldn't be fetched.</param>
    /// <param name="minutes">The step length in minutes, used only when <paramref name="open"/> is null.</param>
    /// <returns>The pieces, earliest first and without overlap.</returns>
    public static IReadOnlyList<BusyRange> Split(BusyRange range, IReadOnlyList<BusyRange>? open, int minutes)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);

        var pieces = new List<BusyRange>();
        if (open is null)
        {
            var step = TimeSpan.FromMinutes(minutes);
            for (var t = range.Start; t + step <= range.End; t += step)
            {
                pieces.Add(new BusyRange(t, t + step));
            }

            return pieces;
        }

        foreach (var slot in open.Where(s => s.Start >= range.Start && s.End <= range.End).OrderBy(s => s.Start))
        {
            if (pieces.Count == 0 || slot.Start >= pieces[^1].End)
            {
                pieces.Add(slot);
            }
        }

        return pieces;
    }

    /// <summary>How much of a bookable time the pick covers.</summary>
    /// <param name="slot">The bookable time.</param>
    /// <param name="drag">The picked range.</param>
    /// <returns>The overlap length, or zero when they don't overlap.</returns>
    private static TimeSpan Covered(BusyRange slot, BusyRange drag)
    {
        var start = slot.Start > drag.Start ? slot.Start : drag.Start;
        var end = slot.End < drag.End ? slot.End : drag.End;
        return end > start ? end - start : TimeSpan.Zero;
    }

    /// <summary>Whether two ranges share any time.</summary>
    /// <param name="a">The first range.</param>
    /// <param name="b">The second range.</param>
    /// <returns>True when they overlap.</returns>
    private static bool Overlaps(BusyRange a, BusyRange b) => a.Start < b.End && b.Start < a.End;
}
