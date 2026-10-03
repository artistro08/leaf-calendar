using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OccurrenceQueryTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();

    public OccurrenceQueryTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account,
            JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Primary, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    void Insert(string json, string account = "109876543210", string calendar = Primary)
    {
        using var conn = _db.Database.Open();
        using var doc  = JsonDocument.Parse(json);
        EventStore.Apply(conn, null, account, calendar, doc.RootElement);
    }

    IReadOnlyList<CalendarOccurrence> Load(DateOnly from, DateOnly to, TimeZoneInfo? zone = null, bool includeDeclined = false)
    {
        using var conn = _db.Database.Open();
        return OccurrenceQuery.Load(conn, from, to, zone ?? NewYork, includeDeclined);
    }

    static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void Load_SingleEvent_ReturnsInstanceWithCalendarColor()
    {
        var o = Assert.Single(Load(D(10, 1), D(10, 2)));

        Assert.Equal("evt-single", o.EventId);
        Assert.Equal("Dentist appointment", o.Title);
        Assert.Equal("#9fe1e7", o.CalendarColor);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), o.Start);
        Assert.False(o.IsAllDay);
    }

    [Fact]
    public void Load_CanceledException_HidesInstance()
    {
        var starts = Load(D(10, 5), D(10, 12)).Where(o => o.RecurringEventId == "evt-weekly").Select(o => o.Start.UtcDateTime.Day);

        Assert.Equal([5, 9], starts);
    }

    [Fact]
    public void Load_MovedException_ReplacesOriginalInstance()
    {
        Insert("""
            {"id":"evt-weekly_20261009T133000Z","status":"confirmed","recurringEventId":"evt-weekly","summary":"Standup (moved)",
             "originalStartTime":{"dateTime":"2026-10-09T09:30:00-04:00","timeZone":"America/New_York"},
             "start":{"dateTime":"2026-10-10T11:00:00-04:00"},"end":{"dateTime":"2026-10-10T11:30:00-04:00"}}
            """);

        var weekly = Load(D(10, 5), D(10, 12)).Where(o => o.RecurringEventId == "evt-weekly").ToList();

        Assert.Equal([5, 10], weekly.Select(o => o.Start.UtcDateTime.Day));
        Assert.Equal("Standup (moved)", weekly[1].Title);
    }

    [Fact]
    public void Load_AllDayEvent_SameDateInEveryZone()
    {
        foreach (var zone in new[] { "Asia/Tokyo", "America/Los_Angeles", "Pacific/Kiritimati", "Pacific/Pago_Pago" })
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);

            var o = Assert.Single(Load(D(10, 12), D(10, 13), tz), x => x.EventId == "evt-allday");
            Assert.True(o.IsAllDay);
            Assert.Equal(D(10, 12), o.AllDayStart);
            Assert.Equal(D(10, 13), o.AllDayEnd);

            Assert.DoesNotContain(Load(D(10, 11), D(10, 12), tz), x => x.EventId == "evt-allday");
            Assert.DoesNotContain(Load(D(10, 13), D(10, 14), tz), x => x.EventId == "evt-allday");
        }
    }

    [Fact]
    public void Load_AllDayWeeklySeries_ExpandsByDate()
    {
        Insert("""
            {"id":"evt-yoga","status":"confirmed","summary":"Yoga","recurrence":["RRULE:FREQ=WEEKLY"],
             "start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}
            """);

        var o = Assert.Single(Load(D(10, 8), D(10, 9)), x => x.RecurringEventId == "evt-yoga");

        Assert.Equal(D(10, 8), o.AllDayStart);
    }

    [Fact]
    public void Load_EventStartingBeforeWindow_Included()
    {
        Insert("""
            {"id":"evt-late","status":"confirmed","summary":"Late shift",
             "start":{"dateTime":"2026-09-30T22:00:00-04:00"},"end":{"dateTime":"2026-10-01T02:00:00-04:00"}}
            """);

        Assert.Contains(Load(D(10, 1), D(10, 2)), o => o.EventId == "evt-late");
    }

    [Fact]
    public void Load_HiddenCalendar_Excluded()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Primary, hidden: true);
        }

        Assert.Empty(Load(D(10, 1), D(10, 31)));
    }

    [Fact]
    public void Load_Declined_ExcludedUnlessRequested()
    {
        Insert("""
            {"id":"evt-no","status":"confirmed","summary":"Optional sync","attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],
             "start":{"dateTime":"2026-10-02T09:00:00-04:00"},"end":{"dateTime":"2026-10-02T10:00:00-04:00"}}
            """);

        Assert.DoesNotContain(Load(D(10, 2), D(10, 3)), o => o.EventId == "evt-no");
        Assert.Equal(ResponseStatus.Declined, Assert.Single(Load(D(10, 2), D(10, 3), includeDeclined: true), o => o.EventId == "evt-no").SelfResponse);
    }

    [Fact]
    public void Load_SameEventInTwoAccounts_ShownOnce()
    {
        using (var conn = _db.Database.Open())
        {
            AccountStore.Upsert(conn, new Account("222", "zzz.second@gmail.com", null, null, AccountStatus.Ok));
            CalendarStore.ReplaceForAccount(conn, "222", [new CalendarListEntry { Id = "zzz.second@gmail.com", Summary = "Second", AccessRole = "owner", Primary = true, Selected = true }]);
        }

        Insert("""
            {"id":"copy-in-second","status":"confirmed","iCalUID":"evt-single@google.com","summary":"Dentist appointment",
             "start":{"dateTime":"2026-10-01T09:00:00-04:00"},"end":{"dateTime":"2026-10-01T10:00:00-04:00"}}
            """, account: "222", calendar: "zzz.second@gmail.com");

        var o = Assert.Single(Load(D(10, 1), D(10, 2)));
        Assert.Equal(Account, o.AccountId);
    }

    [Fact]
    public void Load_SameEventInTwoAccounts_BothKeptWhenTheCallerMergesThem()
    {
        using (var conn = _db.Database.Open())
        {
            AccountStore.Upsert(conn, new Account("222", "zzz.second@gmail.com", null, null, AccountStatus.Ok));
            CalendarStore.ReplaceForAccount(conn, "222", [new CalendarListEntry { Id = "zzz.second@gmail.com", Summary = "Second", AccessRole = "owner", Primary = true, Selected = true }]);
        }

        Insert("""
            {"id":"copy-in-second","status":"confirmed","iCalUID":"evt-single@google.com","summary":"Dentist appointment",
             "start":{"dateTime":"2026-10-01T09:00:00-04:00"},"end":{"dateTime":"2026-10-01T10:00:00-04:00"}}
            """, account: "222", calendar: "zzz.second@gmail.com");

        using var read = _db.Database.Open();
        var copies = OccurrenceQuery.Load(read, D(10, 1), D(10, 2), NewYork, includeDeclined: false, keepSharedCopies: true);

        Assert.Equal(2, copies.Count(o => o.ICalUid == "evt-single@google.com"));
    }

    [Fact]
    public void Load_Title_IsCleanedForOneLineCards()
    {
        Insert("""
            {"id":"evt-messy","status":"confirmed","summary":"Line one\nline two\u202E","start":{"dateTime":"2026-10-03T09:00:00-04:00"},"end":{"dateTime":"2026-10-03T10:00:00-04:00"}}
            """);

        var o = Assert.Single(Load(D(10, 3), D(10, 4)), o => o.EventId == "evt-messy");

        Assert.Equal("Line one line two", o.Title);
    }

    [Fact]
    public void Load_LeafColorOverride_Used()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetColor(conn, Account, Primary, "#16A765");
        }

        Assert.Equal("#16A765", Assert.Single(Load(D(10, 1), D(10, 2))).CalendarColor);
    }
}
