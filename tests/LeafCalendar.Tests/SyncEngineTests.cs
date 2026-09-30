using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;
using Microsoft.Data.Sqlite;

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
        _h.Google.On(HttpMethod.Get, SyncHarness.PrimaryEventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAccountAsync(Account, TestContext.Current.CancellationToken);

        var log = File.ReadAllText(_h.LogPath);
        Assert.Equal("sync-token-empty", Calendar(Family).SyncToken);
        Assert.Null(Calendar(Primary).SyncToken);
        Assert.Contains("sync.calendar.failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("sync.account.failed", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAccountAsync_MalformedEventItem_SkipsItAndStillAdvances()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-1",
            _ => FakeHttpHandler.Json(
                HttpStatusCode.OK,
                """{"nextSyncToken":"sync-token-9","items":[{"id":"evt-bad","status":"confirmed","start":"not-an-object"},{"id":"evt-good","status":"confirmed","start":{"dateTime":"2026-10-03T10:00:00Z"},"end":{"dateTime":"2026-10-03T11:00:00Z"}}]}"""));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.NotNull(Get("evt-good"));
        Assert.Null(Get("evt-bad"));
        Assert.Equal("sync-token-9", Calendar(Primary).SyncToken);
        Assert.Contains("sync.event.skipped", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
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
        _h.RouteEvents(SyncHarness.FamilyEventsUrl, null, null, "events-page2.json");
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        Assert.True(CountEvents(Family) > 0);

        _h.Time.Advance(SyncEngine.CalendarListInterval);
        await _h.Engine.SyncAccountAsync(Account, ct);

        using var conn = _h.Db.Database.Open();
        Assert.Single(CalendarStore.GetForAccount(conn, Account));
        Assert.Equal(0, EventStore.Count(conn, Account, Family));
    }

    [Fact]
    public async Task SyncAccountAsync_CalledConcurrently_RunsOneAtATime()
    {
        var ct       = TestContext.Current.CancellationToken;
        var inFlight = 0;
        var overlap  = false;

        // Probe Route: never matches, but holds every request briefly so overlapping syncs collide
        _h.Google.On(
            _ =>
            {
                overlap |= Interlocked.Increment(ref inFlight) > 1;
                Thread.Sleep(20);
                Interlocked.Decrement(ref inFlight);
                return false;
            },
            _ => throw new InvalidOperationException("Probe route never answers."));
        _h.RouteStandardGoogle();

        var first  = Task.Run(() => _h.Engine.SyncAccountAsync(Account, ct), ct);
        var second = Task.Run(() => _h.Engine.SyncAccountAsync(Account, ct), ct);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.False(overlap);
        Assert.Equal("sync-token-2", Calendar(Primary).SyncToken);
    }

    [Fact]
    public async Task SyncAllAsync_OneAccountDatabaseError_OtherAccountsStillSync()
    {
        // Second Account Sorts First By Email
        using (var conn = _h.Db.Database.Open())
        {
            AccountStore.Upsert(conn, new Account("222", "another@gmail.com", null, null, AccountStatus.Ok));
        }

        _h.Tokens.SetRefreshToken("222", "1//second-refresh-token");
        _h.Google.On(r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.ListUrl, StringComparison.Ordinal), _ => throw new SqliteException("database is locked", 5), once: true);
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Contains("sync.account.failed", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAccountAsync_PendingLocalEdit_IsNotOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            // Held a day, so the outbox sender (Task 6) leaves it alone and only the pull is tested
            EventStore.ApplyJson(conn, null, Account, Primary, """{"id":"evt-single","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "evt-single", OutboxOperation.Patch, """{"summary":"Mine"}""", "\"3181161784712000\"", false, "[]", _h.Time.GetUtcNow().AddDays(1)));
        }

        // The incremental page moves evt-single on Google; the local edit must win until it's sent
        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.Contains("\"Mine\"", Get("evt-single")!.RawJson, StringComparison.Ordinal);
        Assert.NotNull(Get("evt-new"));
        Assert.Equal("sync-token-2", Calendar(Primary).SyncToken);
    }

    [Fact]
    public async Task SyncAccountAsync_FullResync_KeepsPendingEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteEvents(SyncHarness.PrimaryEventsUrl, "sync-token-1", null, "error-410.json", HttpStatusCode.Gone);
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            EventStore.ApplyJson(conn, null, Account, Primary, """{"id":"leafnew0001","status":"confirmed","summary":"Offline","start":{"dateTime":"2026-10-02T13:00:00Z"},"end":{"dateTime":"2026-10-02T14:00:00Z"}}""");
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "leafnew0001", OutboxOperation.Create, "{}", null, false, "[]", _h.Time.GetUtcNow().AddDays(1)));
        }

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.NotNull(Get("leafnew0001"));
        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
    }

    [Fact]
    public async Task SyncAccountAsync_FullResync_KeepsLocallyDeletedSeriesGone()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteEvents(SyncHarness.PrimaryEventsUrl, "sync-token-1", null, "error-410.json", HttpStatusCode.Gone);
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            EventStore.Remove(conn, null, Account, Primary, "evt-weekly");
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "evt-weekly", OutboxOperation.Delete, null, null, false, "[]", _h.Time.GetUtcNow().AddDays(1)));
        }

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.Null(Get("evt-weekly"));
        Assert.Null(Get("evt-weekly_20261007T133000Z"));
    }

    [Fact]
    public async Task SyncAccountAsync_FullResyncOfDestination_KeepsQueuedMovedEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteEvents(SyncHarness.FamilyEventsUrl, "sync-token-empty", null, "error-410.json", HttpStatusCode.Gone);
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            EventStore.MoveCalendar(conn, null, Account, Primary, Family, "evt-single");
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "evt-single", OutboxOperation.Move, Family, null, false, "[]", _h.Time.GetUtcNow().AddDays(1)));
        }

        await _h.Engine.SyncAccountAsync(Account, ct);

        using var check = _h.Db.Database.Open();
        Assert.NotNull(EventStore.Get(check, Account, Family, "evt-single"));
    }
}
