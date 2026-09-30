using System.Globalization;
using LeafCalendar.Core.Recurrence;

namespace LeafCalendar.Core.Editing;

/// <summary>
/// Rewrites Google recurrence lines for "This and following events" and "All events" edits.
/// </summary>
/// <remarks>
/// A "following" edit ends the old series just before the chosen instance (<see cref="EndBefore"/>) and
/// starts a new series there (<see cref="FollowingFrom"/>), the same split Google Calendar makes. A COUNT
/// rule is split so the two series together still have the original number of instances. Moving a whole
/// weekly series to another weekday rotates its BYDAY codes (<see cref="ShiftWeekdays"/>).
/// </remarks>
public static class RecurrenceEdits
{
    static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>Stops the series just before <paramref name="splitStart"/>: UNTIL one second before (timed, in UTC) or the day before (all-day, a date), and no COUNT.</summary>
    public static IReadOnlyList<string> EndBefore(IReadOnlyList<string> recurrence, DateTimeOffset splitStart, bool isAllDay)
    {
        // All-day starts are calendar dates, so use the date as written, not its UTC date
        var until = isAllDay
            ? DateOnly.FromDateTime(splitStart.DateTime).AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : splitStart.UtcDateTime.AddSeconds(-1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        return [.. recurrence.Select(line => IsRule(line) ? WithParts(line, "UNTIL", until) : line)];
    }

    /// <summary>
    /// The lines for a new series starting at <paramref name="splitStart"/>. A COUNT becomes what's left after
    /// the instances before the split. EXDATE lines stay (dates before the new start never match).
    /// </summary>
    public static IReadOnlyList<string> FollowingFrom(IReadOnlyList<string> recurrence, DateTimeOffset seriesStart, string? timeZoneId, DateTimeOffset splitStart, bool isAllDay)
    {
        var result = new List<string>();
        foreach (var line in recurrence)
        {
            // ponytail: RDATE lines (Google's own UI never writes them) aren't carried into the new series; filter their dates if an imported calendar needs them
            if (line.StartsWith("RDATE", StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsRule(line) || Part(line, "COUNT") is not { } countText || !int.TryParse(countText, CultureInfo.InvariantCulture, out var count))
            {
                result.Add(line);
                continue;
            }

            // Occurrences of the rule alone (COUNT counts before EXDATE removes any)
            var seriesDate = DateOnly.FromDateTime(seriesStart.DateTime);
            var before     = isAllDay
                ? RecurrenceExpander.ExpandAllDay([line], seriesDate, seriesDate, DateOnly.FromDateTime(splitStart.DateTime)).Count
                : RecurrenceExpander.ExpandTimed([line], seriesStart, timeZoneId, seriesStart, splitStart).Count;

            result.Add(WithParts(line, "COUNT", Math.Max(1, count - before).ToString(CultureInfo.InvariantCulture)));
        }

        return result;
    }

    /// <summary>Rotates every BYDAY code by <paramref name="days"/> (keeping ordinals like <c>2TU</c>). Zero returns the same list.</summary>
    public static IReadOnlyList<string> ShiftWeekdays(IReadOnlyList<string> recurrence, int days)
    {
        if (days % 7 == 0)
        {
            return recurrence;
        }

        return [.. recurrence.Select(line => IsRule(line) && Part(line, "BYDAY") is { } byDay ? WithParts(line, "BYDAY", string.Join(",", byDay.Split(',').Select(code => Shift(code, days)))) : line)];
    }

    static bool IsRule(string line) => line.StartsWith("RRULE:", StringComparison.Ordinal);

    static string? Part(string line, string name) =>
        line["RRULE:".Length..].Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name && p.Length == 2)?[1];

    // Sets one part and drops UNTIL/COUNT when setting the other (a rule has at most one of them)
    static string WithParts(string line, string name, string value)
    {
        var ends  = name is "UNTIL" or "COUNT";
        var parts = line["RRULE:".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var key = p.Split('=', 2)[0];
                return key != name && !(ends && key is "UNTIL" or "COUNT");
            })
            .Append($"{name}={value}");

        return "RRULE:" + string.Join(";", parts);
    }

    static string Shift(string code, int days)
    {
        var day   = code[^2..];
        var index = Array.IndexOf(DayCodes, day);
        return index < 0 ? code : code[..^2] + DayCodes[((index + days) % 7 + 7) % 7];
    }
}
