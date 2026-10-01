using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
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
    const int MaxPeople = 50;

    static readonly TimeSpan Back  = TimeSpan.FromDays(180);
    static readonly TimeSpan Ahead = TimeSpan.FromDays(30);

    /// <summary>
    /// The account's guests, most frequent first (ties: the one you met most recently), at most 50. An address counts
    /// once per event, ignoring case; its name is the last non-empty one seen.
    /// </summary>
    /// <exception cref="SqliteException">The database couldn't be read.</exception>
    public static IReadOnlyList<Contact> Load(SqliteConnection conn, string accountId, DateTimeOffset now)
    {
        var people = new Dictionary<string, (string Email, string Name, int Count, long Latest)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (start, email, name, isRoom, isSelf) in Attendees(
            conn,
            "AND start_utc BETWEEN $from AND $to AND raw_json LIKE '%\"attendees\"%'",
            ("$account", accountId),
            ("$from", (now - Back).ToUnixTimeMilliseconds()),
            ("$to", (now + Ahead).ToUnixTimeMilliseconds())))
        {
            if (isRoom || isSelf)
            {
                continue;
            }

            var seen = people.GetValueOrDefault(email, (email, "", 0, 0));
            people[email] = (seen.Email, name.Length > 0 ? name : seen.Name, seen.Count + 1, Math.Max(seen.Latest, start));
        }

        return [.. people.Values
            .OrderByDescending(p => p.Count)
            .ThenByDescending(p => p.Latest)
            .Take(MaxPeople)
            .Select(p => new Contact(p.Name, p.Email))];
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
            List<(string, string, bool, bool)> found = [];
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
                {
                    continue;
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
                continue;
            }

            foreach (var (email, name, isRoom, isSelf) in found)
            {
                yield return (start, email, name, isRoom, isSelf);
            }
        }
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Starts(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    static bool StartsAnyWord(string name, string prefix) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => Starts(word, prefix)) || (prefix.Contains(' ', StringComparison.Ordinal) && Starts(name, prefix));
}
