using System.Net;
using LeafCalendar.Core.Http;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class ProviderTokensTests : IDisposable
{
    private const string TokenUrl = "https://api.example.test/oauth2/token";
    private const string Secret = "secret-do-not-leak";

    private readonly FakeHttpHandler _provider = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _saved = [];
    private readonly List<ProviderTokens> _created = [];

    private static readonly SchedulingOAuth OAuth = new(
        new Uri("https://app.example.test/auth/oauth2/authorize"),
        new Uri(TokenUrl),
        ["BOOKING_READ"],
        0,
        "/calcom/callback",
        new Uri("https://app.example.test/settings/developer/oauth"));

    public void Dispose()
    {
        foreach (var tokens in _created)
        {
            tokens.Dispose();
        }

        _provider.Dispose();
    }

    private ProviderTokens Create(string refreshToken = "rt-1")
    {
        var tokens = new ProviderTokens(new HttpClient(_provider), OAuth, new ProviderClient("client-123", Secret), refreshToken, _time, _saved.Add);
        _created.Add(tokens);
        return tokens;
    }

    private static string Answer(string accessToken, string? refreshToken = null) =>
        refreshToken is null
            ? $$"""{"access_token":"{{accessToken}}","expires_in":1800}"""
            : $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":1800}""";

    [Fact]
    public async Task GetAccessTokenAsync_RefreshesWithTheClientAndRefreshToken()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-1"));

        var token = await Create().GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("at-1", token);
        var form = QueryString.Parse(_provider.Requests.Single().Body!);
        Assert.Equal(["client_id", "client_secret", "grant_type", "refresh_token"], form.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt-1", form["refresh_token"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.Equal(Secret, form["client_secret"]);
        Assert.DoesNotContain(Secret, _provider.Requests.Single().Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachedUntilAMinuteBeforeExpiry_ThenRefreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-1"), once: true);
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-2"));
        var tokens = Create();

        Assert.Equal("at-1", await tokens.GetAccessTokenAsync(ct));
        _time.Advance(TimeSpan.FromSeconds(1739));
        Assert.Equal("at-1", await tokens.GetAccessTokenAsync(ct));
        Assert.Single(_provider.Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("at-2", await tokens.GetAccessTokenAsync(ct));
        Assert.Equal(2, _provider.Requests.Count);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoExpiresIn_AssumesThirtyMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, """{"access_token":"at-1"}""", once: true);
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-2"));
        var tokens = Create();

        Assert.Equal("at-1", await tokens.GetAccessTokenAsync(ct));
        _time.Advance(TimeSpan.FromSeconds(1739));
        Assert.Equal("at-1", await tokens.GetAccessTokenAsync(ct));
        Assert.Single(_provider.Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("at-2", await tokens.GetAccessTokenAsync(ct));
    }

    [Fact]
    public async Task GetAccessTokenAsync_TwoCallersAtOnce_ShareOneRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        _provider.On(
            r => r.Uri.AbsoluteUri == TokenUrl,
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                return FakeHttpHandler.Json(HttpStatusCode.OK, Answer("at-1"));
            });
        var tokens = Create();

        var first = Task.Run(() => tokens.GetAccessTokenAsync(ct), ct);
        while (_provider.Requests.Count == 0)
        {
            await Task.Delay(10, ct);
        }

        var second = Task.Run(() => tokens.GetAccessTokenAsync(ct), ct);
        await Task.Delay(100, ct);
        release.Set();

        Assert.Equal(["at-1", "at-1"], await Task.WhenAll(first, second));
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RotatedRefreshToken_IsSavedAndUsedNext()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-1", "rt-2"), once: true);
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-2", "rt-2"));
        var tokens = Create();

        await tokens.GetAccessTokenAsync(ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        await tokens.GetAccessTokenAsync(ct);

        Assert.Equal(["rt-2"], _saved);
        Assert.Equal("rt-2", QueryString.Parse(_provider.Requests[1].Body!)["refresh_token"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetAccessTokenAsync_RefreshRefused_ThrowsAuthOnce(HttpStatusCode status)
    {
        _provider.On(HttpMethod.Post, TokenUrl, status, """{"error":"invalid_grant"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create().GetAccessTokenAsync(TestContext.Current.CancellationToken));

        Assert.Single(_provider.Requests);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AnswerWithoutAccessToken_ThrowsAuth()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, """{"expires_in":1800}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create().GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_ServerError_ThrowsUnavailable()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.ServiceUnavailable, "");

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_RateLimited_ThrowsUnavailableWithRetryAfter()
    {
        _provider.On(
            r => r.Uri.AbsoluteUri == TokenUrl,
            _ =>
            {
                var response = FakeHttpHandler.Json((HttpStatusCode)429, "");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            });

        var ex = await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetAccessTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task GetAccessTokenAsync_Offline_ThrowsUnavailable()
    {
        _provider.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterDispose_ThrowsUnavailable()
    {
        var tokens = Create();
        tokens.Dispose();

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => tokens.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DisposedWhileWaiting_EndsTheWaiter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        _provider.On(
            r => r.Uri.AbsoluteUri == TokenUrl,
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
                return FakeHttpHandler.Json(HttpStatusCode.OK, Answer("at-1"));
            });
        var tokens = Create();

        var first = Task.Run(() => tokens.GetAccessTokenAsync(CancellationToken.None), ct);
        while (_provider.Requests.Count == 0)
        {
            await Task.Delay(10, ct);
        }

        var second = Task.Run(() => tokens.GetAccessTokenAsync(CancellationToken.None), ct);
        await Task.Delay(100, ct);
        tokens.Dispose();
        release.Set();

        await first.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => second.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_Canceled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().GetAccessTokenAsync(cts.Token));
    }

    [Fact]
    public async Task ForgetAccessToken_TokenStillValid_NextCallRefreshes()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-1"));
        var tokens = Create();
        await tokens.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        tokens.ForgetAccessToken();
        await tokens.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _provider.Requests.Count);
    }

    [Fact]
    public async Task GetAccessTokenAsync_PublicClientWithResource_SendsResourceNoSecret()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Answer("at-1"));
        using var tokens = new ProviderTokens(
            new HttpClient(_provider),
            OAuth with { Resource = new Uri("https://api.example.test/mcp") },
            new ProviderClient("public-1", ""),
            "rt-1",
            _time,
            _saved.Add);

        await tokens.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        var form = QueryString.Parse(_provider.Requests.Single().Body!);
        Assert.Equal(["client_id", "grant_type", "refresh_token", "resource"], form.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://api.example.test/mcp", form["resource"]);
    }
}
