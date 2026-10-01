using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>Which events a Shift+drag box covers (spec 7.3).</summary>
public static class BoxSelection
{
    /// <summary>
    /// Timed events on the days between <paramref name="dayA"/> and <paramref name="dayB"/> that overlap the wall-clock
    /// band between <paramref name="minutesA"/> and <paramref name="minutesB"/> after midnight in <paramref name="zone"/>.
    /// Either corner may come first. A zero-minute event counts when it sits inside the band.
    /// </summary>
    /// <remarks>
    /// ponytail: time-band hit test, not card rectangles. Overlapping events that share a band are both picked,
    /// even if the box covers only one's sub-column.
    /// </remarks>
    public static IReadOnlyList<CalendarOccurrence> InTimeBox(IEnumerable<CalendarOccurrence> occurrences, DateOnly dayA, DateOnly dayB, double minutesA, double minutesB, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        var (firstDay, lastDay) = dayA <= dayB ? (dayA, dayB) : (dayB, dayA);
        var (from, to)          = (Math.Min(minutesA, minutesB), Math.Max(minutesA, minutesB));
        var bands               = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        // One Band Per Covered Day
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            bands.Add((DragMath.Instant(day, from, zone), DragMath.Instant(day, to, zone)));
        }

        return [.. occurrences
            .Where(o => !o.IsAllDay)
            .Where(o => bands.Exists(b => o.Start == o.End ? o.Start >= b.Start && o.Start <= b.End : o.Start < b.End && o.End > b.Start))
            .DistinctBy(o => o.Key)];
    }
}
