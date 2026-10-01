using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Recurrence;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Events;

/// <summary>
/// Turns stored events into the instances visible in a date range.
/// </summary>
/// <remarks>
/// Repeating series are expanded with <see cref="RecurrenceExpander"/>. An exception row (a moved,
/// edited, or canceled single instance) replaces the series instance that started at its
/// <c>originalStartTime</c>. Only visible calendars are included. Declined events are dropped unless
/// asked for. The same event seen in two of your accounts (same iCalUID and start) is kept once.
/// </remarks>
public static class OccurrenceQuery
{
    // Exceptions can move an instance far; look this far around the window for them
    static readonly TimeSpan ExceptionPad = TimeSpan.FromDays(32);

    const string Sql = """
        SELECT e.account_id, e.calendar_id, e.id, e.ical_uid, e.status, e.start_utc, e.end_utc, e.is_all_day,
               e.start_time_zone, e.is_recurring_master, e.recurring_event_id, e.original_start_utc, e.raw_json,
               COALESCE(c.leaf_color, c.background_color, '#4285F4')
        FROM events e
        JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
        JOIN accounts a  ON a.id = e.account_id
        WHERE COALESCE(c.leaf_hidden, c.hidden) = 0
          AND (
                (e.is_recurring_master = 1 AND e.status <> 'cancelled' AND e.start_utc < $to)
             OR (e.is_recurring_master = 0 AND e.recurring_event_id IS NULL AND e.status <> 'cancelled'
                 AND e.start_utc < $to AND e.end_utc > $from)
             OR (e.recurring_event_id IS NOT NULL
                 AND ((e.original_start_utc >= $padFrom AND e.original_start_utc < $padTo)
                   OR (e.start_utc < $to AND e.end_utc > $from)))
          )
        ORDER BY a.email, c.sort_order;
        """;

