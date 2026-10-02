using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>
/// Turns pointer positions into event times for dragging, resizing, creating, and pasting.
/// </summary>
/// <remarks>
/// The grid draws every day as 24 equal hours of wall-clock time, so a position is minutes past local
/// midnight. Times snap to 15 minutes. A wall-clock time in a spring-forward gap moves to the first real time
/// after it. Moving by whole days keeps the wall-clock time across a DST change.
/// </remarks>
public static class DragMath
{
    /// <summary>Snap step.</summary>
    public const int SnapMinutes = 15;

    /// <summary>Length of an event created by a click (double-click, or a slot dropped from the all-day row).</summary>
    public static readonly TimeSpan DefaultLength = TimeSpan.FromHours(1);

    /// <summary>The instant <paramref name="minutes"/> past local midnight of <paramref name="day"/> (0 to 1440, unsnapped).</summary>
    public static DateTimeOffset Instant(DateOnly day, double minutes, TimeZoneInfo zone) =>
        ToInstant(day.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Clamp(minutes, 0, 24 * 60)), zone);

    /// <summary>
    /// The nearest quarter hour on the wall clock. Snapping happens in offset space, so a time in the repeated
    /// fall-back hour stays in the same (daylight or standard) half of it.
    /// </summary>
    public static DateTimeOffset Snap(DateTimeOffset instant, TimeZoneInfo zone) =>
        RoundToStep(instant, zone, x => Math.Round(x, MidpointRounding.AwayFromZero));

    /// <summary>
    /// A wall-clock time in <paramref name="zone"/> as an instant. A time in a spring-forward gap moves forward out
    /// of it; a time in the repeated fall-back hour takes the earlier (daylight) offset.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone) => ToInstant(local, zone, null);

    /// <summary>
    /// Like <see cref="ToInstant(DateTime, TimeZoneInfo)"/>, but a time in the repeated fall-back hour keeps
    /// <paramref name="preferredOffset"/> when it is one of the two valid offsets.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone, TimeSpan? preferredOffset)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall))
        {
            wall = wall.AddMinutes(SnapMinutes);
        }

        // Repeated Hour: Keep The Caller's Offset If Valid, Otherwise The Earlier (Daylight) One
        if (zone.IsAmbiguousTime(wall))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(wall);
            var offset  = preferredOffset is { } p && offsets.Contains(p) ? p : offsets.Max();
            return new DateTimeOffset(wall, offset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }

    /// <summary>Where a timed event lands when the point grabbed at <paramref name="grabbedAt"/> is dropped at <paramref name="pointerAt"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) MoveTimed(CalendarOccurrence o, DateTimeOffset grabbedAt, DateTimeOffset pointerAt, TimeZoneInfo zone)
    {
        var start = Snap(o.Start + (pointerAt - grabbedAt), zone);
        return (start, start + (o.End - o.Start));
    }

    /// <summary>The new end when the bottom edge is dragged (at least one snap step after the start).</summary>
    public static DateTimeOffset ResizeEnd(CalendarOccurrence o, DateTimeOffset pointerAt, TimeZoneInfo zone)
    {
        var end = Snap(pointerAt, zone);
        var min = o.Start + TimeSpan.FromMinutes(SnapMinutes);
        return end < min ? min : end;
    }

    /// <summary>The range a create drag covers, in either direction (a single step when both ends snap together).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) CreateRange(DateTimeOffset anchor, DateTimeOffset pointerAt, TimeZoneInfo zone)
    {
        var a = Snap(anchor, zone);
        var b = Snap(pointerAt, zone);
        if (a == b)
        {
            return (a, a + TimeSpan.FromMinutes(SnapMinutes));
        }

        return a < b ? (a, b) : (b, a);
    }

    /// <summary>
    /// The days an all-day create drag covers, in either direction: UTC midnights (how all-day dates are stored), the
    /// end exclusive, so a press and release on one day is that day.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) AllDayRange(DateOnly anchor, DateOnly pointerDay)
    {
        var first = anchor < pointerDay ? anchor : pointerDay;
        var last  = anchor < pointerDay ? pointerDay : anchor;
        return (UtcMidnight(first), UtcMidnight(last.AddDays(1)));
    }

    static DateTimeOffset UtcMidnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The event moved by whole days (all-day by date; timed keeping its wall-clock time).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) ShiftDays(CalendarOccurrence o, int days, TimeZoneInfo zone)
    {
        if (o.IsAllDay)
        {
            return (o.Start.AddDays(days), o.End.AddDays(days));
        }

        var local = TimeZoneInfo.ConvertTime(o.Start, zone);
        var start = ToInstant(local.DateTime.AddDays(days), zone, local.Offset);
        return (start, start + (o.End - o.Start));
    }

    /// <summary>
    /// Where pasted events go: the earliest timed event lands at <paramref name="anchor"/> and the rest keep their
    /// spacing. All-day events (and every event, when all of them are all-day) move by whole days.
    /// </summary>
    public static IReadOnlyList<(CalendarOccurrence Occurrence, DateTimeOffset Start, DateTimeOffset End)> PasteAt(IReadOnlyList<CalendarOccurrence> items, DateTimeOffset anchor, TimeZoneInfo zone)
    {
        if (items.Count == 0)
        {
            return [];
        }

        // Timed events set the spacing when there are any (an all-day event's UTC midnight would sort first)
        var first     = items.Where(o => !o.IsAllDay).MinBy(o => o.Start) ?? items.MinBy(o => o.Start)!;
        var anchorDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(anchor, zone).DateTime);
        var dayShift  = anchorDay.DayNumber - LocalDay(first, zone).DayNumber;

        return [.. items.Select(o =>
        {
            if (o.IsAllDay || first.IsAllDay)
            {
                var (s, e) = ShiftDays(o, dayShift, zone);
                return (o, s, e);
            }

            var start = o.Start + (anchor - first.Start);
            return (o, start, start + (o.End - o.Start));
        })];
    }

    /// <summary>The next quarter hour at or after <paramref name="now"/> (where paste goes when no time is picked).</summary>
    public static DateTimeOffset NextSlot(DateTimeOffset now, TimeZoneInfo zone) =>
        RoundToStep(now, zone, Math.Ceiling);

    static DateTimeOffset RoundToStep(DateTimeOffset instant, TimeZoneInfo zone, Func<double, double> round)
    {
        var minutes = TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay.TotalMinutes;
        var steps   = round(minutes / SnapMinutes) * SnapMinutes;
        return instant + TimeSpan.FromMinutes(steps - minutes);
    }

    static DateOnly LocalDay(CalendarOccurrence o, TimeZoneInfo zone) =>
        o.IsAllDay ? o.AllDayStart : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start, zone).DateTime);
}
