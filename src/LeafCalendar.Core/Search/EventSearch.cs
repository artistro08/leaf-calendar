using System.Text.Json;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Search;

/// <summary>Where a search word was found, best first.</summary>
public enum SearchField
{
    /// <summary>Title.</summary>
    Title,

    /// <summary>Location.</summary>
    Location,

    /// <summary>A guest's address or name.</summary>
    Guest,

    /// <summary>Description.</summary>
    Description,
}

/// <summary>An event the command menu found. A repeating event's <see cref="EventId"/> and times are one instance's.</summary>
/// <remarks><see cref="Title"/> is cleaned for display; <see cref="Color"/> is the event's own color, else its calendar's.</remarks>
public sealed record SearchHit(string AccountId, string CalendarId, string EventId, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string Color, SearchField Field);

/// <summary>
/// Searches every event Leaf has stored, past and future (spec 6.6), by title, location, guests, and description.
/// </summary>
/// <remarks>
/// <para>
/// The query is untrusted text: it's cleaned with <see cref="DisplayText.Clean"/>, capped at <see cref="MaxQuery"/>
/// characters (no ellipsis), and split into words that must all match (case-insensitive). SQL only narrows the rows. It
/// uses a parameterized <c>LIKE</c> with <c>%</c>, <c>_</c>, and <c>\</c> escaped, and only for a word whose characters
/// JSON never escapes. Matching happens on the parsed event.
/// </para>
/// <para>
/// Repeating series are read first (so an old series can't be crowded out by nearer single events), and only the
/// series that matched are expanded, to find the instance to show. Title matches come first, then upcoming events
/// (soonest first), then past ones (newest first). The same event in two accounts (one iCalUID) is listed once.
/// Nothing is logged.
/// </para>
/// </remarks>
public static partial class EventSearch
{
    /// <summary>Longest query used.</summary>
    public const int MaxQuery = 100;

    /// <summary>Most results.</summary>
    public const int MaxResults = 20;

    // ponytail: scans at most this many rows (series first, then nearest to now); an FTS table is the upgrade if people keep years of events
    internal const int MaxRows = 3000;

    // How far either side of now a series is expanded to find its instance
    const int InstanceDays = 366;

    // Longest title kept on a hit
    const int MaxTitle = 200;

    const string Sql = """
        SELECT e.account_id, e.calendar_id, e.id, e.ical_uid, e.start_utc, e.end_utc, e.is_all_day, e.is_recurring_master,
               e.start_time_zone, e.raw_json, COALESCE(c.leaf_color, c.background_color, '#4285F4')
        FROM events e
        JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
        WHERE COALESCE(c.leaf_hidden, c.hidden) = 0 AND c.hidden = 0
          AND e.status <> 'cancelled'
          AND e.raw_json LIKE $like ESCAPE '\'
        ORDER BY e.is_recurring_master DESC, ABS(COALESCE(e.start_utc, 0) - $now)
        LIMIT $max;
        """;

    const string ExceptionSql = """
        SELECT id, status, start_utc, end_utc, original_start_utc
        FROM events
        WHERE account_id = $account AND calendar_id = $calendar AND recurring_event_id = $master AND original_start_utc IS NOT NULL;
        """;

