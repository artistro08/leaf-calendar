using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// A tiny loopback HTTP server that plays Google for UI tests. It serves the recorded fixtures for
/// sign-in, user info, calendar list, and events (full sync in two pages; incremental syncs return
/// no changes). Start the app with <c>--fake-google {BaseUri}</c>.
/// </summary>
public sealed class FakeGoogleServer : IDisposable
{
    const string PrimaryId = "leaf.tester@gmail.com";

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    int _revokes;

    /// <summary>Starts listening on a random loopback port.</summary>
    public FakeGoogleServer()
    {
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}/");
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Root address to pass to <c>--fake-google</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>Every request seen, as <c>"GET /path?query"</c>.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>How many token revocations were requested.</summary>
    public int RevokeCount => Volatile.Read(ref _revokes);

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, target, body) = await ReadRequestAsync(stream);
                Requests.Enqueue($"{method} {target}");

                var (status, content, location) = Route(method, target, body);
                var bytes  = Encoding.UTF8.GetBytes(content);
                var header = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {(status == 302 ? "Found" : status == 200 ? "OK" : "Not Found")}\r\n")
                    .Append("Content-Type: application/json\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Length: {bytes.Length}\r\n")
                    .Append(location is null ? "" : $"Location: {location}\r\n")
                    .Append("Connection: close\r\n\r\n")
                    .ToString();

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(bytes);
            }
            catch (IOException)
            {
                // Client went away; nothing to do in a test fake.
            }
        }
    }

    (int Status, string Content, string? Location) Route(string method, string target, string body)
    {
        var uri   = new Uri(BaseUri, target);
        var path  = uri.AbsolutePath;
        var query = QueryString.Parse(uri.Query);

        // Sign-In
        if (method == "GET" && path == "/auth")
        {
            var redirect = $"{query["redirect_uri"]}?code=fake-code&state={Uri.EscapeDataString(query["state"])}";
            return (302, "", redirect);
        }

        if (method == "POST" && path == "/token")
        {
            var grant = QueryString.Parse(body).GetValueOrDefault("grant_type");
            return (200, Read(grant == "authorization_code" ? "token-response.json" : "token-refresh.json"), null);
        }

        if (method == "POST" && path == "/revoke")
        {
            Interlocked.Increment(ref _revokes);
            return (200, "{}", null);
        }

        if (method == "GET" && path == "/userinfo")
        {
            return (200, Read("userinfo.json"), null);
        }

        // Calendar
        if (method == "GET" && path == "/calendar/v3/users/me/calendarList")
        {
            return (200, Read("calendar-list.json"), null);
        }

        const string eventsPrefix = "/calendar/v3/calendars/";
        if (method == "GET" && path.StartsWith(eventsPrefix, StringComparison.Ordinal) && path.EndsWith("/events", StringComparison.Ordinal))
        {
            var calendarId = Uri.UnescapeDataString(path[eventsPrefix.Length..^"/events".Length]);
            if (calendarId != PrimaryId)
            {
                return (200, Read("events-empty.json"), null);
            }

            if (query.ContainsKey("syncToken"))
            {
                return (200, """{"items":[],"nextSyncToken":"sync-token-1"}""", null);
            }

            return (200, Read(query.GetValueOrDefault("pageToken") == "page-2" ? "events-page2.json" : "events-page1.json"), null);
        }

        return (404, """{"error":{"code":404,"message":"No fake route"}}""", null);
    }

    string Read(string name) => File.ReadAllText(Path.Combine(_fixtures, name));

    static async Task<(string Method, string Target, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[65536];
        var length = 0;
        int headerEnd;

        // Headers
        while ((headerEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0 || (length += read) == buffer.Length)
            {
                throw new IOException("Bad request.");
            }
        }

        var head  = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        var contentLength = lines
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture))
            .FirstOrDefault();

        // Body
        var bodyStart = headerEnd + 4;
        while (length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return (parts[0], parts[1], Encoding.UTF8.GetString(buffer, bodyStart, Math.Max(0, length - bodyStart)));
    }
}
