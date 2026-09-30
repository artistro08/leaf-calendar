using System.Text.Json.Nodes;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Editing;

/// <summary>Which events of a repeating series an edit applies to.</summary>
public enum EditScope
{
    /// <summary>Only the chosen instance (or a single event).</summary>
    This,

    /// <summary>The chosen instance and every later one (the series is split there).</summary>
    Following,

    /// <summary>The whole series.</summary>
    All,
}

/// <summary>One dragged event and where it landed.</summary>
public sealed record EventMove(CalendarOccurrence Occurrence, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay);

/// <summary>The outbox entries a delete made; pass it to <see cref="EventEditor.Undo"/>.</summary>
public sealed record DeleteReceipt(IReadOnlyList<long> Seqs);

/// <summary>A copied event: its account, calendar, stored JSON, and time zone (see <see cref="EventEditor.CopyOf"/>).</summary>
public sealed record EventCopy(string AccountId, string CalendarId, string RawJson, string? TimeZone);

/// <summary>
/// Makes every change to events. Each edit is applied to the local database at once (so the views show it)
/// and queued in the outbox in the same transaction; the sync loop sends the queue.
/// </summary>
/// <remarks>
/// <para>
/// Repeating events follow Google's rules. "This event" patches the instance's own ID
/// (<see cref="EventIds.InstanceId"/>). "All events" patches the series, shifting its start by as much as
/// the instance moved. "This and following" ends the series just before the instance and creates a new
/// series from there; from the first instance it is the same as "All events".
/// </para>
/// <para>
/// Deletes are held in the outbox for <see cref="UndoWindow"/>, so <see cref="Undo"/> can put the rows back
/// from their snapshots before anything reaches Google. Works fully offline.
/// </para>
/// </remarks>
public sealed class EventEditor(LeafDatabase database, TimeProvider time)
{
    /// <summary>How long a delete waits before it's sent (the Undo button shows for less).</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);

    /// <summary>Raised on the calling thread after each committed edit (the views reload; the app nudges the sync loop).</summary>
    public event EventHandler? Changed;

    /// <summary>The PC's IANA zone, given to events that become timed and have no zone of their own.</summary>
    public string LocalZoneId { get; set; } = "UTC";

    // =========================================================================
    // READING
    // =========================================================================

    /// <summary>The editor's fields for an on-screen event.</summary>
    /// <exception cref="InvalidOperationException">The event is no longer stored.</exception>
    public EventDraft Load(CalendarOccurrence occurrence)
    {
        using var conn = database.Open();
        return LoadCore(conn, null, occurrence);
    }

    /// <summary>Whether you may edit the event, and whether you can reply to it.</summary>
    public (bool CanEdit, bool CanRespond) Permissions(CalendarOccurrence occurrence)
    {
        using var conn = database.Open();
        if (EventStore.Get(conn, null, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId) is not { } stored)
        {
            return (false, false);
        }

        var role = CalendarStore.GetForAccount(conn, occurrence.AccountId).FirstOrDefault(c => c.Id == occurrence.CalendarId)?.AccessRole ?? "reader";
        return (EventJson.CanEdit(stored.RawJson, role), EventJson.CanRespond(stored.RawJson));
    }

    // =========================================================================
    // EDITS
    // =========================================================================

    /// <summary>Creates an event under a new client-generated ID and returns the ID.</summary>
    public string Create(EventDraft draft, bool sendUpdates)
    {
        var id   = EventIds.NewId();
        var body = EventJson.BuildCreate(id, draft).ToJsonString();
        InTransaction((conn, tx) => AddCreate(conn, tx, draft.AccountId, draft.CalendarId, id, body, sendUpdates));
        return id;
    }

    /// <summary>Saves the editor's changes (<paramref name="before"/> to <paramref name="after"/>) with the chosen scope.</summary>
    public void Save(CalendarOccurrence occurrence, EventDraft before, EventDraft after, EditScope scope, bool sendUpdates) =>
        InTransaction((conn, tx) => SaveCore(conn, tx, occurrence, before, after, scope, sendUpdates));

    /// <summary>Moves or resizes dragged events (one transaction, one scope for all of them).</summary>
    public void Move(IReadOnlyList<EventMove> moves, EditScope scope, bool sendUpdates) =>
        InTransaction((conn, tx) =>
        {
            foreach (var move in moves)
            {
                var before = LoadCore(conn, tx, move.Occurrence);
                var after  = before with
                {
                    Start    = move.Start,
                    End      = move.End,
                    IsAllDay = move.IsAllDay,
                    TimeZone = move.IsAllDay ? before.TimeZone : before.TimeZone ?? LocalZoneId,
                };
                SaveCore(conn, tx, move.Occurrence, before, after, scope, sendUpdates);
            }
        });

