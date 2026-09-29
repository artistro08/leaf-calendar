using System.Globalization;
using Meziantou.Framework.Scheduling;

namespace LeafCalendar.Core.Recurrence;

/// <summary>
/// Expands Google repeating events into occurrence start times.
/// </summary>
/// <remarks>
/// The RRULE math (DST, BYSETPOS, ordinal BYDAY, UNTIL, WKST) comes from Meziantou.Framework.Scheduling.
/// This class adds what Google's <c>recurrence</c> array needs on top: EXDATE and RDATE lines, a date
/// window, and safety limits. Invites come from anyone, so unreadable lines are skipped, and hostile
/// rules (for example <c>FREQ=SECONDLY</c>) stop at <see cref="MaxOccurrences"/> results or
/// <see cref="MaxScanned"/> steps.
/// </remarks>
/// <seealso href="https://www.meziantou.net/evaluating-cron-and-rrule-expressions-in-dotnet.htm"/>
/// <seealso href="https://developers.google.com/workspace/calendar/api/concepts/events-calendars#recurring_events"/>
public static class RecurrenceExpander
{
    /// <summary>Most occurrences returned for one window.</summary>
    public const int MaxOccurrences = 2000;

    /// <summary>Most occurrences walked (including ones before the window) before giving up.</summary>
    public const int MaxScanned = 100_000;

    /// <summary>
    /// Returns occurrence starts of a timed series with <c>windowStart &lt;= start &lt; windowEnd</c>, sorted and de-duplicated.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> ExpandTimed(
        IReadOnlyList<string> recurrence,
        DateTimeOffset start,
        string? timeZoneId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var zone      = FindZone(timeZoneId) ?? FixedZone(start.Offset);
        var wallClock = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var excluded  = ReadDates(recurrence, "EXDATE", zone).Select(v => v.Utc).OfType<DateTime>().ToHashSet();

        // Rule Occurrences
        var rule   = ReadRule(recurrence);
        IEnumerable<DateTimeOffset> series = rule is null ? [start] : rule.GetNextOccurrences(wallClock, zone);
        var result = new SortedSet<DateTimeOffset>();
        var scanned = 0;

        foreach (var occurrence in series)
        {
            if (occurrence >= windowEnd || ++scanned > MaxScanned || result.Count >= MaxOccurrences)
            {
                break;
            }

            if (occurrence >= windowStart && !excluded.Contains(occurrence.UtcDateTime))
            {
                result.Add(occurrence);
            }
        }

        // Extra Dates
        foreach (var utc in ReadDates(recurrence, "RDATE", zone).Select(v => v.Utc).OfType<DateTime>())
        {
            var extra = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), zone);
            if (extra >= windowStart && extra < windowEnd && !excluded.Contains(utc) && result.Count < MaxOccurrences)
            {
                result.Add(extra);
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Returns occurrence dates of an all-day series with <c>windowStart &lt;= date &lt; windowEnd</c>, sorted and de-duplicated.
    /// </summary>
    public static IReadOnlyList<DateOnly> ExpandAllDay(IReadOnlyList<string> recurrence, DateOnly start, DateOnly windowStart, DateOnly windowEnd)
    {
        var dates    = ReadDates(recurrence, "EXDATE", TimeZoneInfo.Utc).ToList();
        var excluded = dates.Select(v => v.Date ?? DateOnly.FromDateTime(v.Utc!.Value)).ToHashSet();

        // Rule Occurrences
        var rule   = ReadRule(recurrence);
        IEnumerable<DateOnly> series = rule is null ? [start] : rule.GetNextOccurrences(start.ToDateTime(TimeOnly.MinValue)).Select(DateOnly.FromDateTime);
        var result = new SortedSet<DateOnly>();
        var scanned = 0;

        foreach (var date in series)
        {
            if (date >= windowEnd || ++scanned > MaxScanned || result.Count >= MaxOccurrences)
            {
                break;
            }

            if (date >= windowStart && !excluded.Contains(date))
            {
                result.Add(date);
            }
        }

        // Extra Dates
        foreach (var value in ReadDates(recurrence, "RDATE", TimeZoneInfo.Utc))
        {
            var date = value.Date ?? DateOnly.FromDateTime(value.Utc!.Value);
            if (date >= windowStart && date < windowEnd && !excluded.Contains(date) && result.Count < MaxOccurrences)
            {
                result.Add(date);
            }
        }

        return [.. result];
    }

    static RecurrenceRule? ReadRule(IReadOnlyList<string> recurrence)
    {
        var line = recurrence.FirstOrDefault(l => l.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
        if (line is null || line.Length <= 6)
        {
            return null;
        }

        try
        {
            return RecurrenceRule.Parse(line[6..]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    // Reads "EXDATE;TZID=America/New_York:20261104T090000,20261105T090000", "EXDATE;VALUE=DATE:20261104",
    // or "RDATE:20261104T140000Z". Each value is either a date (all-day) or a UTC instant.
    static IEnumerable<(DateOnly? Date, DateTime? Utc)> ReadDates(IReadOnlyList<string> recurrence, string name, TimeZoneInfo defaultZone)
    {
        foreach (var line in recurrence)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var parameters = line[..colon].Split(';');
            if (!parameters[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDate = parameters.Contains("VALUE=DATE", StringComparer.OrdinalIgnoreCase);
            var tzid   = parameters.FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..];
            var zone   = FindZone(tzid) ?? defaultZone;

            foreach (var value in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (isDate)
                {
                    if (DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        yield return (date, null);
                    }

                    continue;
                }

                if (value.EndsWith('Z') &&
                    DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                {
                    yield return (null, utc);
                }
                else if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    // A wall-clock time inside a DST gap doesn't exist; shift it forward like the RFC does
                    var valid = zone.IsInvalidTime(local) ? local.AddHours(1) : local;
                    // A repeated fall-back time means the first (daylight) occurrence, matching the rule expansion
                    yield return (null, zone.IsAmbiguousTime(valid)
                        ? new DateTimeOffset(valid, zone.GetAmbiguousTimeOffsets(valid).Max()).UtcDateTime
                        : TimeZoneInfo.ConvertTimeToUtc(valid, zone));
                }
            }
        }
    }

    static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    static TimeZoneInfo FixedZone(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var id   = $"UTC{sign}{offset.Duration():hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }
}
