using System.Net;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class GoogleServicesTests : IDisposable
{
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    private readonly SyncHarness _h = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _h.Dispose();

    private GoogleServices CreateServices() =>
        new(new HttpClient(_h.Google), new("id.apps.googleusercontent.com", "GOCSPX-test"), _h.Tokens, _h.Db.Database, _h.Log, _time);

    [Fact]
    public async Task DisconnectAsync_SignedInAccount_RevokesAndDeletesEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.OK, "{}");
        _h.RouteStandardGoogle();
        await using var services = CreateServices();
        await services.Sync.SyncAccountAsync(SyncHarness.AccountId, ct);
        using (var seed = _h.Db.Database.Open())
        {
            AlertLedger.TryAdd(seed, $"Invite|{SyncHarness.AccountId}|cal|evt|0", AlertKind.Invite, "T", _time.GetUtcNow().AddDays(1), _time.GetUtcNow());
            AlertLedger.SetMark(seed, $"invites-seeded:{SyncHarness.AccountId}|cal", 1);
        }

        await services.DisconnectAsync(SyncHarness.AccountId, ct);

        Assert.Equal("1//test-refresh-token", _h.Google.Requests.Single(r => r.Uri.AbsoluteUri == RevokeUrl).Form("token"));
        Assert.Null(_h.Tokens.GetRefreshToken(SyncHarness.AccountId));
        using var conn = _h.Db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
        Assert.Empty(CalendarStore.GetForAccount(conn, SyncHarness.AccountId));
        Assert.False(AlertLedger.HasPrefix(conn, $"Invite|{SyncHarness.AccountId}|"));
        Assert.Null(AlertLedger.GetMark(conn, $"invites-seeded:{SyncHarness.AccountId}|cal"));
    }

    [Fact]
    public async Task DisconnectAsync_OpenJoinNow_IsWithdrawnForThatAccountOnly()
    {
        _h.Google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.OK, "{}");
        await using var services = CreateServices();
        using (var seed = _h.Db.Database.Open())
        {
            AlertLedger.TryAdd(seed, $"JoinNow|{SyncHarness.AccountId}|cal|evt|0", AlertKind.JoinNow, "mine", _time.GetUtcNow().AddHours(1), _time.GetUtcNow());
            AlertLedger.TryAdd(seed, "JoinNow|other|cal|evt|0", AlertKind.JoinNow, "theirs", _time.GetUtcNow().AddHours(1), _time.GetUtcNow());
        }

        var withdrawn = new List<string>();
        services.JoinNowWithdrawn += (_, tag) => withdrawn.Add(tag);

        await services.DisconnectAsync(SyncHarness.AccountId, TestContext.Current.CancellationToken);

        Assert.Equal(["mine"], withdrawn);
    }

    [Fact]
    public async Task RefreshHostedDomainsAsync_UnknownDomain_IsLookedUpOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(HttpMethod.Post, SyncHarness.TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _h.Google.On(HttpMethod.Get, UserInfoUrl, HttpStatusCode.OK, """{"sub":"109876543210","email":"leaf.tester@gmail.com","hd":"example.com"}""");
        await using var services = CreateServices();

        await services.RefreshHostedDomainsAsync(ct);
        await services.RefreshHostedDomainsAsync(ct);

        using var conn = _h.Db.Database.Open();
        Assert.Equal("example.com", AccountStore.GetAll(conn).Single().HostedDomain);
        Assert.Single(_h.Google.Requests, r => r.Uri.AbsoluteUri == UserInfoUrl);
    }

    [Fact]
    public async Task RefreshHostedDomainsAsync_Failure_LogsTheAccountOnlyAndTriesAgainLater()
    {
        _h.Google.On(HttpMethod.Post, SyncHarness.TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _h.Google.On(HttpMethod.Get, UserInfoUrl, HttpStatusCode.InternalServerError, """{"email":"leaf.tester@gmail.com"}""");
        await using var services = CreateServices();

        await services.RefreshHostedDomainsAsync(TestContext.Current.CancellationToken);

        using var conn = _h.Db.Database.Open();
        Assert.Null(AccountStore.GetAll(conn).Single().HostedDomain);
        var log = File.ReadAllText(_h.LogPath);
        Assert.Contains($"account.domain.failed account={SyncHarness.AccountId} error=GoogleApiException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("[email]", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectAsync_RevokeFails_StillRemovesLocally()
    {
        _h.Google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.ServiceUnavailable, "{}");
        await using var services = CreateServices();

        await services.DisconnectAsync(SyncHarness.AccountId, TestContext.Current.CancellationToken);

        Assert.Null(_h.Tokens.GetRefreshToken(SyncHarness.AccountId));
        using var conn = _h.Db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
        Assert.Contains("account.revoke.failed", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }
}