    /// <summary>Sets the color of several events (null restores the calendar's color).</summary>
    public void Recolor(IReadOnlyList<CalendarOccurrence> items, string? colorId, EditScope scope) =>
        InTransaction((conn, tx) =>
        {
            foreach (var occurrence in items)
            {
                var before = LoadCore(conn, tx, occurrence);
                SaveCore(conn, tx, occurrence, before, before with { ColorId = colorId }, scope, sendUpdates: false);
            }
        });

    /// <summary>Deletes events (held for <see cref="UndoWindow"/>) and returns what <see cref="Undo"/> needs.</summary>
    public DeleteReceipt Delete(IReadOnlyList<CalendarOccurrence> items, EditScope scope, bool sendUpdates)
    {
        var notBefore = time.GetUtcNow() + UndoWindow;
        var seqs      = new List<long>();

        InTransaction((conn, tx) =>
        {
            foreach (var occurrence in items)
            {
                if (DeleteCore(conn, tx, occurrence, scope, sendUpdates, notBefore) is { } seq)
                {
                    seqs.Add(seq);
                }
            }
        });

        return new DeleteReceipt(seqs);
    }

    /// <summary>Puts deleted events back if none of the delete's entries has been sent yet.</summary>
    public bool Undo(DeleteReceipt receipt)
    {
        var undone = false;
        InTransaction((conn, tx) =>
        {
            var entries = receipt.Seqs.Select(seq => OutboxStore.Get(conn, tx, seq)).ToList();
            if (entries.Count == 0 || entries.Any(e => e is not { State: OutboxState.Pending }))
            {
                return;
            }

            // Newest First, So Each Snapshot Lands On The State It Was Taken From
            foreach (var entry in Enumerable.Reverse(entries))
            {
                EventStore.Restore(conn, tx, entry!.AccountId, entry.CalendarId, entry.EventId, entry.BeforeJson ?? "[]");
                OutboxStore.Remove(conn, tx, entry.Seq);
            }

            undone = true;
        });

        return undone;
    }

    /// <summary>Sets your reply (and an optional note). Google gets it on its latest copy, so replies never conflict.</summary>
    public void Respond(CalendarOccurrence occurrence, ResponseStatus response, string? note, bool sendUpdates, EditScope scope) =>
        InTransaction((conn, tx) =>
        {
            var (eventId, current) = ReplyTarget(conn, tx, occurrence, scope);
            var payload = new JsonObject
            {
                ["responseStatus"] = EventJson.ResponseText(response),
                ["comment"]        = note is { Length: > 0 } ? note : null,
            };

            OutboxStore.Add(conn, tx, new OutboxEntry(0, occurrence.AccountId, occurrence.CalendarId, eventId, OutboxOperation.Rsvp, payload.ToJsonString(), current.Etag, sendUpdates, EventStore.Snapshot(conn, tx, occurrence.AccountId, occurrence.CalendarId, eventId), null));
            EventStore.ApplyJson(conn, tx, occurrence.AccountId, occurrence.CalendarId, EventJson.WithResponse(current.RawJson, response, note));
        });

    /// <summary>What a copy of an event is made from. The clipboard keeps it, so a cut event can still be pasted after its row is gone.</summary>
    /// <exception cref="InvalidOperationException">The event is no longer stored.</exception>
    public EventCopy CopyOf(CalendarOccurrence occurrence)
    {
        using var conn = database.Open();
        var source = Stored(conn, null, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId);
        return new EventCopy(occurrence.AccountId, occurrence.CalendarId, source.RawJson, LoadCore(conn, null, occurrence).TimeZone);
    }

    /// <summary>Creates a copy (without its repeat) at a new time and returns the new ID. Guests aren't emailed.</summary>
    public string Paste(EventCopy copy, DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        var id   = EventIds.NewId();
        var body = EventJson.WithTimes(EventJson.CloneForCreate(copy.RawJson, id, keepRecurrence: false), start, end, isAllDay, copy.TimeZone ?? LocalZoneId);
        InTransaction((conn, tx) => AddCreate(conn, tx, copy.AccountId, copy.CalendarId, id, body, sendUpdates: false));
        return id;
    }

