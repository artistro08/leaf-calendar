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
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>
    /// Stops the series just before <paramref name="splitStart"/>, and drops COUNT. All-day: UNTIL is the day before (a date).
    /// Timed, daily or slower, in a known <paramref name="timeZoneId"/>: the end of the day before in that zone, in UTC (Google's
    /// form, which <see cref="RepeatRule"/> reads back as that day). Other timed rules: one second before, in UTC.
    /// All-day starts are the calendar date at midnight; the offset is ignored.
    /// Never extends a series: a rule whose UNTIL is already earlier, or (given <paramref name="seriesStart"/>) whose COUNT runs out before the split, is kept as it is.
    /// </summary>
    public static IReadOnlyList<string> EndBefore(IReadOnlyList<string> recurrence, DateTimeOffset splitStart, bool isAllDay, DateTimeOffset? seriesStart = null, string? timeZoneId = null) =>
        [.. recurrence.Select(line => IsRule(line) && UntilBefore(line, splitStart, isAllDay, timeZoneId) is var until && !EndsBy(line, until, splitStart, isAllDay, seriesStart, timeZoneId) ? WithParts(line, "UNTIL", until) : line)];

    // The UNTIL that ends one rule just before the split
    private static string UntilBefore(string line, DateTimeOffset splitStart, bool isAllDay, string? timeZoneId)
    {
        // Use The Date As Written For All-Day, Not Its UTC Date
        if (isAllDay)
        {
            return DateOnly.FromDateTime(splitStart.DateTime).AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        // One Instance A Day At Most: End With The Day Before, So Reading It Back As A Date Can't Bring The Split Day Back
        if (RecurrenceExpander.FindZone(timeZoneId) is { } zone
            && Part(line, "FREQ") is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"
            && Part(line, "BYHOUR") is null && Part(line, "BYMINUTE") is null && Part(line, "BYSECOND") is null)
        {
            return RepeatRule.EndOfDayUtc(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(splitStart, zone).DateTime).AddDays(-1), zone);
        }

        return splitStart.UtcDateTime.AddSeconds(-1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An <c>EXDATE</c> line that removes one instance: a UTC time for timed series, a date for all-day ones
    /// (the date as written, like <see cref="EndBefore"/>).
    /// </summary>
    public static string ExDateLine(DateTimeOffset originalStart, bool isAllDay) => isAllDay
        ? string.Create(CultureInfo.InvariantCulture, $"EXDATE;VALUE=DATE:{originalStart.DateTime:yyyyMMdd}")
        : string.Create(CultureInfo.InvariantCulture, $"EXDATE:{originalStart.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");

    // True when the rule already ends at or before the new end (so ending it again would extend it)
    private static bool EndsBy(string line, string until, DateTimeOffset splitStart, bool isAllDay, DateTimeOffset? seriesStart, string? timeZoneId)
    {
        if (Part(line, "UNTIL") is { } existing)
        {
            return UntilEnd(existing) is { } existingEnd && UntilEnd(until) is { } newEnd && existingEnd <= newEnd;
        }

        return seriesStart is { } start
            && Part(line, "COUNT") is { } countText
            && int.TryParse(countText, CultureInfo.InvariantCulture, out var count)
            && CountBefore(line, start, timeZoneId, splitStart, isAllDay) >= count;
    }

    // An UNTIL value as the last moment it allows (a date counts through its end)
    private static DateTime? UntilEnd(string value) =>
        DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ? instant
        : DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.AddDays(1).AddSeconds(-1)
        : null;

    // Occurrences of one rule before the split (COUNT counts before EXDATE removes any)
    private static int CountBefore(string line, DateTimeOffset seriesStart, string? timeZoneId, DateTimeOffset splitStart, bool isAllDay)
    {
        var seriesDate = DateOnly.FromDateTime(seriesStart.DateTime);
        return isAllDay
            ? RecurrenceExpander.ExpandAllDay([line], seriesDate, seriesDate, DateOnly.FromDateTime(splitStart.DateTime)).Count
            : RecurrenceExpander.ExpandTimed([line], seriesStart, timeZoneId, seriesStart, splitStart).Count;
    }

    /// <summary>
    /// The lines for a new series starting at <paramref name="splitStart"/>. A COUNT becomes what's left after
    /// the instances before the split. EXDATE lines stay (dates before the new start never match).
    /// Returns null when a COUNT rule has no instances left at or after the split (nothing to split off).
    /// All-day starts are the calendar date at midnight; the offset is ignored.
    /// </summary>
    public static IReadOnlyList<string>? FollowingFrom(IReadOnlyList<string> recurrence, DateTimeOffset seriesStart, string? timeZoneId, DateTimeOffset splitStart, bool isAllDay)
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

            var before = CountBefore(line, seriesStart, timeZoneId, splitStart, isAllDay);

            // Nothing Left After The Split
            if (count - before < 1)
            {
                return null;
            }

            result.Add(WithParts(line, "COUNT", (count - before).ToString(CultureInfo.InvariantCulture)));
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

    private static bool IsRule(string line) => line.StartsWith("RRULE:", StringComparison.Ordinal);

    private static string? Part(string line, string name) =>
        line["RRULE:".Length..].Split(';').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name && p.Length == 2)?[1];

    // Set One Part And Drop UNTIL/COUNT When Setting The Other (A Rule Has At Most One Of Them)
    private static string WithParts(string line, string name, string value)
    {
        var ends = name is "UNTIL" or "COUNT";
        var parts = line["RRULE:".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var key = p.Split('=', 2)[0];
                return key != name && !(ends && key is "UNTIL" or "COUNT");
            })
            .Append($"{name}={value}");

        return "RRULE:" + string.Join(";", parts);
    }

    private static string Shift(string code, int days)
    {
        // Skip Empty Or Short Entries (Rules Come From Anyone)
        if (code.Length < 2)
        {
            return code;
        }

        var day = code[^2..];
        var index = Array.IndexOf(DayCodes, day);
        return index < 0 ? code : code[..^2] + DayCodes[((index + days) % 7 + 7) % 7];
    }
}