    /// <summary>The cleaned search words.</summary>
    public static IReadOnlyList<string> Words(string? query)
    {
        // Bound The Input, Clean It, Clip It Without An Ellipsis
        var text  = query is { Length: > MaxQuery * 4 } ? query[..(MaxQuery * 4)] : query;
        var plain = DisplayText.Clean(text, int.MaxValue);
        if (plain.Length > MaxQuery)
        {
            plain = plain[..(char.IsHighSurrogate(plain[MaxQuery - 1]) ? MaxQuery - 1 : MaxQuery)];
        }

        return plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Finds events matching every word of <paramref name="query"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was canceled (checked between rows).</exception>
    public static IReadOnlyList<SearchHit> Find(SqliteConnection conn, string? query, DateTimeOffset now, TimeZoneInfo zone, CancellationToken cancel = default)
    {
        // Nothing To Search
        var words = Words(query);
        if (words.Count == 0)
        {
            return [];
        }

        // Narrow In SQL (the longest word JSON stores as-is), Then Match The Parsed Event
        var narrow = words.Where(w => SafeForLike().IsMatch(w)).OrderByDescending(w => w.Length).FirstOrDefault();
        var like   = narrow is null ? "%" : "%" + narrow.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var rows   = conn.Query(null, Sql, Row.Read, ("$like", like), ("$now", now.ToUnixTimeMilliseconds()), ("$max", MaxRows));

        // Matches, Each Repeating Series Expanded To One Instance
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var hits  = new List<(SearchHit Hit, string Key)>();
        foreach (var row in rows)
        {
            cancel.ThrowIfCancellationRequested();
            if (Match(row.RawJson, words) is { } match)
            {
                hits.Add(ToHit(conn, row, match, now, today, zone));
            }
        }

        return hits
            .DistinctBy(h => h.Key)
            .Select(h => h.Hit)
            .OrderBy(h => h.Field == SearchField.Title ? 0 : 1)
            .ThenBy(h => h.End > now ? 0 : 1)
            .ThenBy(h => h.End > now ? h.Start.UtcTicks : -h.Start.UtcTicks)
            .Take(MaxResults)
            .ToList();
    }

    // =========================================================================
    // MATCHING
    // =========================================================================

    /// <summary>
    /// The event's title, color ID, and the best field the first word is found in, when every word is found in the
    /// title, location, guests (addresses and names), or plain-text description; null otherwise or for malformed JSON.
    /// </summary>
    static (string Title, string? ColorId, SearchField Field)? Match(string rawJson, IReadOnlyList<string> words)
    {
        try
        {
            var details = EventDetailsParser.Parse(rawJson);
            string[] fields = [details.Title, details.Location ?? "", Guests(rawJson), details.Description];

            if (!words.All(w => fields.Any(f => f.Contains(w, StringComparison.OrdinalIgnoreCase))))
            {
                return null;
            }

            var best = Array.FindIndex(fields, f => f.Contains(words[0], StringComparison.OrdinalIgnoreCase));
            return (details.Title, details.ColorId, (SearchField)best);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every guest's address and display name, one per line.</summary>
    static string Guests(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("attendees", out var attendees)
            || attendees.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var text = new List<string>();
        foreach (var guest in attendees.EnumerateArray())
        {
            foreach (var name in (string[])["email", "displayName"])
            {
                if (guest.ValueKind == JsonValueKind.Object && guest.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    text.Add(value.GetString()!);
                }
            }
        }

        return string.Join('\n', text);
    }

    // =========================================================================
    // HITS
    // =========================================================================

    /// <summary>
    /// The hit for a matched row and its dedupe key (the iCalUID when present, so one event in two accounts is listed
    /// once). A series shows its next instance, else its latest in the last year, else its own first times.
    /// </summary>
    static (SearchHit Hit, string Key) ToHit(SqliteConnection conn, Row row, (string Title, string? ColorId, SearchField Field) match, DateTimeOffset now, DateOnly today, TimeZoneInfo zone)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(row.StartMs ?? 0);
        var end   = DateTimeOffset.FromUnixTimeMilliseconds(row.EndMs ?? row.StartMs ?? 0);
        var id    = row.Id;

        if (row.IsMaster)
        {
            var instances = Instances(conn, row, today, zone);
            if ((instances.FirstOrDefault(i => i.End > now) ?? instances.LastOrDefault()) is { } instance)
            {
                (id, start, end) = instance;
            }
        }

        var hit = new SearchHit(row.AccountId, row.CalendarId, id, DisplayText.Clean(match.Title, MaxTitle), start, end, row.IsAllDay,
            EventColors.ResolveAccent(match.ColorId, row.Color), match.Field);

        return (hit, row.ICalUid ?? $"{row.AccountId}|{row.CalendarId}|{row.Id}");
    }

    /// <summary>
    /// One series' instances within a year either side of <paramref name="today"/>, sorted by start: canceled instances
    /// are dropped and moved or edited ones carry their own ID and times. All-day instances use UTC midnights, as
    /// <see cref="OccurrenceQuery"/> does.
    /// </summary>
    static List<Instance> Instances(SqliteConnection conn, Row row, DateOnly today, TimeZoneInfo zone)
    {
        var fromDate = today.AddDays(-InstanceDays);
        var toDate   = today.AddDays(InstanceDays);

        // Exceptions Replace The Instance That Started At Their Original Start (OccurrenceQuery's own expansion skips those)
        var exceptions = conn.Query(null, ExceptionSql, Change.Read, ("$account", row.AccountId), ("$calendar", row.CalendarId), ("$master", row.Id))
            .DistinctBy(e => e.OriginalStartMs)
            .ToDictionary(e => e.OriginalStartMs);

        var series = new OccurrenceQuery.SeriesRow(row.StartMs, row.EndMs, row.IsAllDay, row.TimeZone, row.RawJson);
        var result = OccurrenceQuery.ExpandMaster(series, fromDate, toDate, OccurrenceQuery.LocalMidnight(fromDate, zone), OccurrenceQuery.LocalMidnight(toDate, zone), exceptions.ContainsKey)
            .Select(i => new Instance(row.Id, i.Start, i.End))
            .ToList();

        // Moved Or Edited Instances Carry Their Own ID And Times; Canceled Ones Stay Out
        foreach (var exception in exceptions.Values)
        {
            if (exception.Status != "cancelled" && exception.StartMs is { } movedStart && exception.EndMs is { } movedEnd)
            {
                result.Add(new Instance(exception.Id, DateTimeOffset.FromUnixTimeMilliseconds(movedStart), DateTimeOffset.FromUnixTimeMilliseconds(movedEnd)));
            }
        }

        return [.. result.OrderBy(i => i.Start)];
    }

    /// <summary>Characters System.Text.Json's default encoder never escapes, so they appear in <c>raw_json</c> as typed.</summary>
    [GeneratedRegex("^[A-Za-z0-9%_.@-]+$")]
    private static partial Regex SafeForLike();

    /// <summary>One instance of a series: its event ID (the series', or an exception's) and times.</summary>
    sealed record Instance(string Id, DateTimeOffset Start, DateTimeOffset End);

    /// <summary>A stored exception to a series, keyed by the start of the instance it replaces.</summary>
    sealed record Change(string Id, string Status, long? StartMs, long? EndMs, long OriginalStartMs)
    {
        public static Change Read(SqliteDataReader r) => new(
            r.GetString(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt64(2),
            r.IsDBNull(3) ? null : r.GetInt64(3),
            r.GetInt64(4));
    }

    /// <summary>A stored event row the search reads, with its calendar's color.</summary>
    sealed record Row(
        string AccountId,
        string CalendarId,
        string Id,
        string? ICalUid,
        long? StartMs,
        long? EndMs,
        bool IsAllDay,
        bool IsMaster,
        string? TimeZone,
        string RawJson,
        string Color)
    {
        public static Row Read(SqliteDataReader r) => new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.GetStringOrNull(3),
            r.IsDBNull(4) ? null : r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.GetBoolean(6),
            r.GetBoolean(7),
            r.GetStringOrNull(8),
            r.GetString(9),
            r.GetString(10));
    }
}
