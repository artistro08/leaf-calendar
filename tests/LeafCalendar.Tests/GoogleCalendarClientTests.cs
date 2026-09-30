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

    const string SingleUrl = EventsUrl + "/evt-single";

    [Fact]
    public async Task PatchEventAsync_SendsIfMatchBodyAndSendUpdates()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"2\"","summary":"New"}""");

        var json = await CreateClient().PatchEventAsync(Account, "leaf.tester@gmail.com", "evt-single", """{"summary":"New"}""", "\"1\"", sendUpdates: true, TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("\"1\"", request.IfMatch);
        Assert.Equal("""{"summary":"New"}""", request.Body);
        Assert.Equal("all", request.Query("sendUpdates"));
        Assert.Contains("\\\"2\\\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchEventAsync_NoBaseEtag_SendsNoIfMatch()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single"}""");

        await CreateClient().PatchEventAsync(Account, "leaf.tester@gmail.com", "evt-single", "{}", null, sendUpdates: false, TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Null(request.IfMatch);
        Assert.Equal("none", request.Query("sendUpdates"));
    }

    [Fact]
    public async Task PatchEventAsync_412_ThrowsPreconditionFailed()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.PreconditionFailed, """{"error":{"code":412,"errors":[{"reason":"conditionNotMet"}]}}""");

        await Assert.ThrowsAsync<PreconditionFailedException>(
            () => CreateClient().PatchEventAsync(Account, "leaf.tester@gmail.com", "evt-single", "{}", "\"1\"", false, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task DeleteEventAsync_Missing_ThrowsEventGone(HttpStatusCode status)
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Delete, SingleUrl, status, "{}");

        await Assert.ThrowsAsync<EventGoneException>(
            () => CreateClient().DeleteEventAsync(Account, "leaf.tester@gmail.com", "evt-single", "\"1\"", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InsertEventAsync_409_ThrowsDuplicate()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Post, EventsUrl, HttpStatusCode.Conflict, """{"error":{"code":409,"errors":[{"reason":"duplicate"}]}}""");

        await Assert.ThrowsAsync<DuplicateEventException>(
            () => CreateClient().InsertEventAsync(Account, "leaf.tester@gmail.com", """{"id":"abcde12345"}""", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PatchEventAsync_409_ThrowsApiErrorNotDuplicate()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.Conflict, """{"error":{"code":409,"errors":[{"reason":"conflict"}]}}""");

        var error = await Assert.ThrowsAsync<GoogleApiException>(
            () => CreateClient().PatchEventAsync(Account, "leaf.tester@gmail.com", "evt-single", """{"summary":"x"}""", "\"1\"", false, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Conflict, error.Status);
    }

    [Theory]
    [InlineData("""{"id":"abcde12345","conferenceData":{"createRequest":{"requestId":"r1","conferenceSolutionKey":{"type":"hangoutsMeet"}}}}""", "1")]
    [InlineData("""{"id":"abcde12345","summary":"x"}""", null)]
    public async Task InsertEventAsync_SendsConferenceVersionOnlyWithConferenceData(string body, string? expected)
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Post, EventsUrl, HttpStatusCode.OK, """{"id":"abcde12345","status":"confirmed"}""");

        await CreateClient().InsertEventAsync(Account, "leaf.tester@gmail.com", body, false, TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri.StartsWith(EventsUrl, StringComparison.Ordinal));
        Assert.Equal(expected, request.Query("conferenceDataVersion"));
        Assert.Equal("none", request.Query("sendUpdates"));
    }

    [Fact]
    public async Task InsertEventAsync_NoId_ThrowsAndSendsNothing()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateClient().InsertEventAsync(Account, "leaf.tester@gmail.com", """{"summary":"x"}""", false, TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task GetEventAsync_NotFound_ReturnsNull()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, SingleUrl, HttpStatusCode.NotFound, "{}");

        Assert.Null(await CreateClient().GetEventAsync(Account, "leaf.tester@gmail.com", "evt-single", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MoveEventAsync_PostsDestination()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Post, SingleUrl + "/move", HttpStatusCode.OK, """{"id":"evt-single"}""");

        await CreateClient().MoveEventAsync(Account, "leaf.tester@gmail.com", "evt-single", "family123@group.calendar.google.com", false, TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/move", StringComparison.Ordinal));
        Assert.Equal("family123@group.calendar.google.com", request.Query("destination"));
    }

    public void Dispose()
    {
        _google.Dispose();
        GC.SuppressFinalize(this);
    }
}
