using System.Globalization;

namespace LeafCalendar.Core.Views;

/// <summary>English time and date strings for the calendar.</summary>
public static class TimeLabels
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>"9 AM" or "09:00".</summary>
    public static string HourLabel(int hour, bool use24h)
    {
        if (use24h)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hour:00}:00");
        }

        return hour switch
        {
            0    => "12 AM",
            12   => "12 PM",
            < 12 => string.Create(CultureInfo.InvariantCulture, $"{hour} AM"),
            _    => string.Create(CultureInfo.InvariantCulture, $"{hour - 12} PM"),
        };
    }

    /// <summary>"9 AM", "9:30 AM", or "13:05".</summary>
    public static string TimeOfDay(DateTimeOffset instant, TimeZoneInfo zone, bool use24h)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        if (use24h)
        {
            return local.ToString("HH:mm", English);
        }

        return local.ToString(local.Minute == 0 ? "h tt" : "h:mm tt", English);
    }

    /// <summary>"9 AM – 10:30 AM".</summary>
    public static string Range(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone, bool use24h) =>
        $"{TimeOfDay(start, zone, use24h)} – {TimeOfDay(end, zone, use24h)}";

    /// <summary>
    /// A time-grid card's range, without AM/PM (the card's place on the grid says which): "9 – 10:30", "9:15 – 11", or
    /// "09:00 – 10:30" on a 24-hour clock.
    /// </summary>
    public static string GridRange(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone, bool use24h) =>
        use24h ? Range(start, end, zone, true) : $"{Clock(start, zone)} – {Clock(end, zone)}";

    // "9" or "9:15" on a 12-hour clock
    static string Clock(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.ToString(local.Minute == 0 ? "%h" : "h:mm", English); // "%h": a lone "h" would be read as a standard format
    }

    /// <summary>Month-view time: "9a", "1:30p", or "13:30".</summary>
    public static string Compact(DateTimeOffset instant, TimeZoneInfo zone, bool use24h)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        if (use24h)
        {
            return local.ToString("HH:mm", English);
        }

        var hour   = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        var suffix = local.Hour < 12 ? "a" : "p";

        return local.Minute == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hour}{suffix}")
            : string.Create(CultureInfo.InvariantCulture, $"{hour}:{local.Minute:00}{suffix}");
    }

    /// <summary>"Thu".</summary>
    public static string WeekdayShort(DateOnly day) => day.ToString("ddd", English);

    /// <summary>"Thursday, October 1".</summary>
    public static string LongDate(DateOnly day) => day.ToString("dddd, MMMM d", English);

    /// <summary>
    /// "in 12 min" (minutes rounded up, under an hour), "in 2 h" (whole hours, up to 47), "in 3 days" (whole days, from
    /// 48 hours), "Now", or "Ended".
    /// </summary>
    public static string Relative(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        if (now >= end)
        {
            return "Ended";
        }

        if (now >= start)
        {
            return "Now";
        }

        // Whole Minutes, Rounded Up (59.5 minutes is an hour, never "60 min")
        var minutes = (long)Math.Ceiling((start - now).TotalMinutes);
        return minutes switch
        {
            < 60      => string.Create(CultureInfo.InvariantCulture, $"in {minutes} min"),
            < 48 * 60 => string.Create(CultureInfo.InvariantCulture, $"in {minutes / 60} h"),
            _         => string.Create(CultureInfo.InvariantCulture, $"in {minutes / (24 * 60)} days"),
        };
    }
}
