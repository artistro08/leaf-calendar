using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class GoogleServicesTests : IDisposable
{
    const string RevokeUrl = "https://oauth2.googleapis.com/revoke";

    readonly SyncHarness _h = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _h.Dispose();

    GoogleServices CreateServices() =>
        new(new HttpClient(_h.Google), new("id.apps.googleusercontent.com", "GOCSPX-test"), _h.Tokens, _h.Db.Database, _h.Log, _time);

    [Fact]
    public async Task DisconnectAsync_SignedInAccount_RevokesAndDeletesEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.OK, "{}");
        _h.RouteStandardGoogle();
        await using var services = CreateServices();
        await services.Sync.SyncAccountAsync(SyncHarness.AccountId, ct);

        await services.DisconnectAsync(SyncHarness.AccountId, ct);

        Assert.Equal("1//test-refresh-token", _h.Google.Requests.Single(r => r.Uri.AbsoluteUri == RevokeUrl).Form("token"));
        Assert.Null(_h.Tokens.GetRefreshToken(SyncHarness.AccountId));
        using var conn = _h.Db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
        Assert.Empty(CalendarStore.GetForAccount(conn, SyncHarness.AccountId));
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
