using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Search;

public static partial class EventSearch
{
    /// <summary>
    /// The stored events read and parsed once, so the command menu matches each keystroke in memory (the time it takes
    /// to type it) instead of scanning and parsing rows per search. Built off the UI thread when the menu opens, and
    /// again after the events change; the same rows, matching, and order as <see cref="Find"/>.
    /// </summary>
    public sealed class Index
    {
        const string Sql = """
            SELECT e.account_id, e.calendar_id, e.id, e.ical_uid, e.start_utc, e.end_utc, e.is_all_day, e.is_recurring_master,
                   e.start_time_zone, e.raw_json, COALESCE(c.leaf_color, c.background_color, '#4285F4')
            FROM events e
            JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
            WHERE COALESCE(c.leaf_hidden, c.hidden) = 0 AND c.hidden = 0
              AND e.status <> 'cancelled'
            ORDER BY e.is_recurring_master DESC, ABS(COALESCE(e.start_utc, 0) - $now)
            LIMIT $max;
            """;

        readonly List<Entry> _entries;

        Index(List<Entry> entries, DateTimeOffset builtAt)
        {
            _entries = entries;
            BuiltAt  = builtAt;
        }

        /// <summary>When the events were read (the nearest to then are the ones kept).</summary>
        public DateTimeOffset BuiltAt { get; }

        /// <summary>Reads and parses the events (at most <see cref="MaxRows"/>, series first, then nearest to <paramref name="now"/>).</summary>
        public static Index Build(SqliteConnection conn, DateTimeOffset now)
        {
            var entries = new List<Entry>();
            foreach (var row in conn.Query(null, Sql, Row.Read, ("$now", now.ToUnixTimeMilliseconds()), ("$max", MaxRows)))
            {
                if (Parse(row.RawJson) is { } parsed)
                {
                    entries.Add(new Entry(row, parsed.Title, parsed.ColorId, parsed.Fields));
                }
            }

            return new Index(entries, now);
        }

        /// <summary>Finds events matching every word of <paramref name="query"/>, like <see cref="EventSearch.Find"/>.</summary>
        public IReadOnlyList<SearchHit> Find(SqliteConnection conn, string? query, DateTimeOffset now, TimeZoneInfo zone)
        {
            var words = Words(query);
            if (words.Count == 0)
            {
                return [];
            }

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var hits  = new List<(SearchHit Hit, string Key)>();
            foreach (var entry in _entries)
            {
                if (BestField(entry.Fields, words) is { } field)
                {
                    hits.Add(ToHit(conn, entry.Row, (entry.Title, entry.ColorId, field), now, today, zone));
                }
            }

            return Order(hits, now);
        }

        sealed record Entry(Row Row, string Title, string? ColorId, string[] Fields);
    }
}
