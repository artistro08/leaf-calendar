using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>Which events a Shift+drag box covers (spec 7.3).</summary>
public static class BoxSelection
{
    /// <summary>
    /// <see cref="InTimeBox(IEnumerable{CalendarOccurrence}, IEnumerable{DateOnly}, double, double, TimeZoneInfo)"/> over
    /// every calendar day from <paramref name="dayA"/> to <paramref name="dayB"/> (either may come first). A grid that
    /// hides days passes the days it shows instead.
    /// </summary>
    public static IReadOnlyList<CalendarOccurrence> InTimeBox(IEnumerable<CalendarOccurrence> occurrences, DateOnly dayA, DateOnly dayB, double minutesA, double minutesB, TimeZoneInfo zone)
    {
        var (firstDay, lastDay) = dayA <= dayB ? (dayA, dayB) : (dayB, dayA);

        return InTimeBox(occurrences, Enumerable.Range(0, lastDay.DayNumber - firstDay.DayNumber + 1).Select(firstDay.AddDays), minutesA, minutesB, zone);
    }

    /// <summary>
    /// Timed events on <paramref name="days"/> (the day columns the box covers, so a hidden weekend between its corners
    /// stays out) whose cards overlap the wall-clock band between <paramref name="minutesA"/> and
    /// <paramref name="minutesB"/> after midnight in <paramref name="zone"/>. Either corner may come first. A card is
    /// at least <see cref="DayLayout.MinVisualMinutes"/> tall, however short its event. Events of 24 hours or more
    /// are in the all-day row, not under the box.
    /// </summary>
    /// <remarks>
    /// ponytail: time-band hit test, not card rectangles. Overlapping events that share a band are both picked,
    /// even if the box covers only one's sub-column.
    /// </remarks>
    public static IReadOnlyList<CalendarOccurrence> InTimeBox(IEnumerable<CalendarOccurrence> occurrences, IEnumerable<DateOnly> days, double minutesA, double minutesB, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(days);

        var (from, to) = (Math.Min(minutesA, minutesB), Math.Max(minutesA, minutesB));
        var covered    = days.ToList();

        return [.. occurrences
            .Where(o => !SpanLayout.IsSpanning(o) && covered.Exists(day => Hits(o, day, from, to, zone)))
            .DistinctBy(o => o.Key)];
    }

    // The event's part of the day, in wall-clock minutes as the grid draws it, against the band. Wall-clock (not
    // instants), so both passes of the repeated fall-back hour, and a spring-forward gap, count where they're drawn
    static bool Hits(CalendarOccurrence o, DateOnly day, double from, double to, TimeZoneInfo zone)
    {
        var dayStart = DragMath.Instant(day, 0, zone);
        var dayEnd   = DragMath.Instant(day.AddDays(1), 0, zone);

        // A Zero-Minute Event Counts When The Band Touches Its Card (drawn at the least height)
        if (o.Start == o.End)
        {
            return o.Start >= dayStart && o.Start < dayEnd && DayLayout.DrawnStart(WallMinutes(o.Start, zone)) is var at && at + DayLayout.MinVisualMinutes > from && at <= to;
        }

        if (o.Start >= dayEnd || o.End <= dayStart)
        {
            return false;
        }

        // A Short Event's Card Is Drawn At The Least Height, Inside The Day
        var top    = o.Start <= dayStart ? 0 : DayLayout.DrawnStart(WallMinutes(o.Start, zone));
        var bottom = Math.Max(o.End >= dayEnd ? 24 * 60 : WallMinutes(o.End, zone), top + DayLayout.MinVisualMinutes);
        return top < to && bottom > from;
    }

    static double WallMinutes(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay.TotalMinutes;
}