    /// <summary>Loads instances overlapping local days <c>[fromDate, toDate)</c>, sorted by start (longer first on ties).</summary>
    public static IReadOnlyList<CalendarOccurrence> Load(SqliteConnection conn, DateOnly fromDate, DateOnly toDate, TimeZoneInfo zone, bool includeDeclined)
    {
        var from = LocalMidnight(fromDate, zone);
        var to   = LocalMidnight(toDate, zone);

        // Broad SQL window (a day of slack for all-day dates), refined below
        var rows = conn.Query(
            null,
            Sql,
            Row.Read,
            ("$from", (from - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()),
            ("$to", (to + TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()),
            ("$padFrom", (from - ExceptionPad).ToUnixTimeMilliseconds()),
            ("$padTo", (to + ExceptionPad).ToUnixTimeMilliseconds()));

        // Series Instances Replaced By An Exception
        var replaced = rows
            .Where(r => r.RecurringEventId is not null && r.OriginalStartMs is not null)
            .Select(r => (r.AccountId, r.CalendarId, r.RecurringEventId!, r.OriginalStartMs!.Value))
            .ToHashSet();

        var result = new List<CalendarOccurrence>();
        foreach (var row in rows)
        {
            if (row.IsMaster)
            {
                var details = EventDetailsParser.Parse(row.RawJson, includeDescription: false);
                foreach (var (start, end) in ExpandMaster(row.Series, fromDate, toDate, from, to, ms => replaced.Contains((row.AccountId, row.CalendarId, row.Id, ms))))
                {
                    result.Add(Create(row, details, start, end, recurringEventId: row.Id));
                }

                continue;
            }

            if (row.Status == "cancelled" || row.StartMs is not { } startMs || row.EndMs is not { } endMs)
            {
                continue;
            }

            var s = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
            var e = DateTimeOffset.FromUnixTimeMilliseconds(endMs);
            if (Overlaps(row.IsAllDay, s, e, fromDate, toDate, from, to))
            {
                result.Add(Create(row, EventDetailsParser.Parse(row.RawJson, includeDescription: false), s, e, row.RecurringEventId));
            }
        }

        // Declined, Duplicates, Order
        var seen = new HashSet<(string, long)>();
        return result
            .Where(o => includeDeclined || o.SelfResponse != ResponseStatus.Declined)
            .Where(o => o.ICalUid is null || seen.Add((o.ICalUid, o.Start.ToUnixTimeMilliseconds())))
            .OrderBy(o => o.Start)
            .ThenByDescending(o => o.End)
            .ToList();
    }

    /// <summary>The instant local midnight starts <paramref name="day"/> in <paramref name="zone"/> (skipping a DST gap).</summary>
    public static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// A series' instances overlapping local days <c>[fromDate, toDate)</c> (<paramref name="from"/> and <paramref name="to"/>
    /// are those days' local midnights), skipping each instance whose start (Unix ms) <paramref name="isReplaced"/> says an
    /// exception row replaces. All-day instances are UTC midnights; timed series expand in their own zone.
    /// </summary>
    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ExpandMaster(
        SeriesRow row, DateOnly fromDate, DateOnly toDate, DateTimeOffset from, DateTimeOffset to, Func<long, bool> isReplaced) =>
        Expand(row, fromDate, toDate, from, to).Where(i => !isReplaced(i.Start.ToUnixTimeMilliseconds()));

    static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Expand(SeriesRow row, DateOnly fromDate, DateOnly toDate, DateTimeOffset from, DateTimeOffset to)
    {
        if (row.StartMs is not { } startMs || row.EndMs is not { } endMs)
        {
            yield break;
        }

        var start      = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
        var duration   = DateTimeOffset.FromUnixTimeMilliseconds(endMs) - start;
        var ev         = JsonSerializer.Deserialize(row.RawJson, GoogleJsonContext.Default.GoogleEvent);
        var recurrence = ev?.Recurrence ?? [];

        // All-Day Series: expand by date
        if (row.IsAllDay)
        {
            var days = (int)Math.Max(1, Math.Round(duration.TotalDays));
            foreach (var date in RecurrenceExpander.ExpandAllDay(recurrence, DateOnly.FromDateTime(start.UtcDateTime), fromDate.AddDays(-days), toDate))
            {
                if (date.AddDays(days) > fromDate)
                {
                    var s = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    yield return (s, s + duration);
                }
            }

            yield break;
        }

        // Timed Series: expand in the event's own zone so wall-clock time survives DST
        var anchor = ev?.Start?.DateTime ?? start;
        foreach (var s in RecurrenceExpander.ExpandTimed(recurrence, anchor, row.TimeZone, from - duration, to))
        {
            if (s + duration > from)
            {
                yield return (s, s + duration);
            }
        }
    }

    static bool Overlaps(bool isAllDay, DateTimeOffset start, DateTimeOffset end, DateOnly fromDate, DateOnly toDate, DateTimeOffset from, DateTimeOffset to) =>
        isAllDay
            ? DateOnly.FromDateTime(start.UtcDateTime) < toDate && DateOnly.FromDateTime(end.UtcDateTime) > fromDate
            : start < to && end > from;

    static CalendarOccurrence Create(Row row, EventDetails details, DateTimeOffset start, DateTimeOffset end, string? recurringEventId) => new(
        row.AccountId,
        row.CalendarId,
        row.Id,
        row.ICalUid,
        recurringEventId,
        start,
        end,
        row.IsAllDay,
        details.Title,
        details.Kind,
        details.SelfResponse,
        row.CalendarColor,
        details.ColorId,
        details.IsFree,
        details.ConferenceUri is not null);

    /// <summary>The stored fields a repeating series is expanded from (its first instance's times, zone, and raw JSON).</summary>
    internal sealed record SeriesRow(long? StartMs, long? EndMs, bool IsAllDay, string? TimeZone, string RawJson);

    sealed record Row(
        string AccountId,
        string CalendarId,
        string Id,
        string? ICalUid,
        string Status,
        long? StartMs,
        long? EndMs,
        bool IsAllDay,
        string? TimeZone,
        bool IsMaster,
        string? RecurringEventId,
        long? OriginalStartMs,
        string RawJson,
        string CalendarColor)
    {
        /// <summary>What expanding the series needs.</summary>
        public SeriesRow Series => new(StartMs, EndMs, IsAllDay, TimeZone, RawJson);

        public static Row Read(SqliteDataReader r) => new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.GetStringOrNull(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetInt64(6),
            r.GetBoolean(7),
            r.GetStringOrNull(8),
            r.GetBoolean(9),
            r.GetStringOrNull(10),
            r.IsDBNull(11) ? null : r.GetInt64(11),
            r.GetString(12),
            r.GetString(13));
    }
}
