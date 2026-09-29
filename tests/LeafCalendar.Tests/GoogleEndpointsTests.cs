using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class GoogleEndpointsTests : IDisposable
{
    static readonly Uri Root = new("http://127.0.0.1:4567/");

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new();

    public void Dispose() => _google.Dispose();

    [Fact]
    public void ForFake_Root_BuildsAllPathsUnderRoot()
    {
        var endpoints = GoogleEndpoints.ForFake(Root);

        Assert.Equal("http://127.0.0.1:4567/auth", endpoints.Authorization.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/token", endpoints.Token.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/revoke", endpoints.Revoke.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/userinfo", endpoints.UserInfo.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/calendar/v3/", endpoints.CalendarApi.AbsoluteUri);
    }

    [Fact]
    public void Default_UsesGoogle()
    {
        Assert.Equal("https://oauth2.googleapis.com/token", GoogleEndpoints.Default.Token.AbsoluteUri);
        Assert.Equal("https://www.googleapis.com/calendar/v3/", GoogleEndpoints.Default.CalendarApi.AbsoluteUri);
    }

    [Fact]
    public async Task Clients_WithFakeEndpoints_CallFakeUrls()
    {
        var ct       = TestContext.Current.CancellationToken;
        var fake     = GoogleEndpoints.ForFake(Root);
        var http     = new HttpClient(_google);
        var store    = new InMemoryTokenStore();
        store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, "http://127.0.0.1:4567/token", HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, "http://127.0.0.1:4567/calendar/v3/users/me/calendarList", HttpStatusCode.OK, Fixture.Read("calendar-list.json"));
        var oauth    = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "secret"), _time, fake);
        using var tokens = new AccessTokenProvider(oauth, store, _time);
        var calendar = new GoogleCalendarClient(http, tokens, fake);

        var url       = oauth.BuildAuthorizationUrl(new Uri("http://127.0.0.1:5000/"), "s", "c");
        var calendars = await calendar.ListCalendarsAsync("acct", ct);

        Assert.StartsWith("http://127.0.0.1:4567/auth?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(2, calendars.Count);
        Assert.Equal("s", QueryString.Parse(url.Query)["state"]);
    }
}
