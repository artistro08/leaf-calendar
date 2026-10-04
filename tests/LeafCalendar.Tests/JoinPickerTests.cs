using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class JoinPickerTests : IDisposable
{
    private const string Primary = "leaf.tester@gmail.com";
    private const string SecondId = "222222222222";
    private const string Second = "second@example.com";
    private static readonly string Account = TestDatabase.SampleAccount.Id;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 17, 58, 0, TimeSpan.Zero);

    private readonly TestDatabase _db = new();

    public JoinPickerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        AccountStore.Upsert(conn, new Account(SecondId, Second, "Second", null, AccountStatus.Ok));
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        CalendarStore.ReplaceForAccount(conn, SecondId, [new CalendarListEntry { Id = Second, Summary = Second, AccessRole = "owner", Primary = true, Selected = true }]);
    }

    public void Dispose() => _db.Dispose();

    private void Store(string account, string calendar, string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, account, calendar, json);
    }

    private Uri? Find(DateTimeOffset? now = null)
    {
        using var conn = _db.Database.Open();
        return JoinPicker.Find(conn, now ?? Now, TimeZoneInfo.Utc);
    }

    private static JoinTarget Target(string id, DateTimeOffset start, int minutes = 30, bool allDay = false) =>
        new(new CalendarOccurrence("a", "cal", id, null, null, start, start.AddMinutes(minutes), allDay, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, true),
            new Uri("https://meet.google.com/" + id));

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Pick_TwoUpcoming_TakesTheSoonest()
    {
        var picked = JoinPicker.Pick([Target("later", At(18, 6)), Target("sooner", At(18, 3))], Now);

        Assert.Equal("sooner", picked!.Occurrence.EventId);
    }

    [Fact]
    public void Pick_ExactlyTenMinutesOut_Qualifies()
    {
        Assert.NotNull(JoinPicker.Pick([Target("ten", Now.AddMinutes(10))], Now));
        Assert.Null(JoinPicker.Pick([Target("eleven", Now.AddMinutes(11))], Now));
    }

    [Fact]
    public void Pick_UpcomingBeatsInProgress()
    {
        var picked = JoinPicker.Pick([Target("running", At(17, 30), 60), Target("next", At(18, 5))], Now);

        Assert.Equal("next", picked!.Occurrence.EventId);
    }

    [Fact]
    public void Pick_OnlyInProgress_TakesTheOneThatStartedLast()
    {
        var picked = JoinPicker.Pick([Target("long", At(17, 0), 120), Target("recent", At(17, 45), 30)], Now);

        Assert.Equal("recent", picked!.Occurrence.EventId);
    }

    [Fact]
    public void Pick_EndedOrAllDay_NeverQualify()
    {
        Assert.Null(JoinPicker.Pick([Target("over", At(17, 0), 58), Target("allday", At(0, 0), 1440, allDay: true)], Now));
    }

    [Fact]
    public void Find_MeetingsInTwoAccounts_JoinsTheSoonestWithItsOwnAccount()
    {
        Store(Account, Primary, """{"id":"evt-a","status":"confirmed","summary":"A","hangoutLink":"https://meet.google.com/aaa-aaaa-aaa","start":{"dateTime":"2026-10-01T17:30:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");
        Store(SecondId, Second, """{"id":"evt-b","status":"confirmed","summary":"B","hangoutLink":"https://meet.google.com/bbb-bbbb-bbb","start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Equal("https://meet.google.com/bbb-bbbb-bbb?authuser=second%40example.com", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_OnlyTheRunningMeeting_JoinsItWithItsAccount()
    {
        Store(Account, Primary, """{"id":"evt-a","status":"confirmed","summary":"A","hangoutLink":"https://meet.google.com/aaa-aaaa-aaa","start":{"dateTime":"2026-10-01T17:30:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Equal("https://meet.google.com/aaa-aaaa-aaa?authuser=leaf.tester%40gmail.com", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_DeclinedMeeting_Skipped()
    {
        Store(Account, Primary, """
            {"id":"evt-no","status":"confirmed","summary":"No","hangoutLink":"https://meet.google.com/ccc-cccc-ccc",
             "attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],
             "start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.Null(Find());
    }

    // A colleague's calendar you can only see, under an account listed before yours (so its copy of a shared meeting comes first)
    private const string ColleagueAccount = "000000000000";
    private const string Colleague = "colleague@example.com";

    private void AddColleagueCalendar()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, new Account(ColleagueAccount, "aaa@example.com", "A", null, AccountStatus.Ok));
        CalendarStore.ReplaceForAccount(conn, ColleagueAccount, [new CalendarListEntry { Id = Colleague, Summary = "Colleague", AccessRole = "reader", Selected = true }]);
    }

    [Fact]
    public void Find_ColleaguesMeetingYoureNotIn_Skipped()
    {
        // On their calendar Google marks the colleague as "self"
        AddColleagueCalendar();
        Store(ColleagueAccount, Colleague, """
            {"id":"evt-theirs","status":"confirmed","summary":"Theirs","hangoutLink":"https://meet.google.com/ttt-tttt-ttt",
             "attendees":[{"email":"colleague@example.com","self":true,"responseStatus":"accepted"},{"email":"boss@example.com","responseStatus":"accepted"}],
             "start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.Null(Find());
    }

    [Fact]
    public void Find_YourMeetingAlsoOnAColleaguesCalendar_JoinsWithYourCopy()
    {
        AddColleagueCalendar();
        Store(ColleagueAccount, Colleague, """
            {"id":"evt-both","iCalUID":"both@google.com","status":"confirmed","summary":"Both","hangoutLink":"https://meet.google.com/bbb-bbbb-bbb",
             "attendees":[{"email":"colleague@example.com","self":true,"responseStatus":"accepted"},{"email":"leaf.tester@gmail.com","responseStatus":"accepted"}],
             "start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);
        Store(Account, Primary, """
            {"id":"evt-both","iCalUID":"both@google.com","status":"confirmed","summary":"Both","hangoutLink":"https://meet.google.com/bbb-bbbb-bbb",
             "attendees":[{"email":"colleague@example.com","responseStatus":"accepted"},{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"accepted"}],
             "start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.Equal("https://meet.google.com/bbb-bbbb-bbb?authuser=leaf.tester%40gmail.com", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_NoGuestsOnACalendarYouDontOwn_Skipped()
    {
        // Family is a calendar you can edit but don't own
        Store(Account, "family123@group.calendar.google.com", """{"id":"evt-family","status":"confirmed","summary":"Family call","hangoutLink":"https://meet.google.com/fff-ffff-fff","start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Null(Find());
    }

    [Fact]
    public void Find_YourMeetingWithGuests_Joins()
    {
        Store(Account, Primary, """
            {"id":"evt-mine","status":"confirmed","summary":"Mine","hangoutLink":"https://meet.google.com/mmm-mmmm-mmm",
             "attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"tentative"},{"email":"boss@example.com","responseStatus":"accepted"}],
             "start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.NotNull(Find());
    }

    [Fact]
    public void Find_ZoomLinkInTheDescription_OpensAsIs()
    {
        Store(Account, Primary, """
            {"id":"evt-zoom","status":"confirmed","summary":"Zoom","description":"Join: https://example.zoom.us/j/123456789",
             "start":{"dateTime":"2026-10-01T18:05:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.Equal("https://example.zoom.us/j/123456789", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_NoMeetingWithALink_ReturnsNull()
    {
        Store(Account, Primary, """{"id":"evt-plain","status":"confirmed","summary":"Plain","start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Null(Find());
    }

    [Fact]
    public void PickNext_NothingWithinTenMinutes_TakesTheNextWithinTheLookahead()
    {
        var picked = JoinPicker.PickNext([Target("afternoon", At(20, 0)), Target("tomorrow", At(23, 30))], Now, TimeSpan.FromHours(4));

        Assert.Equal("afternoon", picked!.Occurrence.EventId);
    }

    [Fact]
    public void PickNext_TheJoinRuleStillWins()
    {
        // A running meeting beats a later one, as the join rule says
        var picked = JoinPicker.PickNext([Target("running", At(17, 45)), Target("later", At(19, 0))], Now, TimeSpan.FromHours(8));

        Assert.Equal("running", picked!.Occurrence.EventId);
    }

    [Fact]
    public void PickNext_BeyondTheLookahead_IsNull() =>
        Assert.Null(JoinPicker.PickNext([Target("evening", At(23, 0))], Now, TimeSpan.FromHours(2)));

    [Fact]
    public void PickNext_AllDay_Skipped() =>
        Assert.Null(JoinPicker.PickNext([Target("holiday", At(19, 0), allDay: true)], Now, TimeSpan.FromHours(8)));

    [Fact]
    public void FindNext_AnHourOut_OpensIt_WhileFindWaits()
    {
        Store(Account, Primary, """
            {"id":"evt-later","status":"confirmed","summary":"Later","hangoutLink":"https://meet.google.com/ddd-dddd-ddd",
             "start":{"dateTime":"2026-10-01T19:00:00Z"},"end":{"dateTime":"2026-10-01T19:30:00Z"}}
            """);

        using var conn = _db.Database.Open();
        Assert.Null(JoinPicker.Find(conn, Now, TimeZoneInfo.Utc));
        Assert.Equal("https://meet.google.com/ddd-dddd-ddd?authuser=leaf.tester%40gmail.com", JoinPicker.FindNext(conn, Now, TimeZoneInfo.Utc, TimeSpan.FromHours(8))!.AbsoluteUri);
    }

    [Fact]
    public void FindNext_PastMidnight_LoadsTheNextDay()
    {
        Store(Account, Primary, """
            {"id":"evt-early","status":"confirmed","summary":"Early","hangoutLink":"https://meet.google.com/eee-eeee-eee",
             "start":{"dateTime":"2026-10-02T02:00:00Z"},"end":{"dateTime":"2026-10-02T02:30:00Z"}}
            """);

        using var conn = _db.Database.Open();
        Assert.NotNull(JoinPicker.FindNext(conn, Now, TimeZoneInfo.Utc, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Find_UnknownHost_IsNotOpenedBlind()
    {
        Store(Account, Primary, """
            {"id":"evt-odd","status":"confirmed","summary":"Odd","hangoutLink":"https://evil.example/join",
             "conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"https://evil.example/video"}]},
             "start":{"dateTime":"2026-10-01T18:00:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        using var conn = _db.Database.Open();
        Assert.Null(JoinPicker.Find(conn, Now, TimeZoneInfo.Utc));
        Assert.Null(JoinPicker.FindNext(conn, Now, TimeZoneInfo.Utc, TimeSpan.FromHours(8)));

        // The tray's and a toast's Join read this too, so they don't open it blind either
        var odd = Assert.Single(OccurrenceQuery.Load(conn, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), TimeZoneInfo.Utc, includeDeclined: false), o => o.EventId == "evt-odd");
        Assert.Null(JoinPicker.MeetingLink(conn, odd));
    }
}
