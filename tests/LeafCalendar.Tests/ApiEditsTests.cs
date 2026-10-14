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
    public async Task Create_Meet_AsksGoogleForAMeetLink()
    {
        await _api.CallAsync("events.create", "{\"title\":\"Sync\"," + Times + ",\"meet\":true}");

        var payload = Assert.Single(_api.Outbox()).Payload;
        Assert.Contains("createRequest", payload, StringComparison.Ordinal);
        Assert.Contains("hangoutsMeet", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_NoMeet_HasNoConference()
    {
        await _api.CallAsync("events.create", "{\"title\":\"Sync\"," + Times + "}");

        Assert.DoesNotContain("conferenceData", Assert.Single(_api.Outbox()).Payload, StringComparison.Ordinal);
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

    [Fact]
    public async Task Update_Text_ChangesOnlyWhatsGiven()
    {
        var id = await IdOf("One");

        var result = await _api.CallAsync("events.update", $$"""{"id":"{{id}}","title":"One, renamed","location":"Cafe","description":"Bring <notes>"}""");

        Assert.Equal(OutboxOperation.Patch, Assert.Single(_api.Outbox()).Operation);
        var got = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal("One, renamed", got.GetProperty("title").GetString());
        Assert.Equal("Cafe", got.GetProperty("location").GetString());
        Assert.Contains("Bring <notes>", got.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("2026-10-01T14:00:00-04:00", got.GetProperty("start").GetString());
    }

    [Fact]
    public async Task Update_Times_ReturnsTheIdThatFindsIt()
    {
        var id = await IdOf("One");

        var result = await _api.CallAsync("events.update", $$"""{"id":"{{id}}","start":"2026-10-02T09:00:00-04:00","end":"2026-10-02T09:45:00-04:00"}""");

        var got = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal("2026-10-02T09:00:00-04:00", got.GetProperty("start").GetString());
        Assert.Equal("2026-10-02T09:45:00-04:00", got.GetProperty("end").GetString());
    }

    [Fact]
    public async Task Update_Guests_KeepsTheirAnswersAndInvitesNewOnes()
    {
        _api.Store(ApiHarness.Primary, """{"id":"evt-meet","status":"confirmed","summary":"Meet","organizer":{"email":"leaf.tester@gmail.com","self":true},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"organizer":true,"responseStatus":"accepted"},{"email":"sam@example.com","responseStatus":"accepted"},{"email":"gone@example.com","responseStatus":"tentative"}],"start":{"dateTime":"2026-10-01T16:00:00-04:00"},"end":{"dateTime":"2026-10-01T17:00:00-04:00"}}""");
        var id = await IdOf("Meet");

        var result = await _api.CallAsync("events.update", $$"""{"id":"{{id}}","guests":["leaf.tester@gmail.com","SAM@example.com","kim@example.com"]}""");

        Assert.True(Assert.Single(_api.Outbox()).SendUpdates);
        var guests = (await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""")).GetProperty("guests").EnumerateArray()
            .ToDictionary(g => g.GetProperty("email").GetString()!, g => g.GetProperty("response").GetString());
        Assert.Equal("accepted", guests["sam@example.com"]);
        Assert.Equal("needsAction", guests["kim@example.com"]);
        Assert.False(guests.ContainsKey("gone@example.com"));
    }

    [Fact]
    public async Task Update_Calendar_MovesTheEvent()
    {
        var id = await IdOf("One");
        var family = ApiIds.Calendar(ApiHarness.Account, ApiHarness.Family);

        var result = await _api.CallAsync("events.update", $$"""{"id":"{{id}}","calendarId":"{{family}}"}""");

        var got = await _api.CallAsync("events.get", $$"""{"id":"{{result.GetProperty("id").GetString()}}"}""");
        Assert.Equal(family, got.GetProperty("calendarId").GetString());
    }

    [Fact]
    public async Task Update_Meet_AsksGoogleForAMeetLink()
    {
        var id = await IdOf("One");

        await _api.CallAsync("events.update", $$"""{"id":"{{id}}","meet":true,"notifyGuests":false}""");

        var entry = Assert.Single(_api.Outbox());
        Assert.Contains("hangoutsMeet", entry.Payload, StringComparison.Ordinal);
        Assert.False(entry.SendUpdates);
    }

    [Fact]
    public async Task Update_Nothing_ChangesNothing()
    {
        var id = await IdOf("One");

        var result = await _api.CallAsync("events.update", $$"""{"id":"{{id}}","title":"One"}""");

        Assert.Equal(id, result.GetProperty("id").GetString());
        Assert.Empty(_api.Outbox());
    }

    [Theory]
    [InlineData(",\"start\":\"2026-10-02T09:00:00-04:00\"")]
    [InlineData(",\"guests\":[\"not an address\"]")]
    [InlineData(",\"scope\":\"everything\"")]
    public async Task Update_BadParams_IsInvalid(string more)
    {
        var id = await IdOf("One");

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.update", $$"""{"id":"{{id}}"{{more}}}""")).Code);
        Assert.Empty(_api.Outbox());
    }

    [Fact]
    public async Task Update_ReadOnlyEvent_IsReadOnly()
    {
        _api.Store(ApiHarness.Primary, """{"id":"evt-theirs","status":"confirmed","summary":"Theirs","organizer":{"email":"boss@example.com"},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"accepted"},{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"}],"start":{"dateTime":"2026-10-01T11:00:00-04:00"},"end":{"dateTime":"2026-10-01T12:00:00-04:00"}}""");
        var id = await IdOf("Theirs");

        Assert.Equal(ApiErrors.ReadOnly, (await _api.FailAsync("events.update", $$"""{"id":"{{id}}","title":"Mine now"}""")).DataCode);
    }
}