    /// <summary>Copies an event to a new time (Alt+drag) and returns the copy's ID.</summary>
    public string Duplicate(CalendarOccurrence occurrence, DateTimeOffset start, DateTimeOffset end, bool isAllDay) =>
        Paste(CopyOf(occurrence), start, end, isAllDay);

    // =========================================================================
    // SAVING
    // =========================================================================

    void SaveCore(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, EventDraft before, EventDraft after, EditScope scope, bool sendUpdates)
    {
        // Single Event (or an instance whose series isn't stored here)
        if (o.RecurringEventId is not { } masterId || EventStore.Get(conn, tx, o.AccountId, o.CalendarId, masterId) is not { } master)
        {
            SaveWhole(conn, tx, o.AccountId, o.CalendarId, o.EventId, before, after, sendUpdates);
            return;
        }

        var masterDraft   = LoadMaster(master, o);
        var originalStart = OriginalStart(conn, tx, o);

        // "This And Following" From The First Instance Is The Whole Series
        if (scope == EditScope.Following && originalStart <= masterDraft.Start)
        {
            scope = EditScope.All;
        }

        switch (scope)
        {
            case EditScope.All:
                SaveWhole(conn, tx, o.AccountId, o.CalendarId, master.Id, masterDraft, ToSeries(masterDraft, before, after), sendUpdates);
                break;
            case EditScope.Following:
                SplitSeries(conn, tx, o, master, masterDraft, originalStart, before, after, sendUpdates);
                break;
            default:
                SaveInstance(conn, tx, o, master, originalStart, before, after, sendUpdates);
                break;
        }
    }

    static void SaveWhole(SqliteConnection conn, SqliteTransaction tx, string accountId, string calendarId, string eventId, EventDraft before, EventDraft after, bool sendUpdates)
    {
        var stored = Stored(conn, tx, accountId, calendarId, eventId);

        // Another Account: Google can't move between accounts, so copy it there and delete it here
        if (after.AccountId != accountId)
        {
            var newId = EventIds.NewId();
            var copy  = EventJson.ApplyPatch(EventJson.CloneForCreate(stored.RawJson, newId), EventJson.BuildPatch(before, after, stored.RawJson));
            AddCreate(conn, tx, after.AccountId, after.CalendarId, newId, copy, sendUpdates);
            AddDelete(conn, tx, accountId, calendarId, eventId, stored, sendUpdates, notBefore: null);
            return;
        }

        // Another Calendar Of The Same Account
        var target = calendarId;
        if (after.CalendarId != calendarId)
        {
            OutboxStore.Add(conn, tx, new OutboxEntry(0, accountId, calendarId, eventId, OutboxOperation.Move, after.CalendarId, stored.Etag, sendUpdates, EventStore.Snapshot(conn, tx, accountId, calendarId, eventId), null));
            EventStore.MoveCalendar(conn, tx, accountId, calendarId, after.CalendarId, eventId);
            target = after.CalendarId;
        }

        // Changed Fields
        var current = Stored(conn, tx, accountId, target, eventId);
        var patch   = EventJson.BuildPatch(before, after, current.RawJson);
        if (patch.Count > 0)
        {
            AddPatch(conn, tx, accountId, target, eventId, current, patch, sendUpdates, notBefore: null);
        }
    }

    // One instance: its own ID; a repeat change can't apply to one instance, so it's ignored here
    static void SaveInstance(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, StoredEvent master, DateTimeOffset originalStart, EventDraft before, EventDraft after, bool sendUpdates)
    {
        var instanceId = InstanceIdOf(o, master, originalStart);
        var current    = EventStore.Get(conn, tx, o.AccountId, o.CalendarId, instanceId) ?? Materialize(o, master, instanceId, originalStart);
        var patch      = EventJson.BuildPatch(before, after with { Recurrence = before.Recurrence, AccountId = before.AccountId, CalendarId = before.CalendarId }, current.RawJson);
        if (patch.Count == 0)
        {
            return;
        }

        AddPatch(conn, tx, o.AccountId, o.CalendarId, instanceId, current, patch, sendUpdates, notBefore: null);
    }

    // The instance's change applied to the series: its start moves by as much as the instance moved
    EventDraft ToSeries(EventDraft master, EventDraft before, EventDraft after)
    {
        var start      = master.Start + (after.Start - before.Start);
        var days       = LocalDay(after).DayNumber - LocalDay(before).DayNumber;
        var recurrence = after.Recurrence.SequenceEqual(before.Recurrence)
            ? RecurrenceEdits.ShiftWeekdays(master.Recurrence, days)
            : after.Recurrence;

        return after with { Start = start, End = start + (after.End - after.Start), Recurrence = recurrence };
    }

