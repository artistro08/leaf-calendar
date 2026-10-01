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
/// Each calendar is looked at only once its first sync finished, and that first look records its existing invitations
/// without notifying, so adding an account or subscribing to a calendar doesn't bring a flood. Invitations on hidden
/// calendars are recorded quietly too, so showing a calendar again doesn't either, and so are unanswered series that
/// have ended (nothing is left to answer).
/// </para>
/// </remarks>
public static class InviteWatcher
{
    const string SeededMark = "invites-seeded:";

    // How far ahead the next instance of a repeating invitation is looked for
    const int LookaheadDays = 366;

    // Calendars whose first sync finished, shown or hidden
    const string CalendarsSql = """
        SELECT account_id, id, COALESCE(leaf_hidden, hidden)
        FROM calendars
        WHERE sync_token IS NOT NULL
        ORDER BY account_id, sort_order;
        """;

    // "needsAction" in the JSON is a cheap first filter; the JSON is then read properly
    const string Sql = """
        SELECT e.id, e.raw_json, e.is_recurring_master
        FROM events e
        WHERE e.account_id = $account
          AND e.calendar_id = $calendar
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
        var calendars = conn.Query(null, CalendarsSql, r => (Account: r.GetString(0), Id: r.GetString(1), Hidden: r.GetBoolean(2)));

        foreach (var calendar in calendars)
        {
            // The First Look At A Calendar, Or A Hidden One, Records Quietly
            var mark   = SeededMark + calendar.Account + "|" + calendar.Id;
            var seeded = AlertLedger.GetMark(conn, mark) is not null;
            var notify = seeded && !calendar.Hidden;
            var rows   = conn.Query(null, Sql, r => (Id: r.GetString(0), Json: r.GetString(1), IsMaster: r.GetBoolean(2)), ("$account", calendar.Account), ("$calendar", calendar.Id), ("$now", now.ToUnixTimeMilliseconds()));

            foreach (var row in rows)
            {
                if (!IsOpenInvite(row.Json, out var sequence))
                {
                    continue;
                }

                // Seen Before (same event, same sequence)
                var prefix = $"Invite|{calendar.Account}|{calendar.Id}|{row.Id}|";
                var key    = prefix + sequence.ToString(CultureInfo.InvariantCulture);
                if (AlertLedger.Contains(conn, key))
                {
                    continue;
                }

                // Its Next Instance (loaded once, only when something needs it); a single event too far ahead waits until it's in range
                upcoming ??= Upcoming(conn, now, zone);
                var occurrence = upcoming.FirstOrDefault(o => o.AccountId == calendar.Account && o.CalendarId == calendar.Id && o.EventId == row.Id && o.EndIn(zone) > now);
                if (occurrence is null && notify && !row.IsMaster)
                {
                    continue;
                }

                // Record, Then Notify (not on a first look, a hidden calendar, or a series that has ended)
                var isUpdate = AlertLedger.HasPrefix(conn, prefix);
                var tag      = Alert.TagFor(key);
                var end      = row.IsMaster || occurrence is null ? now.AddDays(LookaheadDays) : occurrence.EndIn(zone);
                AlertLedger.TryAdd(conn, key, AlertKind.Invite, tag, end, now);
                if (notify && occurrence is not null)
                {
                    result.Add(new InviteAlert(occurrence, EventDetailsParser.Parse(row.Json), isUpdate, tag));
                }
            }

            if (!seeded)
            {
                AlertLedger.SetMark(conn, mark, 1);
            }
        }

        return result;
    }

    /// <summary>Forgets an account's recorded invitations, reminders, and first-look marks (the account was removed).</summary>
    public static void Forget(SqliteConnection conn, string accountId, SqliteTransaction? tx = null)
    {
        AlertLedger.RemoveForAccount(conn, accountId, tx);
        AlertLedger.DeleteMarksStartingWith(conn, SeededMark + accountId + "|", tx);

        // The Mark Before Per-Calendar Marks (one per account)
        AlertLedger.DeleteMark(conn, SeededMark + accountId, tx);
    }

    static IReadOnlyList<CalendarOccurrence> Upcoming(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(LookaheadDays), zone, includeDeclined: true);
    }

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
