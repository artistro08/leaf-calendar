using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Search;

public static partial class EventSearch
{
    /// <summary>
    /// The stored events read and parsed once, so the command menu matches each keystroke in memory (the time it takes
    /// to type it) instead of scanning and parsing rows per search. Built off the UI thread when the menu opens, and
    /// again after the events change; the same rows, matching, and order as <see cref="Find"/>. With more events than
    /// <see cref="MaxRows"/> it holds none (its rows would drop older matches <see cref="Find"/>'s SQL keeps), and its
    /// search returns null. Each matched series is expanded once per day and zone; use it from one thread.
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

        // Null when there were more events than MaxRows
        readonly List<Entry>? _entries;

        Index(List<Entry>? entries, DateTimeOffset builtAt)
        {
            _entries = entries;
            BuiltAt  = builtAt;
        }

        /// <summary>When the events were read.</summary>
        public DateTimeOffset BuiltAt { get; }

        /// <summary>Reads and parses the events, unless there are more than <see cref="MaxRows"/>.</summary>
        public static Index Build(SqliteConnection conn, DateTimeOffset now)
        {
            // More Than It Holds: Find Searches Them, Narrowed In SQL First
            var rows = conn.Query(null, Sql, Row.Read, ("$now", now.ToUnixTimeMilliseconds()), ("$max", MaxRows + 1));
            if (rows.Count > MaxRows)
            {
                return new Index(null, now);
            }

            var entries = new List<Entry>();
            foreach (var row in rows)
            {
                if (Parse(row.RawJson) is { } parsed)
                {
                    entries.Add(new Entry(row, parsed.Title, parsed.ColorId, parsed.Fields));
                }
            }

            return new Index(entries, now);
        }

        /// <summary>
        /// Finds events matching every word of <paramref name="query"/>, like <see cref="EventSearch.Find"/>; null when
        /// the index holds no events because there were too many (search with <see cref="EventSearch.Find"/>).
        /// </summary>
        public IReadOnlyList<SearchHit>? Find(SqliteConnection conn, string? query, DateTimeOffset now, TimeZoneInfo zone)
        {
            if (_entries is null)
            {
                return null;
            }

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
                    hits.Add(ToHit(entry.Row, (entry.Title, entry.ColorId, field), now, zone, () => entry.InstancesFor(conn, today, zone)));
                }
            }

            return Order(hits, now, zone);
        }

        sealed record Entry(Row Row, string Title, string? ColorId, string[] Fields)
        {
            (DateOnly Today, TimeZoneInfo Zone, List<Instance> Instances)? _expanded;

            // A series' instances, expanded on its first match and kept for the same day and zone (the index is rebuilt when events change)
            public List<Instance> InstancesFor(SqliteConnection conn, DateOnly today, TimeZoneInfo zone)
            {
                if (_expanded is not { } expanded || expanded.Today != today || !expanded.Zone.Equals(zone))
                {
                    expanded  = (today, zone, Instances(conn, Row, today, zone));
                    _expanded = expanded;
                }

                return expanded.Instances;
            }
        }
    }
}
