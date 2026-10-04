using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>What an outbox entry asks Google to do.</summary>
public enum OutboxOperation
{
    /// <summary><c>events.insert</c> with Leaf's client-generated ID.</summary>
    Create,

    /// <summary><c>events.patch</c> with <c>If-Match</c>.</summary>
    Patch,

    /// <summary><c>events.delete</c> with <c>If-Match</c>.</summary>
    Delete,

    /// <summary><c>events.move</c> to another calendar of the same account.</summary>
    Move,

    /// <summary>The signed-in user's reply (sent against Google's latest copy).</summary>
    Rsvp,
}

/// <summary>Where an outbox entry stands.</summary>
public enum OutboxState
{
    /// <summary>Waiting to be sent.</summary>
    Pending,

    /// <summary>Google had a different version; the user decides.</summary>
    Conflict,
}

/// <summary>
/// One edit waiting for Google. <see cref="Payload"/> is the JSON body (create, patch, RSVP) or the
/// destination calendar ID (move). <see cref="BeforeJson"/> is an <see cref="EventStore.Snapshot"/> of the
/// rows before the edit. <see cref="NotBefore"/> holds the entry back (the undo window for deletes).
/// <see cref="DependsOn"/> holds it while that entry is still in the outbox (a split's new series waits for
/// the old series' end).
/// </summary>
public sealed record OutboxEntry(
    long Seq,
    string AccountId,
    string CalendarId,
    string EventId,
    OutboxOperation Operation,
    string? Payload,
    string? BaseEtag,
    bool SendUpdates,
    string? BeforeJson,
    DateTimeOffset? NotBefore,
    OutboxState State = OutboxState.Pending,
    int Attempts = 0,
    string? LastError = null,
    long? DependsOn = null);

/// <summary>
/// Reads and writes the <c>outbox</c> table.
/// </summary>
/// <remarks>
/// Entries are sent in <c>seq</c> order per account. When Google accepts one, later entries for the same
/// event are rebased onto the ETag Google returned (<see cref="Rebase"/>), since they were made on top of it.
/// </remarks>
public static class OutboxStore
{
    private const string Columns = """
        SELECT seq, account_id, calendar_id, event_id, operation, payload, base_etag, send_updates,
               before_json, not_before, state, attempts, last_error, depends_on
        FROM outbox
        """;

    /// <summary>Queues an entry and returns its sequence number.</summary>
    public static long Add(SqliteConnection conn, SqliteTransaction? tx, OutboxEntry entry) =>
        conn.Query(
            tx,
            """
            INSERT INTO outbox (account_id, calendar_id, event_id, operation, payload, base_etag, send_updates, before_json, not_before, depends_on)
            VALUES ($account, $calendar, $event, $operation, $payload, $etag, $send, $before, $notBefore, $dependsOn)
            RETURNING seq;
            """,
            r => r.GetInt64(0),
            ("$account", entry.AccountId),
            ("$calendar", entry.CalendarId),
            ("$event", entry.EventId),
            ("$operation", ToText(entry.Operation)),
            ("$payload", entry.Payload),
            ("$etag", entry.BaseEtag),
            ("$send", entry.SendUpdates),
            ("$before", entry.BeforeJson),
            ("$notBefore", entry.NotBefore?.ToUnixTimeMilliseconds()),
            ("$dependsOn", entry.DependsOn)).Single();

    /// <summary>One entry, or null.</summary>
    public static OutboxEntry? Get(SqliteConnection conn, SqliteTransaction? tx, long seq) =>
        conn.Query(tx, Columns + " WHERE seq = $seq;", Map, ("$seq", seq)).SingleOrDefault();

    /// <summary>An account's pending entries, oldest first.</summary>
    public static IReadOnlyList<OutboxEntry> Pending(SqliteConnection conn, string accountId) =>
        conn.Query(null, Columns + " WHERE account_id = $account AND state = 'pending' ORDER BY seq;", Map, ("$account", accountId));

