using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class InviteWatcherTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Family  = "family123@group.calendar.google.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public InviteWatcherTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        Synced(Primary);
        Synced(Family);
    }

    public void Dispose() => _db.Dispose();

    void Synced(string calendarId)
    {
        using var conn = _db.Database.Open();
        CalendarStore.SetSyncToken(conn, null, Account, calendarId, "sync-token-1");
    }

    static string Invite(string id = "evt-inv", int sequence = 0, string response = "needsAction", string start = "2026-10-05T15:00:00Z", string end = "2026-10-05T16:00:00Z", string others = "accepted", string extra = "") => $$"""
        {"id":"{{id}}","status":"confirmed","summary":"Planning","sequence":{{sequence}},"organizer":{"email":"boss@example.com"},{{extra}}
         "attendees":[{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"},
                      {"email":"sam@example.com","responseStatus":"{{others}}"},
                      {"email":"leaf.tester@gmail.com","self":true,"responseStatus":"{{response}}"}],
         "start":{"dateTime":"{{start}}"},"end":{"dateTime":"{{end}}"} }
        """;

    void Store(string json, string calendarId = Primary)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, calendarId, json);
    }

    IReadOnlyList<InviteAlert> TakeNew(DateTimeOffset? at = null)
    {
        using var conn = _db.Database.Open();
        return InviteWatcher.TakeNew(conn, at ?? Now, TimeZoneInfo.Utc);
    }

    const string Weekly = "\"recurrence\":[\"RRULE:FREQ=WEEKLY;BYDAY=MO\"],";

    // The Monday 5 October instance of "evt-series", moved to Tuesday by its organizer
    static string MovedInstance() => Invite(
        id: "evt-series_20261005T150000Z",
        start: "2026-10-06T15:00:00Z",
        end: "2026-10-06T16:00:00Z",
        extra: "\"recurringEventId\":\"evt-series\",\"originalStartTime\":{\"dateTime\":\"2026-10-05T15:00:00Z\"},");

    [Fact]
    public void TakeNew_SomeoneElsesCalendar_Ignored()
    {
        TakeNew();
        Store(Invite(), Family);

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_ChangedInstanceOfASeriesYouHave_NotifiesAsAnUpdateAnsweredAlone()
    {
        TakeNew();
        Store(Invite(id: "evt-series", response: "accepted", start: "2026-09-28T15:00:00Z", end: "2026-09-28T16:00:00Z", extra: Weekly));
        Assert.Empty(TakeNew());

        Store(MovedInstance());

        var invite = Assert.Single(TakeNew());
        Assert.True(invite.IsUpdate);
        Assert.Equal("evt-series_20261005T150000Z", invite.Occurrence.EventId);
        Assert.Equal(EditScope.This, InviteWatcher.ReplyScope(invite.Occurrence));
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_NewSeriesWithChangedInstances_NotifiesOnceAnsweredForTheSeries()
    {
        TakeNew();
        Store(MovedInstance());
        Store(Invite(id: "evt-series", start: "2026-09-28T15:00:00Z", end: "2026-09-28T16:00:00Z", extra: Weekly));

        var invite = Assert.Single(TakeNew());

        Assert.False(invite.IsUpdate);
        Assert.Equal("evt-series", invite.Occurrence.EventId);
        Assert.Equal(EditScope.All, InviteWatcher.ReplyScope(invite.Occurrence));
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_UnansweredSeriesAYearOn_StaysRecorded()
    {
        Store(Invite(id: "evt-series", start: "2026-09-28T15:00:00Z", end: "2026-09-28T16:00:00Z", extra: Weekly));
        Assert.Empty(TakeNew());
        Assert.Empty(TakeNew(Now.AddDays(300)));

        // What the scheduler does on every plan: forget notifications whose event ended two days ago
        using (var conn = _db.Database.Open())
        {
            AlertLedger.Prune(conn, Now.AddDays(368));
        }

        Assert.Empty(TakeNew(Now.AddDays(370)));
    }

    [Fact]
    public void TakeNew_FirstLookAtAnAccount_RecordsItsInvitesQuietly()
    {
        Store(Invite());

        Assert.Empty(TakeNew());
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_NewInviteLater_NotifiesOnce()
    {
        TakeNew();
        Store(Invite());

        var invite = Assert.Single(TakeNew());
        Assert.Equal("evt-inv", invite.Occurrence.EventId);
        Assert.Equal("Planning", invite.Details.Title);
        Assert.False(invite.IsUpdate);
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_GuestReplyOnly_DoesNotNotifyAgain()
    {
        TakeNew();
        Store(Invite());
        TakeNew();

        Store(Invite(others: "declined"));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_OrganizerChange_NotifiesAsAnUpdate()
    {
        TakeNew();
        Store(Invite());
        TakeNew();

        Store(Invite(sequence: 1, start: "2026-10-05T17:00:00Z", end: "2026-10-05T18:00:00Z"));

        Assert.True(Assert.Single(TakeNew()).IsUpdate);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("declined")]
    [InlineData("tentative")]
    public void TakeNew_AlreadyAnswered_Ignored(string response)
    {
        TakeNew();
        Store(Invite(response: response));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_YouOrganizeIt_Ignored()
    {
        TakeNew();
        Store(Invite(extra: "\"creator\":{\"self\":true},").Replace("\"organizer\":{\"email\":\"boss@example.com\"}", "\"organizer\":{\"email\":\"leaf.tester@gmail.com\",\"self\":true}", StringComparison.Ordinal));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_PastInvite_Ignored()
    {
        TakeNew();
        Store(Invite(start: "2026-09-29T15:00:00Z", end: "2026-09-29T16:00:00Z"));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_HiddenCalendar_Ignored()
    {
        TakeNew();
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Primary, true);
        }

        Store(Invite());

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_AccountStillOnItsFirstSync_WaitsThenRecordsQuietly()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetSyncToken(conn, null, Account, Family, null);
        }

        Store(Invite());
        Assert.Empty(TakeNew());

        Synced(Family);
        Assert.Empty(TakeNew());

        Store(Invite(id: "evt-inv2"));
        Assert.Equal("evt-inv2", Assert.Single(TakeNew()).Occurrence.EventId);
    }

    [Fact]
    public void TakeNew_RepeatingInvite_PointsAtTheNextInstance()
    {
        TakeNew();
        Store(Invite(id: "evt-series", start: "2026-09-28T15:00:00Z", end: "2026-09-28T16:00:00Z", extra: "\"recurrence\":[\"RRULE:FREQ=WEEKLY;BYDAY=MO\"],"));

        var invite = Assert.Single(TakeNew());

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), invite.Occurrence.Start);
    }

    [Fact]
    public void TakeNew_NewlySubscribedCalendar_RecordsItsInvitesQuietly()
    {
        TakeNew();
        const string Team = "team456@group.calendar.google.com";
        using (var conn = _db.Database.Open())
        {
            var entries = JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items;
            entries.Add(new CalendarListEntry { Id = Team, Summary = "Team", Selected = true });
            CalendarStore.ReplaceForAccount(conn, Account, entries);
        }

        Synced(Team);
        Store(Invite(), Team);
        Assert.Empty(TakeNew());

        // Not your own calendar, so its invitations are never yours to answer
        Store(Invite(id: "evt-inv2"), Team);
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_UnhiddenCalendar_DoesNotBringInvitesThatCameWhileHidden()
    {
        TakeNew();
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Primary, true);
        }

        Store(Invite());
        Assert.Empty(TakeNew());

        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Primary, false);
        }

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_OneCalendarStillOnItsFirstSync_OthersStillNotify()
    {
        TakeNew();
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetSyncToken(conn, null, Account, Family, null);
        }

        Store(Invite());

        Assert.Equal("evt-inv", Assert.Single(TakeNew()).Occurrence.EventId);
    }

    [Fact]
    public void TakeNew_EndedUnansweredSeries_RecordedQuietly()
    {
        TakeNew();
        Store(Invite(id: "evt-old", start: "2026-08-03T15:00:00Z", end: "2026-08-03T16:00:00Z", extra: "\"recurrence\":[\"RRULE:FREQ=WEEKLY;COUNT=3\"],"));

        Assert.Empty(TakeNew());

        using var conn = _db.Database.Open();
        Assert.True(AlertLedger.Contains(conn, $"Invite|{Account}|{Primary}|evt-old|0"));
    }

    [Fact]
    public void Forget_AccountRemoved_ClearsItsMarksAndAlerts()
    {
        Store(Invite());
        TakeNew();

        using var conn = _db.Database.Open();
        InviteWatcher.Forget(conn, Account);

        Assert.False(AlertLedger.HasPrefix(conn, $"Invite|{Account}|"));
        Assert.Null(AlertLedger.GetMark(conn, $"invites-seeded:{Account}|{Primary}"));
    }
}
