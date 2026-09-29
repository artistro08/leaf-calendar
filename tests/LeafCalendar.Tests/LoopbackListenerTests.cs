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
    public async Task WaitForCallbackAsync_ValidRedirect_ReturnsQueryAndSuccessPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(ct);

        using var response = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);
        var query = await wait;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("You're signed in", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("abc", query["code"]);
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_FaviconThenRedirect_IgnoresFavicon()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(ct);

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
        var wait = listener.WaitForCallbackAsync(ct);

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

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=ok&state=s"), ct);

        Assert.Equal("ok", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_JunkQueryParameters_RejectsThenAcceptsValidRedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(ct);

        using var junk = await Http.GetAsync(new Uri(listener.RedirectUri, "?x=1"), ct);
        Assert.Equal(HttpStatusCode.NotFound, junk.StatusCode);
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);

        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
        Assert.Equal("abc", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_Cancelled_Throws()
    {
        using var listener = new LoopbackListener();
        using var cts = new CancellationTokenSource();
        var wait = listener.WaitForCallbackAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
