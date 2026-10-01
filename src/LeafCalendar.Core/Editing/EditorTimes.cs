namespace LeafCalendar.Core.Editing;

/// <summary>
/// The editor's dates and times are wall-clock values in the event's own zone. These turn them into instants and back.
/// </summary>
public static class EditorTimes
{
    /// <summary>
    /// The instant a wall-clock date and time mean in <paramref name="zone"/>. A time skipped by a spring-forward change
    /// moves forward by the gap (2:30 becomes 3:30); a time that happens twice at a fall-back change is the first one.
    /// </summary>
    public static DateTimeOffset ToInstant(DateOnly date, TimeSpan time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = date.ToDateTime(TimeOnly.MinValue) + time;

        // ponytail: every zone Leaf users meet skips one hour; per-rule deltas only if a half-hour zone (Lord Howe) matters
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        // The Repeated Hour: The First Time Through Is On Daylight Time, The Larger Offset
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>An instant as the date and time of day <paramref name="zone"/>'s clock shows.</summary>
    public static (DateOnly Date, TimeSpan Time) FromInstant(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        return (DateOnly.FromDateTime(local), local.TimeOfDay);
    }
}
