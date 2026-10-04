using System.Net;
using System.Net.Sockets;
using System.Text;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public class LoopbackListenerTests
{
    static readonly HttpClient Http = new();

    [Fact]
    public void RedirectUri_Created_BindsLoopbackOnly()
    {
        using var listener = new LoopbackListener();

        Assert.Equal("127.0.0.1", listener.RedirectUri.Host);
        Assert.Equal("/", listener.RedirectUri.AbsolutePath);
        Assert.True(listener.RedirectUri.Port > 0);
    }

    [Fact]
    public void Constructor_OtherSocketReusesPort_CannotBind()
    {
        using var listener = new LoopbackListener();
        using var hijacker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        hijacker.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // Another local process must not be able to share the port and steal the redirect
        Assert.Throws<SocketException>(() => hijacker.Bind(new IPEndPoint(IPAddress.Loopback, listener.RedirectUri.Port)));
    }

    [Fact]
    public async Task WaitForCallbackAsync_ValidRedirect_ReturnsQueryAndSuccessPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using var response = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);
        var query = await wait;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("You're signed in", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("abc", query["code"]);
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_GoogleReturnsError_ReturnsQueryAndDidNotFinishPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using var response = await Http.GetAsync(new Uri(listener.RedirectUri, "?error=leaf_marker_%3Cb%3E&state=xyz"), ct);
        var query = await wait;
        var page  = await response.Content.ReadAsStringAsync(ct);

        // Canceled Or Refused: Not "Signed In", And Nothing From The Query Is Echoed
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign-in didn't finish", page, StringComparison.Ordinal);
        Assert.Contains("go back to Leaf Calendar to try again", page, StringComparison.Ordinal);
        Assert.DoesNotContain("You're signed in", page, StringComparison.Ordinal);
        Assert.DoesNotContain("leaf_marker", page[page.IndexOf("<body", StringComparison.Ordinal)..], StringComparison.Ordinal); // body only: a local ad blocker may add scripts naming the URL to the head
        Assert.Equal("leaf_marker_<b>", query["error"]);
    }

    [Theory]
    [InlineData("?code=abc&state=xyz")]
    [InlineData("?error=access_denied&state=xyz")]
    public async Task WaitForCallbackAsync_EitherPage_LinksBackToLeaf(string reply)
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using var response = await Http.GetAsync(new Uri(listener.RedirectUri, reply), ct);
        await wait;
        var page = await response.Content.ReadAsStringAsync(ct);

        // The Button Works On Its Own; The Page Also Tries The Link Once When It Loads
        Assert.Contains("<a href=\"leaf-calendar:\"", page, StringComparison.Ordinal);
        Assert.Contains("Open Leaf Calendar", page, StringComparison.Ordinal);
        Assert.Contains("<meta http-equiv=\"refresh\" content=\"0;url=leaf-calendar:\">", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForCallbackAsync_FaviconThenRedirect_IgnoresFavicon()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using var favicon = await Http.GetAsync(new Uri(listener.RedirectUri, "favicon.ico"), ct);
        using var bare = await Http.GetAsync(listener.RedirectUri, ct);
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);

        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.Equal("abc", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_OversizedRequest_RejectsThenAcceptsValidRedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using (var junk = new TcpClient())
        {
            await junk.ConnectAsync(IPAddress.Loopback, listener.RedirectUri.Port, ct);
            var stream = junk.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /?" + new string('a', 9000)), ct);
            var buffer = new byte[64];
            try
            {
                _ = await stream.ReadAsync(buffer, ct);
            }
            catch (IOException)
            {
                // Server closed connection with unread data; expected.
            }
        }
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=ok&state=xyz"), ct);

        Assert.Equal("ok", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_JunkQueryParameters_RejectsThenAcceptsValidRedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using var junk = await Http.GetAsync(new Uri(listener.RedirectUri, "?x=1"), ct);
        Assert.Equal(HttpStatusCode.NotFound, junk.StatusCode);
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);

        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
        Assert.Equal("abc", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_WrongState_RejectsThenAcceptsValidRedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        // Any Page Can Hit The Port; A Forged Reply Must Not End Sign-In
        using var forged    = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=stolen&state=forged"), ct);
        using var stateless = await Http.GetAsync(new Uri(listener.RedirectUri, "?error=access_denied"), ct);
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stateless.StatusCode);
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);

        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
        Assert.Equal("abc", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_ConnectionResetInBacklog_KeepsWaitingForRedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();

        // A Local Caller Connects Then Resets Before The Listener Accepts It
        using (var reset = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await reset.ConnectAsync(IPAddress.Loopback, listener.RedirectUri.Port, ct);
            reset.LingerState = new LingerOption(true, 0);
        }

        await Task.Delay(100, ct);
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);

        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
        Assert.Equal("abc", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_HugeCookieHeader_CompletesWithSuccessPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync("xyz", ct);

        // Browsers send every 127.0.0.1 cookie from local dev servers along with the redirect
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(listener.RedirectUri, "?code=abc&state=xyz"));
        request.Headers.Add("Cookie", "dev=" + new string('c', 16 * 1024));
        using var response = await Http.SendAsync(request, ct);
        var query = await wait.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("You're signed in", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("abc", query["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_Canceled_Throws()
    {
        using var listener = new LoopbackListener();
        using var cts = new CancellationTokenSource();
        var wait = listener.WaitForCallbackAsync("xyz", cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
