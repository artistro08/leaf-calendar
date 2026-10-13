using System.Net;
using System.Net.Sockets;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Http;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SchedulingHubTests : IDisposable
{
    private const string Api = "https://api.cal.com/";
    private const string TokenUrl = "https://api.cal.com/v2/auth/oauth2/token";
    private const string Fake = "http://127.0.0.1:5000/";

    private static readonly HttpClient Browser = new();

    private readonly TempFolder _folder = new();
    private readonly FakeHttpHandler _cal = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero));

    // Never Cal.com's real port 53817: the owner's Leaf may be signing in on it
    private readonly SchedulingProviderInfo _calcom = SchedulingProviders.Find("calcom")! with
    {
        OAuth = CalComProvider.OAuth with { RedirectPort = FreePort() },
    };

    public void Dispose()
    {
        _cal.Dispose();
        _folder.Dispose();
    }

    private SchedulingHub Create(Uri? fakeRoot = null, ITokenStore? tokens = null) =>
        new(new HttpClient(_cal), tokens ?? _tokens, new AppLog(_folder.Path, _time), _time, fakeRoot) { Providers = [_calcom] };

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Answer(string accessToken, string? refreshToken = null) =>
        refreshToken is null
            ? $$"""{"access_token":"{{accessToken}}","expires_in":1800}"""
            : $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":1800}""";

    private void LinksAnswer(string baseUrl = Api) =>
        _cal.On(HttpMethod.Get, baseUrl + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));

    private void TokenAnswer(string grant, string body, string tokenUrl = TokenUrl) =>
        _cal.On(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri == tokenUrl && r.Form("grant_type") == grant, _ => FakeHttpHandler.Json(HttpStatusCode.OK, body));

    private void SaveClient(ITokenStore? tokens = null)
    {
        (tokens ?? _tokens).SetProviderSecret("calcom", "client_id", "client-123");
        (tokens ?? _tokens).SetProviderSecret("calcom", "client_secret", "secret-1");
    }

    private void SaveSignIn(ITokenStore? tokens = null)
    {
        SaveClient(tokens);
        (tokens ?? _tokens).SetProviderSecret("calcom", "refresh_token", "rt-1");
    }

    private int LinkRequests => _cal.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/v2/event-types", StringComparison.Ordinal));

    // Acts like Cal.com + the browser: approves at once by hitting the loopback redirect with a code and the state
    private static Func<Uri, Task> Approves(List<Uri>? opened = null) => url =>
    {
        opened?.Add(url);
        var query = QueryString.Parse(url.Query);
        var redirect = new Uri(query["redirect_uri"]);
        _ = Task.Run(() => Browser.GetAsync(new Uri(redirect, $"?code=auth-code-1&state={Uri.EscapeDataString(query["state"])}")));
        return Task.CompletedTask;
    };

    [Fact]
    public void StateOf_NothingSaved_NoClient() =>
        Assert.Equal(ProviderState.NoClient, Create().StateOf("calcom"));

    [Fact]
    public void ProviderClient_ToString_LeavesOutTheSecret()
    {
        var text = new ProviderClient("client-123", "secret-1").ToString();

        Assert.DoesNotContain("secret-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveClient_TrimsSavesAndIsReadyToSignIn()
    {
        var hub = Create();
        var changed = 0;
        hub.Changed += (_, _) => changed++;

        hub.SaveClient("calcom", new ProviderClient(" client-123 ", "secret-1\r\n"));

        Assert.Equal("client-123", _tokens.GetProviderSecret("calcom", "client_id"));
        Assert.Equal("secret-1", _tokens.GetProviderSecret("calcom", "client_secret"));
        Assert.Equal(ProviderState.NotSignedIn, hub.StateOf("calcom"));
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData("", "secret-1")]
    [InlineData("client-123", " ")]
    public void SaveClient_EmptyValue_ThrowsAndSavesNothing(string clientId, string clientSecret)
    {
        var hub = Create();

        Assert.Throws<ArgumentException>(() => hub.SaveClient("calcom", new ProviderClient(clientId, clientSecret)));

        Assert.Null(_tokens.GetProviderSecret("calcom", "client_id"));
        Assert.Equal(ProviderState.NoClient, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task SignInAsync_Approved_ListsLinksThenSavesRefreshToken()
    {
        TokenAnswer("authorization_code", Answer("at-0", "rt-1"));
        TokenAnswer("refresh_token", Answer("at-1"));
        LinksAnswer();
        SaveClient();
        var hub = Create();
        hub.Load();

        var links = await hub.SignInAsync("calcom", Approves(), TestContext.Current.CancellationToken);

        Assert.Equal(2, links.Count);
        Assert.Equal("rt-1", _tokens.GetProviderSecret("calcom", "refresh_token"));
        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
        Assert.NotNull(hub.Get("calcom"));
        Assert.Equal("at-1", _cal.Last.BearerToken);
    }

    [Fact]
    public async Task SignInAsync_LinksRefused_SavesNothing()
    {
        TokenAnswer("authorization_code", Answer("at-0", "rt-1"));
        TokenAnswer("refresh_token", Answer("at-1"));
        _cal.On(HttpMethod.Get, Api + "v2/event-types", HttpStatusCode.Unauthorized, "{}");
        SaveClient();
        var hub = Create();
        hub.Load();

        await Assert.ThrowsAsync<SchedulingAuthException>(() => hub.SignInAsync("calcom", Approves(), TestContext.Current.CancellationToken));

        Assert.Null(_tokens.GetProviderSecret("calcom", "refresh_token"));
        Assert.Equal(ProviderState.NotSignedIn, hub.StateOf("calcom"));
        Assert.Null(hub.Get("calcom"));
    }

    [Fact]
    public async Task SignInAsync_RefreshTokenRotatedWhileListing_SavesOnlyTheNewOne()
    {
        TokenAnswer("authorization_code", Answer("at-0", "rt-1"));
        TokenAnswer("refresh_token", Answer("at-1", "rt-2"));
        LinksAnswer();
        var tokens = new RecordingTokenStore();
        SaveClient(tokens);
        var hub = Create(tokens: tokens);
        hub.Load();

        await hub.SignInAsync("calcom", Approves(), TestContext.Current.CancellationToken);

        Assert.Equal(["rt-2"], tokens.SavedRefreshTokens);
    }

    [Fact]
    public async Task SignInAsync_NoClient_ThrowsWithoutOpeningTheBrowser()
    {
        var opened = new List<Uri>();
        var hub = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SignInAsync("calcom", Approves(opened), TestContext.Current.CancellationToken));

        Assert.Empty(opened);
        Assert.Empty(_cal.Requests);
    }

    [Fact]
    public async Task SignInAsync_AfterNeedsReconnect_SignedInAgain()
    {
        _cal.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", once: true);
        TokenAnswer("authorization_code", Answer("at-0", "rt-9"));
        TokenAnswer("refresh_token", Answer("at-1"));
        LinksAnswer();
        SaveSignIn();
        var hub = Create();
        hub.Load();
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProviderState.NeedsReconnect, hub.StateOf("calcom"));

        await hub.SignInAsync("calcom", Approves(), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
        Assert.Equal("rt-9", _tokens.GetProviderSecret("calcom", "refresh_token"));
    }

    [Fact]
    public async Task SignInAsync_FakeRoot_UsesTheFakesEndpointsAndTheFixedRedirect()
    {
        TokenAnswer("authorization_code", Answer("at-0", "rt-1"), Fake + "calcom/v2/auth/oauth2/token");
        TokenAnswer("refresh_token", Answer("at-1"), Fake + "calcom/v2/auth/oauth2/token");
        LinksAnswer(Fake + "calcom/");
        SaveClient();
        var hub = Create(new Uri(Fake));
        hub.Load();
        var opened = new List<Uri>();

        await hub.SignInAsync("calcom", Approves(opened), TestContext.Current.CancellationToken);

        var authorize = Assert.Single(opened);
        Assert.Equal(Fake + "calcom/auth/oauth2/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal($"http://127.0.0.1:{_calcom.OAuth.RedirectPort}/calcom/callback", QueryString.Parse(authorize.Query)["redirect_uri"]);
        Assert.All(_cal.Requests, r => Assert.StartsWith(Fake + "calcom/", r.Uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.Equal(3, _cal.Requests.Count);
    }

    [Fact]
    public void Load_ClientOnly_NotSignedIn()
    {
        SaveClient();
        var hub = Create();

        hub.Load();

        Assert.Equal(ProviderState.NotSignedIn, hub.StateOf("calcom"));
        Assert.Null(hub.Get("calcom"));
        Assert.Empty(_cal.Requests);
    }

    [Fact]
    public async Task Load_SavedRefreshToken_SignedInAndRefreshesOnFirstCall()
    {
        TokenAnswer("refresh_token", Answer("at-1"));
        LinksAnswer();
        SaveSignIn();
        var hub = Create();

        hub.Load();
        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
        Assert.Empty(_cal.Requests);

        Assert.Equal(2, (await hub.GetLinksAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Equal("rt-1", _cal.Requests[0].Form("refresh_token"));
        Assert.Equal("at-1", _cal.Last.BearerToken);
    }

    [Fact]
    public async Task Load_FakeRoot_UsesProviderFolderOfFake()
    {
        TokenAnswer("refresh_token", Answer("at-1"), Fake + "calcom/v2/auth/oauth2/token");
        LinksAnswer(Fake + "calcom/");
        SaveSignIn();
        var hub = Create(new Uri(Fake));

        hub.Load();
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith(Fake + "calcom/v2/event-types", _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SecretsUnavailable_KeepsNothingAndRetriesLater()
    {
        var tokens = new ThrowingProviderTokenStore();
        var hub = Create(tokens: tokens);

        hub.Load();
        Assert.Equal(ProviderState.NoClient, hub.StateOf("calcom"));

        tokens.Fail = false;
        SaveSignIn(tokens);
        hub.Load();
        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
    }

    [Fact]
    public void Load_SecretsUnavailableAfterSignIn_KeepsSignedIn()
    {
        var tokens = new ThrowingProviderTokenStore { Fail = false };
        SaveSignIn(tokens);
        var hub = Create(tokens: tokens);
        hub.Load();

        tokens.Fail = true;
        hub.Load();

        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
        Assert.NotNull(hub.Get("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_TokenRevoked_MarksNeedsReconnect()
    {
        TokenAnswer("refresh_token", Answer("at-1"));
        _cal.On(HttpMethod.Get, Api + "v2/event-types", HttpStatusCode.Unauthorized, "{}");
        SaveSignIn();
        var hub = Create();
        hub.Load();

        Assert.Empty(await hub.GetLinksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderState.NeedsReconnect, hub.StateOf("calcom"));
        Assert.Empty(hub.Connected);
    }

    [Fact]
    public async Task GetLinksAsync_RefreshRefused_MarksNeedsReconnectAndKeepsSecrets()
    {
        _cal.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        SaveSignIn();
        var hub = Create();
        hub.Load();

        Assert.Empty(await hub.GetLinksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ProviderState.NeedsReconnect, hub.StateOf("calcom"));
        Assert.Equal(0, LinkRequests);
        Assert.Equal("rt-1", _tokens.GetProviderSecret("calcom", "refresh_token"));

        // Stays that way on a later load
        hub.Load();
        Assert.Equal(ProviderState.NeedsReconnect, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_RefreshTokenRotated_SavesIt()
    {
        TokenAnswer("refresh_token", Answer("at-1", "rt-2"));
        LinksAnswer();
        SaveSignIn();
        var hub = Create();
        hub.Load();

        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.Equal("rt-2", _tokens.GetProviderSecret("calcom", "refresh_token"));
    }

    [Fact]
    public async Task GetLinksAsync_RotatedTokenCantBeSaved_StillAnswers()
    {
        TokenAnswer("refresh_token", Answer("at-1", "rt-2"));
        LinksAnswer();
        var tokens = new ThrowingProviderTokenStore { Fail = false };
        SaveSignIn(tokens);
        var hub = Create(tokens: tokens);
        hub.Load();
        tokens.FailWrites = true;

        Assert.Equal(2, (await hub.GetLinksAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_Unavailable_KeepsCachedLinks()
    {
        TokenAnswer("refresh_token", Answer("at-1"));
        _cal.On(HttpMethod.Get, Api + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"), once: true);
        _cal.On(HttpMethod.Get, Api + "v2/event-types", HttpStatusCode.InternalServerError, "{}");
        SaveSignIn();
        var hub = Create();
        hub.Load();
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(6));
        var links = await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, LinkRequests);
        Assert.Equal(2, links.Count);
        Assert.Equal(ProviderState.SignedIn, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_Cached_ForFiveMinutes()
    {
        TokenAnswer("refresh_token", Answer("at-1"));
        LinksAnswer();
        SaveSignIn();
        var hub = Create();
        hub.Load();

        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, LinkRequests);

        _time.Advance(TimeSpan.FromMinutes(6));
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, LinkRequests);
    }

    [Fact]
    public void SignOut_RemovesRefreshTokenOnly()
    {
        SaveSignIn();
        var hub = Create();
        hub.Load();

        hub.SignOut("calcom");

        Assert.True(string.IsNullOrEmpty(_tokens.GetProviderSecret("calcom", "refresh_token")));
        Assert.Equal("client-123", _tokens.GetProviderSecret("calcom", "client_id"));
        Assert.Equal("secret-1", _tokens.GetProviderSecret("calcom", "client_secret"));
        Assert.Equal(ProviderState.NotSignedIn, hub.StateOf("calcom"));
        Assert.Null(hub.Get("calcom"));

        hub.Load();
        Assert.Equal(ProviderState.NotSignedIn, hub.StateOf("calcom"));
    }

    [Fact]
    public void RemoveClient_RemovesEverySecret()
    {
        SaveSignIn();
        var hub = Create();
        hub.Load();

        hub.RemoveClient("calcom");

        Assert.Null(_tokens.GetProviderSecret("calcom", "client_id"));
        Assert.Null(_tokens.GetProviderSecret("calcom", "client_secret"));
        Assert.Null(_tokens.GetProviderSecret("calcom", "refresh_token"));
        Assert.Equal(ProviderState.NoClient, hub.StateOf("calcom"));
        Assert.Empty(hub.Connected);
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

    // A token store whose provider reads (and, when asked, writes) fail the way an unreadable-right-now secrets file does
    private sealed class ThrowingProviderTokenStore : ITokenStore
    {
        private readonly InMemoryTokenStore _inner = new();

        public bool Fail { get; set; } = true;

        public bool FailWrites { get; set; }

        public OAuthClientCredentials? GetClientCredentials() => _inner.GetClientCredentials();
        public void SetClientCredentials(OAuthClientCredentials credentials) => _inner.SetClientCredentials(credentials);
        public void ClearClientCredentials() => _inner.ClearClientCredentials();
        public string? GetRefreshToken(string accountId) => _inner.GetRefreshToken(accountId);
        public void SetRefreshToken(string accountId, string refreshToken) => _inner.SetRefreshToken(accountId, refreshToken);
        public void RemoveRefreshToken(string accountId) => _inner.RemoveRefreshToken(accountId);
        public IReadOnlyList<string> GetAccountIds() => _inner.GetAccountIds();
        public string? GetProviderSecret(string providerId, string key) =>
            Fail ? throw SecretsUnavailable.Create("test", new IOException()) : _inner.GetProviderSecret(providerId, key);

        public void SetProviderSecret(string providerId, string key, string value)
        {
            if (FailWrites)
            {
                throw SecretsUnavailable.Create("test", new IOException());
            }

            _inner.SetProviderSecret(providerId, key, value);
        }

        public void RemoveProviderSecrets(string providerId) => _inner.RemoveProviderSecrets(providerId);
    }

    // A token store that records every refresh token written, in order
    private sealed class RecordingTokenStore : ITokenStore
    {
        private readonly InMemoryTokenStore _inner = new();

        public List<string> SavedRefreshTokens { get; } = [];

        public OAuthClientCredentials? GetClientCredentials() => _inner.GetClientCredentials();
        public void SetClientCredentials(OAuthClientCredentials credentials) => _inner.SetClientCredentials(credentials);
        public void ClearClientCredentials() => _inner.ClearClientCredentials();
        public string? GetRefreshToken(string accountId) => _inner.GetRefreshToken(accountId);
        public void SetRefreshToken(string accountId, string refreshToken) => _inner.SetRefreshToken(accountId, refreshToken);
        public void RemoveRefreshToken(string accountId) => _inner.RemoveRefreshToken(accountId);
        public IReadOnlyList<string> GetAccountIds() => _inner.GetAccountIds();
        public string? GetProviderSecret(string providerId, string key) => _inner.GetProviderSecret(providerId, key);

        public void SetProviderSecret(string providerId, string key, string value)
        {
            if (key == "refresh_token")
            {
                SavedRefreshTokens.Add(value);
            }

            _inner.SetProviderSecret(providerId, key, value);
        }

        public void RemoveProviderSecrets(string providerId) => _inner.RemoveProviderSecrets(providerId);
    }
}
