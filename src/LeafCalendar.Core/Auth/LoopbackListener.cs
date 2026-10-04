using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Receives Google's OAuth redirect on <c>http://127.0.0.1:&lt;random port&gt;/</c>.
/// </summary>
/// <remarks>
/// Binds the loopback interface only, so nothing else on the network can reach it. It answers
/// unrelated requests (favicon, bare <c>/</c>, requests without <c>code</c> or <c>error</c> in the query, a
/// <c>state</c> other than this sign-in's, oversized or malformed input) with 404 and keeps waiting; only a
/// <c>GET /?...</c> with <c>code</c> or <c>error</c> and the expected <c>state</c> ends the wait, so another page
/// hitting the port can't break sign-in. It never echoes tokens or codes into the page.
/// Only the request line is read (up to 8 KB), so any amount of browser headers, such as cookies
/// that local dev servers set for 127.0.0.1, can't stall sign-in. After answering, the listener
/// closes its sending side and briefly reads away the headers it skipped. Closing a socket with
/// unread data makes Windows send a reset, and a browser that sees the reset can drop the page.
/// </remarks>
public sealed class LoopbackListener : IDisposable
{
    private const int MaxRequestLineBytes = 8192;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    // Google signed in, or answered with an error (Cancel, access denied). Fixed text only: nothing from the query is
    // echoed. Each page tries the leaf-calendar: link once as it loads (the browser asks first) and also offers it as a
    // link, which works on its own; the link only brings Leaf to the front.
    private const string SignedInPage = "<h1>You're signed in</h1><p>You can close this tab and go back to Leaf Calendar.</p>";
    private const string NotFinishedPage = "<h1>Sign-in didn't finish</h1><p>You can close this tab and go back to Leaf Calendar to try again.</p>";

    private static string Page(string content) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=leaf-calendar:\"><title>Leaf Calendar</title></head>" +
        "<body style=\"font-family:'Segoe UI',sans-serif;padding:48px\">" +
        content +
        "<p><a href=\"leaf-calendar:\">Open Leaf Calendar</a></p></body></html>";

    private readonly TcpListener _listener;

    /// <summary>Starts listening on a random free loopback port.</summary>
    public LoopbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 4);

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
    }

    /// <summary>The redirect URI to send to Google. Always ends with <c>/</c>.</summary>
    public Uri RedirectUri { get; }

    /// <summary>
    /// Waits for the OAuth redirect carrying <paramref name="expectedState"/> and returns its query parameters.
    /// </summary>
    /// <param name="expectedState">The <c>state</c> sent to Google; a redirect with any other state gets a 404.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is canceled.</exception>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();

            // Read Request With A Per-Connection Timeout
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(ReadTimeout);

            string? target;
            try
            {
                target = await ReadTargetAsync(stream, readTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            // Ignore Anything That Isn't The Redirect
            if (target is null || !target.StartsWith("/?", StringComparison.Ordinal))
            {
                await RespondAsync(client, stream, "404 Not Found", "", ct);
                continue;
            }

            // Require Code Or Error Parameter And This Sign-In's State
            var query = QueryString.Parse(target[2..]);
            if ((!query.ContainsKey("code") && !query.ContainsKey("error")) || !Pkce.StateMatches(expectedState, query.GetValueOrDefault("state")))
            {
                await RespondAsync(client, stream, "404 Not Found", "", ct);
                continue;
            }

            await RespondAsync(client, stream, "200 OK", Page(query.ContainsKey("error") ? NotFinishedPage : SignedInPage), ct);
            return query;
        }
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => _listener.Stop();

    // Reads up to the end of the request line (8 KB at most) and returns the GET target, or null for anything else.
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestLineBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                return null;
            }

            length += read;
            var lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                continue;
            }

            var parts = Encoding.ASCII.GetString(buffer, 0, lineEnd).Split(' ');

            return parts is ["GET", var target, var version] && version.StartsWith("HTTP/", StringComparison.Ordinal)
                ? target
                : null;
        }

        return null;
    }

    // Writes the response, closes the sending side, then reads away unread headers until the
    // browser closes (or a second passes), so the browser gets the page instead of a reset
    private static async Task RespondAsync(TcpClient client, NetworkStream stream, string status, string body, CancellationToken ct)
    {
        await TryWriteAsync(stream, status, body, ct);

        try
        {
            client.Client.Shutdown(SocketShutdown.Send);

            using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainTimeout.CancelAfter(DrainTimeout);

            var scratch = new byte[4096];
            while (await stream.ReadAsync(scratch, drainTimeout.Token) > 0)
            {
                // Discard.
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            // Timed out or the browser already hung up; either way the connection closes now.
        }
    }

    private static async Task TryWriteAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Connection: close\r\n\r\n";

        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bodyBytes, ct);
        }
        catch (IOException)
        {
            // Browser closed the connection early; nothing else to do.
        }
    }
}
