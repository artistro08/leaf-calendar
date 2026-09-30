using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// An outbox entry Google didn't accept because its copy changed. <see cref="GoogleJson"/> is null when
/// Google deleted the event or its copy couldn't be read; <see cref="LocalJson"/> is null when the local edit was a delete.
/// </summary>
public sealed record ConflictInfo(OutboxEntry Entry, string? LocalJson, string? GoogleJson, DateTimeOffset DetectedAt);

/// <summary>Reads and writes the <c>conflicts</c> table.</summary>
public static class ConflictStore
{
    /// <summary>Records a conflict and marks its outbox entry as waiting for the user.</summary>
    public static void Add(SqliteConnection conn, SqliteTransaction? tx, long seq, string? localJson, string? googleJson, DateTimeOffset detectedAt)
    {
        conn.Execute(
            tx,
            """
            INSERT INTO conflicts (outbox_seq, local_json, google_json, detected_utc) VALUES ($seq, $local, $google, $at)
            ON CONFLICT (outbox_seq) DO UPDATE SET local_json = excluded.local_json, google_json = excluded.google_json, detected_utc = excluded.detected_utc;
            """,
            ("$seq", seq),
            ("$local", localJson),
            ("$google", googleJson),
            ("$at", detectedAt.ToUnixTimeMilliseconds()));
        conn.Execute(tx, "UPDATE outbox SET state = 'conflict' WHERE seq = $seq;", ("$seq", seq));
    }

    /// <summary>Every open conflict, oldest first.</summary>
    public static IReadOnlyList<ConflictInfo> GetAll(SqliteConnection conn) =>
        conn.Query(
            null,
            """
            SELECT o.seq, o.account_id, o.calendar_id, o.event_id, o.operation, o.payload, o.base_etag, o.send_updates,
                   o.before_json, o.not_before, o.state, o.attempts, o.last_error, c.local_json, c.google_json, c.detected_utc
            FROM conflicts c JOIN outbox o ON o.seq = c.outbox_seq
            ORDER BY o.seq;
            """,
            r => new ConflictInfo(OutboxStore.Map(r), r.GetStringOrNull(13), r.GetStringOrNull(14), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(15))));

    /// <summary>Number of open conflicts (the badge).</summary>
    public static int Count(SqliteConnection conn) =>
        conn.Query(null, "SELECT COUNT(*) FROM conflicts;", r => r.GetInt32(0)).Single();

    /// <summary>Removes a conflict row (the outbox entry is handled by the caller).</summary>
    public static void Remove(SqliteConnection conn, SqliteTransaction? tx, long seq) =>
        conn.Execute(tx, "DELETE FROM conflicts WHERE outbox_seq = $seq;", ("$seq", seq));
}
