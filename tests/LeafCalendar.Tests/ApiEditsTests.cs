using LeafCalendar.Core.Api;
using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiEditsTests : IDisposable
{
    private const string Times = "\"start\":\"2026-10-02T10:00:00-04:00\",\"end\":\"2026-10-02T10:30:00-04:00\"";

    private readonly ApiHarness _api = new();

    public ApiEditsTests()
    {
        _api.Store(ApiHarness.Primary, """{"id":"evt-one","status":"confirmed","summary":"One","start":{"dateTime":"2026-10-01T14:00:00-04:00"},"end":{"dateTime":"2026-10-01T15:00:00-04:00"}}""");
        _api.Store(ApiHarness.Primary, """{"id":"evt-weekly","status":"confirmed","summary":"Weekly","recurrence":["RRULE:FREQ=WEEKLY"],"start":{"dateTime":"2026-10-01T09:00:00-04:00","timeZone":"America/New_York"},"end":{"dateTime":"2026-10-01T09:30:00-04:00","timeZone":"America/New_York"}}""");
    }

    public void Dispose() => _api.Dispose();

    private async Task<string> IdOf(string title, string day = "2026-10-01")
    {
        var events = await _api.CallAsync("events.list", $$"""{"start":"{{day}}T00:00:00-04:00","end":"{{day}}T23:59:00-04:00"}""");
        return events.EnumerateArray().First(e => e.GetProperty("title").GetString() == title).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Create_Defaults_GoesToTheDefaultCalendarThroughTheOutbox()
    {
        var result = await _api.CallAsync("events.create", "{\"title\":\"Coffee\"," + Times + ",\"location\":\"Cafe\",\"description\":\"Bring <notes> & ideas\\nline 2\"}");

        var entry = Assert.Single(_api.Outbox());
        Assert.Equal(OutboxOperation.Create, entry.Operation);
        Assert.Equal(ApiHarness.Primary, entry.CalendarId);
        Assert.Contains("America/New_York", entry.Payload, StringComparison.Ordinal);
        var got = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal("Coffee", got.GetProperty("title").GetString());
        Assert.Equal("Cafe", got.GetProperty("location").GetString());
        Assert.Contains("Bring <notes> & ideas", got.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("line 2", got.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_Guests_AreInvitedByDefault()
    {
        await _api.CallAsync("events.create", "{\"title\":\"Sync\"," + Times + ",\"guests\":[\"sam@example.com\",\"SAM@example.com\",\"kim@example.com\"]}");

        var entry = Assert.Single(_api.Outbox());
        Assert.True(entry.SendUpdates);
        Assert.Contains("sam@example.com", entry.Payload, StringComparison.Ordinal);
        Assert.Contains("kim@example.com", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("SAM@example.com", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_NotifyGuestsFalse_SendsNoInvitations()
    {
        await _api.CallAsync("events.create", "{\"title\":\"Sync\"," + Times + ",\"guests\":[\"sam@example.com\"],\"notifyGuests\":false}");

        Assert.False(Assert.Single(_api.Outbox()).SendUpdates);
    }

    [Fact]
    public async Task Create_AllDayDates_AreAllDay()
    {
        await _api.CallAsync("events.create", """{"title":"Trip","start":"2026-10-05","end":"2026-10-07"}""");

        Assert.Contains("\"date\":\"2026-10-05\"", Assert.Single(_api.Outbox()).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ChosenCalendar_IsUsed()
    {
        var family = ApiIds.Calendar(ApiHarness.Account, ApiHarness.Family);

        await _api.CallAsync("events.create", "{\"calendarId\":\"" + family + "\",\"title\":\"Dinner\"," + Times + "}");

        Assert.Equal(ApiHarness.Family, Assert.Single(_api.Outbox()).CalendarId);
    }

    [Theory]
    [InlineData("""{"title":"x","start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T10:30:00-04:00","guests":["not an email"]}""")]
    [InlineData("""{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T10:30:00-04:00"}""")]
    [InlineData("""{"title":"x","start":"2026-10-02T10:00:00","end":"2026-10-02T10:30:00"}""")]
    public async Task Create_BadParams_AreInvalidAndWriteNothing(string json)
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.create", json)).Code);
        Assert.Empty(_api.Outbox());
    }

    [Fact]
    public async Task Create_TooManyGuests_IsInvalid()
    {
        var guests = string.Join(",", Enumerable.Range(0, 101).Select(i => $"\"g{i}@example.com\""));

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.create", "{\"title\":\"x\"," + Times + ",\"guests\":[" + guests + "]}")).Code);
    }

    [Fact]
    public async Task Create_NoAccount_IsNotSignedIn()
    {
        using (var conn = _api.Database.Open())
        {
            AccountStore.Delete(conn, ApiHarness.Account);
        }

        Assert.Equal(ApiErrors.NotSignedIn, (await _api.FailAsync("events.create", "{\"title\":\"x\"," + Times + "}")).DataCode);
    }

    [Fact]
    public async Task Move_Single_QueuesAPatchAndReturnsTheNewId()
    {
        var id = await IdOf("One");

        var result = await _api.CallAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-01T16:00:00-04:00","end":"2026-10-01T17:00:00-04:00"}""");

        Assert.Equal(OutboxOperation.Patch, Assert.Single(_api.Outbox()).Operation);
        var moved = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal("2026-10-01T16:00:00-04:00", moved.GetProperty("start").GetString());
    }

    [Fact]
    public async Task Move_SeriesInstance_ReturnsIdThatFindsIt()
    {
        var id = await IdOf("Weekly", "2026-10-08");

        var result = await _api.CallAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-08T11:00:00-04:00","end":"2026-10-08T11:30:00-04:00","scope":"this"}""");

        var moved = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal("2026-10-08T11:00:00-04:00", moved.GetProperty("start").GetString());
        var next = await _api.CallAsync("events.get", $$"""{"id":"{{await IdOf("Weekly", "2026-10-15")}}"}""");
        Assert.Equal("2026-10-15T09:00:00-04:00", next.GetProperty("start").GetString());
    }

    [Fact]
    public async Task Move_Following_ReturnsNoId()
    {
        var id = await IdOf("Weekly", "2026-10-08");

        var result = await _api.CallAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-08T11:00:00-04:00","end":"2026-10-08T11:30:00-04:00","scope":"following"}""");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Move_FollowingOntoAnEarlierInstance_ReturnsNoId()
    {
        var id = await IdOf("Weekly", "2026-10-15");

        var result = await _api.CallAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-08T09:00:00-04:00","end":"2026-10-08T09:30:00-04:00","scope":"following"}""");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Move_SingleWithFollowing_ReturnsTheNewId()
    {
        var id = await IdOf("One");

        var result = await _api.CallAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-01T16:00:00-04:00","end":"2026-10-01T17:00:00-04:00","scope":"following"}""");

        var moved = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal("2026-10-01T16:00:00-04:00", moved.GetProperty("start").GetString());
    }

    [Fact]
    public async Task Move_BadScope_IsInvalid()
    {
        var id = await IdOf("One");

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-01T16:00:00-04:00","end":"2026-10-01T17:00:00-04:00","scope":"everything"}""")).Code);
    }

    [Fact]
    public async Task Move_ReadOnlyEvent_IsReadOnly()
    {
        _api.Store(ApiHarness.Primary, """{"id":"evt-theirs","status":"confirmed","summary":"Theirs","organizer":{"email":"boss@example.com"},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"accepted"},{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"}],"start":{"dateTime":"2026-10-01T11:00:00-04:00"},"end":{"dateTime":"2026-10-01T12:00:00-04:00"}}""");
        var id = await IdOf("Theirs");

        Assert.Equal(ApiErrors.ReadOnly, (await _api.FailAsync("events.move", $$"""{"id":"{{id}}","start":"2026-10-01T16:00:00-04:00","end":"2026-10-01T17:00:00-04:00"}""")).DataCode);
        Assert.Empty(_api.Outbox());
    }

    [Fact]
    public async Task Delete_Single_QueuesADeleteThatTellsGuests()
    {
        var id = await IdOf("One");

        Assert.Equal("{}", (await _api.CallAsync("events.delete", $$"""{"id":"{{id}}"}""")).GetRawText());

        var entry = Assert.Single(_api.Outbox());
        Assert.Equal(OutboxOperation.Delete, entry.Operation);
        Assert.True(entry.SendUpdates);
        Assert.Equal(ApiErrors.NotFound, (await _api.FailAsync("events.get", $$"""{"id":"{{id}}"}""")).DataCode);
    }

    [Fact]
    public async Task Delete_NotifyGuestsFalse_TellsNoOne()
    {
        var id = await IdOf("One");

        await _api.CallAsync("events.delete", $$"""{"id":"{{id}}","notifyGuests":false}""");

        Assert.False(Assert.Single(_api.Outbox()).SendUpdates);
    }

    [Fact]
    public async Task Delete_SeriesInstance_LeavesTheRest()
    {
        var id = await IdOf("Weekly", "2026-10-08");

        await _api.CallAsync("events.delete", $$"""{"id":"{{id}}","scope":"this"}""");

        Assert.Equal(ApiErrors.NotFound, (await _api.FailAsync("events.get", $$"""{"id":"{{id}}"}""")).DataCode);
        Assert.Equal("Weekly", (await _api.CallAsync("events.get", $$"""{"id":"{{await IdOf("Weekly", "2026-10-15")}}"}""")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Delete_BadScope_IsInvalid()
    {
        var id = await IdOf("One");

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.delete", $$"""{"id":"{{id}}","scope":"everything"}""")).Code);
        Assert.Empty(_api.Outbox());
    }

    [Fact]
    public async Task Delete_ReadOnlyEvent_IsReadOnly()
    {
        _api.Store(ApiHarness.Primary, """{"id":"evt-theirs","status":"confirmed","summary":"Theirs","organizer":{"email":"boss@example.com"},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"accepted"},{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"}],"start":{"dateTime":"2026-10-01T11:00:00-04:00"},"end":{"dateTime":"2026-10-01T12:00:00-04:00"}}""");
        var id = await IdOf("Theirs");

        Assert.Equal(ApiErrors.ReadOnly, (await _api.FailAsync("events.delete", $$"""{"id":"{{id}}"}""")).DataCode);
        Assert.Empty(_api.Outbox());
    }
}
