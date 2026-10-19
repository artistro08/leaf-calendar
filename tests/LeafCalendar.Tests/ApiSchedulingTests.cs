using System.Net;
using LeafCalendar.Core.Api;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiSchedulingTests : IDisposable
{
    private const string Api = "https://api.cal.com/";

    private readonly ApiHarness _api = new();

    public void Dispose() => _api.Dispose();

    // A signed-in Cal.com: a saved refresh token, a token answer, the links fixture, and open times (or a failure)
    private void SignIn(HttpStatusCode slots = HttpStatusCode.OK)
    {
        _api.Tokens.SetProviderSecret("calcom", "client_id", "client-123");
        _api.Tokens.SetProviderSecret("calcom", "client_secret", "secret-1");
        _api.Tokens.SetProviderSecret("calcom", "refresh_token", "rt-1");
        _api.Http.On(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri == Api + "v2/auth/oauth2/token", _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at-1","expires_in":1800}"""));
        _api.Http.On(HttpMethod.Get, Api + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));
        _api.Http.On(HttpMethod.Get, Api + "v2/slots", slots, slots == HttpStatusCode.OK ? Fixture.Read("calcom-slots.json") : "{}");
        _api.Scheduling.Load();
    }

    private async Task<string> LinkId()
    {
        var links = await _api.CallAsync("scheduling.links");
        return links.EnumerateArray().First(l => l.GetProperty("name").GetString() == "30 min meeting").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Links_NotSignedIn_IsNotConnected()
    {
        Assert.Equal(ApiErrors.NotConnected, (await _api.FailAsync("scheduling.links")).DataCode);
    }

    [Fact]
    public async Task Links_ListsTheProvidersLinks()
    {
        SignIn();

        var links = (await _api.CallAsync("scheduling.links")).EnumerateArray().ToList();

        Assert.Equal(3, links.Count);
        Assert.Equal("Cal.com", links[0].GetProperty("provider").GetString());
        Assert.Equal(30, links[0].GetProperty("minutes").GetInt32());
        Assert.False(links[1].GetProperty("allowsGuests").GetBoolean());
    }

    [Fact]
    public async Task Links_SwitchedOffInSettings_AreLeftOut()
    {
        SignIn();
        _api.Host.Settings = _api.Host.Settings.WithProvider(new("calcom", "", "") { OffLinkIds = ["102"] }).Normalize();

        var names = (await _api.CallAsync("scheduling.links")).EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList();

        Assert.Equal(["30 min meeting", "Secret"], names);
    }

    [Fact]
    public async Task OpenTimes_LinkSwitchedOff_IsNotFound()
    {
        SignIn();
        var id = await LinkId();
        _api.Host.Settings = _api.Host.Settings.WithProvider(new("calcom", "", "") { OffLinkIds = ["101"] }).Normalize();

        Assert.Equal(ApiErrors.NotFound, (await _api.FailAsync("scheduling.openTimes", $$"""{"linkId":"{{id}}","start":"2026-10-13T00:00:00Z","end":"2026-10-14T00:00:00Z"}""")).DataCode);
    }

    [Fact]
    public async Task OpenTimes_ReturnsTheBookableTimes()
    {
        SignIn();

        var times = (await _api.CallAsync("scheduling.openTimes", $$"""{"linkId":"{{await LinkId()}}","start":"2026-10-13T00:00:00-04:00","end":"2026-10-15T00:00:00-04:00"}""")).EnumerateArray().ToList();

        Assert.Equal(3, times.Count);
        Assert.Equal("2026-10-13T10:00:00-04:00", times[0].GetProperty("start").GetString());
    }

    [Fact]
    public async Task OpenTimes_OverAMonth_IsInvalid()
    {
        SignIn();

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("scheduling.openTimes", $$"""{"linkId":"{{await LinkId()}}","start":"2026-10-01T00:00:00Z","end":"2026-11-05T00:00:00Z"}""")).Code);
    }

    [Fact]
    public async Task OpenTimes_ProviderDown_IsUnavailable()
    {
        SignIn(HttpStatusCode.InternalServerError);

        Assert.Equal(ApiErrors.Unavailable, (await _api.FailAsync("scheduling.openTimes", $$"""{"linkId":"{{await LinkId()}}","start":"2026-10-13T00:00:00Z","end":"2026-10-14T00:00:00Z"}""")).DataCode);
    }

    [Fact]
    public async Task TimeLink_IsTheBookingPageOnThatTime()
    {
        SignIn();

        var result = await _api.CallAsync("scheduling.timeLink", $$"""{"linkId":"{{await LinkId()}}","start":"2026-10-13T10:00:00-04:00","guests":[{"name":"Sam","email":"sam@example.com"}]}""");

        var url = result.GetProperty("url").GetString()!;
        Assert.StartsWith("https://cal.com/leaf-tester/30min?", url, StringComparison.Ordinal);
        Assert.Contains("email=sam%40example.com", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeLink_UnknownLink_IsNotFound()
    {
        SignIn();

        Assert.Equal(ApiErrors.NotFound, (await _api.FailAsync("scheduling.timeLink", $$"""{"linkId":"{{ApiIds.Link("calcom", "999")}}","start":"2026-10-13T10:00:00-04:00"}""")).DataCode);
    }

    [Fact]
    public async Task TimeLink_BadGuestEmail_IsInvalid()
    {
        SignIn();

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("scheduling.timeLink", $$"""{"linkId":"{{await LinkId()}}","start":"2026-10-13T10:00:00-04:00","guests":[{"email":"nope"}]}""")).Code);
    }
}
