using System.Text.Json;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// A stored event row. Times are UTC. All-day dates are stored as midnight UTC with
/// <see cref="IsAllDay"/> set, since all-day events float across time zones.
/// </summary>
public sealed record StoredEvent(
    string Id,
    string Status,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool IsAllDay,
    string? RecurringEventId,
    DateTimeOffset? OriginalStart,
    string? Etag,
    string RawJson);

/// <summary>Reads and writes the <c>events</c> table.</summary>
public static class EventStore
{
    /// <summary>
    /// Applies one event from Google.
    /// </summary>
    /// <remarks>
    /// A canceled event without <c>recurringEventId</c> is a deletion: the row and any of its
    /// occurrence exceptions are removed. A canceled occurrence of a repeating event is kept,
    /// since it hides that one date when the series is expanded. Everything else is upserted with
    /// Google's full JSON kept in <c>raw_json</c>.
    /// </remarks>
    public static void Apply(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, JsonElement item)
    {
        var ev = JsonSerializer.Deserialize(item, GoogleJsonContext.Default.GoogleEvent)
            ?? throw new InvalidDataException("Google returned an empty event.");

        // Deleted Event Or Series (an event with local edits waiting in the outbox stays until they're sent)
        if (ev.Status == "cancelled" && ev.RecurringEventId is null)
        {
            conn.Execute(
                tx,
                $"""
                DELETE FROM events
                WHERE account_id = $account AND calendar_id = $calendar AND (id = $id OR recurring_event_id = $id)
                  AND id NOT IN ({OutboxStore.ProtectedIdsSql})
                  AND (recurring_event_id IS NULL OR recurring_event_id NOT IN ({OutboxStore.ProtectedIdsSql}));
                """,
                ("$account", accountId),
                ("$calendar", calendarId),
                ("$id", ev.Id));
            return;
        }

        // Upsert
        conn.Execute(
            tx,
            """
            INSERT INTO events (account_id, calendar_id, id, ical_uid, etag, status, start_utc, end_utc, is_all_day, start_time_zone,
                                is_recurring_master, recurring_event_id, original_start_utc, updated_utc, raw_json)
            VALUES ($account, $calendar, $id, $uid, $etag, $status, $start, $end, $allDay, $zone,
                    $master, $recurringId, $originalStart, $updated, $raw)
            ON CONFLICT (account_id, calendar_id, id) DO UPDATE SET
                ical_uid            = excluded.ical_uid,
                etag                = excluded.etag,
                status              = excluded.status,
                start_utc           = excluded.start_utc,
                end_utc             = excluded.end_utc,
                is_all_day          = excluded.is_all_day,
                start_time_zone     = excluded.start_time_zone,
                is_recurring_master = excluded.is_recurring_master,
                recurring_event_id  = excluded.recurring_event_id,
                original_start_utc  = excluded.original_start_utc,
                updated_utc         = excluded.updated_utc,
                raw_json            = excluded.raw_json;
            """,
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", ev.Id),
            ("$uid", ev.ICalUid),
            ("$etag", ev.Etag),
            ("$status", ev.Status ?? "confirmed"),
            ("$start", ToUnixMs(ev.Start)),
            ("$end", ToUnixMs(ev.End)),
            ("$allDay", ev.Start?.Date is not null),
            ("$zone", ev.Start?.TimeZone),
            ("$master", ev.Recurrence is { Count: > 0 }),
            ("$recurringId", ev.RecurringEventId),
            ("$originalStart", ToUnixMs(ev.OriginalStartTime)),
            ("$updated", ev.Updated?.ToUnixTimeMilliseconds()),
            ("$raw", item.GetRawText()));
    }

