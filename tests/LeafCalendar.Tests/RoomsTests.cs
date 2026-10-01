using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class RoomsTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    readonly TestDatabase _db = new();

    public RoomsTests()
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
    public void Load_RoomsYouBookedBefore_DistinctAndNamed()
    {
        // Years Old Bookings Count Too; Only Google's Resource Addresses Are Rooms (a flagged outside address is spoofed)
        Insert(With("a", "2024-01-01T10:00:00Z", """{"email":"c_2@resource.calendar.google.com","displayName":"‮Zeta room","resource":true},{"email":"frank@example.com"}"""));
        Insert(With("b", "2026-09-20T10:00:00Z", """{"email":"C_2@resource.calendar.google.com","resource":true},{"email":"c_1boardroom@resource.calendar.google.com"}"""));
        Insert(With("c", "2026-09-21T10:00:00Z", """{"email":"lab@example.com","displayName":"Lab","resource":true},{"email":"bad room@resource.calendar.google.com","resource":true}"""));

        using var conn = _db.Database.Open();
        var rooms = Rooms.Load(conn, Account);

        Assert.Equal(
            [
                new Room("c_1boardroom", "c_1boardroom@resource.calendar.google.com"),
                new Room("Zeta room", "c_2@resource.calendar.google.com"),
            ],
            rooms);
        Assert.Empty(Rooms.Load(conn, "someone-else"));
    }

    [Theory]
    [InlineData("room", 2)]
    [InlineData("WEST", 1)]
    [InlineData("4 w", 1)]
    [InlineData("lab", 0)]
    public void Match_ContainsIgnoringCase(string query, int count) =>
        Assert.Equal(count, Rooms.Match([new("Room 4 West", "c_1@resource.calendar.google.com"), new("Boardroom", "c_2@resource.calendar.google.com")], query).Count);
}
