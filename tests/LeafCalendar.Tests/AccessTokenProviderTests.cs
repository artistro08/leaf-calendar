using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class AccessTokenProviderTests : IDisposable
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";

    private readonly FakeHttpHandler _google = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryTokenStore _store = new();

    private AccessTokenProvider CreateProvider() =>
        new(new GoogleOAuthClient(new HttpClient(_google), new("id.apps.googleusercontent.com", "secret"), _time), _store, _time);

    [Fact]
    public async Task GetAccessTokenAsync_FreshToken_ReusesCache()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        var provider = CreateProvider();

        var first = await provider.GetAccessTokenAsync("acct", ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.GetAccessTokenAsync("acct", ct);

        Assert.Equal("ya29.test-refreshed-token", first);
        Assert.Equal(first, second);
        Assert.Single(_google.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithinOneMinuteOfExpiry_Refreshes()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        var provider = CreateProvider();
        provider.Seed("acct", new TokenSet("ya29.seeded", _time.GetUtcNow().AddSeconds(50), null, ""));

        var token = await provider.GetAccessTokenAsync("acct", ct);

        Assert.Equal("ya29.test-refreshed-token", token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_GoogleRotatesRefreshToken_SavesNewOne()
    {
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh-rotated.json"));

        await CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken);

        Assert.Equal("1//test-rotated-refresh-token", _store.GetRefreshToken("acct"));
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidGrant_ThrowsNeedsSignIn()
    {
        _store.SetRefreshToken("acct", "1//revoked");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json"));

        var error = await Assert.ThrowsAsync<AccountNeedsSignInException>(
            () => CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("acct", error.AccountId);
    }

    [Theory]
    [InlineData("unauthorized_client")]
    [InlineData("invalid_client")]
    [InlineData("deleted_client")]
    public async Task GetAccessTokenAsync_ClientRejected_ThrowsNeedsSignIn(string code)
    {
        _store.SetRefreshToken("acct", "1//issued-to-the-old-client");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.Unauthorized, $$"""{"error":"{{code}}","error_description":"Unauthorized"}""");

        var error = await Assert.ThrowsAsync<AccountNeedsSignInException>(
            () => CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("acct", error.AccountId);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoRefreshToken_ThrowsNeedsSignIn()
    {
        await Assert.ThrowsAsync<AccountNeedsSignInException>(
            () => CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken).AsTask());

        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task Forget_AfterSeed_ForcesRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        var provider = CreateProvider();
        provider.Seed("acct", new TokenSet("ya29.seeded", _time.GetUtcNow().AddHours(1), null, ""));

        provider.Forget("acct");
        var token = await provider.GetAccessTokenAsync("acct", ct);

        Assert.Equal("ya29.test-refreshed-token", token);
    }

    [Fact]
    public async Task HasScopeAsync_ReadsTheGrantedScopes()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        using var provider = CreateProvider();

        Assert.True(await provider.HasScopeAsync("acct", GoogleOAuthClient.CalendarScope, ct));
        Assert.False(await provider.HasScopeAsync("acct", GoogleOAuthClient.ContactsScope, ct));
        Assert.Single(_google.Requests);
    }

    [Fact]
    public async Task HasScopeAsync_EmptyScopeString_ReturnsTrue()
    {
        using var provider = CreateProvider();
        provider.Seed("acct", new TokenSet("ya29.seeded", _time.GetUtcNow().AddHours(1), null, ""));

        Assert.True(await provider.HasScopeAsync("acct", GoogleOAuthClient.ContactsScope, TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    public void Dispose()
    {
        _google.Dispose();
        GC.SuppressFinalize(this);
    }
}
