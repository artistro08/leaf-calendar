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

/// <summary>What a delete removed, which decides how a late <see cref="EventEditor.Undo"/> brings it back.</summary>
public enum DeleteKind
{
    /// <summary>A single event or a whole series (a delete of that ID); a late undo creates a quiet copy.</summary>
    Event,

    /// <summary>One instance of a repeating event (a delete of its instance ID); a late undo restores it.</summary>
    Instance,

    /// <summary>"This and following" (a patch ending the series' repeat); a late undo restores the repeat.</summary>
    Following,
}

/// <summary>One outbox entry a delete made (as queued, with its snapshot) and what kind of delete it was.</summary>
public sealed record DeletedItem(OutboxEntry Entry, DeleteKind Kind);

/// <summary>The outbox entries a delete made; pass it to <see cref="EventEditor.Undo"/>.</summary>
public sealed record DeleteReceipt(IReadOnlyList<long> Seqs)
{
    /// <summary>Each entry as queued, in the same order as <see cref="Seqs"/> (a late undo works from these).</summary>
    public IReadOnlyList<DeletedItem> Items { get; init; } = [];
}

/// <summary>What <see cref="EventEditor.Undo"/> did.</summary>
public enum UndoResult
{
    /// <summary>Nothing (an empty receipt, one already used, or everything in it is back already, e.g. Google refused the delete).</summary>
    Nothing,

    /// <summary>The delete was still held: the rows were put back and nothing is sent.</summary>
    Restored,

    /// <summary>The delete may have reached Google: each item comes back through quiet writes (no emails).</summary>
    Recreated,
}

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
/// series from there, queued to wait behind the end (<see cref="OutboxEntry.DependsOn"/>); from the first
/// instance it is the same as "All events".
/// </para>
/// <para>
/// Deletes are held in the outbox for <see cref="UndoWindow"/>, so <see cref="Undo"/> can put the rows back
/// from their snapshots before anything reaches Google. Works fully offline.
/// </para>
/// <para>
/// After the hold, Undo still works (this replaces the old rule that refused it): the delete is left alone, since
/// it may be at Google already, and each item comes back quietly (<c>sendUpdates=none</c>). A single event or a
/// whole series is re-created as a copy with a new ID (<see cref="EventJson.QuietCopy"/>, a series' canceled days
/// kept as <c>EXDATE</c> lines); one instance or "this and following" is restored and patched back. When the delete
/// is still queued, the new write waits behind it (<see cref="OutboxEntry.DependsOn"/>).
/// </para>
/// </remarks>
public sealed class EventEditor(LeafDatabase database, TimeProvider time)
{
    /// <summary>How long a delete waits before it's sent (the Undo button shows for less).</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);

    /// <summary>Raised on the calling thread after each committed edit (the views reload; the app nudges the sync loop).</summary>
    public event EventHandler? Changed;

    /// <summary>Your own IANA zone (primary, else the PC's; never time travel's), given to events that become timed and have no zone of their own.</summary>
    public string LocalZoneId
    {
        get;
        set
        {
            field                   = value;
            EventJson.FallbackZoneId = value;
        }
    } = "UTC";

    // Receipts already undone (each works once)
    readonly HashSet<long> _undone = [];

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
        return PermissionsCore(conn, null, occurrence);
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
            foreach (var move in OncePerSeries(conn, tx, moves, m => m.Occurrence, scope))
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
            foreach (var occurrence in OncePerSeries(conn, tx, items, o => o, scope))
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
        var deleted   = new List<DeletedItem>();

        InTransaction((conn, tx) =>
        {
            foreach (var occurrence in OncePerSeries(conn, tx, items, o => o, scope))
            {
                if (DeleteCore(conn, tx, occurrence, scope, sendUpdates, notBefore) is { } done)
                {
                    seqs.Add(done.Seq);
                    deleted.Add(new DeletedItem(OutboxStore.Get(conn, tx, done.Seq)!, done.Kind));
                }
            }
        });

