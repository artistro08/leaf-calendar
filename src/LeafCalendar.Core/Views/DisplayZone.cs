namespace LeafCalendar.Core.Views;

/// <summary>
/// Which time zone the calendar is drawn in: time travel (this session only) first, then Leaf's primary time zone
/// (Settings › Time zones), then Windows' own zone.
/// </summary>
public static class DisplayZone
{
    /// <summary>The zone on screen; an ID this PC doesn't know falls through to the next choice.</summary>
    public static TimeZoneInfo Resolve(string? travelZoneId, string? primaryZoneId, TimeZoneInfo windows)
    {
        if (travelZoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(travelZoneId, out var travel))
        {
            return travel;
        }

        return primaryZoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(primaryZoneId, out var primary) ? primary : windows;
    }

    /// <summary>
    /// True when Leaf should offer to switch to Windows' new zone: a primary zone is pinned, the prompt setting is on,
    /// and Windows' zone is a different zone (a Windows ID and an IANA ID for the same zone count as the same).
    /// </summary>
    public static bool ShouldOfferSwitch(string? primaryZoneId, bool prompt, TimeZoneInfo newWindows) =>
        prompt
        && primaryZoneId is not null
        && TimeZoneInfo.TryFindSystemTimeZoneById(primaryZoneId, out var primary)
        && !string.Equals(TimeZoneCatalog.WindowsId(primary), TimeZoneCatalog.WindowsId(newWindows), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "Tokyo time (JST)", "New York time (EDT)": the zone's short name as it is at <paramref name="at"/> (its UTC offset
    /// when it has none), left out when the label already has it ("UTC time").
    /// </summary>
    public static string Describe(TimeZoneInfo zone, DateTimeOffset at)
    {
        var label = $"{TimeZoneCatalog.CityFor(TimeZoneCatalog.IanaId(zone))} time";
        var name  = ZoneAbbreviation.For(zone, at);
        return label.Split(' ').Contains(name, StringComparer.Ordinal) ? label : $"{label} ({name})";
    }
}
