using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.People;

/// <summary>
/// "People you meet often": guests from your own synced events, offered first in the guest box.
/// </summary>
/// <remarks>
/// Computed from local events each time (the last 180 days and the next 30), and never stored. Names and addresses are
/// untrusted: names are cleaned and capped like contact names, and only addresses that pass the guest field's rule are
/// kept. You, rooms, and canceled events are left out.
/// </remarks>
public static class FrequentPeople
{
    private const int MaxPeople = 50;

    private static readonly TimeSpan Back = TimeSpan.FromDays(180);
    private static readonly TimeSpan Ahead = TimeSpan.FromDays(30);

    /// <summary>
    /// The account's guests, most frequent first (ties: the one you met most recently), at most 50. An address counts
    /// once per event (a repeating one: once per instance in the window, however long ago it began), ignoring case;
    /// its name is the last non-empty one seen.
    /// </summary>
    /// <exception cref="SqliteException">The database couldn't be read.</exception>
    public static IReadOnlyList<Contact> Load(SqliteConnection conn, string accountId, DateTimeOffset now)
    {
        var people = new Dictionary<string, (string Email, string Name, int Count, long Latest)>(StringComparer.OrdinalIgnoreCase);
        var from = now - Back;
        var to = now + Ahead;

        // Single Events, And A Series' Moved Or Edited Instances (their own rows)
        foreach (var (start, email, name, isRoom, isSelf) in Attendees(
            conn,
            "AND is_recurring_master = 0 AND start_utc BETWEEN $from AND $to AND raw_json LIKE '%\"attendees\"%'",
            ("$account", accountId),
            ("$from", from.ToUnixTimeMilliseconds()),
            ("$to", to.ToUnixTimeMilliseconds())))
        {
            Count(email, name, isRoom, isSelf, 1, start);
        }

        // Repeating Series: Each Instance In The Window, Skipping Those An Exception Row Replaces (counted above)
        var replaced = conn.Query(
            null,
            "SELECT calendar_id, recurring_event_id, original_start_utc FROM events WHERE account_id = $account AND original_start_utc IS NOT NULL;",
            r => (r.GetString(0), r.GetStringOrNull(1), r.GetInt64(2)),
            ("$account", accountId)).ToHashSet();

        var series = conn.Query(
            null,
            """
            SELECT calendar_id, id, start_utc, end_utc, is_all_day, start_time_zone, raw_json FROM events
            WHERE account_id = $account AND status <> 'cancelled' AND is_recurring_master = 1 AND start_utc <= $to AND raw_json LIKE '%"attendees"%';
            """,
            r => (Calendar: r.GetString(0), Id: r.GetString(1), Row: new OccurrenceQuery.SeriesRow(
                r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetBoolean(4), r.GetStringOrNull(5), r.GetString(6))),
            ("$account", accountId),
            ("$to", to.ToUnixTimeMilliseconds()));

        foreach (var (calendar, id, row) in series)
        {
            List<long> starts;
            try
            {
                starts = [.. OccurrenceQuery.ExpandMaster(row, DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime).AddDays(1), from, to,
                        ms => replaced.Contains((calendar, id, ms)))
                    .Select(i => i.Start.ToUnixTimeMilliseconds())];
            }
            catch (JsonException)
            {
                // A series Leaf can't read has no guests to offer
                continue;
            }

            if (starts.Count > 0)
            {
                foreach (var (email, name, isRoom, isSelf) in Guests(row.RawJson))
                {
                    Count(email, name, isRoom, isSelf, starts.Count, starts.Max());
                }
            }
        }

        return [.. people.Values
            .OrderByDescending(p => p.Count)
            .ThenByDescending(p => p.Latest)
            .Take(MaxPeople)
            .Select(p => new Contact(p.Name, p.Email))];

        void Count(string email, string name, bool isRoom, bool isSelf, int times, long latest)
        {
            if (isRoom || isSelf)
            {
                return;
            }

            var seen = people.GetValueOrDefault(email, (email, "", 0, 0));
            people[email] = (seen.Email, name.Length > 0 ? name : seen.Name, seen.Count + times, Math.Max(seen.Latest, latest));
        }
    }

    /// <summary>
    /// The people whose name has a word starting with <paramref name="query"/>, or whose address or its domain does
    /// (ignoring case), in their order, at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<Contact> Match(IReadOnlyList<Contact> people, string query, int max = 8)
    {
        ArgumentNullException.ThrowIfNull(people);

        var text = query?.Trim() ?? "";
        if (text.Length == 0)
        {
            return [];
        }

        return [.. people.Where(p => StartsAnyWord(p.Name, text) || Starts(p.Email, text) || Starts(p.Email[(p.Email.IndexOf('@', StringComparison.Ordinal) + 1)..], text)).Take(max)];
    }

    /// <summary>
    /// Every attendee of the account's stored events (not canceled) matching the extra SQL filter, in start order:
    /// the event start (Unix ms), the checked address, the cleaned name, and whether it's a room or you. Attendees
    /// without a valid address are skipped. <paramref name="parameters"/> must bind <c>$account</c>.
    /// </summary>
    internal static IEnumerable<(long Start, string Email, string Name, bool IsRoom, bool IsSelf)> Attendees(SqliteConnection conn, string filter, params (string Name, object? Value)[] parameters)
    {
        var rows = conn.Query(
            null,
            $"SELECT start_utc, raw_json FROM events WHERE account_id = $account AND status <> 'cancelled' {filter} ORDER BY start_utc;",
            r => (Start: r.IsDBNull(0) ? 0 : r.GetInt64(0), Json: r.GetString(1)),
            parameters);

        foreach (var (start, json) in rows)
        {
            foreach (var (email, name, isRoom, isSelf) in Guests(json))
            {
                yield return (start, email, name, isRoom, isSelf);
            }
        }
    }

    // One event's attendees with a valid address, each address once; none for a row Leaf can't read
    private static List<(string Email, string Name, bool IsRoom, bool IsSelf)> Guests(string json)
    {
        List<(string Email, string Name, bool IsRoom, bool IsSelf)> found = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var attendee in attendees.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
            {
                if (ContactSearch.ValidEmail(Text(attendee, "email")) is not { } email)
                {
                    continue;
                }

                found.Add((email, ContactSearch.Plain(Text(attendee, "displayName"), ContactSearch.MaxName), EventJson.IsRoom(attendee, email), attendee.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True));
            }
        }
        catch (JsonException)
        {
            // A row Leaf can't read has no guests to offer
            return [];
        }

        // An Address Listed Twice In One Event Counts Once
        return [.. found.DistinctBy(f => f.Email, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Starts(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool StartsAnyWord(string name, string prefix) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => Starts(word, prefix)) || (prefix.Contains(' ', StringComparison.Ordinal) && Starts(name, prefix));
}
