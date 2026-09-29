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
/// unrelated requests (favicon, bare <c>/</c>, oversized or malformed input) with 404 and keeps
/// waiting; the first <c>GET /?...</c> ends the wait. It never echoes tokens or codes into the page.
/// </remarks>
public sealed class LoopbackListener : IDisposable
{
    const int MaxHeaderBytes = 8192;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    const string SuccessPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Leaf Calendar</title></head>" +
        "<body style=\"font-family:'Segoe UI',sans-serif;padding:48px\">" +
        "<h1>You're signed in</h1><p>You can close this tab and go back to Leaf Calendar.</p></body></html>";

    readonly TcpListener _listener;

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
    /// Waits for the OAuth redirect and returns its query parameters.
    /// </summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
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
                await TryWriteAsync(stream, "404 Not Found", "", ct);
                continue;
            }

            await TryWriteAsync(stream, "200 OK", SuccessPage, ct);
            return QueryString.Parse(target[2..]);
        }
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => _listener.Stop();

    // Reads the request headers (up to 8 KB) and returns the GET target, or null for anything else.
    static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                return null;
            }

            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            {
                continue;
            }

            var lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
            var parts   = Encoding.ASCII.GetString(buffer, 0, lineEnd).Split(' ');

            return parts is ["GET", var target, var version] && version.StartsWith("HTTP/", StringComparison.Ordinal)
                ? target
                : null;
        }

        return null;
    }

    static async Task TryWriteAsync(NetworkStream stream, string status, string body, CancellationToken ct)
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
