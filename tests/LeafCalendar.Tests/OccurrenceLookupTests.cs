using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OccurrenceLookupTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();

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

    CalendarOccurrence? Find(string eventId, DateTimeOffset start)
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
}
