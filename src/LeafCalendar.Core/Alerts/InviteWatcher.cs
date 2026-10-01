using System.Globalization;
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>An invitation to notify about: the next instance, the event's details, whether it's an update, and its tag.</summary>
public sealed record InviteAlert(CalendarOccurrence Occurrence, EventDetails Details, bool IsUpdate, string Tag);

/// <summary>
/// Finds invitations to notify about (spec 8.4 item 3), after each sync that wrote something.
/// </summary>
/// <remarks>
/// <para>
/// An invitation is an event on a shown calendar, not canceled, not over, that someone else organizes and that you
/// haven't answered (<c>needsAction</c>). Each one is recorded in <see cref="AlertLedger"/> under its event and
/// Google's <c>sequence</c> number, which Google raises only when the organizer changes something that matters (time,
/// place, ...). So another guest's reply, which changes the event but not its sequence, never notifies twice, and an
/// organizer's change notifies once as an update.
/// </para>
/// <para>
/// An account is looked at only once every calendar of it finished its first sync, and that first look records its
/// existing invitations without notifying, so adding an account doesn't bring a flood.
/// </para>
/// </remarks>
public static class InviteWatcher
{
    const string SeededMark = "invites-seeded:";

    // How far ahead the next instance of a repeating invitation is looked for
    const int LookaheadDays = 366;

    // "needsAction" in the JSON is a cheap first filter; the JSON is then read properly
    const string Sql = """
        SELECT e.account_id, e.calendar_id, e.id, e.raw_json, e.is_recurring_master
        FROM events e
        JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
        WHERE e.account_id = $account
          AND COALESCE(c.leaf_hidden, c.hidden) = 0
          AND e.status <> 'cancelled'
          AND (e.end_utc > $now OR e.is_recurring_master = 1)
          AND e.raw_json LIKE '%needsAction%'
        ORDER BY e.start_utc;
        """;

    /// <summary>Invitations that are new since the last call (each only once, ever), recorded as it goes.</summary>
    public static IReadOnlyList<InviteAlert> TakeNew(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var result = new List<InviteAlert>();
        IReadOnlyList<CalendarOccurrence>? upcoming = null;

        foreach (var account in AccountStore.GetAll(conn))
        {
            // Not Before The Account's First Sync Finished
            if (!IsSynced(conn, account.Id))
            {
                continue;
            }

            var seeded = AlertLedger.GetMark(conn, SeededMark + account.Id) is not null;
            var rows   = conn.Query(null, Sql, r => (Calendar: r.GetString(1), Id: r.GetString(2), Json: r.GetString(3), IsMaster: r.GetBoolean(4)), ("$account", account.Id), ("$now", now.ToUnixTimeMilliseconds()));

            foreach (var row in rows)
            {
                if (!IsOpenInvite(row.Json, out var sequence))
                {
                    continue;
                }

                // Seen Before (same event, same sequence)
                var prefix = $"Invite|{account.Id}|{row.Calendar}|{row.Id}|";
                var key    = prefix + sequence.ToString(CultureInfo.InvariantCulture);
                if (AlertLedger.Contains(conn, key))
                {
                    continue;
                }

                // Its Next Instance (loaded once, only when something needs it)
                upcoming ??= Upcoming(conn, now, zone);
                var occurrence = upcoming.FirstOrDefault(o => o.AccountId == account.Id && o.CalendarId == row.Calendar && o.EventId == row.Id && o.EndIn(zone) > now);
                if (occurrence is null && seeded)
                {
                    continue;
                }

                // Record, Then Notify (unless this is the account's first look)
                var isUpdate = AlertLedger.HasPrefix(conn, prefix);
                var tag      = Alert.TagFor(key);
                var end      = row.IsMaster || occurrence is null ? now.AddDays(LookaheadDays) : occurrence.EndIn(zone);
                AlertLedger.TryAdd(conn, key, AlertKind.Invite, tag, end, now);
                if (seeded && occurrence is not null)
                {
                    result.Add(new InviteAlert(occurrence, EventDetailsParser.Parse(row.Json), isUpdate, tag));
                }
            }

            if (!seeded)
            {
                AlertLedger.SetMark(conn, SeededMark + account.Id, 1);
            }
        }

        return result;
    }

    static IReadOnlyList<CalendarOccurrence> Upcoming(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(LookaheadDays), zone, includeDeclined: true);
    }

    // Every calendar of the account has a sync token (its first sync finished)
    static bool IsSynced(SqliteConnection conn, string accountId) =>
        conn.Query(
            null,
            "SELECT COUNT(*) > 0 AND SUM(CASE WHEN sync_token IS NULL THEN 1 ELSE 0 END) = 0 FROM calendars WHERE account_id = $account;",
            r => !r.IsDBNull(0) && r.GetBoolean(0),
            ("$account", accountId)).Single();

    // Someone else organizes it and your own reply is still "needsAction"; also returns Google's sequence number
    static bool IsOpenInvite(string json, out int sequence)
    {
        sequence = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("organizer", out var organizer) && organizer.ValueKind == JsonValueKind.Object
                && organizer.TryGetProperty("self", out var mine) && mine.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            if (root.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt32(out var n))
            {
                sequence = n;
            }

            if (!root.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var attendee in attendees.EnumerateArray())
            {
                if (attendee.ValueKind == JsonValueKind.Object
                    && attendee.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
                {
                    return attendee.TryGetProperty("responseStatus", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "needsAction";
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
