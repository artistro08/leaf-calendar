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
        var days                = Enumerable.Range(0, lastDay.DayNumber - firstDay.DayNumber + 1).Select(firstDay.AddDays).ToList();

        return [.. occurrences
            .Where(o => !o.IsAllDay && days.Exists(day => Hits(o, day, from, to, zone)))
            .DistinctBy(o => o.Key)];
    }

    // The event's part of the day, in wall-clock minutes as the grid draws it, against the band. Wall-clock (not
    // instants), so both passes of the repeated fall-back hour, and a spring-forward gap, count where they're drawn
    static bool Hits(CalendarOccurrence o, DateOnly day, double from, double to, TimeZoneInfo zone)
    {
        var dayStart = DragMath.Instant(day, 0, zone);
        var dayEnd   = DragMath.Instant(day.AddDays(1), 0, zone);

        // A Zero-Minute Event Counts When It Sits Inside The Band
        if (o.Start == o.End)
        {
            return o.Start >= dayStart && o.Start < dayEnd && WallMinutes(o.Start, zone) is var at && at >= from && at <= to;
        }

        if (o.Start >= dayEnd || o.End <= dayStart)
        {
            return false;
        }

        var top    = o.Start <= dayStart ? 0 : WallMinutes(o.Start, zone);
        var bottom = o.End >= dayEnd ? 24 * 60 : WallMinutes(o.End, zone);
        return top < to && bottom > from;
    }

    static double WallMinutes(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay.TotalMinutes;
}
