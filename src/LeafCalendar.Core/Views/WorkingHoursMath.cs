using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>The stretches of a day outside your working hours, in minutes from midnight on the clock on screen (end exclusive).</summary>
public static class WorkingHoursMath
{
    /// <summary>
    /// The work days in a few words, for Settings › General's work days button: "Weekdays", "Every day", "No days", or
    /// the short names in the week's order from <paramref name="weekStart"/> ("Sun, Fri").
    /// </summary>
    public static string DaysLabel(IReadOnlyCollection<DayOfWeek> days, DayOfWeek weekStart)
    {
        var set = days.ToHashSet();
        if (set.Count == 0)
        {
            return "No days";
        }

        if (set.Count == 7)
        {
            return "Every day";
        }

        if (set.Count == 5 && !set.Contains(DayOfWeek.Saturday) && !set.Contains(DayOfWeek.Sunday))
        {
            return "Weekdays";
        }

        var names = System.Globalization.CultureInfo.GetCultureInfo("en-US").DateTimeFormat;
        return string.Join(", ", Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)(((int)weekStart + i) % 7))
            .Where(set.Contains)
            .Select(names.GetAbbreviatedDayName));
    }

    /// <summary>
    /// Nothing when shading is off. Your working hours (in <paramref name="userZone"/>) are laid onto the day drawn in
    /// <paramref name="displayZone"/>, so while time traveling to Tokyo your 9 to 5 shows where it falls in Tokyo time;
    /// a working day can split across the drawn day's midnight. Everything else is shaded.
    /// </summary>
    /// <param name="hours">Your working hours (Leaf's own setting; Google doesn't expose them).</param>
    /// <param name="date">The day drawn.</param>
    /// <param name="userZone">Your own zone (primary, else Windows).</param>
    /// <param name="displayZone">The zone on screen.</param>
    /// <returns>Off-hours ranges in minutes from midnight, earliest first; empty ones dropped.</returns>
    public static IReadOnlyList<(int StartMinute, int EndMinute)> OffHours(WorkingHours hours, DateOnly date, TimeZoneInfo userZone, TimeZoneInfo displayZone)
    {
        if (!hours.Enabled)
        {
            return [];
        }

        // The Drawn Day As Instants
        var dayStart = OccurrenceQuery.LocalMidnight(date, displayZone);
        var dayEnd   = OccurrenceQuery.LocalMidnight(date.AddDays(1), displayZone);

        // Your Working Stretches From The Days Either Side That Can Overlap It, Clipped To The Drawn Day
        var work = new List<(int Start, int End)>();
        for (var offset = -2; offset <= 2; offset++)
        {
            var day = date.AddDays(offset);
            if (!hours.Days.Contains(day.DayOfWeek) || hours.EndMinute <= hours.StartMinute)
            {
                continue;
            }

            var start = OccurrenceQuery.LocalMidnight(day, userZone).AddMinutes(hours.StartMinute);
            var end   = OccurrenceQuery.LocalMidnight(day, userZone).AddMinutes(hours.EndMinute);
            start     = start < dayStart ? dayStart : start;
            end       = end > dayEnd ? dayEnd : end;
            if (end > start)
            {
                work.Add((Minutes(start - dayStart), Minutes(end - dayStart)));
            }
        }

        // Everything Between The Working Stretches Is Off Hours
        var off    = new List<(int, int)>();
        var cursor = 0;
        foreach (var (start, end) in work.OrderBy(w => w.Start))
        {
            if (start > cursor)
            {
                off.Add((cursor, start));
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < 1440)
        {
            off.Add((cursor, 1440));
        }

        return off;
    }

    // Whole minutes, capped to the grid's 24 hours (a daylight saving day is a little longer or shorter)
    static int Minutes(TimeSpan span) => Math.Clamp((int)Math.Round(span.TotalMinutes), 0, 1440);
}
