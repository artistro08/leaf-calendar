using System.Text.Json;
using System.Text.Json.Nodes;
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
/// since they were made on top of the rejected one.
/// </remarks>
public sealed class ConflictResolver(LeafDatabase database)
{
    /// <summary>Raised after a resolution (views reload; the app nudges the sync loop).</summary>
    public event EventHandler? Changed;

    /// <summary>Every open conflict, oldest first.</summary>
    public IReadOnlyList<ConflictInfo> GetAll()
    {
        Settle();
        using var conn = database.Open();
        return ConflictStore.GetAll(conn);
    }

    /// <summary>Open conflicts, and edits still waiting to be sent (for the title bar badge and indicator).</summary>
    public (int Conflicts, int Pending) Counts()
    {
        Settle();
        using var conn = database.Open();
        var conflicts = ConflictStore.Count(conn);
        return (conflicts, OutboxStore.Count(conn) - conflicts);
    }

    /// <summary>Edits of one account Google doesn't have yet (disconnecting would lose them).</summary>
    public int UnsentFor(string accountId)
    {
        Settle();
        using var conn = database.Open();
        return OutboxStore.CountForAccount(conn, accountId);
    }

    /// <summary>Keeps the local version.</summary>
    public void KeepMine(ConflictInfo conflict) => InTransaction((conn, tx) =>
    {
        var entry = conflict.Entry;
        ConflictStore.Remove(conn, tx, entry.Seq);

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

    /// <summary>Keeps Google's version and drops the local change (and later local edits of the event).</summary>
    public void KeepGoogles(ConflictInfo conflict) => InTransaction((conn, tx) =>
    {
        var entry      = conflict.Entry;
        var calendarId = OutboxSender.LocalCalendarOf(entry);

        foreach (var edit in OutboxStore.ForEvent(conn, tx, entry.AccountId, calendarId, entry.EventId).Where(e => e.Seq > entry.Seq))
        {
            OutboxStore.Remove(conn, tx, edit.Seq);
        }

        OutboxStore.Remove(conn, tx, entry.Seq);

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

    // Lost Response: Google already applied an edit whose answer never arrived, so the retry got a 412.
    // When Google's copy already holds every field the patch sets, the edit counts as sent.
    void Settle()
    {
        var done = GetAllRaw().Where(c => c.Entry.Operation == OutboxOperation.Patch && Matches(c.Entry.Payload, c.GoogleJson)).ToList();
        if (done.Count == 0)
        {
            return;
        }

        using (var conn = database.Open())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var conflict in done)
            {
                var entry = conflict.Entry;
                var etag  = OutboxSender.EtagOf(conflict.GoogleJson!);
                OutboxStore.Remove(conn, tx, entry.Seq);

                // Later Edits Were Made On Top Of This One: they go out against Google's ETag
                if (OutboxStore.ForEvent(conn, tx, entry.AccountId, entry.CalendarId, entry.EventId).Count > 0)
                {
                    OutboxStore.Rebase(conn, tx, entry.AccountId, entry.CalendarId, entry.EventId, entry.Seq, etag);
                    EventStore.SetEtag(conn, tx, entry.AccountId, entry.CalendarId, entry.EventId, etag);
                }
                else
                {
                    EventStore.ApplyJson(conn, tx, entry.AccountId, entry.CalendarId, conflict.GoogleJson!);
                }
            }

            tx.Commit();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    IReadOnlyList<ConflictInfo> GetAllRaw()
    {
        using var conn = database.Open();
        return ConflictStore.GetAll(conn);
    }

    static bool Matches(string? payload, string? googleJson)
    {
        if (payload is null || googleJson is null)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject patch || patch.Count == 0 || JsonNode.Parse(googleJson) is not JsonObject current)
            {
                return false;
            }

            return patch.All(p => JsonNode.DeepEquals(p.Value, current[p.Key]));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