    /// <summary>Every entry (any state) for one event, oldest first.</summary>
    public static IReadOnlyList<OutboxEntry> ForEvent(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string eventId) =>
        conn.Query(
            tx,
            Columns + " WHERE account_id = $account AND calendar_id = $calendar AND event_id = $event ORDER BY seq;",
            Map,
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$event", eventId));

    /// <summary>Removes an entry (sent, undone, or dropped). Its conflict row goes with it.</summary>
    public static void Remove(SqliteConnection conn, SqliteTransaction? tx, long seq) =>
        conn.Execute(tx, "DELETE FROM outbox WHERE seq = $seq;", ("$seq", seq));

    /// <summary>
    /// Drops the entries that depend on <paramref name="seq"/> (it will never reach Google), with every entry and
    /// local row of their events, and anything that depends on those in turn. A split's new series goes this way
    /// when the old series' end is refused or the user keeps Google's version of it. A dependent delete (a move to
    /// another account deletes the original only after Google has the copy) is dropped alone and its rows put back,
    /// so the event is never lost; the edits queued before it still go.
    /// </summary>
    public static void DropDependents(SqliteConnection conn, SqliteTransaction? tx, long seq)
    {
        foreach (var dependent in conn.Query(tx, Columns + " WHERE depends_on = $seq;", Map, ("$seq", seq)))
        {
            // A Move's Delete Behind Its Copy: the original stays
            if (dependent.Operation == OutboxOperation.Delete)
            {
                Remove(conn, tx, dependent.Seq);
                DropDependents(conn, tx, dependent.Seq);
                EventStore.Restore(conn, tx, dependent.AccountId, dependent.CalendarId, dependent.EventId, dependent.BeforeJson ?? "[]");
                continue;
            }

            foreach (var entry in ForEvent(conn, tx, dependent.AccountId, dependent.CalendarId, dependent.EventId))
            {
                Remove(conn, tx, entry.Seq);
                DropDependents(conn, tx, entry.Seq);

                // A move already put the local row in its destination calendar
                var localCalendar = entry.Operation == OutboxOperation.Move ? entry.Payload ?? entry.CalendarId : entry.CalendarId;
                EventStore.Remove(conn, tx, entry.AccountId, localCalendar, entry.EventId);
            }

            EventStore.Remove(conn, tx, dependent.AccountId, dependent.CalendarId, dependent.EventId);
        }
    }

    /// <summary>
    /// Drops the entries queued after <paramref name="entry"/> for its event (edits made on top of one that will
    /// never reach Google), with what depends on them. A later move took the local row, and the edits after it,
    /// to its destination calendar, so the entries queued there go too. Returns the calendar the local row is in now.
    /// </summary>
    public static string DropLater(SqliteConnection conn, SqliteTransaction tx, OutboxEntry entry)
    {
        var calendarId = entry.Operation == OutboxOperation.Move ? entry.Payload ?? entry.CalendarId : entry.CalendarId;
        foreach (var later in ForEvent(conn, tx, entry.AccountId, calendarId, entry.EventId).Where(e => e.Seq > entry.Seq))
        {
            Remove(conn, tx, later.Seq);
            DropDependents(conn, tx, later.Seq);

            // A Later Move: the rest of the event's edits are queued under its destination
            if (later.Operation == OutboxOperation.Move)
            {
                return DropLater(conn, tx, later);
            }
        }

        return calendarId;
    }

    /// <summary>Counts a failed try. <paramref name="error"/> is a status or reason only, never event content.</summary>
    public static void RecordAttempt(SqliteConnection conn, long seq, string error) =>
        conn.Execute(null, "UPDATE outbox SET attempts = attempts + 1, last_error = $error WHERE seq = $seq;", ("$error", error), ("$seq", seq));

    /// <summary>Puts an entry back in the queue with a new base ETag ("Keep mine").</summary>
    public static void Requeue(SqliteConnection conn, SqliteTransaction? tx, long seq, string? baseEtag) =>
        conn.Execute(tx, "UPDATE outbox SET state = 'pending', base_etag = $etag WHERE seq = $seq;", ("$etag", baseEtag), ("$seq", seq));

    /// <summary>Rewrites an entry in place (same sequence number) and puts it back in the queue.</summary>
    public static void Replace(SqliteConnection conn, SqliteTransaction? tx, OutboxEntry entry) =>
        conn.Execute(
            tx,
            """
            UPDATE outbox SET account_id = $account, calendar_id = $calendar, event_id = $event, operation = $operation,
                              payload = $payload, base_etag = $etag, send_updates = $send, before_json = $before,
                              not_before = $notBefore, state = 'pending'
            WHERE seq = $seq;
            """,
            ("$account", entry.AccountId),
            ("$calendar", entry.CalendarId),
            ("$event", entry.EventId),
            ("$operation", ToText(entry.Operation)),
            ("$payload", entry.Payload),
            ("$etag", entry.BaseEtag),
            ("$send", entry.SendUpdates),
            ("$before", entry.BeforeJson),
            ("$notBefore", entry.NotBefore?.ToUnixTimeMilliseconds()),
            ("$seq", entry.Seq));

    /// <summary>Gives the later pending entries of one event the ETag Google just returned for it.</summary>
    public static void Rebase(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string eventId, long afterSeq, string? etag) =>
        conn.Execute(
            tx,
            """
            UPDATE outbox SET base_etag = $etag
            WHERE account_id = $account AND calendar_id = $calendar AND event_id = $event AND seq > $after AND state = 'pending';
            """,
            ("$etag", etag),
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$event", eventId),
            ("$after", afterSeq));

    /// <summary>
    /// SQL for the event IDs a pull must leave alone in <c>$calendar</c>: entries queued under it, plus moves
    /// headed into it (a move is stored under its source calendar with the destination in <c>payload</c>).
    /// </summary>
    internal const string ProtectedIdsSql = """
        SELECT event_id FROM outbox
        WHERE account_id = $account AND (calendar_id = $calendar OR (operation = 'move' AND payload = $calendar))
        """;

    /// <summary>IDs of a calendar's events that have entries (pending or conflicted); pulls leave these and their series exceptions alone.</summary>
    public static IReadOnlySet<string> EventIdsFor(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId) =>
        conn.Query(
            tx,
            ProtectedIdsSql.Replace("SELECT event_id", "SELECT DISTINCT event_id") + ";",
            r => r.GetString(0),
            ("$account", accountId),
            ("$calendar", calendarId)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Entries not yet accepted by Google (pending plus conflicted).</summary>
    public static int Count(SqliteConnection conn) =>
        conn.Query(null, "SELECT COUNT(*) FROM outbox;", r => r.GetInt32(0)).Single();

    /// <summary>
    /// Pending entries a sync would send at <paramref name="now"/>: past their undo window, and not behind a
    /// conflicted entry for the same event or a conflicted entry they depend on.
    /// </summary>
    public static int CountSendable(SqliteConnection conn, DateTimeOffset now) =>
        conn.Query(
            null,
            """
            SELECT COUNT(*) FROM outbox o
            WHERE o.state = 'pending'
              AND (o.not_before IS NULL OR o.not_before <= $now)
              AND NOT EXISTS (
                  SELECT 1 FROM outbox c
                  WHERE c.state = 'conflict'
                    AND ((c.account_id = o.account_id AND c.event_id = o.event_id AND c.seq < o.seq) OR c.seq = o.depends_on));
            """,
            r => r.GetInt32(0),
            ("$now", now.ToUnixTimeMilliseconds())).Single();

    /// <summary>One account's entries not yet accepted by Google.</summary>
    public static int CountForAccount(SqliteConnection conn, string accountId) =>
        conn.Query(null, "SELECT COUNT(*) FROM outbox WHERE account_id = $account;", r => r.GetInt32(0), ("$account", accountId)).Single();

    internal static OutboxEntry Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        FromText(r.GetString(4)),
        r.GetStringOrNull(5),
        r.GetStringOrNull(6),
        r.GetBoolean(7),
        r.GetStringOrNull(8),
        r.GetUnixMsOrNull(9),
        r.GetString(10) == "conflict" ? OutboxState.Conflict : OutboxState.Pending,
        r.GetInt32(11),
        r.GetStringOrNull(12),
        r.IsDBNull(13) ? null : r.GetInt64(13));

    private static string ToText(OutboxOperation operation) => operation switch
    {
        OutboxOperation.Create => "create",
        OutboxOperation.Delete => "delete",
        OutboxOperation.Move => "move",
        OutboxOperation.Rsvp => "rsvp",
        _ => "patch",
    };

    private static OutboxOperation FromText(string text) => text switch
    {
        "create" => OutboxOperation.Create,
        "delete" => OutboxOperation.Delete,
        "move" => OutboxOperation.Move,
        "rsvp" => OutboxOperation.Rsvp,
        _ => OutboxOperation.Patch,
    };
}
