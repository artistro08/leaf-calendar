using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class GoogleOAuthClientTests : IDisposable
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    private static readonly OAuthClientCredentials Credentials = new("123-abc.apps.googleusercontent.com", "GOCSPX-test-secret");

    private readonly FakeHttpHandler _google = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    private GoogleOAuthClient CreateClient() => new(new HttpClient(_google), Credentials, _time);

    void IDisposable.Dispose()
    {
        _google.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void BuildAuthorizationUrl_Called_IncludesPkceOfflineAndScopes()
    {
        var url = CreateClient().BuildAuthorizationUrl(new Uri("http://127.0.0.1:5000/"), "state-1", "challenge-1", "me@example.com");
        var query = QueryString.Parse(url.Query);

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(Credentials.ClientId, query["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("me@example.com", query["login_hint"]);
        Assert.Contains(GoogleOAuthClient.CalendarScope, query["scope"].Split(' '));
        Assert.DoesNotContain(Credentials.ClientSecret, url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAuthorizationUrl_IncludesGrantedScopes()
    {
        var url = CreateClient().BuildAuthorizationUrl(new Uri("http://127.0.0.1:5000/"), "state-1", "challenge-1");
        var query = QueryString.Parse(url.Query);

        Assert.Equal("true", query["include_granted_scopes"]);
        Assert.Equal(
            "openid email profile https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/contacts.readonly https://www.googleapis.com/auth/contacts.other.readonly https://www.googleapis.com/auth/directory.readonly",
            query["scope"]);
        Assert.Equal(GoogleOAuthClient.ContactsScope, GoogleOAuthClient.Scopes[4]);
        Assert.Equal(GoogleOAuthClient.OtherContactsScope, GoogleOAuthClient.Scopes[5]);
    }

    [Fact]
    public async Task ExchangeCodeAsync_Success_ReturnsTokensWithExpiry()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-response.json"));

        var tokens = await CreateClient().ExchangeCodeAsync("4/code", "verifier-1", new Uri("http://127.0.0.1:5000/"), TestContext.Current.CancellationToken);

        var request = Assert.Single(_google.Requests);
        Assert.Equal("authorization_code", request.Form("grant_type"));
        Assert.Equal("4/code", request.Form("code"));
        Assert.Equal("verifier-1", request.Form("code_verifier"));
        Assert.Equal("http://127.0.0.1:5000/", request.Form("redirect_uri"));
        Assert.Equal(Credentials.ClientSecret, request.Form("client_secret"));
        Assert.Equal("ya29.test-access-token", tokens.AccessToken);
        Assert.Equal("1//test-refresh-token", tokens.RefreshToken);
        Assert.Equal(_time.GetUtcNow().AddSeconds(3599), tokens.ExpiresAt);
        Assert.True(tokens.HasScope(GoogleOAuthClient.CalendarScope));
    }

    [Fact]
    public async Task RefreshAsync_InvalidGrant_ThrowsInvalidGrantException()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json"));

        await Assert.ThrowsAsync<InvalidGrantException>(() => CreateClient().RefreshAsync("1//old", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_Success_SendsRefreshGrant()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));

        var tokens = await CreateClient().RefreshAsync("1//test-refresh-token", TestContext.Current.CancellationToken);

        Assert.Equal("refresh_token", _google.Requests[0].Form("grant_type"));
        Assert.Equal("1//test-refresh-token", _google.Requests[0].Form("refresh_token"));
        Assert.Equal("ya29.test-refreshed-token", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
    }

    [Fact]
    public async Task RevokeAsync_AlreadyInvalid_DoesNotThrow()
    {
        _google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.BadRequest, """{"error":"invalid_token"}""");

        await CreateClient().RevokeAsync("1//gone", TestContext.Current.CancellationToken);

        Assert.Equal("1//gone", _google.Requests[0].Form("token"));
    }

    [Fact]
    public async Task GetUserInfoAsync_Success_SendsBearerAndReadsUser()
    {
        _google.On(HttpMethod.Get, UserInfoUrl, HttpStatusCode.OK, Fixture.Read("userinfo.json"));

        var user = await CreateClient().GetUserInfoAsync("ya29.test-access-token", TestContext.Current.CancellationToken);

        Assert.Equal("ya29.test-access-token", _google.Requests[0].BearerToken);
        Assert.Equal("109876543210", user.Sub);
        Assert.Equal("leaf.tester@gmail.com", user.Email);
    }

    [Fact]
    public void TokenSet_ToString_OmitsTokens()
    {
        var text = new TokenSet("ya29.secret", _time.GetUtcNow(), "1//secret", "").ToString();

        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }
}
