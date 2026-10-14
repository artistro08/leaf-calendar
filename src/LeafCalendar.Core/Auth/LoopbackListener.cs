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
    // link, which works on its own; the link only brings Leaf Calendar to the front.
    private const string SignedInPage = "<h1>You're signed in</h1><p>You can close this tab and go back to Leaf Calendar.</p>";
    private const string NotFinishedPage = "<h1>Sign-in didn't finish</h1><p>You can close this tab and go back to Leaf Calendar to try again.</p>";

    // The page around that text: Leaf Calendar's icon, centered on a card in Windows 11's light or dark colors (following the
    // browser's theme), with the link as a button in the icon's green (darker in light mode so white text stays readable)
    private static string Page(string content) =>
        $$"""
        <!doctype html>
        <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="color-scheme" content="light dark">
                <meta http-equiv="refresh" content="0;url=leaf-calendar:">
                <title>Leaf Calendar</title>
                <link rel="icon" href="{{IconDataUri}}">
                <style>
                  :root {
                    --page: #f3f3f3;
                    --card: #ffffff;
                    --stroke: rgba(0, 0, 0, 0.0578);
                    --text: #1a1a1a;
                    --text-secondary: #5d5d5d;
                    --accent: #2c7d00;
                    --accent-hover: #358f05;
                    --on-accent: #ffffff;
                  }
                  @media (prefers-color-scheme: dark) {
                    :root {
                      --page: #202020;
                      --card: #2b2b2b;
                      --stroke: rgba(255, 255, 255, 0.0698);
                      --text: #ffffff;
                      --text-secondary: #c5c5c5;
                      --accent: #3aad01;
                      --accent-hover: #46bd0c;
                      --on-accent: #000000;
                    }
                  }
                  html, body { height: 100%; margin: 0; }
                  body {
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    padding: 16px;
                    box-sizing: border-box;
                    background: var(--page);
                    color: var(--text);
                    font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                  }
                  main {
                    max-width: 400px;
                    padding: 32px;
                    border: 1px solid var(--stroke);
                    border-radius: 8px;
                    background: var(--card);
                    text-align: center;
                  }
                  img { width: 64px; height: 64px; }
                  h1 { margin: 16px 0 8px; font-family: "Segoe UI Variable Display", "Segoe UI", sans-serif; font-size: 28px; font-weight: 600; }
                  p { margin: 0 0 24px; color: var(--text-secondary); font-size: 14px; line-height: 20px; }
                  a {
                    display: inline-block;
                    padding: 6px 16px;
                    border-radius: 4px;
                    background: var(--accent);
                    color: var(--on-accent);
                    font-size: 14px;
                    text-decoration: none;
                    transition: background-color 83ms ease-out;
                  }
                  a:hover { background: var(--accent-hover); }
                  p.action { margin: 0; }
                  a:focus-visible { outline: 2px solid var(--text); outline-offset: 2px; }
                </style>
            </head>
            <body>
                <main>
                    <img src="{{IconDataUri}}" alt="">
                    {{content}}
                    <p class="action"><a href="leaf-calendar:">Open Leaf Calendar</a></p>
                </main>
            </body>
        </html>
        """;

    // Leaf Calendar's 64 px icon (embedded from the App's assets), as a data URI so the page needs nothing else from the listener
    private static readonly string IconDataUri = LoadIcon();

    private static string LoadIcon()
    {
        using var stream = typeof(LoopbackListener).Assembly.GetManifestResourceStream("LeafCalendar.Core.Auth.SignInIcon.png");
        if (stream is null)
        {
            return "";
        }

        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
    }

    private readonly TcpListener _listener;
    private readonly string _path;

    /// <summary>Starts listening on a random free loopback port, at the root path.</summary>
    public LoopbackListener()
        : this(0, "/")
    {
    }

    /// <summary>
    /// Starts listening on a fixed loopback port and path, for providers that match the redirect URI exactly.
    /// </summary>
    /// <param name="port">The loopback port to bind; 0 picks a random free one.</param>
    /// <param name="path">The redirect path, starting with <c>/</c>; no trailing slash is added.</param>
    /// <exception cref="SocketException">With <see cref="SocketError.AddressAlreadyInUse"/> when the port is taken.</exception>
    public LoopbackListener(int port, string path)
    {
        _path = path;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start(backlog: 4);

        var bound = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{bound.ToString(CultureInfo.InvariantCulture)}{path}");
    }

    /// <summary>The redirect URI to send to the provider. Ends with <c>/</c> unless a path was given.</summary>
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
            // A Caller That Reset Before It Was Accepted Fails The Accept On Some Windows Setups; the redirect may still come
            TcpClient accepted;
            try
            {
                accepted = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                continue;
            }

            using var client = accepted;
            NetworkStream connected;
            try
            {
                connected = client.GetStream();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
            {
                continue;
            }

            await using var stream = connected;

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
            if (target is null || !target.StartsWith(_path + "?", StringComparison.Ordinal))
            {
                await RespondAsync(client, stream, "404 Not Found", "", ct);
                continue;
            }

            // Require Code Or Error Parameter And This Sign-In's State
            var query = QueryString.Parse(target[(_path.Length + 1)..]);
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
