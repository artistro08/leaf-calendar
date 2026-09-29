using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SyncEngineTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Family  = "family123@group.calendar.google.com";
    const string Account = SyncHarness.AccountId;

    readonly SyncHarness _h = new();

    public void Dispose() => _h.Dispose();

    CalendarInfo Calendar(string id)
    {
        using var conn = _h.Db.Database.Open();
        return CalendarStore.GetForAccount(conn, Account).Single(c => c.Id == id);
    }

    int CountEvents(string calendarId)
    {
        using var conn = _h.Db.Database.Open();
        return EventStore.Count(conn, Account, calendarId);
    }

    StoredEvent? Get(string id)
    {
        using var conn = _h.Db.Database.Open();
        return EventStore.Get(conn, Account, Primary, id);
    }

    [Fact]
    public async Task SyncAccountAsync_FirstRun_StoresCalendarsEventsAndToken()
    {
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAccountAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Equal("sync-token-empty", Calendar(Family).SyncToken);
        Assert.Equal(4, CountEvents(Primary));
        Assert.Null(Get("evt-deleted"));
        Assert.Contains(_h.Google.Requests, r => r.Query("pageToken") == "page-2");
    }

    [Fact]
    public async Task SyncAccountAsync_SecondRun_AppliesIncrementalChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.Equal("sync-token-2", Calendar(Primary).SyncToken);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), Get("evt-single")!.Start);
        Assert.Null(Get("evt-allday"));
        Assert.NotNull(Get("evt-new"));
        Assert.Equal(4, CountEvents(Primary));
    }

    [Fact]
    public async Task SyncAccountAsync_SyncTokenExpired_DoesFullResyncReplacingStaleRows()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteEvents(SyncHarness.PrimaryEventsUrl, "sync-token-1", null, "error-410.json", HttpStatusCode.Gone);
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            using var doc = System.Text.Json.JsonDocument.Parse("""{"id":"evt-stale","status":"confirmed","start":{"date":"2026-01-01"},"end":{"date":"2026-01-02"}}""");
            EventStore.Apply(conn, null, Account, Primary, doc.RootElement);
        }

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.Null(Get("evt-stale"));
        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Equal(4, CountEvents(Primary));
    }

    [Fact]
    public async Task SyncAccountAsync_FailureMidPagination_KeepsPreviousDataAndToken()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-1" && r.Query("pageToken") is null,
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"evt-single","status":"cancelled"}],"nextPageToken":"inc-2"}"""));
        _h.Google.On(
            r => r.Query("pageToken") == "inc-2",
            _ => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.NotNull(Get("evt-single"));
        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Contains("sync.calendar.failed", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAccountAsync_OneCalendarForbidden_OthersStillSync()
    {
        _h.Google.On(HttpMethod.Get, SyncHarness.FamilyEventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAccountAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Null(Calendar(Family).SyncToken);
    }

    [Fact]
    public async Task SyncAccountAsync_RefreshTokenRevoked_MarksNeedsSignInAndKeepsEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        // Revoked token is refused; routes are first-match, so this goes in before the standard routes
        _h.Google.On(r => r.Form("refresh_token") == "1//revoked", _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json")));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        var eventsBefore = CountEvents(Primary);

        _h.Tokens.SetRefreshToken(Account, "1//revoked");
        await _h.NewEngine().SyncAccountAsync(Account, ct);

        using var conn = _h.Db.Database.Open();
        Assert.Equal(AccountStatus.NeedsSignIn, AccountStore.GetAll(conn).Single().Status);
        Assert.Equal(eventsBefore, CountEvents(Primary));
    }

    [Fact]
    public async Task SyncAllAsync_AccountNeedsSignIn_IsSkipped()
    {
        _h.RouteStandardGoogle();
        using (var conn = _h.Db.Database.Open())
        {
            AccountStore.SetStatus(conn, Account, AccountStatus.NeedsSignIn);
        }

        await _h.Engine.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_h.Google.Requests);
    }

    [Fact]
    public async Task SyncAccountAsync_CalendarRemovedFromList_DeletesItsEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(HttpMethod.Get, SyncHarness.ListUrl, HttpStatusCode.OK, Fixture.Read("calendar-list.json"), once: true);
        _h.Google.On(HttpMethod.Get, SyncHarness.ListUrl, HttpStatusCode.OK, Fixture.Read("calendar-list-primary-only.json"));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        using var conn = _h.Db.Database.Open();
        Assert.Single(CalendarStore.GetForAccount(conn, Account));
        Assert.Equal(0, EventStore.Count(conn, Account, Family));
    }
}