        return new DeleteReceipt(seqs) { Items = deleted };
    }

    /// <summary>
    /// Brings a delete back. While every entry is still held (<see cref="UndoWindow"/>), the snapshots are put back
    /// and nothing is sent (<see cref="UndoResult.Restored"/>). After that the delete may be at Google already, so it is
    /// left alone and each item comes back quietly (<see cref="UndoResult.Recreated"/>, no emails): a copy with a new
    /// ID for an event or a whole series, a restore for one instance or "this and following". Items already back (a refused
    /// delete) are skipped, and a calendar that became read-only refuses. Each receipt works once.
    /// </summary>
    public UndoResult Undo(DeleteReceipt receipt) => Undo(receipt, out _);

    /// <summary>
    /// <see cref="Undo(DeleteReceipt)"/>, also giving how many items actually came back in <paramref name="restored"/>
    /// (items already back are not counted).
    /// </summary>
    public UndoResult Undo(DeleteReceipt receipt, out int restored)
    {
        var result = UndoResult.Nothing;
        var count  = 0;
        var now    = time.GetUtcNow();
        InTransaction((conn, tx) =>
        {
            // Each Receipt Works Once
            if (receipt.Seqs.Count == 0 || receipt.Seqs.Any(_undone.Contains))
            {
                return;
            }

            var entries = receipt.Seqs.Select(seq => OutboxStore.Get(conn, tx, seq)).ToList();

            // Still Held: Put The Rows Back (newest first, so each snapshot lands on the state it was taken from)
            if (entries.All(e => e is { State: OutboxState.Pending, NotBefore: { } notBefore } && notBefore > now))
            {
                foreach (var entry in Enumerable.Reverse(entries))
                {
                    EventStore.Restore(conn, tx, entry!.AccountId, entry.CalendarId, entry.EventId, entry.BeforeJson ?? "[]");
                    OutboxStore.Remove(conn, tx, entry.Seq);
                }

                result = UndoResult.Restored;
                count  = receipt.Items.Count;
            }
            else
            {
                // Late: Bring Each One Back Quietly (behind the delete while it's still in the outbox, even in conflict, as the sender does)
                var brought = 0;
                foreach (var item in receipt.Items)
                {
                    var queued = OutboxStore.Get(conn, tx, item.Entry.Seq) is not null ? item.Entry.Seq : (long?)null;
                    brought += BringBack(conn, tx, item, queued) ? 1 : 0;
                }

                result = brought > 0 ? UndoResult.Recreated : UndoResult.Nothing;
                count  = brought;
            }

            _undone.UnionWith(receipt.Seqs);
        });

        restored = count;
        return result;
    }

    /// <summary>Sets your reply (and an optional note). Google gets it on its latest copy, so replies never conflict.</summary>
    public void Respond(CalendarOccurrence occurrence, ResponseStatus response, string? note, bool sendUpdates, EditScope scope) =>
        InTransaction((conn, tx) =>
        {
            if (!PermissionsCore(conn, tx, occurrence).CanRespond)
            {
                throw new InvalidOperationException("You can't reply to this event.");
            }

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

    /// <summary>Creates a private copy (without its repeat or guests, see <see cref="EventJson.PrivateCopy"/>) at a new time and returns the new ID.</summary>
    public string Paste(EventCopy copy, DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        var id   = EventIds.NewId();
        var body = EventJson.WithTimes(EventJson.PrivateCopy(copy.RawJson, id), start, end, isAllDay, copy.TimeZone ?? LocalZoneId);
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
        RequireEdit(conn, tx, o);

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

        // Only A Whole Series Can Change Calendar Or Account
        if (scope != EditScope.All && (after.AccountId != before.AccountId || after.CalendarId != before.CalendarId))
        {
            throw new ArgumentException("Only \"All events\" can move a repeating event to another calendar.", nameof(after));
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
            var copy  = EventJson.WithMeetIfWanted(EventJson.ApplyPatch(EventJson.CloneForCreate(stored.RawJson, newId), EventJson.BuildPatch(before, after, stored.RawJson)), after.HasConference, newId);
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
        var patch      = EventJson.BuildPatch(before, after with { Recurrence = before.Recurrence }, current.RawJson);
        if (patch.Count == 0)
        {
            return;
        }

        AddPatch(conn, tx, o.AccountId, o.CalendarId, instanceId, current, patch, sendUpdates, notBefore: null);
    }

    // The instance's changes applied to the series: only the edited fields, and its start and end move as much as the instance's did
    EventDraft ToSeries(EventDraft master, EventDraft before, EventDraft after)
    {
        var days       = LocalDay(after).DayNumber - LocalDay(before).DayNumber;
        var recurrence = after.Recurrence.SequenceEqual(before.Recurrence)
            ? RecurrenceEdits.ShiftWeekdays(master.Recurrence, days)
            : after.Recurrence;

        return WithChanges(master, before, after) with
        {
            Start      = master.Start + (after.Start - before.Start),
            End        = master.End + (after.End - before.End),
            Recurrence = recurrence,
        };
    }

    // The target with only the fields that differ between before and after (times and repeat are the caller's)
    static EventDraft WithChanges(EventDraft target, EventDraft before, EventDraft after) => target with
    {
        AccountId           = after.AccountId,
        CalendarId          = after.CalendarId,
        Title               = before.Title == after.Title ? target.Title : after.Title,
        IsAllDay            = before.IsAllDay == after.IsAllDay ? target.IsAllDay : after.IsAllDay,
        TimeZone            = before.TimeZone == after.TimeZone ? target.TimeZone : after.TimeZone,
        Location            = before.Location == after.Location ? target.Location : after.Location,
        Description         = before.DescriptionTooLong || DescriptionHtml.Normalize(before.Description) == DescriptionHtml.Normalize(after.Description) ? target.Description : after.Description,
        ColorId             = before.ColorId == after.ColorId ? target.ColorId : after.ColorId,
        Guests              = before.Guests.SequenceEqual(after.Guests) ? target.Guests : after.Guests,
        UseDefaultReminders = before.UseDefaultReminders == after.UseDefaultReminders ? target.UseDefaultReminders : after.UseDefaultReminders,
        ReminderMinutes     = before.ReminderMinutes.SequenceEqual(after.ReminderMinutes) ? target.ReminderMinutes : after.ReminderMinutes,
        HasConference       = before.HasConference == after.HasConference ? target.HasConference : after.HasConference,
    };

    static void SplitSeries(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, StoredEvent master, EventDraft masterDraft, DateTimeOffset originalStart, EventDraft before, EventDraft after, bool sendUpdates)
    {
        // End The Old Series Just Before This Instance (its later exceptions go with it)
        var ended = masterDraft with { Recurrence = RecurrenceEdits.EndBefore(masterDraft.Recurrence, originalStart, masterDraft.IsAllDay, masterDraft.Start, masterDraft.TimeZone) };
        var endPatch = EventJson.BuildPatch(masterDraft, ended, master.RawJson);

        // Already Ended Before This Instance (a stale occurrence): nothing to split
        if (endPatch.Count == 0)
        {
            return;
        }

        var endSeq = AddPatch(conn, tx, o.AccountId, o.CalendarId, master.Id, master, endPatch, sendUpdates, notBefore: null);
        EventStore.RemoveExceptionsFrom(conn, tx, o.AccountId, o.CalendarId, master.Id, originalStart);

        // Start A New Series Here, With The Changes (none when a COUNT rule has nothing left at the split)
        var recurrence = after.Recurrence.SequenceEqual(before.Recurrence)
            ? RecurrenceEdits.FollowingFrom(masterDraft.Recurrence, masterDraft.Start, masterDraft.TimeZone, originalStart, masterDraft.IsAllDay)
            : after.Recurrence;
        if (recurrence is null)
        {
            return;
        }

        // The New Series Keeps The Old One's Fields And Time Slot, Plus What Was Edited
        var next = WithChanges(masterDraft, before, after) with
        {
            Start      = originalStart + (after.Start - before.Start),
            End        = originalStart + (masterDraft.End - masterDraft.Start) + (after.End - before.End),
            Recurrence = recurrence,
        };
        var newId = EventIds.NewId();
        var body  = EventJson.WithMeetIfWanted(EventJson.ApplyPatch(EventJson.CloneForCreate(master.RawJson, newId), EventJson.BuildPatch(masterDraft, next, master.RawJson)), next.HasConference, newId);
        // Sent Only After The Old Series' End Reaches Google, So The Meetings Are Never Doubled
        AddCreate(conn, tx, o.AccountId, o.CalendarId, newId, body, sendUpdates, dependsOn: endSeq);
    }

    // =========================================================================
    // DELETING AND REPLYING
    // =========================================================================

    static (long Seq, DeleteKind Kind)? DeleteCore(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o, EditScope scope, bool sendUpdates, DateTimeOffset notBefore)
    {
        // Already Gone
        if (EventStore.Get(conn, tx, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
        {
            return null;
        }

        RequireEdit(conn, tx, o);

        // Single Event (or an instance whose series isn't stored here)
        if (o.RecurringEventId is not { } masterId || EventStore.Get(conn, tx, o.AccountId, o.CalendarId, masterId) is not { } master)
        {
            return (AddDelete(conn, tx, o.AccountId, o.CalendarId, o.EventId, stored, sendUpdates, notBefore), DeleteKind.Event);
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
                return (AddDelete(conn, tx, o.AccountId, o.CalendarId, master.Id, master, sendUpdates, notBefore), DeleteKind.Event);

            case EditScope.Following:
                // ponytail: the old series' later exceptions stay on screen until the next sync (Google cancels them); snapshot them too if that flash matters
                var ended = masterDraft with { Recurrence = RecurrenceEdits.EndBefore(masterDraft.Recurrence, originalStart, masterDraft.IsAllDay, masterDraft.Start, masterDraft.TimeZone) };
                var endPatch = EventJson.BuildPatch(masterDraft, ended, master.RawJson);

                // Already Ended Before This Instance (a stale occurrence): nothing to do
                return endPatch.Count == 0 ? null : (AddPatch(conn, tx, o.AccountId, o.CalendarId, master.Id, master, endPatch, sendUpdates, notBefore), DeleteKind.Following);

            default:
                var instanceId = InstanceIdOf(o, master, originalStart);
                var existing   = EventStore.Get(conn, tx, o.AccountId, o.CalendarId, instanceId);
                var seq        = OutboxStore.Add(conn, tx, new OutboxEntry(0, o.AccountId, o.CalendarId, instanceId, OutboxOperation.Delete, null, existing?.Etag, sendUpdates, EventStore.Snapshot(conn, tx, o.AccountId, o.CalendarId, instanceId), notBefore));
                EventStore.ApplyJson(conn, tx, o.AccountId, o.CalendarId, EventJson.CancelledInstance(master.Id, instanceId, originalStart, o.IsAllDay, masterDraft.TimeZone));
                return (seq, DeleteKind.Instance);
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

    static long AddCreate(SqliteConnection conn, SqliteTransaction tx, string accountId, string calendarId, string id, string bodyJson, bool sendUpdates, long? dependsOn = null)
    {
        var seq = OutboxStore.Add(conn, tx, new OutboxEntry(0, accountId, calendarId, id, OutboxOperation.Create, bodyJson, null, sendUpdates, "[]", null, DependsOn: dependsOn));
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

    // Late undo for one deleted item, sent quietly; waits behind the delete when it's still queued. False when there's nothing to bring back.
    static bool BringBack(SqliteConnection conn, SqliteTransaction tx, DeletedItem item, long? dependsOn)
    {
        var e       = item.Entry;
        var rows    = JsonNode.Parse(e.BeforeJson ?? "[]")!.AsArray().OfType<JsonObject>().ToList();
        var current = EventStore.Get(conn, tx, e.AccountId, e.CalendarId, e.EventId);

        // An instance Google never stored has an empty snapshot; restoring it still drops the local canceled row
        if (rows.Count == 0 && item.Kind != DeleteKind.Instance)
        {
            return false;
        }

        // Already Back (Google refused the delete, or "Keep Google's" won a conflict)
        var alreadyBack = item.Kind switch
        {
            DeleteKind.Instance  => current is not { Status: "cancelled" },
            DeleteKind.Following => current is null || EventJson.RecurrenceOf(current.RawJson).SequenceEqual(EventJson.RecurrenceOf(rows[0].ToJsonString())),
            _                    => current is not null,
        };
        if (alreadyBack)
        {
            return false;
        }

        // Same Permission Check As Every Other Write (a calendar may have become read-only)
        var role = CalendarStore.GetForAccount(conn, e.AccountId, tx).FirstOrDefault(c => c.Id == e.CalendarId)?.AccessRole ?? "reader";
        if (!EventJson.CanEdit(rows.FirstOrDefault()?.ToJsonString() ?? current!.RawJson, role))
        {
            throw new InvalidOperationException("You can't change this event.");
        }

        switch (item.Kind)
        {
            case DeleteKind.Instance:
                EventStore.Restore(conn, tx, e.AccountId, e.CalendarId, e.EventId, e.BeforeJson ?? "[]");
                // If-Match On Google's Canceled Copy: a row synced from Google carries its ETag. Deliberate exception: with the
                // delete still queued or not synced back yet, the row is Leaf's own canceled stub with no ETag, so the patch goes
                // without If-Match (there is no newer Google change to overwrite, only the day Leaf just canceled)
                OutboxStore.Add(conn, tx, new OutboxEntry(0, e.AccountId, e.CalendarId, e.EventId, OutboxOperation.Patch, """{"status":"confirmed"}""", current?.Etag, false, "[]", null, DependsOn: dependsOn));
                break;

            case DeleteKind.Following:
                // Snapshot The Ended Series First, And Keep The Etag The Row Has Now (not the snapshot's)
                var before = EventStore.Snapshot(conn, tx, e.AccountId, e.CalendarId, e.EventId);
                var etag   = current!.Etag;
                // ponytail: later changed days (exceptions) Google dropped when the series ended come back from the snapshot but not at Google, so they show until the next sync removes them; re-send them as exceptions if that's missed
                EventStore.Restore(conn, tx, e.AccountId, e.CalendarId, e.EventId, e.BeforeJson!);
                EventStore.SetEtag(conn, tx, e.AccountId, e.CalendarId, e.EventId, etag);
                var patch = new JsonObject { ["recurrence"] = rows[0]["recurrence"]?.DeepClone() };
                OutboxStore.Add(conn, tx, new OutboxEntry(0, e.AccountId, e.CalendarId, e.EventId, OutboxOperation.Patch, patch.ToJsonString(), etag, false, before, null, DependsOn: dependsOn));
                break;

            default:
                // ponytail: a series' changed instances (moved or retitled days) aren't re-created, only canceled days (as EXDATEs); copy them as exceptions if that's missed
                var master = rows[0];
                var id     = EventIds.NewId();
                var copy   = JsonNode.Parse(EventJson.QuietCopy(master.ToJsonString(), id))!.AsObject();
                if (copy["recurrence"] is JsonArray lines)
                {
                    foreach (var canceled in rows.Skip(1).Where(r => (string?)r["status"] == "cancelled"))
                    {
                        var (start, isAllDay) = EventJson.OriginalStartOf(canceled);
                        lines.Add((JsonNode?)JsonValue.Create(RecurrenceEdits.ExDateLine(start, isAllDay)));
                    }
                }

                AddCreate(conn, tx, e.AccountId, e.CalendarId, id, copy.ToJsonString(), sendUpdates: false, dependsOn);
                break;
        }

        return true;
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

    static (bool CanEdit, bool CanRespond) PermissionsCore(SqliteConnection conn, SqliteTransaction? tx, CalendarOccurrence o)
    {
        if (EventStore.Get(conn, tx, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
        {
            return (false, false);
        }

        var role = CalendarStore.GetForAccount(conn, o.AccountId, tx).FirstOrDefault(c => c.Id == o.CalendarId)?.AccessRole ?? "reader";
        return (EventJson.CanEdit(stored.RawJson, role), EventJson.CanRespond(stored.RawJson));
    }

    // Defense in depth behind the UI: a guest-only invite or a read-only calendar is never changed
    static void RequireEdit(SqliteConnection conn, SqliteTransaction tx, CalendarOccurrence o)
    {
        if (!PermissionsCore(conn, tx, o).CanEdit)
        {
            throw new InvalidOperationException("You can't change this event.");
        }
    }

    // "All" and "Following" touch a series once (from its earliest chosen instance), so it's never shifted or split twice
    static List<T> OncePerSeries<T>(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<T> items, Func<T, CalendarOccurrence> occurrenceOf, EditScope scope)
    {
        if (scope == EditScope.This)
        {
            return [.. items];
        }

        return [.. items
            .GroupBy(item =>
            {
                var o = occurrenceOf(item);
                return o.RecurringEventId is { } masterId && EventStore.Get(conn, tx, o.AccountId, o.CalendarId, masterId) is not null
                    ? (o.AccountId, o.CalendarId, masterId, true)
                    : (o.AccountId, o.CalendarId, o.EventId, false);
            })
            .Select(series => series.MinBy(item => OriginalStart(conn, tx, occurrenceOf(item)))!)];
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
