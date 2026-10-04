using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class FreeBusyLookupTests : IDisposable
{
    private const string Account = "109876543210";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";

    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly FreeBusyLookup _lookup;

    public FreeBusyLookupTests()
    {
        _handler.On(HttpMethod.Post, TokenUrl, System.Net.HttpStatusCode.OK, Fixture.Read("token-refresh.json"));

        var store = new InMemoryTokenStore();
        store.SetRefreshToken(Account, "1//test-refresh-token");
        var http = new HttpClient(_handler);
        var oauth = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "secret"), _time);

        _log = new AppLog(_folder.Path, _time);
        _lookup = new FreeBusyLookup(new GoogleCalendarClient(http, new AccessTokenProvider(oauth, store, _time)), _log);
    }

    public void Dispose() => _folder.Dispose();

    private static DateTimeOffset Utc(int hour, int minute = 0) => new(2026, 10, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task Lookup_NotFound_IsUnknownNotFree()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"nobody@example.org":{"errors":[{"domain":"global","reason":"notFound"}],"busy":[]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/nobody%40example.org/events", 404, "{}");

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["nobody@example.org"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal(PersonBusyState.Unknown, person.State);
        Assert.Empty(person.Blocks);
    }

    [Fact]
    public async Task Lookup_FreeBusyError_IsUnknownEvenWithDetails()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"errors":[{"domain":"global","reason":"notFound"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """
            {"items":[{"id":"a","status":"confirmed","summary":"Sync","start":{"dateTime":"2026-10-01T17:00:00Z"},"end":{"dateTime":"2026-10-01T17:30:00Z"}}]}
            """);

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal(PersonBusyState.Unknown, person.State);
        Assert.Empty(person.Blocks);
    }

    [Fact]
    public async Task Lookup_FreeBusyOnly_BlocksWithoutTitles()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T15:00:00Z","end":"2026-10-01T16:00:00Z"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 403, "{}");

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal(PersonBusyState.Known, person.State);
        Assert.Equal([new BusyBlock(Utc(15), Utc(16), null)], person.Blocks);
    }

    [Fact]
    public async Task Lookup_SharedWithDetails_CleanTitlesOfBusyTimedEventsOnly()
    {
        // Free/busy is the authority: it marks 17:00–17:30 busy; the details only name it
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T17:00:00Z","end":"2026-10-01T17:30:00Z"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """
            {"items":[
              {"id":"a","status":"confirmed","summary":"‮1:1 with Sam","start":{"dateTime":"2026-10-01T17:00:00Z"},"end":{"dateTime":"2026-10-01T17:30:00Z"}},
              {"id":"b","status":"confirmed","summary":"Lunch","transparency":"transparent","start":{"dateTime":"2026-10-01T16:00:00Z"},"end":{"dateTime":"2026-10-01T17:00:00Z"}},
              {"id":"c","status":"cancelled","summary":"Gone","start":{"dateTime":"2026-10-01T18:00:00Z"},"end":{"dateTime":"2026-10-01T19:00:00Z"}},
              {"id":"d","status":"confirmed","summary":"Holiday","start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}]}
            """);

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal(PersonBusyState.Known, person.State);
        Assert.Equal([new BusyBlock(Utc(17), Utc(17, 30), "1:1 with Sam")], person.Blocks);
    }

    [Fact]
    public async Task Lookup_DetailsMissingSomeBusyTime_KeepsItUntitled()
    {
        // Busy 13:00–15:00, but the readable details only cover 13:00–14:00 (e.g. a private event fills the rest)
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T13:00:00Z","end":"2026-10-01T15:00:00Z"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """
            {"items":[{"id":"a","status":"confirmed","summary":"Review","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}]}
            """);

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal([new BusyBlock(Utc(13), Utc(14), "Review"), new BusyBlock(Utc(14), Utc(15), null)], person.Blocks);
    }

    [Fact]
    public async Task Lookup_DuplicatesAndOverTheCap_AreTrimmed()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{}}""");
        _handler.RespondToAny(HttpMethod.Get, 404, "{}");

        var emails = Enumerable.Range(0, 30).Select(i => $"p{i}@example.com").Append("P0@example.com").Prepend(" p0@example.com ").ToList();
        var people = await _lookup.LookupAsync(Account, emails, From, To, TestContext.Current.CancellationToken);

        Assert.Equal(FreeBusyLookup.MaxPeople, people.Count);
        Assert.Equal("p0@example.com", people[0].Email);
        Assert.Single(people, p => p.Email.Equals("p0@example.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Lookup_NoEmails_IsEmptyAndAsksNothing()
    {
        Assert.Empty(await _lookup.LookupAsync(Account, [" ", ""], From, To, TestContext.Current.CancellationToken));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Lookup_GoogleUnreachable_Throws()
    {
        _handler.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_DetailsCallFails_FallsBackToFreeBusy()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T15:00:00Z","end":"2026-10-01T16:00:00Z"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 500, """{"error":{"code":500,"message":"backend"}}""");

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal([new BusyBlock(Utc(15), Utc(16), null)], person.Blocks);
    }

    [Fact]
    public async Task Lookup_DetailsCallTimesOut_FallsBackToFreeBusy()
    {
        // HttpClient's own timeout throws TaskCanceledException though the caller never canceled
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T15:00:00Z","end":"2026-10-01T16:00:00Z"}]}}}""");
        _handler.On(r => r.Method == HttpMethod.Get, _ => throw new TaskCanceledException("timed out"));

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal([new BusyBlock(Utc(15), Utc(16), null)], person.Blocks);
    }

    [Fact]
    public async Task Lookup_Canceled_Throws()
    {
        using var cts = new CancellationTokenSource();
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[]}}}""");
        _handler.On(r => r.Method == HttpMethod.Get, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _lookup.LookupAsync(Account, ["dana@example.com"], From, To, cts.Token));
    }

    [Fact]
    public async Task Lookup_MeetingThePersonDeclined_IsNotATitledBlock()
    {
        // Dana declined All-hands and accepted the 1:1, so free/busy has only the 1:1's half hour
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T13:30:00Z","end":"2026-10-01T14:00:00Z"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """
            {"items":[
              {"id":"a","status":"confirmed","summary":"All-hands","attendees":[{"email":"boss@example.com","responseStatus":"accepted"},{"email":"dana@example.com","self":true,"responseStatus":"declined"}],
               "start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}},
              {"id":"b","status":"confirmed","summary":"1:1","attendees":[{"email":"boss@example.com","responseStatus":"declined"},{"email":"dana@example.com","self":true,"responseStatus":"accepted"}],
               "start":{"dateTime":"2026-10-01T13:30:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}]}
            """);

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal([new BusyBlock(Utc(13, 30), Utc(14), "1:1")], person.Blocks);
    }

    [Fact]
    public async Task Lookup_LogsNoAddresses()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 404, "{}");

        await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken);

        var log = File.ReadAllText(_log.FilePath);
        Assert.Contains("freebusy.lookup", log, StringComparison.Ordinal);
        Assert.DoesNotContain("[email]", log, StringComparison.Ordinal);
        Assert.DoesNotContain("dana", log, StringComparison.OrdinalIgnoreCase);
    }
}
