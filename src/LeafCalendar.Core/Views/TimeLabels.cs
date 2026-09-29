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

    /// <summary>"in 12 min", "in 2 h", "Now", or "Ended".</summary>
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

        var until = start - now;
        return until.TotalMinutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"in {(int)Math.Ceiling(until.TotalMinutes)} min")
            : string.Create(CultureInfo.InvariantCulture, $"in {(int)Math.Floor(until.TotalHours)} h");
    }
}
