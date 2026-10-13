using System.Text.Json;
using LeafCalendar.Core.Api;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiEventsTests : IDisposable
{
    private const string Range = """{"start":"2026-10-01T00:00:00-04:00","end":"2026-10-02T00:00:00-04:00"}""";

    private readonly ApiHarness _api = new();

    public ApiEventsTests()
    {
        _api.Store(ApiHarness.Primary, """
            {"id":"evt-meet","status":"confirmed","summary":"Design review","location":"Room 4","hangoutLink":"https://meet.google.com/abc-defg-hij",
             "description":"Agenda<br>1. Mocks","attendees":[{"email":"leaf.tester@gmail.com","self":true,"organizer":true,"responseStatus":"accepted"},{"email":"sam@example.com","displayName":"Sam","responseStatus":"tentative"}],
             "start":{"dateTime":"2026-10-01T14:00:00-04:00"},"end":{"dateTime":"2026-10-01T15:00:00-04:00"}}
            """);
        _api.Store(ApiHarness.Family, """{"id":"evt-day","status":"confirmed","summary":"Holiday","start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}""");
        _api.Store(ApiHarness.Primary, """{"id":"evt-next","status":"confirmed","summary":"Tomorrow","start":{"dateTime":"2026-10-02T09:00:00-04:00"},"end":{"dateTime":"2026-10-02T10:00:00-04:00"}}""");
        _api.Store(ApiHarness.Primary, """{"id":"evt-bad","status":"confirmed","summary":"Phish","location":"https://meet.google.com.evil.example/x","description":"https://meet.google.com.evil.example/x","start":{"dateTime":"2026-10-01T16:00:00-04:00"},"end":{"dateTime":"2026-10-01T17:00:00-04:00"}}""");
    }

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task List_ReturnsTheRangeSortedWithFields()
    {
        var events = (await _api.CallAsync("events.list", Range)).EnumerateArray().ToList();

        Assert.Equal(["Holiday", "Design review", "Phish"], events.Select(e => e.GetProperty("title").GetString()));
        var meeting = events[1];
        Assert.Equal("2026-10-01T14:00:00-04:00", meeting.GetProperty("start").GetString());
        Assert.False(meeting.GetProperty("allDay").GetBoolean());
        Assert.Equal("Room 4", meeting.GetProperty("location").GetString());
        Assert.Equal("accepted", meeting.GetProperty("response").GetString());
        Assert.Equal("https://meet.google.com/abc-defg-hij", meeting.GetProperty("meetingLink").GetString());
        Assert.Equal("2026-10-01", events[0].GetProperty("start").GetString());
        Assert.Equal("2026-10-02", events[0].GetProperty("end").GetString());
        Assert.True(events[0].GetProperty("allDay").GetBoolean());
    }

    [Fact]
    public async Task List_LookAlikeHost_HasNoMeetingLink()
    {
        var events = (await _api.CallAsync("events.list", Range)).EnumerateArray();

        Assert.Equal(JsonValueKind.Null, events.Single(e => e.GetProperty("title").GetString() == "Phish").GetProperty("meetingLink").ValueKind);
    }

    [Fact]
    public async Task List_CalendarIds_Narrow()
    {
        var family = ApiIds.Calendar(ApiHarness.Account, ApiHarness.Family);

        var events = (await _api.CallAsync("events.list", $$"""{"start":"2026-10-01T00:00:00-04:00","end":"2026-10-02T00:00:00-04:00","calendarIds":["{{family}}"]}""")).EnumerateArray();

        Assert.Equal("Holiday", Assert.Single(events).GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("""{"start":"2026-10-01T00:00:00","end":"2026-10-02T00:00:00-04:00"}""")]
    [InlineData("""{"start":"2026-10-02T00:00:00-04:00","end":"2026-10-01T00:00:00-04:00"}""")]
    [InlineData("""{"start":"2026-01-01T00:00:00Z","end":"2027-01-03T00:00:00Z"}""")]
    [InlineData("""{"start":"2026-10-01T00:00:00Z","end":"2026-10-02T00:00:00Z","calendarIds":["nope"]}""")]
    public async Task List_BadParams_AreInvalid(string json)
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.list", json)).Code);
    }

    [Fact]
    public async Task Get_AddsPlainDescriptionAndGuests()
    {
        var id = (await _api.CallAsync("events.list", Range)).EnumerateArray().Single(e => e.GetProperty("title").GetString() == "Design review").GetProperty("id").GetString();

        var result = await _api.CallAsync("events.get", $$"""{"id":"{{id}}"}""");

        Assert.DoesNotContain("<br>", result.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("1. Mocks", result.GetProperty("description").GetString(), StringComparison.Ordinal);
        var sam = result.GetProperty("guests").EnumerateArray().Single(g => g.GetProperty("email").GetString() == "sam@example.com");
        Assert.Equal("Sam", sam.GetProperty("name").GetString());
        Assert.Equal("tentative", sam.GetProperty("response").GetString());
        Assert.False(sam.GetProperty("organizer").GetBoolean());
    }

    [Fact]
    public async Task Get_UnknownEvent_IsNotFound()
    {
        var id = ApiIds.Event(ApiHarness.Account, ApiHarness.Primary, "evt-missing", DateTimeOffset.UnixEpoch);

        Assert.Equal(ApiErrors.NotFound, (await _api.FailAsync("events.get", $$"""{"id":"{{id}}"}""")).DataCode);
    }

    [Fact]
    public async Task Get_MalformedId_IsInvalidParams()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("events.get", """{"id":"zzz"}""")).Code);
    }
}
