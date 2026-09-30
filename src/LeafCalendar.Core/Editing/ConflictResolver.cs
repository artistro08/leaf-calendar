using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Sync;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Editing;

/// <summary>
/// The user's answer to a conflict (spec 5.5): "Keep mine" re-sends the local change on top of Google's current
/// version; "Keep Google's" drops the local change and shows Google's version.
/// </summary>
/// <remarks>
/// When Google deleted the event, "Keep mine" brings it back as a new event (Google never reuses a deleted
/// ID), and a local delete is simply done. "Keep Google's" also drops every later local edit of that event,
/// since they were made on top of the rejected one. Either answer makes the next sync reload the event's
/// calendar in full, since pulls skipped Google's changes to it while the conflict was open.
/// </remarks>
public sealed class ConflictResolver(LeafDatabase database, TimeProvider time)
{
    /// <summary>Raised after a resolution (views reload; the app nudges the sync loop).</summary>
    public event EventHandler? Changed;

    /// <summary>Every open conflict, oldest first.</summary>
    public IReadOnlyList<ConflictInfo> GetAll()
    {
        using var conn = database.Open();
        return ConflictStore.GetAll(conn);
    }

    /// <summary>
    /// Open conflicts, and edits the next sync would send (for the title bar badge and indicator). Deletes still
    /// in their undo window and edits waiting behind a conflict aren't counted, so the count doesn't flash after
    /// every delete.
    /// </summary>
    public (int Conflicts, int Pending) Counts()
    {
        using var conn = database.Open();
        return (ConflictStore.Count(conn), OutboxStore.CountSendable(conn, time.GetUtcNow()));
    }

    /// <summary>Edits of one account Google doesn't have yet (disconnecting would lose them).</summary>
    public int UnsentFor(string accountId)
    {
        using var conn = database.Open();
        return OutboxStore.CountForAccount(conn, accountId);
    }

    /// <summary>Keeps the local version.</summary>
    public void KeepMine(ConflictInfo conflict) => InTransaction((conn, tx) =>
    {
        var entry = conflict.Entry;
        ConflictStore.Remove(conn, tx, entry.Seq);
        ReloadCalendars(conn, tx, entry);

        // A null GoogleJson is treated as deleted on Google
        // Re-Send On Top Of Google's Current Version
        if (conflict.GoogleJson is { } google)
        {
            // Never Send Without If-Match: if Google's copy has no ETag, keep the old one (a safe 412)
            OutboxStore.Requeue(conn, tx, entry.Seq, OutboxSender.EtagOf(google) ?? entry.BaseEtag);
            return;
        }

        // Google Deleted It: a delete is done, anything else comes back as a new event
        var calendarId = OutboxSender.LocalCalendarOf(entry);
        var local      = conflict.LocalJson ?? EventStore.Get(conn, tx, entry.AccountId, calendarId, entry.EventId)?.RawJson;
        if (entry.Operation == OutboxOperation.Delete || local is null)
        {
            OutboxStore.Remove(conn, tx, entry.Seq);
            return;
        }

        // Later edits of the old ID would only hit the deleted event; the local JSON already includes them
        foreach (var later in OutboxStore.ForEvent(conn, tx, entry.AccountId, calendarId, entry.EventId).Where(e => e.Seq > entry.Seq))
        {
            OutboxStore.Remove(conn, tx, later.Seq);
        }

        var newId = EventIds.NewId();
        var body  = EventJson.CloneForCreate(local, newId);
        OutboxStore.Replace(conn, tx, entry with { CalendarId = calendarId, EventId = newId, Operation = OutboxOperation.Create, Payload = body, BaseEtag = null, BeforeJson = "[]", NotBefore = null });
        EventStore.Remove(conn, tx, entry.AccountId, calendarId, entry.EventId);
        EventStore.ApplyJson(conn, tx, entry.AccountId, calendarId, EventJson.AsLocal(body));
    });

    /// <summary>Keeps Google's version and drops the local change (and later local edits of the event, and what waits behind them).</summary>
    public void KeepGoogles(ConflictInfo conflict) => InTransaction((conn, tx) =>
    {
        var entry      = conflict.Entry;
        var calendarId = OutboxSender.LocalCalendarOf(entry);
        ReloadCalendars(conn, tx, entry);

        foreach (var edit in OutboxStore.ForEvent(conn, tx, entry.AccountId, calendarId, entry.EventId).Where(e => e.Seq > entry.Seq))
        {
            OutboxStore.Remove(conn, tx, edit.Seq);
            OutboxStore.DropDependents(conn, tx, edit.Seq);
        }

        // A Split's New Series Waits Behind The Old Series' End: Google kept the old series running, so it goes too
        OutboxStore.Remove(conn, tx, entry.Seq);
        OutboxStore.DropDependents(conn, tx, entry.Seq);

        // A null GoogleJson is treated as deleted on Google
        // Google Deleted It Too
        if (conflict.GoogleJson is not { } google)
        {
            EventStore.Remove(conn, tx, entry.AccountId, calendarId, entry.EventId);
            return;
        }

        // A local delete removed the rows; put them back before Google's version lands on top
        if (entry.Operation == OutboxOperation.Delete)
        {
            EventStore.Restore(conn, tx, entry.AccountId, entry.CalendarId, entry.EventId, entry.BeforeJson ?? "[]");
        }
        else if (calendarId != entry.CalendarId)
        {
            EventStore.MoveCalendar(conn, tx, entry.AccountId, calendarId, entry.CalendarId, entry.EventId);
        }

        EventStore.ApplyJson(conn, tx, entry.AccountId, entry.CalendarId, google);
    });

    // Pulls skipped this event (and a series' exceptions) while the conflict was open but still advanced the
    // sync token; forgetting the token makes the next sync reload the calendar, so nothing skipped is lost
    static void ReloadCalendars(SqliteConnection conn, SqliteTransaction tx, OutboxEntry entry)
    {
        CalendarStore.SetSyncToken(conn, tx, entry.AccountId, entry.CalendarId, null);
        CalendarStore.SetSyncToken(conn, tx, entry.AccountId, OutboxSender.LocalCalendarOf(entry), null);
    }

    void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using (var conn = database.Open())
        using (var tx = conn.BeginTransaction())
        {
            work(conn, tx);
            tx.Commit();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
