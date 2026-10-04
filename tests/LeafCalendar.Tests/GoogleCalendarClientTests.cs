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
    public async Task PatchEventAsync_TokenRefreshRefused_ThrowsUnauthorizedNeverAnEventRefusal()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, """{"error":"invalid_request"}""");

        var error = await Assert.ThrowsAsync<GoogleApiException>(
            () => CreateClient().PatchEventAsync(Account, "leaf.tester@gmail.com", "evt-single", "{}", "\"1\"", sendUpdates: false, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        Assert.DoesNotContain(_google.Requests, r => r.Method == HttpMethod.Patch);
    }

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

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task InsertEventAsync_404Or410_ThrowsApiErrorNotGone(HttpStatusCode status)
    {
        // The calendar is gone, not the event (it never existed)
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Post, EventsUrl, status, """{"error":{"code":404,"errors":[{"reason":"notFound"}]}}""");

        var error = await Assert.ThrowsAsync<GoogleApiException>(
            () => CreateClient().InsertEventAsync(Account, "leaf.tester@gmail.com", """{"id":"abcde12345"}""", false, TestContext.Current.CancellationToken));
        Assert.Equal(status, error.Status);
    }

    [Fact]
    public async Task InsertEventAsync_SendsSupportsAttachments()
    {
        // A copy keeps the original's attachments; Google ignores them without this
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Post, EventsUrl, HttpStatusCode.OK, """{"id":"abcde12345","status":"confirmed"}""");

        await CreateClient().InsertEventAsync(Account, "leaf.tester@gmail.com", """{"id":"abcde12345","attachments":[{"fileUrl":"https://drive.google.com/open?id=1"}]}""", false, TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri.StartsWith(EventsUrl, StringComparison.Ordinal));
        Assert.Equal("true", request.Query("supportsAttachments"));
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

    [Theory]
    [InlineData("""{"conferenceData":{"createRequest":{"requestId":"r1"}}}""", "1")]
    [InlineData("""{"conferenceData":null}""", "1")]
    [InlineData("""{"summary":"x"}""", null)]
    [InlineData("not json", null)]
    public async Task Patch_SendsConferenceVersionOnlyWithConferenceData(string body, string? expected)
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single"}""");

        await CreateClient().PatchEventAsync(Account, "leaf.tester@gmail.com", "evt-single", body, "\"1\"", false, TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal(expected, request.Query("conferenceDataVersion"));
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

    // =========================================================================
    // CALENDAR LIST AND FREE/BUSY
    // =========================================================================

    // A client whose access-token refresh succeeds
    GoogleCalendarClient SignedIn()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        return CreateClient();
    }

    [Fact]
    public async Task QueryFreeBusy_PostsRangeAndIds_ReadsBusyAndErrors()
    {
        var client = SignedIn();
        _google.Respond(HttpMethod.Post, "freeBusy", 200, """
            {"calendars":{
              "dana@example.com":{"busy":[{"start":"2026-10-01T15:00:00Z","end":"2026-10-01T16:00:00Z"}]},
              "nobody@example.org":{"errors":[{"domain":"global","reason":"notFound"}],"busy":[]}}}
            """);

        var result = await client.QueryFreeBusyAsync(Account, ["dana@example.com", "nobody@example.org"],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        var body = _google.Last.Body!;
        Assert.Contains("\"timeMin\":\"2026-10-01T00:00:00Z\"", body, StringComparison.Ordinal);
        Assert.Contains("\"timeMax\":\"2026-10-02T00:00:00Z\"", body, StringComparison.Ordinal);
        Assert.Contains("\"items\":[{\"id\":\"dana@example.com\"},{\"id\":\"nobody@example.org\"}]", body, StringComparison.Ordinal);
        Assert.Equal([new BusyRange(new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero))], result["dana@example.com"].Busy);
        Assert.Null(result["dana@example.com"].Error);
        Assert.Equal("error", result["nobody@example.org"].Error);
    }

    [Fact]
    public async Task QueryFreeBusy_MatchesIdsIgnoringCase_ClipsToWindow_AndCapsCount()
    {
        var client = SignedIn();
        var many   = string.Join(',', Enumerable.Range(0, 600).Select(i => $"{{\"start\":\"2026-10-01T{i / 60:00}:{i % 60:00}:00Z\",\"end\":\"2026-10-01T{i / 60:00}:{i % 60:00}:30Z\"}}"));
        _google.Respond(HttpMethod.Post, "freeBusy", 200, "{\"calendars\":{"
            + "\"Dana@Example.com\":{\"busy\":[{\"start\":\"2026-09-30T20:00:00Z\",\"end\":\"2026-10-01T02:00:00Z\"},{\"start\":\"2026-10-03T00:00:00Z\",\"end\":\"2026-10-03T01:00:00Z\"}]},"
            + "\"big@example.com\":{\"busy\":[" + many + "]}}}");

        var result = await client.QueryFreeBusyAsync(Account, ["dana@example.com", "big@example.com"],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        Assert.Equal([new BusyRange(new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero))], result["dana@example.com"].Busy);
        Assert.Null(result["dana@example.com"].Error);
        Assert.Equal(GoogleCalendarClient.MaxBusyPerCalendar, result["big@example.com"].Busy.Count);
    }

    [Fact]
    public async Task QueryFreeBusy_IdMissingFromAnswer_IsAnError()
    {
        var client = SignedIn();
        _google.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{}}""");

        var result = await client.QueryFreeBusyAsync(Account, ["dana@example.com"], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal("missing", result["dana@example.com"].Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task QueryFreeBusy_ZeroOrOver50Ids_Throws(int count)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient().QueryFreeBusyAsync(Account,
            [.. Enumerable.Range(0, count).Select(i => $"p{i}@example.com")], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1),
            TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task PatchCalendarList_SendsPatchToTheListEntry()
    {
        var client = SignedIn();
        _google.Respond(HttpMethod.Patch, "users/me/calendarList/family%40group.calendar.google.com", 200, """{"id":"family@group.calendar.google.com","summaryOverride":"Kids"}""");

        var json = await client.PatchCalendarListAsync(Account, "family@group.calendar.google.com", """{"summaryOverride":"Kids"}""", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, _google.Last.Method);
        Assert.Equal("""{"summaryOverride":"Kids"}""", _google.Last.Body);
        Assert.Contains("Kids", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchCalendarList_Refused_ThrowsGoogleApiException()
    {
        var client = SignedIn();
        _google.Respond(HttpMethod.Patch, "users/me/calendarList/x", 400, Fixture.Read("error-forbidden.json"));

        await Assert.ThrowsAsync<GoogleApiException>(() => client.PatchCalendarListAsync(Account, "x", "{}", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ListEventsInRange_NotReadable_ReturnsNull(int status)
    {
        var client = SignedIn();
        _google.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", status, "{}");

        Assert.Null(await client.ListEventsInRangeAsync(Account, "dana@example.com", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListEventsInRange_AsksForSingleEventsInTheRange()
    {
        var client = SignedIn();
        _google.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """{"items":[{"id":"a","status":"confirmed"}]}""");

        var page = await client.ListEventsInRangeAsync(Account, "dana@example.com",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Single(page!.Items);
        Assert.Equal("2026-10-01T00:00:00Z", _google.Last.Query("timeMin"));
        Assert.Equal("2026-10-02T00:00:00Z", _google.Last.Query("timeMax"));
        Assert.Equal("true", _google.Last.Query("singleEvents"));
    }

    public void Dispose()
    {
        _google.Dispose();
        GC.SuppressFinalize(this);
    }
}