    static void SplitSeries(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, StoredEvent master, EventDraft masterDraft, DateTimeOffset originalStart, EventDraft before, EventDraft after, bool sendUpdates)
    {
        // End The Old Series Just Before This Instance (its later exceptions go with it)
        var ended = masterDraft with { Recurrence = RecurrenceEdits.EndBefore(masterDraft.Recurrence, originalStart, masterDraft.IsAllDay) };
        AddPatch(conn, tx, o.AccountId, o.CalendarId, master.Id, master, EventJson.BuildPatch(masterDraft, ended, master.RawJson), sendUpdates, notBefore: null);
        EventStore.RemoveExceptionsFrom(conn, tx, o.AccountId, o.CalendarId, master.Id, originalStart);

        // Start A New Series Here, With The Changes (none when a COUNT rule has nothing left at the split)
        var recurrence = after.Recurrence.SequenceEqual(before.Recurrence)
            ? RecurrenceEdits.FollowingFrom(masterDraft.Recurrence, masterDraft.Start, masterDraft.TimeZone, originalStart, masterDraft.IsAllDay)
            : after.Recurrence;
        if (recurrence is null)
        {
            return;
        }

        var newId = EventIds.NewId();
        var body  = EventJson.ApplyPatch(
            EventJson.CloneForCreate(master.RawJson, newId),
            EventJson.BuildPatch(masterDraft, after with { Recurrence = recurrence, AccountId = masterDraft.AccountId, CalendarId = masterDraft.CalendarId }, master.RawJson));
        AddCreate(conn, tx, o.AccountId, o.CalendarId, newId, body, sendUpdates);
    }

    // =========================================================================
    // DELETING AND REPLYING
    // =========================================================================

    static long? DeleteCore(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, EditScope scope, bool sendUpdates, DateTimeOffset notBefore)
    {
        // Single Event (or already gone, e.g. two instances of one series deleted with "All events")
        if (o.RecurringEventId is not { } masterId || EventStore.Get(conn, tx, o.AccountId, o.CalendarId, masterId) is not { } master)
        {
            return EventStore.Get(conn, tx, o.AccountId, o.CalendarId, o.EventId) is { } stored
                ? AddDelete(conn, tx, o.AccountId, o.CalendarId, o.EventId, stored, sendUpdates, notBefore)
                : null;
        }

        var masterDraft   = LoadMaster(master, o);
        var originalStart = OriginalStart(conn, tx, o);
        if (scope == EditScope.Following && originalStart <= masterDraft.Start)
        {
            scope = EditScope.All;
        }

        switch (scope)
        {
            case EditScope.All:
                return AddDelete(conn, tx, o.AccountId, o.CalendarId, master.Id, master, sendUpdates, notBefore);

            case EditScope.Following:
                // ponytail: the old series' later exceptions stay on screen until the next sync (Google cancels them); snapshot them too if that flash matters
                var ended = masterDraft with { Recurrence = RecurrenceEdits.EndBefore(masterDraft.Recurrence, originalStart, masterDraft.IsAllDay) };
                return AddPatch(conn, tx, o.AccountId, o.CalendarId, master.Id, master, EventJson.BuildPatch(masterDraft, ended, master.RawJson), sendUpdates, notBefore);

            default:
                var instanceId = InstanceIdOf(o, master, originalStart);
                var existing   = EventStore.Get(conn, tx, o.AccountId, o.CalendarId, instanceId);
                var seq        = OutboxStore.Add(conn, tx, new OutboxEntry(0, o.AccountId, o.CalendarId, instanceId, OutboxOperation.Delete, null, existing?.Etag, sendUpdates, EventStore.Snapshot(conn, tx, o.AccountId, o.CalendarId, instanceId), notBefore));
                EventStore.ApplyJson(conn, tx, o.AccountId, o.CalendarId, EventJson.CancelledInstance(master.Id, instanceId, originalStart, o.IsAllDay, masterDraft.TimeZone));
                return seq;
        }
    }

    static (string EventId, StoredEvent Current) ReplyTarget(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, EditScope scope)
    {
        if (o.RecurringEventId is not { } masterId || EventStore.Get(conn, tx, o.AccountId, o.CalendarId, masterId) is not { } master)
        {
            return (o.EventId, Stored(conn, tx, o.AccountId, o.CalendarId, o.EventId));
        }

        if (scope == EditScope.All)
        {
            return (master.Id, master);
        }

        var originalStart = OriginalStart(conn, tx, o);
        var instanceId    = InstanceIdOf(o, master, originalStart);
        return (instanceId, EventStore.Get(conn, tx, o.AccountId, o.CalendarId, instanceId) ?? Materialize(o, master, instanceId, originalStart));
    }

