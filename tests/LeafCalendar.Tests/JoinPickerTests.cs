using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class JoinPickerTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string SecondId = "222222222222";
    const string Second = "second@example.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly DateTimeOffset Now = new(2026, 10, 1, 17, 58, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public JoinPickerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        AccountStore.Upsert(conn, new Account(SecondId, Second, "Second", null, AccountStatus.Ok));
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        CalendarStore.ReplaceForAccount(conn, SecondId, [new CalendarListEntry { Id = Second, Summary = Second, AccessRole = "owner", Primary = true, Selected = true }]);
    }

    public void Dispose() => _db.Dispose();

    void Store(string account, string calendar, string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, account, calendar, json);
    }

    Uri? Find(DateTimeOffset? now = null)
    {
        using var conn = _db.Database.Open();
        return JoinPicker.Find(conn, now ?? Now, TimeZoneInfo.Utc);
    }

    static JoinTarget Target(string id, DateTimeOffset start, int minutes = 30, bool allDay = false) =>
        new(new CalendarOccurrence("a", "cal", id, null, null, start, start.AddMinutes(minutes), allDay, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, true),
            new Uri("https://meet.google.com/" + id));

    static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, TimeSpan.Zero);

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
}
