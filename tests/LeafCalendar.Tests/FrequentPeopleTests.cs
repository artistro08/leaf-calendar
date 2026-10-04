using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class FrequentPeopleTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public FrequentPeopleTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account,
            JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
    }

    public void Dispose() => _db.Dispose();

    void Insert(string json, string account = "109876543210", string calendar = Primary)
    {
        using var conn = _db.Database.Open();
        using var doc  = JsonDocument.Parse(json);
        EventStore.Apply(conn, null, account, calendar, doc.RootElement);
    }

    static string With(string id, string start, string attendees) =>
        $$"""{"id":"{{id}}","status":"confirmed","summary":"x","start":{"dateTime":"{{start}}"},"end":{"dateTime":"{{start}}"},"attendees":[{{attendees}}]}""";

    [Fact]
    public void Load_MostFrequentFirst_SkipsYouRoomsAndOldEvents()
    {
        const string self = """{"email":"leaf.tester@gmail.com","self":true}""";
        const string room = """{"email":"c_1@resource.calendar.google.com","displayName":"Room 1","resource":true}""";
        Insert(With("a", "2026-09-20T10:00:00Z", $$"""{{self}},{{room}},{"email":"frank@example.com","displayName":"Frank Often"},{"email":"amy@example.com"}"""));
        Insert(With("b", "2026-09-21T10:00:00Z", $$"""{{self}},{"email":"Frank@example.com"}"""));
        Insert(With("c", "2025-01-01T10:00:00Z", $$"""{{self}},{"email":"old@example.com"}"""));

        using var conn = _db.Database.Open();
        var people = FrequentPeople.Load(conn, Account, Now);

        Assert.Equal(["frank@example.com", "amy@example.com"], people.Select(p => p.Email));
        Assert.Equal("Frank Often", people[0].Name);
    }

    [Fact]
    public void Load_HostileNamesAndAddresses_AreCleanedOrDropped()
    {
        Insert(With("h", "2026-09-20T10:00:00Z", """{"email":"eve@example.com","displayName":"‮Eve\u0000"},{"email":"not an address"}"""));

        using var conn = _db.Database.Open();
        var person = Assert.Single(FrequentPeople.Load(conn, Account, Now));

        Assert.Equal(("Eve", "eve@example.com"), (person.Name, person.Email));
    }

    [Fact]
    public void Load_OtherAccounts_AreLeftOut()
    {
        Insert(With("y", "2026-09-20T10:00:00Z", """{"email":"kept@example.com"}"""));

        using var conn = _db.Database.Open();

        Assert.Equal(["kept@example.com"], FrequentPeople.Load(conn, Account, Now).Select(p => p.Email));
        Assert.Empty(FrequentPeople.Load(conn, "someone-else", Now));
    }

    [Theory]
    [InlineData("fra", 1)]
    [InlineData("often", 1)]
    [InlineData("example", 2)]
    [InlineData("zz", 0)]
    public void Match_PrefixOfANameWordOrTheAddress(string query, int count) =>
        Assert.Equal(count, FrequentPeople.Match([new("Frank Often", "frank@example.com"), new("", "amy@example.com")], query).Count);

    static string Weekly(string id, string start, string rule, string attendees) =>
        $$"""{"id":"{{id}}","status":"confirmed","summary":"x","start":{"dateTime":"{{start}}","timeZone":"America/New_York"},"end":{"dateTime":"{{start}}","timeZone":"America/New_York"},"recurrence":["{{rule}}"],"attendees":[{{attendees}}]}""";

    [Fact]
    public void Load_RepeatingSeries_CountsEachInstanceInTheWindow()
    {
        // A weekly 1:1 since early 2025, a 4-week series that began last month, and three one-off meetings
        Insert(Weekly("sam-1on1", "2025-01-06T10:00:00-05:00", "RRULE:FREQ=WEEKLY", """{"email":"sam@example.com"}"""));
        Insert(Weekly("kim-series", "2026-09-01T10:00:00-04:00", "RRULE:FREQ=WEEKLY;COUNT=4", """{"email":"kim@example.com"}"""));
        Insert(With("p1", "2026-09-02T10:00:00Z", """{"email":"pat@example.com"}"""));
        Insert(With("p2", "2026-09-09T10:00:00Z", """{"email":"pat@example.com"}"""));
        Insert(With("p3", "2026-09-16T10:00:00Z", """{"email":"pat@example.com"}"""));

        // One 1:1 Was Moved: It Counts Once, As Its Own Row
        Insert("""{"id":"sam-1on1_20260922T140000Z","status":"confirmed","summary":"x","recurringEventId":"sam-1on1","originalStartTime":{"dateTime":"2026-09-22T10:00:00-04:00"},"start":{"dateTime":"2026-09-23T10:00:00-04:00"},"end":{"dateTime":"2026-09-23T10:00:00-04:00"},"attendees":[{"email":"sam@example.com"}]}""");

        using var conn = _db.Database.Open();

        Assert.Equal(["sam@example.com", "kim@example.com", "pat@example.com"], FrequentPeople.Load(conn, Account, Now).Select(p => p.Email));
    }

    [Fact]
    public void Load_AnAddressTwiceInOneEvent_CountsOnce()
    {
        Insert(With("d", "2026-09-20T10:00:00Z", """{"email":"twice@example.com"},{"email":"TWICE@example.com"},{"email":"once@example.com"}"""));
        Insert(With("e", "2026-09-19T10:00:00Z", """{"email":"once@example.com"}"""));

        using var conn = _db.Database.Open();

        Assert.Equal(["once@example.com", "twice@example.com"], FrequentPeople.Load(conn, Account, Now).Select(p => p.Email));
    }
}