    // =========================================================================
    // OUTBOX + LOCAL ROWS (always together, in the caller's transaction)
    // =========================================================================

    static long AddCreate(SqliteConnection conn, SqliteTransaction tx, string accountId, string calendarId, string id, string bodyJson, bool sendUpdates)
    {
        var seq = OutboxStore.Add(conn, tx, new OutboxEntry(0, accountId, calendarId, id, OutboxOperation.Create, bodyJson, null, sendUpdates, "[]", null));
        EventStore.ApplyJson(conn, tx, accountId, calendarId, EventJson.AsLocal(bodyJson));
        return seq;
    }

    static long AddPatch(SqliteConnection conn, SqliteTransaction tx, string accountId, string calendarId, string eventId, StoredEvent current, JsonObject patch, bool sendUpdates, DateTimeOffset? notBefore)
    {
        var seq = OutboxStore.Add(conn, tx, new OutboxEntry(0, accountId, calendarId, eventId, OutboxOperation.Patch, patch.ToJsonString(), current.Etag, sendUpdates, EventStore.Snapshot(conn, tx, accountId, calendarId, eventId), notBefore));
        EventStore.ApplyJson(conn, tx, accountId, calendarId, EventJson.ApplyPatch(current.RawJson, patch));
        return seq;
    }

    static long AddDelete(SqliteConnection conn, SqliteTransaction tx, string accountId, string calendarId, string eventId, StoredEvent stored, bool sendUpdates, DateTimeOffset? notBefore)
    {
        var seq = OutboxStore.Add(conn, tx, new OutboxEntry(0, accountId, calendarId, eventId, OutboxOperation.Delete, null, stored.Etag, sendUpdates, EventStore.Snapshot(conn, tx, accountId, calendarId, eventId), notBefore));
        EventStore.Remove(conn, tx, accountId, calendarId, eventId);
        return seq;
    }

    // =========================================================================
    // INTERNALS
    // =========================================================================

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

    static EventDraft LoadCore(SqliteConnection conn, SqliteTransaction? tx, CalendarOccurrence o)
    {
        var stored = Stored(conn, tx, o.AccountId, o.CalendarId, o.EventId);
        var series = o.RecurringEventId is { } masterId && masterId != o.EventId ? EventStore.Get(conn, tx, o.AccountId, o.CalendarId, masterId) : null;

        return EventJson.ReadDraft(o.AccountId, o.CalendarId, stored.RawJson, o.Start, o.End, o.IsAllDay, series is null ? null : EventJson.RecurrenceOf(series.RawJson));
    }

    static EventDraft LoadMaster(StoredEvent master, CalendarOccurrence o) =>
        EventJson.ReadDraft(o.AccountId, o.CalendarId, master.RawJson, master.Start ?? o.Start, master.End ?? o.End, master.IsAllDay);

    // An expanded instance starts where the series put it; an exception row remembers where that was
    static DateTimeOffset OriginalStart(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o) =>
        o.EventId == o.RecurringEventId ? o.Start : EventStore.Get(conn, tx, o.AccountId, o.CalendarId, o.EventId)?.OriginalStart ?? o.Start;

    static string InstanceIdOf(CalendarOccurrence o, StoredEvent master, DateTimeOffset originalStart) =>
        o.EventId != master.Id ? o.EventId : EventIds.InstanceId(master.Id, originalStart, o.IsAllDay);

    // An instance Google hasn't sent as its own row: built from the series, not stored until the patch applies
    static StoredEvent Materialize(CalendarOccurrence o, StoredEvent master, string instanceId, DateTimeOffset originalStart) =>
        new(instanceId, "confirmed", o.Start, o.End, o.IsAllDay, master.Id, originalStart, null, EventJson.MaterializeInstance(master.RawJson, instanceId, originalStart, o.IsAllDay, o.Start, o.End));

    static StoredEvent Stored(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string id) =>
        EventStore.Get(conn, tx, accountId, calendarId, id) ?? throw new InvalidOperationException("The event isn't stored on this PC anymore.");

    DateOnly LocalDay(EventDraft draft)
    {
        if (draft.IsAllDay)
        {
            return DateOnly.FromDateTime(draft.Start.UtcDateTime);
        }

        var zoneId = draft.TimeZone ?? LocalZoneId;
        var zone   = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(draft.Start, zone).DateTime);
    }
}
