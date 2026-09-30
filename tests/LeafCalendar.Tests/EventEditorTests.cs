using System.Text.Json;
using System.Text.Json.Nodes;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class EventEditorTests : IDisposable
{
    const string Calendar = "leaf.tester@gmail.com";
    const string Family   = "family123@group.calendar.google.com";
    const string Invite   = """{"id":"evt-invite","status":"confirmed","etag":"\"5\"","summary":"Planning","start":{"dateTime":"2026-10-03T14:00:00Z"},"end":{"dateTime":"2026-10-03T15:00:00Z"},"organizer":{"email":"boss@example.com"},"attendees":[{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"},{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"needsAction"}]}""";

    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateOnly Oct1  = new(2026, 10, 1);
    static readonly DateOnly Oct3  = new(2026, 10, 3);
    static readonly DateOnly Oct5  = new(2026, 10, 5);
    static readonly DateOnly Oct9  = new(2026, 10, 9);
    static readonly DateOnly Oct10 = new(2026, 10, 10);
    static readonly DateOnly Oct12 = new(2026, 10, 12);

    readonly TestDatabase _db = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly EventEditor _editor;

    public EventEditorTests()
    {
        _editor = new EventEditor(_db.Database, _time) { LocalZoneId = "America/New_York" };

        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Calendar, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    IReadOnlyList<CalendarOccurrence> Day(DateOnly day)
    {
        using var conn = _db.Database.Open();
        return OccurrenceQuery.Load(conn, day, day.AddDays(1), NewYork, includeDeclined: true);
    }

    CalendarOccurrence Occurrence(string eventId, DateOnly day) => Day(day).Single(o => o.EventId == eventId);

    IReadOnlyList<OutboxEntry> Outbox()
    {
        using var conn = _db.Database.Open();
        return OutboxStore.Pending(conn, Account);
    }

    void Seed(string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, Calendar, json);
    }

    static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StoresEventQueuesCreateAndRaisesChanged()
    {
        var changed = 0;
        _editor.Changed += (_, _) => changed++;
        var draft = new EventDraft { AccountId = Account, CalendarId = Calendar, Title = "Coffee", Start = Utc(10, 2, 14), End = Utc(10, 2, 14, 30), TimeZone = "America/New_York" };

        var id = _editor.Create(draft, sendUpdates: false);

        var entry = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Create, entry.Operation);
        Assert.Equal(id, entry.EventId);
        Assert.Null(entry.BaseEtag);
        Assert.Equal("Coffee", Occurrence(id, new DateOnly(2026, 10, 2)).Title);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Save_SingleTitle_PatchesSummaryWithBaseEtag()
    {
        var o      = Occurrence("evt-single", Oct1);
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { Title = "Dentist (moved)" }, EditScope.This, sendUpdates: true);

        var entry = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Patch, entry.Operation);
        Assert.Equal("evt-single", entry.EventId);
        Assert.Equal("\"3181161784712000\"", entry.BaseEtag);
        Assert.Equal("""{"summary":"Dentist (moved)"}""", entry.Payload);
        Assert.True(entry.SendUpdates);
        Assert.Equal("Dentist (moved)", Occurrence("evt-single", Oct1).Title);
    }

    [Fact]
    public void Save_ThisInstance_PatchesInstanceIdAndMovesOnlyThatDay()
    {
        var o      = Occurrence("evt-weekly", Oct9);
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { Start = o.Start.AddHours(1), End = o.End.AddHours(1) }, EditScope.This, sendUpdates: true);

        var entry = Assert.Single(Outbox());
        Assert.Equal("evt-weekly_20261009T133000Z", entry.EventId);
        Assert.Null(entry.BaseEtag);
        Assert.Equal(o.Start.AddHours(1), Assert.Single(Day(Oct9), x => x.RecurringEventId == "evt-weekly").Start);
        Assert.Equal(Utc(10, 12, 13, 30), Occurrence("evt-weekly", Oct12).Start);
    }

    [Fact]
    public void Save_AllEvents_ShiftsTheSeriesStart()
    {
        var o      = Occurrence("evt-weekly", Oct9);
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { Start = o.Start.AddHours(1), End = o.End.AddHours(1) }, EditScope.All, sendUpdates: true);

        var entry = Assert.Single(Outbox());
        Assert.Equal("evt-weekly", entry.EventId);
        Assert.Contains("\"2026-10-05T10:30:00-04:00\"", entry.Payload!, StringComparison.Ordinal);
        Assert.DoesNotContain("recurrence", entry.Payload!, StringComparison.Ordinal);
        Assert.Equal(Utc(10, 12, 14, 30), Occurrence("evt-weekly", Oct12).Start);
    }

    [Fact]
    public void Save_FollowingOnFirstInstance_EditsWholeSeries()
    {
        var o      = Occurrence("evt-weekly", Oct5);
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { Title = "Standup" }, EditScope.Following, sendUpdates: true);

        var entry = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Patch, entry.Operation);
        Assert.Equal("evt-weekly", entry.EventId);
        Assert.Equal("""{"summary":"Standup"}""", entry.Payload);
    }

    [Fact]
    public void Save_Following_EndsSeriesAndStartsANewOne()
    {
        var o      = Occurrence("evt-weekly", Oct9);
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { Title = "Standup v2" }, EditScope.Following, sendUpdates: true);

        var entries = Outbox();
        Assert.Equal(2, entries.Count);
        Assert.Equal(OutboxOperation.Patch, entries[0].Operation);
        Assert.Contains("UNTIL=20261009T132959Z", entries[0].Payload!, StringComparison.Ordinal);
        Assert.Equal(OutboxOperation.Create, entries[1].Operation);
        Assert.Null(entries[0].DependsOn);
        Assert.Equal(entries[0].Seq, entries[1].DependsOn);
        var created = JsonNode.Parse(entries[1].Payload!)!;
        Assert.Equal("Standup v2", (string?)created["summary"]);
        Assert.Equal("2026-10-09T09:30:00-04:00", (string?)created["start"]!["dateTime"]);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR", (string?)created["recurrence"]![0]);
        Assert.Equal("Team standup", Occurrence("evt-weekly", Oct5).Title);
        Assert.Equal("Standup v2", Occurrence(entries[1].EventId, Oct12).Title);
    }

    [Fact]
    public void Save_FollowingWithStaleOccurrencePastTheEnd_CreatesNoNewSeries()
    {
        var fri    = Occurrence("evt-weekly", Oct9);
        var mon    = Occurrence("evt-weekly", Oct12);
        var before = _editor.Load(fri);
        _editor.Save(fri, before, before with { Title = "Standup v2" }, EditScope.Following, sendUpdates: false);

        // A Separate Save With An Occurrence Captured Before The Split (the old series already ends before it)
        var stale = _editor.Load(mon);
        _editor.Save(mon, stale, stale with { Title = "Phantom" }, EditScope.Following, sendUpdates: false);

        Assert.Equal(2, Outbox().Count);
        Assert.DoesNotContain(Day(Oct12), x => x.Title == "Phantom");
    }

    [Fact]
    public void Save_OtherCalendarSameAccount_QueuesMoveThenPatch()
    {
        var o      = Occurrence("evt-single", Oct1);
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { CalendarId = Family, Title = "Family dentist" }, EditScope.This, sendUpdates: false);

        var entries = Outbox();
        Assert.Equal(OutboxOperation.Move, entries[0].Operation);
        Assert.Equal(Calendar, entries[0].CalendarId);
        Assert.Equal(Family, entries[0].Payload);
        Assert.Equal(OutboxOperation.Patch, entries[1].Operation);
        Assert.Equal(Family, entries[1].CalendarId);
        Assert.Equal(Family, Occurrence("evt-single", Oct1).CalendarId);
    }

    [Fact]
    public void Delete_ThisInstance_HidesOnlyThatDayAndIsHeldForUndo()
    {
        var o = Occurrence("evt-weekly", Oct9);

        var receipt = _editor.Delete([o], EditScope.This, sendUpdates: true);

        var entry = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Delete, entry.Operation);
        Assert.Equal("evt-weekly_20261009T133000Z", entry.EventId);
        Assert.Equal(_time.GetUtcNow() + EventEditor.UndoWindow, entry.NotBefore);
        Assert.Equal([entry.Seq], receipt.Seqs);
        Assert.DoesNotContain(Day(Oct9), x => x.RecurringEventId == "evt-weekly");
        Assert.Contains(Day(Oct12), x => x.RecurringEventId == "evt-weekly");
    }

    [Fact]
    public void Undo_BeforeSend_RestoresEventAndDropsEntry()
    {
        var receipt = _editor.Delete([Occurrence("evt-single", Oct1)], EditScope.This, sendUpdates: true);
        Assert.DoesNotContain(Day(Oct1), x => x.EventId == "evt-single");

        Assert.Equal(UndoResult.Restored, _editor.Undo(receipt));

        Assert.Empty(Outbox());
        Assert.Equal("Dentist appointment", Occurrence("evt-single", Oct1).Title);
    }

    [Fact]
    public void Undo_JustBeforeHoldEnds_StillWorks()
    {
        var receipt = _editor.Delete([Occurrence("evt-single", Oct1)], EditScope.This, sendUpdates: true);
        _time.Advance(EventEditor.UndoWindow - TimeSpan.FromMilliseconds(1));

        Assert.Equal(UndoResult.Restored, _editor.Undo(receipt));
        Assert.Empty(Outbox());
    }

    // Late Undo (after the hold window): a quiet copy with a new ID, or a restore for repeating events

    [Fact]
    public void Undo_AfterSend_QueuesQuietCopyWithNewId()
    {
        var receipt = _editor.Delete([Occurrence("evt-single", Oct1)], EditScope.This, sendUpdates: true);
        SendAll();

        Assert.Equal(UndoResult.Recreated, _editor.Undo(receipt));

        var create = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Create, create.Operation);
        Assert.False(create.SendUpdates);
        Assert.NotEqual("evt-single", create.EventId);
        Assert.Null(create.DependsOn);
        Assert.Equal("Dentist appointment", Day(Oct1).Single(o => o.EventId == create.EventId).Title);
    }

    [Fact]
    public void Undo_AfterHoldEnds_KeepsTheDeleteAndQueuesTheCopyBehindIt()
    {
        var receipt = _editor.Delete([Occurrence("evt-single", Oct1)], EditScope.This, sendUpdates: true);
        _time.Advance(EventEditor.UndoWindow);

        Assert.Equal(UndoResult.Recreated, _editor.Undo(receipt));

        var entries = Outbox();
        Assert.Equal(2, entries.Count);
        Assert.Equal(OutboxOperation.Delete, entries[0].Operation);
        Assert.Equal(OutboxOperation.Create, entries[1].Operation);
        Assert.Equal(entries[0].Seq, entries[1].DependsOn);
    }

    [Fact]
    public void Undo_AfterSend_CopyKeepsGuestsAsksForNewMeetAndResetsReplies()
    {
        Seed("""{"id":"evt-guests","status":"confirmed","etag":"\"7\"","summary":"Sync","start":{"dateTime":"2026-10-01T15:00:00Z"},"end":{"dateTime":"2026-10-01T16:00:00Z"},"organizer":{"email":"leaf.tester@gmail.com","self":true},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"organizer":true,"responseStatus":"accepted"},{"email":"sam@example.com","responseStatus":"accepted","comment":"See you"}],"hangoutLink":"https://meet.google.com/abc-defg-hij","conferenceData":{"conferenceId":"abc-defg-hij","conferenceSolution":{"key":{"type":"hangoutsMeet"}},"entryPoints":[{"entryPointType":"video","uri":"https://meet.google.com/abc-defg-hij"}]}}""");
        var receipt = _editor.Delete([Occurrence("evt-guests", Oct1)], EditScope.This, sendUpdates: true);
        SendAll();

        _editor.Undo(receipt);

        var body = JsonNode.Parse(Assert.Single(Outbox()).Payload!)!.AsObject();
        var sam  = body["attendees"]!.AsArray().Single(a => (string?)a!["email"] == "sam@example.com")!;
        Assert.Equal("needsAction", (string?)sam["responseStatus"]);
        Assert.Null(sam["comment"]);
        Assert.Null(body["hangoutLink"]);
        Assert.Null(body["conferenceData"]!["conferenceId"]);
        Assert.Null(body["conferenceData"]!["entryPoints"]);
        Assert.Equal("hangoutsMeet", (string?)body["conferenceData"]!["createRequest"]!["conferenceSolutionKey"]!["type"]);
        Assert.False(string.IsNullOrEmpty((string?)body["conferenceData"]!["createRequest"]!["requestId"]));
    }

    [Fact]
    public void Undo_AfterSend_RepeatingInstance_RestoresInsteadOfCopying()
    {
        var receipt = _editor.Delete([Occurrence("evt-weekly", Oct9)], EditScope.This, sendUpdates: true);
        SendAll();

        Assert.Equal(UndoResult.Recreated, _editor.Undo(receipt));

        var patch = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Patch, patch.Operation);
        Assert.Equal("evt-weekly_20261009T133000Z", patch.EventId);
        Assert.Equal("""{"status":"confirmed"}""", patch.Payload);
        Assert.Null(patch.BaseEtag);
        Assert.False(patch.SendUpdates);
        Assert.Contains(Day(Oct9), x => x.RecurringEventId == "evt-weekly");
    }

    [Fact]
    public void Undo_AfterSend_WholeSeries_RecreatesTheSeriesWithCanceledDaysAsExDates()
    {
        _editor.Delete([Occurrence("evt-weekly", Oct12)], EditScope.This, sendUpdates: true);
        SendAll();
        var receipt = _editor.Delete([Occurrence("evt-weekly", Oct5)], EditScope.All, sendUpdates: true);
        SendAll();

        _editor.Undo(receipt);

        var create = Assert.Single(Outbox());
        var lines  = JsonNode.Parse(create.Payload!)!["recurrence"]!.AsArray().Select(l => (string)l!).ToList();
        Assert.False(create.SendUpdates);
        Assert.Contains(lines, l => l.StartsWith("RRULE:", StringComparison.Ordinal));
        Assert.Contains("EXDATE:20261012T133000Z", lines);
        Assert.DoesNotContain(Day(Oct12), x => x.RecurringEventId == create.EventId);
        Assert.Contains(Day(Oct9), x => x.RecurringEventId == create.EventId);
    }

    [Fact]
    public void Undo_AfterSend_ThisAndFollowing_RestoresTheRepeat()
    {
        var before  = EventJson.RecurrenceOf(Stored("evt-weekly").RawJson);
        var etag    = Stored("evt-weekly").Etag;
        var receipt = _editor.Delete([Occurrence("evt-weekly", Oct9)], EditScope.Following, sendUpdates: true);
        SendAll();

        _editor.Undo(receipt);

        var patch = Assert.Single(Outbox());
        Assert.Equal("evt-weekly", patch.EventId);
        Assert.Equal(etag, patch.BaseEtag);
        Assert.False(patch.SendUpdates);
        Assert.Equal(before, JsonNode.Parse(patch.Payload!)!["recurrence"]!.AsArray().Select(l => (string)l!).ToList());
        Assert.Contains(Day(Oct12), x => x.RecurringEventId == "evt-weekly");
    }

    [Fact]
    public void Undo_SameReceiptTwice_SecondDoesNothing()
    {
        var receipt = _editor.Delete([Occurrence("evt-single", Oct1)], EditScope.This, sendUpdates: true);
        SendAll();

        _editor.Undo(receipt);

        Assert.Equal(UndoResult.Nothing, _editor.Undo(receipt));
        Assert.Single(Outbox());
    }

    // What the sender does on success: the entries leave the outbox
    void SendAll()
    {
        using var conn = _db.Database.Open();
        foreach (var entry in OutboxStore.Pending(conn, Account))
        {
            OutboxStore.Remove(conn, null, entry.Seq);
        }
    }

    StoredEvent Stored(string id)
    {
        using var conn = _db.Database.Open();
        return EventStore.Get(conn, null, Account, Calendar, id)!;
    }

    [Fact]
    public void Respond_SetsSelfLocallyAndQueuesReply()
    {
        Seed(Invite);
        var o = Occurrence("evt-invite", Oct3);

        _editor.Respond(o, ResponseStatus.Declined, "Out sick", sendUpdates: true, EditScope.This);

        var entry = Assert.Single(Outbox());
        Assert.Equal(OutboxOperation.Rsvp, entry.Operation);
        Assert.Equal("""{"responseStatus":"declined","comment":"Out sick"}""", entry.Payload);
        Assert.Equal(ResponseStatus.Declined, Occurrence("evt-invite", Oct3).SelfResponse);
    }

    [Fact]
    public void Permissions_GuestOrReaderCalendar_CannotEdit()
    {
        Seed(Invite);
        Assert.Equal((false, true), _editor.Permissions(Occurrence("evt-invite", Oct3)));
        Assert.Equal((true, false), _editor.Permissions(Occurrence("evt-single", Oct1)));

        // The same calendar shared with you read-only
        using (var conn = _db.Database.Open())
        {
            var entries = JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items;
            entries[0].AccessRole = "reader";
            CalendarStore.ReplaceForAccount(conn, Account, entries);
        }

        Assert.Equal((false, false), _editor.Permissions(Occurrence("evt-single", Oct1)));
    }

    [Fact]
    public void Duplicate_CopiesWithoutRepeatAtTheNewTime()
    {
        var o = Occurrence("evt-weekly", Oct9);

        var id = _editor.Duplicate(o, Utc(10, 10, 15), Utc(10, 10, 15, 30), isAllDay: false);

        var body = JsonNode.Parse(Assert.Single(Outbox()).Payload!)!;
        Assert.Equal(id, (string?)body["id"]);
        Assert.Null(body["recurrence"]);
        Assert.Equal("Team standup", Occurrence(id, Oct10).Title);
    }

    [Fact]
    public void Paste_AfterSourceDeleted_StillCreatesCopy()
    {
        var o    = Occurrence("evt-single", Oct1);
        var copy = _editor.CopyOf(o);
        _editor.Delete([o], EditScope.This, sendUpdates: false);

        var id = _editor.Paste(copy, Utc(10, 3, 17), Utc(10, 3, 18), isAllDay: false);

        Assert.Equal("Dentist appointment", Occurrence(id, Oct3).Title);
        Assert.Equal("2026-10-03T13:00:00-04:00", (string?)JsonNode.Parse(Outbox().Single(e => e.Operation == OutboxOperation.Create).Payload!)!["start"]!["dateTime"]);
    }

    [Fact]
    public void Paste_OfAMeeting_IsAPrivateCopyWithoutGuests()
    {
        Seed(Invite);
        var copy = _editor.CopyOf(Occurrence("evt-invite", Oct3));

        var id = _editor.Paste(copy, Utc(10, 5, 17), Utc(10, 5, 18), isAllDay: false);

        var body = JsonNode.Parse(Assert.Single(Outbox()).Payload!)!.AsObject();
        Assert.False(body.ContainsKey("attendees"));
        Assert.False(body.ContainsKey("organizer"));
        Assert.Equal("Planning", (string?)body["summary"]);
        Assert.Empty(_editor.Load(Occurrence(id, Oct5)).Guests);
    }

    [Fact]
    public void Recolor_TwoEvents_PatchesEach()
    {
        _editor.Recolor([Occurrence("evt-single", Oct1), Occurrence("evt-allday", Oct12)], "11", EditScope.This);

        var entries = Outbox();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("""{"colorId":"11"}""", e.Payload));
    }

    [Fact]
    public void Move_AllDayIntoTimedSlot_UsesLocalZone()
    {
        var o = Occurrence("evt-allday", Oct12);

        _editor.Move([new EventMove(o, Utc(10, 12, 14), Utc(10, 12, 15), IsAllDay: false)], EditScope.This, sendUpdates: false);

        var patch = JsonNode.Parse(Assert.Single(Outbox()).Payload!)!;
        Assert.Equal("2026-10-12T10:00:00-04:00", (string?)patch["start"]!["dateTime"]);
        Assert.Equal("America/New_York", (string?)patch["start"]!["timeZone"]);
        Assert.False(Occurrence("evt-allday", Oct12).IsAllDay);
    }

    // Makes the Oct 9 standup its own exception (own title, a guest, and 30 minutes longer) and returns it
    CalendarOccurrence ChangedInstance()
    {
        var o      = Occurrence("evt-weekly", Oct9);
        var before = _editor.Load(o);
        _editor.Save(o, before, before with { Title = "Own title", Guests = [new Guest("guest@example.com")], End = o.End.AddMinutes(30) }, EditScope.This, sendUpdates: false);

        var changed = Assert.Single(Day(Oct9), x => x.RecurringEventId == "evt-weekly");
        Assert.NotEqual("evt-weekly", changed.EventId);
        return changed;
    }

    [Fact]
    public void Save_AllFromChangedInstance_PatchesOnlyTheEditedField()
    {
        var o      = ChangedInstance();
        var before = _editor.Load(o);

        _editor.Save(o, before, before with { Location = "Room 4" }, EditScope.All, sendUpdates: false);

        var entry = Outbox()[^1];
        Assert.Equal("evt-weekly", entry.EventId);
        Assert.Equal("""{"location":"Room 4"}""", entry.Payload);
        Assert.Equal("Team standup", Occurrence("evt-weekly", Oct12).Title);
    }

    [Fact]
    public void Move_AllFromChangedInstance_OnlyShiftsSeriesTimes()
    {
        var o = ChangedInstance();

        _editor.Move([new EventMove(o, o.Start.AddHours(1), o.End.AddHours(1), IsAllDay: false)], EditScope.All, sendUpdates: false);

        var patch = JsonNode.Parse(Outbox()[^1].Payload!)!.AsObject();
        Assert.Equal(["end", "start"], patch.Select(p => p.Key).Order());
        Assert.Equal("2026-10-05T10:30:00-04:00", (string?)patch["start"]!["dateTime"]);
        Assert.Equal("2026-10-05T11:00:00-04:00", (string?)patch["end"]!["dateTime"]);
    }

    [Fact]
    public void Move_TwoInstancesWithAll_ShiftsTheSeriesOnce()
    {
        var fri = Occurrence("evt-weekly", Oct9);
        var mon = Occurrence("evt-weekly", Oct12);

        _editor.Move([new EventMove(fri, fri.Start.AddHours(1), fri.End.AddHours(1), false), new EventMove(mon, mon.Start.AddHours(1), mon.End.AddHours(1), false)], EditScope.All, sendUpdates: false);

        Assert.Single(Outbox());
        Assert.Equal(Utc(10, 12, 14, 30), Occurrence("evt-weekly", Oct12).Start);
    }

    [Fact]
    public void Delete_TwoInstancesWithFollowing_EndsBeforeTheEarliest()
    {
        var fri = Occurrence("evt-weekly", Oct9);
        var mon = Occurrence("evt-weekly", Oct12);

        _editor.Delete([mon, fri], EditScope.Following, sendUpdates: false);

        var entry = Assert.Single(Outbox());
        Assert.Contains("UNTIL=20261009T132959Z", entry.Payload!, StringComparison.Ordinal);
        Assert.DoesNotContain(Day(Oct9), x => x.RecurringEventId == "evt-weekly");
        Assert.DoesNotContain(Day(Oct12), x => x.RecurringEventId == "evt-weekly");
        Assert.Contains(Day(Oct5), x => x.RecurringEventId == "evt-weekly");
    }

    [Fact]
    public void Edits_WithoutPermission_ThrowAndChangeNothing()
    {
        Seed(Invite);
        var invite = Occurrence("evt-invite", Oct3);
        var before = _editor.Load(invite);
        var own    = Occurrence("evt-single", Oct1);

        Assert.Throws<InvalidOperationException>(() => _editor.Save(invite, before, before with { Title = "Mine now" }, EditScope.This, sendUpdates: false));
        Assert.Throws<InvalidOperationException>(() => _editor.Move([new EventMove(invite, invite.Start.AddHours(1), invite.End.AddHours(1), false)], EditScope.This, sendUpdates: false));
        Assert.Throws<InvalidOperationException>(() => _editor.Recolor([invite], "11", EditScope.This));
        Assert.Throws<InvalidOperationException>(() => _editor.Delete([invite], EditScope.This, sendUpdates: false));
        Assert.Throws<InvalidOperationException>(() => _editor.Respond(own, ResponseStatus.Accepted, null, sendUpdates: false, EditScope.This));

        Assert.Empty(Outbox());
        Assert.Equal("Planning", Occurrence("evt-invite", Oct3).Title);
    }

    [Theory]
    [InlineData(EditScope.This)]
    [InlineData(EditScope.Following)]
    public void Save_InstanceToOtherCalendar_ThrowsForThisAndFollowing(EditScope scope)
    {
        var o      = Occurrence("evt-weekly", Oct9);
        var before = _editor.Load(o);

        Assert.Throws<ArgumentException>(() => _editor.Save(o, before, before with { CalendarId = Family }, scope, sendUpdates: false));
        Assert.Empty(Outbox());
    }

    [Fact]
    public void Delete_FollowingWithStaleLaterOccurrence_NeverExtendsTheSeries()
    {
        var fri = Occurrence("evt-weekly", Oct9);
        var mon = Occurrence("evt-weekly", Oct12);
        _editor.Delete([fri], EditScope.Following, sendUpdates: false);

        // A Separate Call With An Occurrence Captured Before The First Delete
        _editor.Delete([mon], EditScope.Following, sendUpdates: false);

        var entry = Assert.Single(Outbox());
        Assert.Contains("UNTIL=20261009T132959Z", entry.Payload!, StringComparison.Ordinal);
        Assert.DoesNotContain(Day(Oct9), x => x.RecurringEventId == "evt-weekly");
    }
}
