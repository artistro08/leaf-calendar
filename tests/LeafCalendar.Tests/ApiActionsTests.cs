using LeafCalendar.Core.Api;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiActionsTests : IDisposable
{
    private readonly ApiHarness _api = new();

    public ApiActionsTests()
    {
        // The harness clock is Oct 1, 8 AM New York; the meeting is at 2 PM, inside the default 8-hour upcoming lookahead
        _api.Store(ApiHarness.Primary, """{"id":"evt-meet","status":"confirmed","summary":"Design review","hangoutLink":"https://meet.google.com/abc-defg-hij","start":{"dateTime":"2026-10-01T14:00:00-04:00"},"end":{"dateTime":"2026-10-01T15:00:00-04:00"}}""");
        _api.Store(ApiHarness.Primary, """{"id":"evt-plain","status":"confirmed","summary":"Lunch","start":{"dateTime":"2026-10-01T12:00:00-04:00"},"end":{"dateTime":"2026-10-01T13:00:00-04:00"}}""");
    }

    public void Dispose() => _api.Dispose();

    private async Task<string> IdOf(string title)
    {
        var events = await _api.CallAsync("events.list", """{"start":"2026-10-01T00:00:00-04:00","end":"2026-10-02T00:00:00-04:00"}""");
        return events.EnumerateArray().First(e => e.GetProperty("title").GetString() == title).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Join_NoId_OpensTheNextMeetingWithItsAccount()
    {
        var result = await _api.CallAsync("meetings.join");

        Assert.True(result.GetProperty("opened").GetBoolean());
        Assert.Equal("https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com", Assert.Single(_api.Host.Opened).AbsoluteUri);
        Assert.Equal(await IdOf("Design review"), result.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Join_EventWithoutLink_IsNoMeeting()
    {
        var id = await IdOf("Lunch");

        Assert.Equal(ApiErrors.NoMeeting, (await _api.FailAsync("meetings.join", $$"""{"id":"{{id}}"}""")).DataCode);
        Assert.Empty(_api.Host.Opened);
    }

    [Fact]
    public async Task Join_NothingAhead_IsNoMeeting()
    {
        _api.Time.Advance(TimeSpan.FromHours(8));

        Assert.Equal(ApiErrors.NoMeeting, (await _api.FailAsync("meetings.join")).DataCode);
    }

    [Fact]
    public async Task Join_LaunchFails_SaysNotOpened()
    {
        _api.Host.OpenSucceeds = false;

        Assert.False((await _api.CallAsync("meetings.join")).GetProperty("opened").GetBoolean());
    }

    [Fact]
    public async Task ShowDay_AsksTheAppForThatDay()
    {
        await _api.CallAsync("app.showDay", """{"date":"2026-10-08"}""");

        Assert.Equal(new DateOnly(2026, 10, 8), Assert.Single(_api.Host.Days));
    }

    [Fact]
    public async Task ShowDay_BadDate_IsInvalid()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("app.showDay", """{"date":"10/08/2026"}""")).Code);
    }

    [Fact]
    public async Task ShowEvent_AsksTheAppForThatEvent()
    {
        await _api.CallAsync("app.showEvent", $$"""{"id":"{{await IdOf("Lunch")}}"}""");

        Assert.Equal("evt-plain", Assert.Single(_api.Host.Events).EventId);
    }
}
