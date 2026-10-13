using System.Net;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SchedulingHubTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeHttpHandler _cal = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero));
    private readonly SchedulingProviderInfo _calcom = SchedulingProviders.Find("calcom")!;

    private SchedulingHub Create(Uri? fakeRoot = null) => new(new HttpClient(_cal), _tokens, new AppLog(_folder.Path, _time), _time, fakeRoot);

    private void LinksAnswer(string baseUrl = "https://api.cal.com/") =>
        _cal.On(HttpMethod.Get, baseUrl + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task ConnectAsync_GoodKey_SavesSecretAndConnects()
    {
        LinksAnswer();
        var hub = Create();

        var links = await hub.ConnectAsync(_calcom, new Dictionary<string, string> { ["api_key"] = " cal_live_x ", ["server"] = "" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, links.Count);
        Assert.Equal("cal_live_x", _tokens.GetProviderSecret("calcom", "api_key"));
        Assert.Equal(ProviderState.Connected, hub.StateOf("calcom"));
        Assert.NotNull(hub.Get("calcom"));
    }

    [Fact]
    public async Task ConnectAsync_BadKey_SavesNothing()
    {
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/event-types", HttpStatusCode.Unauthorized, "{}");
        var hub = Create();

        await Assert.ThrowsAsync<SchedulingAuthException>(() => hub.ConnectAsync(_calcom, new Dictionary<string, string> { ["api_key"] = "cal_live_bad" }, TestContext.Current.CancellationToken));

        Assert.Null(_tokens.GetProviderSecret("calcom", "api_key"));
        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));
    }

    [Theory]
    [InlineData("nope", "")]
    [InlineData("cal_live_x", "http://cal.example.com")]
    [InlineData("cal_live_x", "https://user@cal.example.com")]
    [InlineData("cal_live_x", "https://cal.example.com/?a=b")]
    [InlineData("cal_live x", "")]
    [InlineData("cal_live\nx", "")]
    [InlineData("cal_live\tx", "")]
    [InlineData("", "")]
    public void Check_InvalidValues_ReturnsMessage(string key, string server) =>
        Assert.NotNull(SchedulingHub.Check(_calcom, new Dictionary<string, string> { ["api_key"] = key, ["server"] = server }));

    [Theory]
    [InlineData(" cal_live_x ", "")]
    [InlineData("cal_live_x\r\n", "https://cal.example.com/api")]
    public void Check_ValidValues_ReturnsNull(string key, string server) =>
        Assert.Null(SchedulingHub.Check(_calcom, new Dictionary<string, string> { ["api_key"] = key, ["server"] = server }));

    [Fact]
    public async Task ConnectAsync_KeyWithInnerSpace_ThrowsAndSendsNothing()
    {
        LinksAnswer();
        var hub = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => hub.ConnectAsync(_calcom, new Dictionary<string, string> { ["api_key"] = "cal_live x" }, TestContext.Current.CancellationToken));

        Assert.Empty(_cal.Requests);
        Assert.Null(_tokens.GetProviderSecret("calcom", "api_key"));
    }

    [Fact]
    public async Task Load_SelfHostedServer_UsesIt()
    {
        LinksAnswer("https://cal.example.com/api/");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();

        hub.Load([new ProviderSettings("calcom", "https://cal.example.com/api", "")]);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("https://cal.example.com/api/v2/event-types", _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_HttpServerInSettings_LeavesProviderOut()
    {
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();

        hub.Load([new ProviderSettings("calcom", "http://cal.example.com/", "")]);

        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task Load_FakeRoot_UsesProviderFolderOfFake()
    {
        LinksAnswer("http://127.0.0.1:5000/calcom/");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create(new Uri("http://127.0.0.1:5000/"));

        hub.Load([]);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("http://127.0.0.1:5000/calcom/v2/event-types", _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SecretsUnavailable_KeepsNothingAndRetriesLater()
    {
        var tokens = new ThrowingProviderTokenStore();
        var hub = new SchedulingHub(new HttpClient(_cal), tokens, new AppLog(_folder.Path, _time), _time);

        hub.Load([]);
        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));

        tokens.Fail = false;
        tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        hub.Load([]);
        Assert.Equal(ProviderState.Connected, hub.StateOf("calcom"));
    }

    [Fact]
    public void Load_SecretsUnavailableAfterConnect_KeepsConnected()
    {
        var tokens = new ThrowingProviderTokenStore { Fail = false };
        tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = new SchedulingHub(new HttpClient(_cal), tokens, new AppLog(_folder.Path, _time), _time);
        hub.Load([]);

        tokens.Fail = true;
        hub.Load([]);

        Assert.Equal(ProviderState.Connected, hub.StateOf("calcom"));
        Assert.NotNull(hub.Get("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_KeyRevoked_MarksNeedsReconnect()
    {
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/event-types", HttpStatusCode.Unauthorized, "{}");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);

        Assert.Empty(await hub.GetLinksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderState.NeedsReconnect, hub.StateOf("calcom"));
        Assert.Empty(hub.Connected);
    }

    [Fact]
    public async Task GetLinksAsync_Unavailable_KeepsCachedLinks()
    {
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"), once: true);
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/event-types", HttpStatusCode.InternalServerError, "{}");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(6));
        var links = await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _cal.Requests.Count);
        Assert.Equal(2, links.Count);
        Assert.Equal(ProviderState.Connected, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_Cached_ForFiveMinutes()
    {
        LinksAnswer();
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);

        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Single(_cal.Requests);

        _time.Advance(TimeSpan.FromMinutes(6));
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _cal.Requests.Count);
    }

    [Fact]
    public void Disconnect_RemovesSecretsAndState()
    {
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);

        hub.Disconnect("calcom");

        Assert.Null(_tokens.GetProviderSecret("calcom", "api_key"));
        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));
    }

    [Fact]
    public void WaitFor_RetryAfter_WaitsThatLong()
    {
        var hub = Create();

        hub.WaitFor("calcom", TimeSpan.FromSeconds(90));
        Assert.True(hub.IsWaiting("calcom"));

        _time.Advance(TimeSpan.FromSeconds(91));
        Assert.False(hub.IsWaiting("calcom"));
    }

    [Fact]
    public void WaitFor_NoRetryAfter_WaitsSixtySeconds()
    {
        var hub = Create();

        hub.WaitFor("calcom", null);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(hub.IsWaiting("calcom"));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(hub.IsWaiting("calcom"));
    }

    // A token store whose provider reads fail the way an unreadable-right-now secrets file does
    private sealed class ThrowingProviderTokenStore : Core.Auth.ITokenStore
    {
        private readonly InMemoryTokenStore _inner = new();

        public bool Fail { get; set; } = true;

        public Core.Auth.OAuthClientCredentials? GetClientCredentials() => _inner.GetClientCredentials();
        public void SetClientCredentials(Core.Auth.OAuthClientCredentials credentials) => _inner.SetClientCredentials(credentials);
        public void ClearClientCredentials() => _inner.ClearClientCredentials();
        public string? GetRefreshToken(string accountId) => _inner.GetRefreshToken(accountId);
        public void SetRefreshToken(string accountId, string refreshToken) => _inner.SetRefreshToken(accountId, refreshToken);
        public void RemoveRefreshToken(string accountId) => _inner.RemoveRefreshToken(accountId);
        public IReadOnlyList<string> GetAccountIds() => _inner.GetAccountIds();
        public string? GetProviderSecret(string providerId, string key) =>
            Fail ? throw Core.Auth.SecretsUnavailable.Create("test", new IOException()) : _inner.GetProviderSecret(providerId, key);
        public void SetProviderSecret(string providerId, string key, string value) => _inner.SetProviderSecret(providerId, key, value);
        public void RemoveProviderSecrets(string providerId) => _inner.RemoveProviderSecrets(providerId);
    }
}
