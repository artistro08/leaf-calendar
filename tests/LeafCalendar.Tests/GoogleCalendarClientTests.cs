using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class GoogleCalendarClientTests : IDisposable
{
    const string Account     = "109876543210";
    const string TokenUrl    = "https://oauth2.googleapis.com/token";
    const string ListUrl     = "https://www.googleapis.com/calendar/v3/users/me/calendarList";
    const string EventsUrl   = "https://www.googleapis.com/calendar/v3/calendars/leaf.tester%40gmail.com/events";

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    GoogleCalendarClient CreateClient()
    {
        var store = new InMemoryTokenStore();
        store.SetRefreshToken(Account, "1//test-refresh-token");
        var http  = new HttpClient(_google);
        var oauth = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "secret"), _time);
        return new GoogleCalendarClient(http, new AccessTokenProvider(oauth, store, _time));
    }

    [Fact]
    public async Task ListCalendarsAsync_TwoPages_ReturnsAll()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(r => r.Uri.AbsoluteUri.StartsWith(ListUrl, StringComparison.Ordinal) && r.Query("pageToken") is null,
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"a","summary":"A","accessRole":"owner"}],"nextPageToken":"p2"}"""));
        _google.On(r => r.Uri.AbsoluteUri.StartsWith(ListUrl, StringComparison.Ordinal) && r.Query("pageToken") == "p2",
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"b","summary":"B","accessRole":"reader"}]}"""));

        var calendars = await CreateClient().ListCalendarsAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], calendars.Select(c => c.Id));
        Assert.All(_google.Requests.Where(r => r.Method == HttpMethod.Get), r => Assert.Equal("ya29.test-refreshed-token", r.BearerToken));
    }

    [Fact]
    public async Task ListEventsAsync_WithSyncToken_SendsExpectedQuery()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.OK, Fixture.Read("events-incremental.json"));

        var page = await CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", "sync-token-1", "pg", TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal("sync-token-1", request.Query("syncToken"));
        Assert.Equal("pg", request.Query("pageToken"));
        Assert.Equal("true", request.Query("showDeleted"));
        Assert.Equal("false", request.Query("singleEvents"));
        Assert.Equal("2500", request.Query("maxResults"));
        Assert.Equal("sync-token-2", page.NextSyncToken);
        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task ListEventsAsync_Gone_ThrowsSyncTokenExpired()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.Gone, Fixture.Read("error-410.json"));

        await Assert.ThrowsAsync<SyncTokenExpiredException>(
            () => CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", "old", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListEventsAsync_Forbidden_ThrowsGoogleApiExceptionWithReason()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));

        var error = await Assert.ThrowsAsync<GoogleApiException>(
            () => CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", null, null, TestContext.Current.CancellationToken));

        Assert.Equal("forbidden", error.Reason);
    }

    [Fact]
    public async Task ListEventsAsync_Unauthorized_RefreshesAndRetriesOnce()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.Unauthorized, "{}", once: true);
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.OK, Fixture.Read("events-empty.json"));

        var page = await CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", null, null, TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-empty", page.NextSyncToken);
        Assert.Equal(2, _google.Requests.Count(r => r.Uri.AbsoluteUri == TokenUrl));
    }

    public void Dispose()
    {
        _google.Dispose();
        GC.SuppressFinalize(this);
    }
}
