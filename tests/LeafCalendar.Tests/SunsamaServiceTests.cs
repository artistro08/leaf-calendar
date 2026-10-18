using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Http;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SunsamaServiceTests : IDisposable
{
    private const string Root = "https://fake.test/";
    private const string Email = "secret.owner@example.com";
    private const string TaskTitle = "Private task title";
    private const string Channel = "Private channel";

    private static readonly HttpClient Browser = new();
    private static readonly DateOnly Day = new(2026, 10, 16);

    private readonly FakeHttpHandler _server = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly TestDatabase _db = new();
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 13, 0, 0, TimeSpan.Zero));
    private readonly SunsamaEndpoints _endpoints = SunsamaEndpoints.ForFake(new Uri(Root)) with { RedirectPort = FreePort() };
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);
    private readonly List<SunsamaService> _created = [];
    private readonly AppLog _log;

    public SunsamaServiceTests()
    {
        _log = new AppLog(_folder.Path, _time);
        _resources["sunsama://me"] = $$$"""{"user":{"email":"{{{Email}}}","timezone":"America/Chicago","calendars":[{"id":"work@example.com","provider":"google-calendar"}]}}""";
        _resources["sunsama://tasks/2026-10-16"] = $$"""
            {"tasks":[{"_id":"6ac7a44534029300011d076e","title":"{{TaskTitle}}","channel":"{{Channel}}","timeEstimate":"2 hours",
              "actualTimeSpent":{"total":"30 minutes"},
              "projectedTimeEntries":[{"startDate":"2026-10-16 10:50 AM","endDate":"2026-10-16 11:30 AM"}]}]}
            """;
        _resources["sunsama://calendar/events/2026-10-16"] = """{"events":[]}""";
    }

    public void Dispose()
    {
        foreach (var service in _created)
        {
            service.Dispose();
        }

        _server.Dispose();
        _db.Dispose();
        _folder.Dispose();
    }

    private SunsamaService Create(ITokenStore? tokens = null)
    {
        var service = new SunsamaService(new HttpClient(_server), tokens ?? _tokens, _db.Database, _log, _time, _endpoints);
        _created.Add(service);
        return service;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Acts like Sunsama + the browser: reads the authorize URL and hits the loopback redirect
    private static Task Approve(Uri url)
    {
        var query = QueryString.Parse(url.Query);
        var redirect = new Uri(query["redirect_uri"]);
        _ = Task.Run(() => Browser.GetAsync(new Uri(redirect, $"?code=code-1&state={Uri.EscapeDataString(query["state"])}")));
        return Task.CompletedTask;
    }

    private void SeedSignedIn()
    {
        _tokens.SetProviderSecret(SunsamaService.ProviderId, "client_id", "c-saved");
        _tokens.SetProviderSecret(SunsamaService.ProviderId, "refresh_token", "rt-saved");
        _tokens.SetProviderSecret(SunsamaService.ProviderId, "email", Email);
    }

    private static string Method(RecordedRequest r) => JsonDocument.Parse(r.Body!).RootElement.GetProperty("method").GetString()!;

    private static bool IsMcp(RecordedRequest r) => r.Uri.AbsoluteUri == Root + "sunsama/mcp";

    private static HttpResponseMessage Rpc(string json, string? session = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (session is not null)
        {
            response.Headers.Add("Mcp-Session-Id", session);
        }

        return response;
    }

    private HttpResponseMessage Read(RecordedRequest r)
    {
        var uri = JsonDocument.Parse(r.Body!).RootElement.GetProperty("params").GetProperty("uri").GetString()!;
        var text = JsonSerializer.Serialize(_resources[uri]);
        return Rpc($$$"""{"jsonrpc":"2.0","id":2,"result":{"contents":[{"uri":"{{{uri}}}","text":{{{text}}}}]}}""");
    }

    private void ServeToken(HttpStatusCode status = HttpStatusCode.OK, string body = """{"access_token":"at-1","refresh_token":"rt-new","expires_in":3600}""") =>
        _server.On(HttpMethod.Post, Root + "sunsama/oauth/token", status, body);

    private void ServeMcp()
    {
        _server.On(r => IsMcp(r) && Method(r) == "initialize", _ => Rpc("""{"jsonrpc":"2.0","id":1,"result":{}}""", "s1"));
        _server.On(r => IsMcp(r) && Method(r) == "notifications/initialized", _ => new HttpResponseMessage(HttpStatusCode.Accepted));
        _server.On(r => IsMcp(r) && Method(r) == "resources/read", Read);
    }

    private void SaveRow(string task)
    {
        using var conn = _db.Database.Open();
        SunsamaStore.ReplaceDay(conn, null, Day, [new SunsamaBlock(Day, task, "t", "c", _time.GetUtcNow(), _time.GetUtcNow().AddHours(1), null, null, false, SunsamaBlockKind.Projected, null, null, null)]);
    }

    private IReadOnlyList<SunsamaBlock> Rows()
    {
        using var conn = _db.Database.Open();
        return SunsamaStore.Range(conn, Day, Day.AddDays(1));
    }

    [Fact]
    public void Load_SavedRefreshToken_IsSignedInWithEmail()
    {
        SeedSignedIn();
        var service = Create();

        service.Load();

        Assert.Equal(SunsamaState.SignedIn, service.State);
        Assert.Equal(Email, service.Email);
    }

    [Fact]
    public void Load_NothingSaved_IsSignedOut()
    {
        var service = Create();

        service.Load();

        Assert.Equal(SunsamaState.SignedOut, service.State);
    }

    [Fact]
    public void Load_SecretsUnavailable_StaysSignedOutAndKeepsSecrets()
    {
        SeedSignedIn();
        var unreadable = new FlakyTokenStore(_tokens) { FailReads = true };
        var service = Create(unreadable);

        service.Load();

        Assert.Equal(SunsamaState.SignedOut, service.State);
        Assert.Equal("rt-saved", _tokens.GetProviderSecret(SunsamaService.ProviderId, "refresh_token"));
    }

    [Fact]
    public async Task SignInAsync_FirstTime_RegistersSignsInAndSavesClientTokenEmail()
    {
        _server.On(HttpMethod.Post, Root + "sunsama/oauth/register", HttpStatusCode.Created, """{"client_id":"c1"}""");
        ServeToken();
        ServeMcp();
        var service = Create();
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SignInAsync(Approve, TestContext.Current.CancellationToken);

        Assert.Equal("c1", _tokens.GetProviderSecret(SunsamaService.ProviderId, "client_id"));
        Assert.Equal("rt-new", _tokens.GetProviderSecret(SunsamaService.ProviderId, "refresh_token"));
        Assert.Equal(Email, _tokens.GetProviderSecret(SunsamaService.ProviderId, "email"));
        Assert.Equal(SunsamaState.SignedIn, service.State);
        Assert.Equal(Email, service.Email);
        Assert.Equal(1, changed);
        var register = _server.Requests.First(r => r.Uri.AbsolutePath == "/sunsama/oauth/register");
        Assert.Contains($"http://127.0.0.1:{_endpoints.RedirectPort}/sunsama/callback", register.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInAsync_SavedClientId_DoesNotRegisterAgain()
    {
        _tokens.SetProviderSecret(SunsamaService.ProviderId, "client_id", "c-saved");
        ServeToken();
        ServeMcp();
        var service = Create();

        await service.SignInAsync(Approve, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_server.Requests, r => r.Uri.AbsolutePath == "/sunsama/oauth/register");
        Assert.Equal("c-saved", _server.Requests.First(r => r.Uri.AbsolutePath == "/sunsama/oauth/token").Form("client_id"));
    }

    [Fact]
    public async Task SignInAsync_ClientRefused_ClearsClientIdSoNextTimeRegisters()
    {
        _tokens.SetProviderSecret(SunsamaService.ProviderId, "client_id", "c-stale");
        ServeToken(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");
        var service = Create();

        await Assert.ThrowsAsync<SchedulingAuthException>(() => service.SignInAsync(Approve, TestContext.Current.CancellationToken));

        Assert.Null(_tokens.GetProviderSecret(SunsamaService.ProviderId, "client_id"));
        Assert.Equal(SunsamaState.SignedOut, service.State);
    }

    [Fact]
    public async Task SyncAsync_SavesBlocksForDaysOnScreen()
    {
        SeedSignedIn();
        ServeToken();
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        var dataChanged = 0;
        service.DataChanged += (_, _) => dataChanged++;

        await service.SyncAsync(TestContext.Current.CancellationToken);

        var block = Assert.Single(Rows());
        Assert.Equal(SunsamaBlockKind.Projected, block.Kind);
        Assert.Equal(TaskTitle, block.Title);
        Assert.Equal(30, block.TrackedMinutes);
        Assert.Equal(1, dataChanged);
    }

    [Fact]
    public async Task SyncAsync_NothingChanged_DoesNotRaiseDataChangedAgain()
    {
        SeedSignedIn();
        ServeToken();
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        var dataChanged = 0;
        service.DataChanged += (_, _) => dataChanged++;

        await service.SyncAsync(TestContext.Current.CancellationToken);
        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, dataChanged);
        _resources["sunsama://tasks/2026-10-16"] = """{"tasks":[]}""";
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, dataChanged);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task SyncAsync_RateLimited_WaitsForRetryAfter()
    {
        SeedSignedIn();
        ServeToken();
        _server.On(IsMcp, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));

        await service.SyncAsync(TestContext.Current.CancellationToken);
        var asked = _server.Requests.Count(IsMcp);
        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(asked, _server.Requests.Count(IsMcp));
        _time.Advance(TimeSpan.FromSeconds(61));
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.True(_server.Requests.Count(IsMcp) > asked);
    }

    [Fact]
    public async Task SyncAsync_WhileAPassRuns_ReturnsAtOnce()
    {
        SeedSignedIn();
        ServeToken();
        var reading = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _server.On(r => IsMcp(r) && Method(r) == "initialize", _ =>
        {
            reading.TrySetResult();
            release.Task.Wait(TestContext.Current.CancellationToken);
            return Rpc("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        });
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));

        var first = Task.Run(() => service.SyncAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var asked = _server.Requests.Count;
        await service.SyncAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(asked, _server.Requests.Count);
        release.SetResult();
        await first;
        Assert.Single(Rows());
    }

    [Fact]
    public async Task SyncAsync_ThreeFailedPassesInARow_IsUnreachableUntilOneWorks()
    {
        SeedSignedIn();
        ServeToken();
        var down = true;
        _server.On(r => down && IsMcp(r), _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SyncAsync(TestContext.Current.CancellationToken);
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Unreachable);
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.True(service.Unreachable);
        Assert.Equal(1, changed);

        down = false;
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Unreachable);
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task SignInAsync_AfterARefusal_ReplacesTheOldRows()
    {
        SeedSignedIn();
        SaveRow("old-account");
        var refuse = true;
        _server.On(
            r => r.Uri.AbsoluteUri == Root + "sunsama/oauth/token",
            _ => refuse
                ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
                : FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-new","expires_in":3600}"""));
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SunsamaState.NeedsSignIn, service.State);

        refuse = false;
        await service.SignInAsync(Approve, TestContext.Current.CancellationToken);

        Assert.Equal(SunsamaState.SignedIn, service.State);
        Assert.Empty(Rows());
    }

    [Fact]
    public void SignOut_SecretsCantBeRemoved_StillClearsRowsAndSaysSo()
    {
        SeedSignedIn();
        SaveRow("gone");
        var service = Create(new FlakyTokenStore(_tokens) { FailRemove = true });
        service.Load();
        var changed = 0;
        service.Changed += (_, _) => changed++;

        Assert.Throws<IOException>(service.SignOut);

        Assert.Empty(Rows());
        Assert.Equal(SunsamaState.SignedOut, service.State);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task SyncAsync_RotatedTokenCantBeSaved_StillSyncs()
    {
        SeedSignedIn();
        ServeToken();
        ServeMcp();
        var service = Create(new FlakyTokenStore(_tokens) { FailWrites = true });
        service.Load();
        service.SetDays(Day, Day.AddDays(1));

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Single(Rows());
        Assert.Contains("sunsama.token.save-failed", File.ReadAllText(_log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_Disabled_FetchesNothing()
    {
        SeedSignedIn();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        service.Enabled = false;

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task SyncAsync_SignedOut_FetchesNothing()
    {
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task SyncAsync_Offline_KeepsSavedRows()
    {
        SeedSignedIn();
        SaveRow("kept");
        ServeToken();
        _server.On(IsMcp, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal("kept", Assert.Single(Rows()).TaskId);
        Assert.Equal(SunsamaState.SignedIn, service.State);
    }

    [Fact]
    public async Task SyncAsync_RefusedRefresh_NeedsSignInKeepsRows()
    {
        SeedSignedIn();
        SaveRow("kept");
        ServeToken(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SunsamaState.NeedsSignIn, service.State);
        Assert.Equal(1, changed);
        Assert.Equal("kept", Assert.Single(Rows()).TaskId);
        Assert.Equal("rt-saved", _tokens.GetProviderSecret(SunsamaService.ProviderId, "refresh_token"));
    }

    [Fact]
    public void SignOut_RemovesSecretsAndRows()
    {
        SeedSignedIn();
        SaveRow("gone");
        var service = Create();
        service.Load();
        var changed = 0;
        var dataChanged = 0;
        service.Changed += (_, _) => changed++;
        service.DataChanged += (_, _) => dataChanged++;

        service.SignOut();

        Assert.Equal(SunsamaState.SignedOut, service.State);
        Assert.Null(service.Email);
        Assert.Null(_tokens.GetProviderSecret(SunsamaService.ProviderId, "refresh_token"));
        Assert.Null(_tokens.GetProviderSecret(SunsamaService.ProviderId, "client_id"));
        Assert.Null(_tokens.GetProviderSecret(SunsamaService.ProviderId, "email"));
        Assert.Empty(Rows());
        Assert.Equal((1, 1), (changed, dataChanged));
    }

    [Fact]
    public async Task SignOut_WhileATokenRefreshRuns_RotatedTokenIsNotSavedAgain()
    {
        SeedSignedIn();
        var refreshing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _server.On(
            r => r.Uri.AbsoluteUri == Root + "sunsama/oauth/token",
            _ =>
            {
                refreshing.TrySetResult();
                release.Task.Wait(TestContext.Current.CancellationToken);
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-new","expires_in":3600}""");
            });
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));

        var pass = Task.Run(() => service.SyncAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        service.SignOut();
        release.SetResult();
        await pass;

        Assert.Null(_tokens.GetProviderSecret(SunsamaService.ProviderId, "refresh_token"));
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task SignInAsync_ClientRefusedWhileNeedsSignIn_RegistersAgainNextTime()
    {
        SeedSignedIn();
        string? refusal = "invalid_grant";
        _server.On(
            r => r.Uri.AbsoluteUri == Root + "sunsama/oauth/token",
            _ => refusal is null
                ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-new","expires_in":3600}""")
                : FakeHttpHandler.Json(HttpStatusCode.BadRequest, $$"""{"error":"{{refusal}}"}"""));
        _server.On(HttpMethod.Post, Root + "sunsama/oauth/register", HttpStatusCode.Created, """{"client_id":"c-new"}""");
        ServeMcp();
        var service = Create();
        service.Load();
        service.SetDays(Day, Day.AddDays(1));
        await service.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SunsamaState.NeedsSignIn, service.State);

        refusal = "invalid_client";
        await Assert.ThrowsAsync<SchedulingAuthException>(() => service.SignInAsync(Approve, TestContext.Current.CancellationToken));
        Assert.Equal("rt-saved", _tokens.GetProviderSecret(SunsamaService.ProviderId, "refresh_token"));
        refusal = null;
        await service.SignInAsync(Approve, TestContext.Current.CancellationToken);

        Assert.Single(_server.Requests, r => r.Uri.AbsolutePath == "/sunsama/oauth/register");
        Assert.Equal("c-new", _tokens.GetProviderSecret(SunsamaService.ProviderId, "client_id"));
        Assert.Equal(SunsamaState.SignedIn, service.State);
    }

    [Fact]
    public async Task SyncAsync_AfterSignIn_LogsNoTaskTextOrEmail()
    {
        _server.On(HttpMethod.Post, Root + "sunsama/oauth/register", HttpStatusCode.Created, """{"client_id":"c1"}""");
        ServeToken();
        ServeMcp();
        var service = Create();
        await service.SignInAsync(Approve, TestContext.Current.CancellationToken);
        service.SetDays(Day, Day.AddDays(1));

        await service.SyncAsync(TestContext.Current.CancellationToken);

        var log = File.ReadAllText(_log.FilePath);
        Assert.Contains("sunsama.", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, log, StringComparison.Ordinal);
        Assert.DoesNotContain(TaskTitle, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Channel, log, StringComparison.Ordinal);
        Assert.DoesNotContain("rt-new", log, StringComparison.Ordinal);
    }

    // A token store whose provider secrets can't be read, written or removed right now (DPAPI not ready, a full disk)
    private sealed class FlakyTokenStore(InMemoryTokenStore inner) : ITokenStore
    {
        public OAuthClientCredentials? GetClientCredentials() => inner.GetClientCredentials();

        public void SetClientCredentials(OAuthClientCredentials credentials) => inner.SetClientCredentials(credentials);

        public void ClearClientCredentials() => inner.ClearClientCredentials();

        public string? GetRefreshToken(string accountId) => inner.GetRefreshToken(accountId);

        public void SetRefreshToken(string accountId, string refreshToken) => inner.SetRefreshToken(accountId, refreshToken);

        public void RemoveRefreshToken(string accountId) => inner.RemoveRefreshToken(accountId);

        public IReadOnlyList<string> GetAccountIds() => inner.GetAccountIds();

        public bool FailReads { get; init; }

        public bool FailWrites { get; init; }

        public bool FailRemove { get; init; }

        public string? GetProviderSecret(string providerId, string key) =>
            FailReads ? throw SecretsUnavailable.Create("Secrets can't be read right now.", new IOException()) : inner.GetProviderSecret(providerId, key);

        public void SetProviderSecret(string providerId, string key, string value)
        {
            if (FailWrites)
            {
                throw new IOException("The disk is full.");
            }

            inner.SetProviderSecret(providerId, key, value);
        }

        public void RemoveProviderSecrets(string providerId)
        {
            if (FailRemove)
            {
                throw new IOException("The disk is full.");
            }

            inner.RemoveProviderSecrets(providerId);
        }
    }
}
