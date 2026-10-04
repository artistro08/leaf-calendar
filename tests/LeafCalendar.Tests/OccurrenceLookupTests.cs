using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OccurrenceLookupTests : IDisposable
{
    private const string Primary = "leaf.tester@gmail.com";
    private static readonly string Account = TestDatabase.SampleAccount.Id;
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly TestDatabase _db = new();

    public OccurrenceLookupTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Primary, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    private CalendarOccurrence? Find(string eventId, DateTimeOffset start)
    {
        using var conn = _db.Database.Open();
        return OccurrenceLookup.Find(conn, Account, Primary, eventId, start, NewYork);
    }

    [Fact]
    public void Find_SingleEvent()
    {
        var start = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal(start, Find("evt-single", start)!.Start);
    }

    [Fact]
    public void Find_SeriesInstanceByItsStart()
    {
        var friday = new DateTimeOffset(2026, 10, 9, 13, 30, 0, TimeSpan.Zero);

        var o = Find("evt-weekly", friday)!;

        Assert.Equal(friday, o.Start);
        Assert.Equal("evt-weekly", o.RecurringEventId);
    }

    [Fact]
    public void Find_StartMovedSince_FallsBackToTheEventThatDay()
    {
        var o = Find("evt-single", new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));

        Assert.Equal("evt-single", o!.EventId);
    }

    [Fact]
    public void Find_Unknown_Null()
    {
        Assert.Null(Find("evt-nope", new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Find_StartMovedSince_PicksTheNearestInstance()
    {
        // Thursday evening sits between Wednesday's and Friday's standups, closer to Friday's
        var o = Find("evt-weekly", new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 10, 16, 13, 30, 0, TimeSpan.Zero), o!.Start);
    }

    [Fact]
    public void Find_SharedEvent_FindsTheSecondAccountsCopy()
    {
        // The same meeting (iCalUID and start) is in two accounts; the copy asked for is the one that sorts second
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, new Account("222", "zzz.second@gmail.com", null, null, AccountStatus.Ok));
        CalendarStore.ReplaceForAccount(conn, "222", [new CalendarListEntry { Id = "zzz.second@gmail.com", Summary = "Second", AccessRole = "owner", Primary = true, Selected = true }]);
        using var doc = JsonDocument.Parse("""
            {"id":"copy-in-second","status":"confirmed","iCalUID":"evt-single@google.com","summary":"Dentist appointment",
             "start":{"dateTime":"2026-10-01T09:00:00-04:00"},"end":{"dateTime":"2026-10-01T10:00:00-04:00"}}
            """);
        EventStore.Apply(conn, null, "222", "zzz.second@gmail.com", doc.RootElement);

        var o = OccurrenceLookup.Find(conn, "222", "zzz.second@gmail.com", "copy-in-second", new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), NewYork);

        Assert.Equal("222", o?.AccountId);
    }
}
