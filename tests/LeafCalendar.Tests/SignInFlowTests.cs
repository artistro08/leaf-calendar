using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SignInFlowTests : IDisposable
{
    const string TokenUrl    = "https://oauth2.googleapis.com/token";
    const string RevokeUrl   = "https://oauth2.googleapis.com/revoke";
    const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    static readonly HttpClient Browser = new();

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly InMemoryTokenStore _store = new();
    readonly TestDatabase _db = new();
    readonly TempFolder _logs = new();

    readonly List<AccessTokenProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _db.Dispose();
        _logs.Dispose();
    }

    SignInFlow CreateFlow(Func<Uri, Task> openBrowser)
    {
        var oauth = new GoogleOAuthClient(new HttpClient(_google), new("id.apps.googleusercontent.com", "GOCSPX-test"), _time);
        var tokens = new AccessTokenProvider(oauth, _store, _time);
        _providers.Add(tokens);
        return new SignInFlow(oauth, _store, tokens, _db.Database, openBrowser, _time, new AppLog(_logs.Path, _time));
    }

    // Acts like Google + the browser: reads the consent URL and hits the loopback redirect.
    static Func<Uri, Task> GoogleRedirects(Func<IReadOnlyDictionary<string, string>, string> replyQuery) => consentUrl =>
    {
        var query    = QueryString.Parse(consentUrl.Query);
        var redirect = new Uri(query["redirect_uri"]);
        _ = Task.Run(() => Browser.GetAsync(new Uri(redirect, "?" + replyQuery(query))));
        return Task.CompletedTask;
    };

    static string Approve(IReadOnlyDictionary<string, string> q) => $"code=4%2Fauth-code&state={Uri.EscapeDataString(q["state"])}";

    void GoogleAccepts(string tokenFixture = "token-response.json")
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read(tokenFixture));
        _google.On(HttpMethod.Get, UserInfoUrl, HttpStatusCode.OK, Fixture.Read("userinfo.json"));
        _google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.OK, "{}");
    }

    [Fact]
    public async Task RunAsync_UserApproves_SavesAccountAndRefreshToken()
    {
        GoogleAccepts();
        Uri? consentUrl = null;
        var redirect = GoogleRedirects(Approve);

        var account = await CreateFlow(url => { consentUrl = url; return redirect(url); }).RunAsync(null, TestContext.Current.CancellationToken);

        // Code exchanged with the matching PKCE verifier and redirect
        var consent  = QueryString.Parse(consentUrl!.Query);
        var exchange = _google.Requests.Single(r => r.Uri.AbsoluteUri == TokenUrl);
        Assert.Equal("4/auth-code", exchange.Form("code"));
        Assert.Equal(consent["code_challenge"], Pkce.CreateChallenge(exchange.Form("code_verifier")!));
        Assert.Equal(consent["redirect_uri"], exchange.Form("redirect_uri"));

        // Account saved
        Assert.Equal("109876543210", account.Id);
        Assert.Equal("leaf.tester@gmail.com", account.Email);
        Assert.Equal("1//test-refresh-token", _store.GetRefreshToken("109876543210"));
        using var conn = _db.Database.Open();
        Assert.Equal(account, AccountStore.GetAll(conn).Single());
    }

    [Fact]
    public async Task RunAsync_ForgedStateFirst_IgnoresItAndSignsIn()
    {
        GoogleAccepts();

        // A Forged Reply Hits The Port Before Google's Real One
        Func<Uri, Task> forgedThenReal = consentUrl =>
        {
            var query    = QueryString.Parse(consentUrl.Query);
            var redirect = new Uri(query["redirect_uri"]);
            _ = Task.Run(async () =>
            {
                using var forged = await Browser.GetAsync(new Uri(redirect, "?code=4%2Fstolen&state=forged"));
                using var real   = await Browser.GetAsync(new Uri(redirect, "?" + Approve(query)));
            });
            return Task.CompletedTask;
        };

        var account = await CreateFlow(forgedThenReal).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("109876543210", account.Id);
        Assert.Equal("4/auth-code", _google.Requests.Single(r => r.Uri.AbsoluteUri == TokenUrl).Form("code"));
    }

    [Fact]
    public async Task RunAsync_UserDenies_FailsAsCanceled()
    {
        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(q => $"error=access_denied&state={Uri.EscapeDataString(q["state"])}"))
                .RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("Sign-in was canceled.", error.Message);
    }

    [Fact]
    public async Task RunAsync_CalendarScopeNotGranted_FailsAndSavesNothing()
    {
        GoogleAccepts("token-response-no-calendar.json");

        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(Approve)).RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("calendar access", error.Message, StringComparison.Ordinal);
        Assert.Empty(_store.GetAccountIds());
        Assert.Contains(_google.Requests, r => r.Uri.AbsoluteUri == RevokeUrl);
        using var conn = _db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
    }

    [Fact]
    public async Task RunAsync_TokenEndpointRejectsCode_FailsAndSavesNothing()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json"));

        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(Approve)).RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("Google couldn't complete sign-in. Try again.", error.Message);
        Assert.Empty(_store.GetAccountIds());
        using var conn = _db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
    }

    [Fact]
    public async Task RunAsync_TokenRequestTimesOut_FailsWithSignInException()
    {
        _google.On(r => r.Uri.AbsoluteUri == TokenUrl, _ => throw new TaskCanceledException("timed out", new TimeoutException()));

        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(Approve)).RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("Google couldn't complete sign-in. Try again.", error.Message);
        Assert.Empty(_store.GetAccountIds());
    }

    [Fact]
    public async Task RunAsync_RevokeTimesOut_StillAsksForCalendarAccess()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-response-no-calendar.json"));
        _google.On(r => r.Uri.AbsoluteUri == RevokeUrl, _ => throw new TaskCanceledException("timed out", new TimeoutException()));

        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(Approve)).RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("calendar access", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExpectedAccount_AnotherUserSignsIn_FailsAndSavesNothing()
    {
        GoogleAccepts();

        var error = await Assert.ThrowsAsync<WrongAccountException>(
            () => CreateFlow(GoogleRedirects(Approve)).RunAsync("work‮@example.com", "555", TestContext.Current.CancellationToken));

        // Both addresses come back as plain text, and nothing about the other user is kept (its grant isn't revoked:
        // it may be an account Leaf already holds)
        Assert.Equal("leaf.tester@gmail.com", error.SignedInEmail);
        Assert.Equal("work@example.com", error.ExpectedEmail);
        Assert.Empty(_store.GetAccountIds());
        Assert.DoesNotContain(_google.Requests, r => r.Uri.AbsoluteUri == RevokeUrl);
        using var conn = _db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
    }

    [Fact]
    public async Task RunAsync_ExpectedAccount_SameUser_SavesIt()
    {
        GoogleAccepts();

        var account = await CreateFlow(GoogleRedirects(Approve)).RunAsync("leaf.tester@gmail.com", "109876543210", TestContext.Current.CancellationToken);

        Assert.Equal("109876543210", account.Id);
        Assert.Equal("1//test-refresh-token", _store.GetRefreshToken("109876543210"));
    }

    [Fact]
    public async Task RunAsync_SameAccountTwice_KeepsOneAccountWithLatestToken()
    {
        var ct = TestContext.Current.CancellationToken;
        GoogleAccepts();
        await CreateFlow(GoogleRedirects(Approve)).RunAsync(null, ct);
        _store.SetRefreshToken("109876543210", "1//older");

        await CreateFlow(GoogleRedirects(Approve)).RunAsync(null, ct);

        using var conn = _db.Database.Open();
        Assert.Single(AccountStore.GetAll(conn));
        Assert.Equal("1//test-refresh-token", _store.GetRefreshToken("109876543210"));
    }

    [Fact]
    public async Task RunAsync_NoReplyWithinFiveMinutes_TimesOut()
    {
        var run = CreateFlow(_ => Task.CompletedTask).RunAsync(null, TestContext.Current.CancellationToken);

        _time.Advance(SignInFlow.Timeout);

        // Its own type, so the app can tell a sign-in nobody finished from one the browser came back from
        var error = await Assert.ThrowsAsync<SignInTimeoutException>(() => run);
        Assert.Equal("Sign-in timed out. Try again.", error.Message);
        Assert.IsAssignableFrom<SignInException>(error);
    }
}