    /// <summary>Applies an event given as JSON text (local edits use this; same rules as <see cref="Apply"/>).</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static void ApplyJson(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        Apply(conn, tx, accountId, calendarId, doc.RootElement);
    }

    /// <summary>Removes an event and, for a series, its exceptions (a local delete).</summary>
    public static void Remove(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string id) =>
        conn.Execute(
            tx,
            "DELETE FROM events WHERE account_id = $account AND calendar_id = $calendar AND (id = $id OR recurring_event_id = $id);",
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", id));

    /// <summary>Removes a series' exceptions whose original start is at or after <paramref name="originalStartFrom"/> (a split series).</summary>
    public static void RemoveExceptionsFrom(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string masterId, DateTimeOffset originalStartFrom) =>
        conn.Execute(
            tx,
            """
            DELETE FROM events
            WHERE account_id = $account AND calendar_id = $calendar AND recurring_event_id = $master AND original_start_utc >= $from;
            """,
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$master", masterId),
            ("$from", originalStartFrom.ToUnixTimeMilliseconds()));

    /// <summary>Moves an event (and a series' exceptions) to another calendar of the same account.</summary>
    public static void MoveCalendar(SqliteConnection conn, SqliteTransaction? tx, string accountId, string fromCalendarId, string toCalendarId, string id) =>
        conn.Execute(
            tx,
            "UPDATE events SET calendar_id = $to WHERE account_id = $account AND calendar_id = $from AND (id = $id OR recurring_event_id = $id);",
            ("$to", toCalendarId),
            ("$account", accountId),
            ("$from", fromCalendarId),
            ("$id", id));

    /// <summary>Stores the ETag Google returned while the local JSON keeps later, unsent edits.</summary>
    public static void SetEtag(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string id, string? etag) =>
        conn.Execute(
            tx,
            "UPDATE events SET etag = $etag WHERE account_id = $account AND calendar_id = $calendar AND id = $id;",
            ("$etag", etag),
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", id));

    /// <summary>The event's row and (for a series) its exception rows, as a JSON array (<c>[]</c> when missing).</summary>
    public static string Snapshot(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string id)
    {
        var rows = conn.Query(
            tx,
            """
            SELECT raw_json FROM events
            WHERE account_id = $account AND calendar_id = $calendar AND (id = $id OR recurring_event_id = $id)
            ORDER BY recurring_event_id IS NOT NULL;
            """,
            r => r.GetString(0),
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", id));

        return "[" + string.Join(",", rows) + "]";
    }

    /// <summary>Puts back the rows of a <see cref="Snapshot"/> (undo), replacing whatever is stored for the event now.</summary>
    /// <exception cref="JsonException">The snapshot is invalid.</exception>
    public static void Restore(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string id, string snapshot)
    {
        Remove(conn, tx, accountId, calendarId, id);

        using var doc = JsonDocument.Parse(snapshot);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            Apply(conn, tx, accountId, calendarId, row);
        }
    }

    /// <summary>Removes every event of a calendar before a full resync, except events with local edits waiting in the outbox.</summary>
    public static void DeleteAllForCalendar(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId) =>
        conn.Execute(
            tx,
            $"""
            DELETE FROM events
            WHERE account_id = $account AND calendar_id = $calendar
              AND id NOT IN ({OutboxStore.ProtectedIdsSql})
              AND (recurring_event_id IS NULL OR recurring_event_id NOT IN ({OutboxStore.ProtectedIdsSql}));
            """,
            ("$account", accountId),
            ("$calendar", calendarId));

    /// <summary>Returns one event, or null.</summary>
    public static StoredEvent? Get(SqliteConnection conn, string accountId, string calendarId, string id) =>
        Get(conn, null, accountId, calendarId, id);

    /// <summary>Returns one event, or null (inside <paramref name="tx"/> when one is open).</summary>
    public static StoredEvent? Get(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string id) =>
        conn.Query(
            tx,
            """
            SELECT id, status, start_utc, end_utc, is_all_day, recurring_event_id, original_start_utc, etag, raw_json
            FROM events WHERE account_id = $account AND calendar_id = $calendar AND id = $id;
            """,
            r => new StoredEvent(
                r.GetString(0),
                r.GetString(1),
                r.GetUnixMsOrNull(2),
                r.GetUnixMsOrNull(3),
                r.GetBoolean(4),
                r.GetStringOrNull(5),
                r.GetUnixMsOrNull(6),
                r.GetStringOrNull(7),
                r.GetString(8)),
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", id)).SingleOrDefault();

    /// <summary>Number of stored rows for a calendar (including canceled occurrences).</summary>
    public static int Count(SqliteConnection conn, string accountId, string calendarId) =>
        conn.Query(
            null,
            "SELECT COUNT(*) FROM events WHERE account_id = $account AND calendar_id = $calendar;",
            r => r.GetInt32(0),
            ("$account", accountId),
            ("$calendar", calendarId)).Single();

    static long? ToUnixMs(EventDateTime? value) => value switch
    {
        { DateTime: { } dateTime } => dateTime.ToUnixTimeMilliseconds(),
        { Date: { } date } => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds(),
        _ => null,
    };
}
