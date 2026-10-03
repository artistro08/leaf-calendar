using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Search;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class EventSearchTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(-4));

    readonly TestDatabase _db = new();

    public EventSearchTests()
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

    IReadOnlyList<SearchHit> Find(string query)
    {
        using var conn = _db.Database.Open();
        return EventSearch.Find(conn, query, Now, NewYork);
    }

    [Theory]
    [InlineData("dentist")]
    [InlineData("DENT")]
    [InlineData("a")]
    [InlineData("nothing-like-this")]
    [InlineData("")]
    public void Index_FindsWhatFindFinds(string query)
    {
        using var conn = _db.Database.Open();
        var index = EventSearch.Index.Build(conn, Now);

        Assert.Equal(Find(query), index.Find(conn, query, Now, NewYork));
        Assert.Equal(Now, index.BuiltAt);
    }

    static string Timed(string id, string title, string start, string end, string extra = "") =>
        $$"""{"id":"{{id}}","status":"confirmed","summary":"{{title}}","start":{"dateTime":"{{start}}"},"end":{"dateTime":"{{end}}"}{{extra}}}""";

    [Fact]
    public void Find_Title_IgnoresCase()
    {
        var hit = Assert.Single(Find("DENTIST"));

        Assert.Equal("evt-single", hit.EventId);
        Assert.Equal(SearchField.Title, hit.Field);
        Assert.Equal("#9FE1E7", hit.Color);                   // EventColors.ResolveAccent (F22)
    }

    [Fact]
    public void Find_LocationGuestAndDescription()
    {
        Insert(Timed("evt-x", "Sync", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00",
            ""","location":"Room 4","description":"<b>Agenda</b> inside","attendees":[{"email":"sam@example.com","displayName":"Sam Lee"}]"""));

        Assert.Equal(SearchField.Location, Assert.Single(Find("room 4")).Field);
        Assert.Equal(SearchField.Guest, Assert.Single(Find("sam@example")).Field);
        Assert.Equal(SearchField.Guest, Assert.Single(Find("lee")).Field);
        Assert.Equal(SearchField.Description, Assert.Single(Find("agenda")).Field);
    }

    [Fact]
    public void Find_EveryWordMustMatch() => Assert.Empty(Find("team holiday"));

    [Fact]
    public void Find_Repeating_GivesTheNextInstanceSkippingCanceledOnes()
    {
        // Now is Tue Oct 6 noon; Wed Oct 7 is canceled in the fixture, so the next standup is Fri Oct 9 9:30 ET
        var hit = Assert.Single(Find("standup"));

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 13, 30, 0, TimeSpan.Zero), hit.Start);
    }

    [Fact]
    public void Find_Repeating_MovedInstance_CarriesTheExceptionsIdAndTimes()
    {
        // Fri Oct 9's standup moved to 11:00 ET
        Insert("""{"id":"evt-weekly_20261009T133000Z","status":"confirmed","iCalUID":"evt-weekly@google.com","summary":"Team standup","recurringEventId":"evt-weekly","originalStartTime":{"dateTime":"2026-10-09T09:30:00-04:00"},"start":{"dateTime":"2026-10-09T11:00:00-04:00"},"end":{"dateTime":"2026-10-09T11:30:00-04:00"}}""",
            Account);

        var hit = Assert.Single(Find("standup"));

        Assert.Equal("evt-weekly_20261009T133000Z", hit.EventId);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero), hit.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 15, 30, 0, TimeSpan.Zero), hit.End);
    }

    [Fact]
    public void Find_AllDaySeries_GivesTheNextDate()
    {
        Insert("""{"id":"bday","status":"confirmed","summary":"Birthday party","start":{"date":"2026-03-10"},"end":{"date":"2026-03-11"},"recurrence":["RRULE:FREQ=YEARLY"]}""");

        var hit = Assert.Single(Find("birthday"));

        Assert.True(hit.IsAllDay);
        Assert.Equal("bday", hit.EventId);
        Assert.Equal(new DateTimeOffset(2027, 3, 10, 0, 0, 0, TimeSpan.Zero), hit.Start);
    }

    [Fact]
    public void Find_Series_ReadBeforeTheRowCap()
    {
        // More single matches than the scan reads, all nearer to now than the series start, all in the past
        using (var conn = _db.Database.Open())
        using (var tx = conn.BeginTransaction())
        {
            for (var i = 0; i < EventSearch.MaxRows; i++)
            {
                using var doc = JsonDocument.Parse(Timed($"note-{i}", "Standup notes", "2026-10-06T10:00:00-04:00", "2026-10-06T10:30:00-04:00"));
                EventStore.Apply(conn, tx, Account, Primary, doc.RootElement);
            }

            tx.Commit();
        }

        var first = Find("standup")[0];

        Assert.Equal("evt-weekly", first.EventId);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 13, 30, 0, TimeSpan.Zero), first.Start);
    }

    [Fact]
    public void Find_UpcomingFirstThenPastNewestFirst()
    {
        Insert(Timed("r1", "Review one", "2026-09-01T10:00:00-04:00", "2026-09-01T11:00:00-04:00"));
        Insert(Timed("r2", "Review two", "2026-10-20T10:00:00-04:00", "2026-10-20T11:00:00-04:00"));
        Insert(Timed("r3", "Review three", "2026-09-20T10:00:00-04:00", "2026-09-20T11:00:00-04:00"));

        Assert.Equal(["r2", "r3", "r1"], Find("review").Select(h => h.EventId));
    }

    [Fact]
    public void Find_PercentAndUnderscore_MatchLiterally()
    {
        Insert(Timed("p", "100% done", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00"));
        Insert(Timed("u", "a_b plan", "2026-10-08T12:00:00-04:00", "2026-10-08T13:00:00-04:00"));

        Assert.Equal(["p"], Find("%").Select(h => h.EventId));
        Assert.Equal(["u"], Find("_").Select(h => h.EventId));
    }

    [Fact]
    public void Find_HostileQuery_IsCleanedAndCapped()
    {
        Assert.Single(Find("‮dentist\u0000"));
        Assert.Empty(Find(new string('x', 5000)));
        Assert.Empty(Find("'; DROP TABLE events; --"));
        Assert.Single(Find("dentist"));                       // the table is still there
        Assert.Equal(100, EventSearch.Words(new string('y', 5000)).Single().Length);
    }

    [Fact]
    public void Find_BlankQuery_ReturnsNothing() => Assert.Empty(Find("   "));

    [Fact]
    public void Find_CanceledSearch_Stops()
    {
        using var conn = _db.Database.Open();

        Assert.ThrowsAny<OperationCanceledException>(() => EventSearch.Find(conn, "dentist", Now, NewYork, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Find_CanceledEvent_IsLeftOut()
    {
        Insert("""{"id":"ghost","status":"cancelled","summary":"Ghost","start":{"dateTime":"2026-10-08T10:00:00-04:00"},"end":{"dateTime":"2026-10-08T11:00:00-04:00"}}""");

        Assert.Empty(Find("ghost"));
    }

    [Fact]
    public void Find_SameEventInTwoAccounts_ShownOnce()
    {
        using (var conn = _db.Database.Open())
        {
            AccountStore.Upsert(conn, new Account("222", "two@example.com", null, null, AccountStatus.Ok));
            CalendarStore.ReplaceForAccount(conn, "222", [new CalendarListEntry { Id = "two@example.com", Summary = "two", AccessRole = "owner", Primary = true, Selected = true }]);
        }

        Insert(Timed("shared-a", "Board meeting", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00", ""","iCalUID":"board@x" """));
        Insert(Timed("shared-b", "Board meeting", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00", ""","iCalUID":"board@x" """), "222", "two@example.com");

        Assert.Single(Find("board"));
    }

    [Fact]
    public void Find_NonAsciiWord_StillFound()
    {
        Insert(Timed("cafe", "Café chat", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00"));

        Assert.Single(Find("café"));
    }
}
