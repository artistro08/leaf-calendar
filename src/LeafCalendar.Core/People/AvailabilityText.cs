using System.Globalization;
using System.Text;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.People;

/// <summary>
/// Free times written out for pasting into a message: one line per day, such as "Wed Sep 30: 10–11 AM, 2–4 PM ET".
/// </summary>
/// <remarks>
/// Times are the wall clock of the chosen zone, so a stretch that crosses midnight there is split across two lines,
/// and a DST day shows the times as that zone's clock reads them. The text carries only times, never event details.
/// </remarks>
public static class AvailabilityText
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // Short Labels People Use For North American Zones (Everything Else Is "{City} time")
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["America/New_York"] = "ET",
        ["America/Detroit"] = "ET",
        ["America/Toronto"] = "ET",
        ["America/Indiana/Indianapolis"] = "ET",
        ["America/Chicago"] = "CT",
        ["America/Winnipeg"] = "CT",
        ["America/Denver"] = "MT",
        ["America/Edmonton"] = "MT",
        ["America/Phoenix"] = "MT",
        ["America/Boise"] = "MT",
        ["America/Los_Angeles"] = "PT",
        ["America/Vancouver"] = "PT",
        ["America/Anchorage"] = "AKT",
        ["Pacific/Honolulu"] = "HT",
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC",
    };

    /// <summary>The free stretches as lines joined with <c>\r\n</c>, one per day in <paramref name="zone"/>; <c>""</c> when there are none.</summary>
    /// <param name="free">Free stretches (merged here, so overlaps are fine).</param>
    /// <param name="zone">The zone whose clock the reader uses.</param>
    /// <param name="use24Hour">"14:00–16:00" instead of "2–4 PM".</param>
    public static string Format(IReadOnlyList<BusyRange> free, TimeZoneInfo zone, bool use24Hour)
    {
        // Wall-Clock Pieces, Split At Each Local Midnight
        var pieces = new List<(DateTime Start, DateTime End)>();
        foreach (var r in BusyMath.Merge(free))
        {
            var start = TimeZoneInfo.ConvertTime(r.Start, zone).DateTime;
            var end = TimeZoneInfo.ConvertTime(r.End, zone).DateTime;

            while (start.Date < end.Date)
            {
                pieces.Add((start, start.Date.AddDays(1)));
                start = start.Date.AddDays(1);
            }

            if (start != end)
            {
                pieces.Add((start, end));
            }
        }

        if (pieces.Count == 0)
        {
            return "";
        }

        // One Line Per Day
        var label = ZoneLabel(zone);
        var lines = pieces
            .GroupBy(p => p.Start.Date)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key.ToString("ddd MMM d", English)}: {string.Join(", ", g.Select(p => Range(p.Start, p.End, use24Hour)))} {label}");

        return string.Join("\r\n", lines);
    }

    /// <summary>Where the proposed times go in a share message.</summary>
    public const string TimesPlaceholder = "{times}";

    /// <summary>The share message out of the box.</summary>
    public const string DefaultMessage = "Here are some times that work for me:\r\n{times}";

    /// <summary>Longest share message kept.</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>
    /// The text Copy puts on the clipboard: <paramref name="message"/> with <see cref="TimesPlaceholder"/> replaced by
    /// <paramref name="times"/> (every one of them), or the times on their own line after the message when it has no
    /// placeholder, or only the times when the message is blank. Line breaks are written <c>\r\n</c>.
    /// </summary>
    public static string Compose(string? message, string times)
    {
        ArgumentNullException.ThrowIfNull(times);

        var text = (message ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.Length == 0)
        {
            return times;
        }

        var composed = text.Contains(TimesPlaceholder, StringComparison.Ordinal)
            ? text.Replace(TimesPlaceholder, times.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal)
            : $"{text}\n{times.Replace("\r\n", "\n", StringComparison.Ordinal)}";

        return composed.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary><c>ET</c>, <c>CT</c>, <c>MT</c>, <c>PT</c>, <c>AKT</c>, <c>HT</c>, or <c>UTC</c>; otherwise "{City} time".</summary>
    public static string ZoneLabel(TimeZoneInfo zone)
    {
        var iana = TimeZoneCatalog.IanaId(zone);
        return Labels.TryGetValue(iana, out var label) ? label : $"{TimeZoneCatalog.CityFor(iana)} time";
    }

    // "10–11 AM", "11 AM–1 PM", "10:30–11 AM", or "10:00–11:00". A piece ending at the next midnight reads "9 AM–12 AM"
    // (its 12 AM is never folded into the start's AM) or "21:00–24:00"
    private static string Range(DateTime start, DateTime end, bool use24Hour)
    {
        var endsAtMidnight = end.Date > start.Date;
        if (use24Hour)
        {
            return $"{start.ToString("HH:mm", English)}–{(endsAtMidnight ? "24:00" : end.ToString("HH:mm", English))}";
        }

        var sameHalf = !endsAtMidnight && start.Hour < 12 == end.Hour < 12;
        return $"{Clock(start, withMeridiem: !sameHalf)}–{Clock(end, withMeridiem: true)}";
    }

    // "10", "10:30", "12 PM" (noon), "12 AM" (midnight)
    private static string Clock(DateTime t, bool withMeridiem)
    {
        var hour = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        var text = new StringBuilder(hour.ToString(English));

        if (t.Minute != 0)
        {
            text.Append(':').Append(t.Minute.ToString("00", English));
        }

        if (withMeridiem)
        {
            text.Append(t.Hour < 12 ? " AM" : " PM");
        }

        return text.ToString();
    }
}
