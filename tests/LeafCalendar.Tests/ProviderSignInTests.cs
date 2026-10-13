using System.Net;
using System.Net.Sockets;
using LeafCalendar.Core.Http;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class ProviderSignInTests : IDisposable
{
    private const string TokenUrl = "https://api.example.test/oauth2/token";
    private const string Secret = "secret-do-not-leak";
    private const string TokenAnswer = """{"access_token":"at-1","refresh_token":"rt-1","expires_in":1800,"scope":"BOOKING_READ"}""";

    private static readonly HttpClient Browser = new();

    private readonly FakeHttpHandler _provider = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly SchedulingOAuth _oauth;

    public ProviderSignInTests()
    {
        // Never Cal.com's real port 53817: the owner's Leaf may be signing in on it
        _oauth = new SchedulingOAuth(
            new Uri("https://app.example.test/auth/oauth2/authorize"),
            new Uri(TokenUrl),
            ["EVENT_TYPE_READ", "BOOKING_READ"],
            FreePort(),
            "/calcom/callback",
            new Uri("https://app.example.test/settings/developer/oauth"));
    }

    public void Dispose() => _provider.Dispose();

    private ProviderSignIn Create(Func<Uri, Task> openBrowser) =>
        new(new HttpClient(_provider), _oauth, new ProviderClient("client-123", Secret), openBrowser, _time);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Acts like the provider + the browser: reads the authorize URL and hits the loopback redirect.
    private static Func<Uri, Task> Redirects(Func<IReadOnlyDictionary<string, string>, string> replyQuery) => url =>
    {
        var query = QueryString.Parse(url.Query);
        var redirect = new Uri(query["redirect_uri"]);
        _ = Task.Run(() => Browser.GetAsync(new Uri(redirect, "?" + replyQuery(query))));
        return Task.CompletedTask;
    };

    private static string Approve(IReadOnlyDictionary<string, string> q) => $"code=auth-code-1&state={Uri.EscapeDataString(q["state"])}";

    [Fact]
    public async Task RunAsync_UserApproves_SendsEveryParameterAndExchangesWithTheSecret()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, TokenAnswer);
        Uri? authorize = null;
        var redirect = Redirects(Approve);

        var refreshToken = await Create(url => { authorize = url; return redirect(url); }).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("rt-1", refreshToken);

        // Authorize URL
        var q = QueryString.Parse(authorize!.Query);
        Assert.Equal("https://app.example.test/auth/oauth2/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("client-123", q["client_id"]);
        Assert.Equal($"http://127.0.0.1:{_oauth.RedirectPort}/calcom/callback", q["redirect_uri"]);
        Assert.Equal("EVENT_TYPE_READ BOOKING_READ", q["scope"]);
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.Equal(5, q.Count);

        // Exchange body, exactly
        var exchange = _provider.Requests.Single();
        Assert.Equal(TokenUrl, exchange.Uri.AbsoluteUri);
        var form = QueryString.Parse(exchange.Body!);
        Assert.Equal(["client_id", "client_secret", "code", "grant_type", "redirect_uri"], form.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("auth-code-1", form["code"]);
        Assert.Equal(q["redirect_uri"], form["redirect_uri"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.Equal(Secret, form["client_secret"]);

        // Nothing secret in the authorize URL besides the client ID
        Assert.DoesNotContain(Secret, authorize.AbsoluteUri, StringComparison.Ordinal);
        Assert.Null(exchange.BearerToken);
    }

    [Fact]
    public async Task RunAsync_UserDenies_ThrowsAuth()
    {
        await Assert.ThrowsAsync<SchedulingAuthException>(
            () => Create(Redirects(q => $"error=access_denied&state={Uri.EscapeDataString(q["state"])}")).RunAsync(TestContext.Current.CancellationToken));

        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task RunAsync_EmptyCode_ThrowsAuth()
    {
        await Assert.ThrowsAsync<SchedulingAuthException>(
            () => Create(Redirects(q => $"code=&state={Uri.EscapeDataString(q["state"])}")).RunAsync(TestContext.Current.CancellationToken));

        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RunAsync_ExchangeRefused_ThrowsAuth(HttpStatusCode status)
    {
        _provider.On(HttpMethod.Post, TokenUrl, status, """{"error":"invalid_client"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create(Redirects(Approve)).RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_ExchangeServerError_ThrowsUnavailable()
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadGateway, "");

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create(Redirects(Approve)).RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_Offline_ThrowsUnavailable()
    {
        _provider.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create(Redirects(Approve)).RunAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"access_token":"at-1","expires_in":1800}""")]
    [InlineData("""{"refresh_token":"rt-1","expires_in":1800}""")]
    public async Task RunAsync_AnswerMissingAToken_ThrowsAuth(string answer)
    {
        _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, answer);

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create(Redirects(Approve)).RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_PortBusy_ThrowsPortBusy()
    {
        var holder = new TcpListener(IPAddress.Loopback, _oauth.RedirectPort);
        holder.Start();
        try
        {
            var opened = false;

            await Assert.ThrowsAsync<SchedulingPortBusyException>(
                () => Create(_ => { opened = true; return Task.CompletedTask; }).RunAsync(TestContext.Current.CancellationToken));

            Assert.False(opened);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public async Task RunAsync_NoReplyWithinFiveMinutes_TimesOut()
    {
        var run = Create(_ => Task.CompletedTask).RunAsync(TestContext.Current.CancellationToken);

        _time.Advance(ProviderSignIn.Timeout);

        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Equal(TimeSpan.FromMinutes(5), ProviderSignIn.Timeout);
    }

    [Fact]
    public void CalComOAuth_MatchesTheSpec()
    {
        var oauth = CalComProvider.OAuth;

        Assert.Equal("https://app.cal.com/auth/oauth2/authorize", oauth.Authorize.AbsoluteUri);
        Assert.Equal("https://api.cal.com/v2/auth/oauth2/token", oauth.Token.AbsoluteUri);
        Assert.Equal(["EVENT_TYPE_READ", "BOOKING_READ", "BOOKING_WRITE", "SCHEDULE_READ"], oauth.Scopes);
        Assert.Equal(53817, oauth.RedirectPort);
        Assert.Equal("/calcom/callback", oauth.RedirectPath);
        Assert.Equal("https://app.cal.com/settings/developer/oauth", oauth.ClientPage.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"error\":\"invalid_client\",\"error_description\":\"invalid_client_credentials\"}", "invalid_client invalid_client_credentials")]
    [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"BadRequestException\",\"message\":\"secret abc\"}}", "BadRequestException")]
    [InlineData("{\"error\":\"bad <script> value!\"}", "badscriptvalue")]
    [InlineData("not json", "")]
    [InlineData("[1]", "")]
    public void ErrorCodes_KeepsOnlyTheCodes(string body, string expected)
    {
        Assert.Equal(expected, SchedulingTokenRequest.ErrorCodes(body));
    }
}
