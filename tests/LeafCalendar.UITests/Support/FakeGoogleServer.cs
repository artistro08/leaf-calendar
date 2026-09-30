using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Http;

namespace LeafCalendar.UITests.Support;

/// <summary>A change Leaf sent to the fake Google.</summary>
public sealed record FakeWrite(string Method, string Path, string Query, string? IfMatch, string Body);

/// <summary>
/// A tiny loopback HTTP server that plays Google for UI tests.
/// </summary>
/// <remarks>
/// Sign-in, user info, and the calendar list come from the recorded fixtures. Events live in memory, seeded from
/// the fixtures (plus <c>events-meeting.json</c>), and behave like Google's:
/// <list type="bullet">
/// <item>Every change gets a new ETag.</item>
/// <item>A write with a stale <c>If-Match</c> gets <c>412</c>, and an insert with an existing ID gets <c>409</c>.</item>
/// <item>An instance ID (<c>{series}_{start}</c>) Google hasn't stored yet is made from its series on first write.</item>
/// <item>Incremental syncs return what changed since <c>sync-token-N</c>.</item>
/// </list>
/// <see cref="Offline"/> drops every connection, and <see cref="EditOnGoogle"/> changes an event as if someone
/// edited it elsewhere. Start the app with <c>--fake-google {BaseUri}</c>.
/// </remarks>
public sealed class FakeGoogleServer : IDisposable
{
    const string PrimaryId    = "leaf.tester@gmail.com";
    const string FamilyId     = "family123@group.calendar.google.com";
    const string EventsPrefix = "/calendar/v3/calendars/";

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    readonly Lock _gate = new();

    // Google's copy: calendar ID -> event ID -> event
    readonly Dictionary<string, Dictionary<string, JsonObject>> _events = new(StringComparer.Ordinal);

    // Change feed for incremental syncs
    readonly List<(long Version, string Calendar, string Id)> _changes = [];
    long _version = 1;
    long _etag = 9_000_000;
    int _revokes;

    /// <summary>Seeds the events and starts listening on a random loopback port.</summary>
    public FakeGoogleServer()
    {
        Seed(PrimaryId, "events-page1.json");
        Seed(PrimaryId, "events-page2.json");
        Seed(PrimaryId, "events-meeting.json");
        Seed(FamilyId, "events-family.json");

        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}/");
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Root address to pass to <c>--fake-google</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>Every request seen, as <c>"GET /path?query"</c>.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Every event write (insert, patch, delete, move), including refused ones, in order.</summary>
    public ConcurrentQueue<FakeWrite> Writes { get; } = new();

    /// <summary>Serve ten calendars instead of two, so the sidebar overflows and scrolls (set before launching).</summary>
    public bool ManyCalendars { get; set; }

    /// <summary>When true, every connection is dropped without an answer (Leaf sees a network failure).</summary>
    public bool Offline { get; set; }

    /// <summary>How many token revocations were requested.</summary>
    public int RevokeCount => Volatile.Read(ref _revokes);

    /// <summary>Changes an event as if someone edited it in Google Calendar (new ETag; the next incremental sync sends it).</summary>
    public void EditOnGoogle(string calendarId, string eventId, Action<JsonObject> change)
    {
        lock (_gate)
        {
            var ev = Store(calendarId)[eventId];
            change(ev);
            Touch(calendarId, ev);
        }
    }

    /// <summary>A copy of Google's current version of an event, or null.</summary>
    public JsonObject? EventOnGoogle(string calendarId, string eventId)
    {
        lock (_gate)
        {
            return Store(calendarId).TryGetValue(eventId, out var ev) ? ev.DeepClone().AsObject() : null;
        }
    }

    /// <summary>Waits for the latest write matching <paramref name="match"/>.</summary>
    public FakeWrite WaitForWrite(Func<FakeWrite, bool> match, int seconds = 20) =>
        Retry.WhileNull(() => Writes.LastOrDefault(match), TimeSpan.FromSeconds(seconds)).Result
        ?? throw new InvalidOperationException("Leaf didn't send the expected change to Google.");

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    // =========================================================================
    // HTTP
    // =========================================================================

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
                var (method, target, headers, body) = await ReadRequestAsync(stream);
                Requests.Enqueue($"{method} {target}");

                // Offline: hang up without an answer
                if (Offline)
                {
                    return;
                }

                var (status, content, location) = Route(method, target, headers.GetValueOrDefault("if-match"), body);
                var bytes  = Encoding.UTF8.GetBytes(content);
                var header = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {Reason(status)}\r\n")
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

    (int Status, string Content, string? Location) Route(string method, string target, string? ifMatch, string body)
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

        // Calendar List
        if (method == "GET" && path == "/calendar/v3/users/me/calendarList")
        {
            return (200, Read(ManyCalendars ? "calendar-list-many.json" : "calendar-list.json"), null);
        }

        // Events
        if (path.StartsWith(EventsPrefix, StringComparison.Ordinal))
        {
            return RouteEvents(method, path[EventsPrefix.Length..], uri.Query, query, ifMatch, body);
        }

        return NotFound();
    }

    (int, string, string?) RouteEvents(string method, string rest, string rawQuery, IReadOnlyDictionary<string, string> query, string? ifMatch, string body)
    {
        var parts = rest.Split('/');
        if (parts.Length < 2 || parts[1] != "events")
        {
            return NotFound();
        }

        if (method != "GET")
        {
            Writes.Enqueue(new FakeWrite(method, EventsPrefix + rest, rawQuery, ifMatch, body));
        }

        var calendarId = Uri.UnescapeDataString(parts[0]);
        lock (_gate)
        {
            if (parts.Length == 2)
            {
                return method switch
                {
                    "GET"  => List(calendarId, query),
                    "POST" => Insert(calendarId, body),
                    _      => NotFound(),
                };
            }

            var id = Uri.UnescapeDataString(parts[2]);
            if (parts.Length == 4 && parts[3] == "move" && method == "POST")
            {
                return Move(calendarId, id, query.GetValueOrDefault("destination") ?? "");
            }

            return method switch
            {
                "GET"    => Get(calendarId, id),
                "PATCH"  => Patch(calendarId, id, ifMatch, body),
                "DELETE" => Delete(calendarId, id, ifMatch),
                _        => NotFound(),
            };
        }
    }

    // =========================================================================
    // EVENTS (called under _gate)
    // =========================================================================

    (int, string, string?) List(string calendarId, IReadOnlyDictionary<string, string> query)
    {
        var events = Store(calendarId);
        IEnumerable<JsonObject> items = events.Values;

        // Incremental: what changed since the token
        if (query.TryGetValue("syncToken", out var token))
        {
            var since = long.TryParse(token.Replace("sync-token-", "", StringComparison.Ordinal), CultureInfo.InvariantCulture, out var n) ? n : 1;
            items = _changes.Where(c => c.Version > since && c.Calendar == calendarId).Select(c => c.Id).Distinct().Select(id => events[id]);
        }

        var page = new JsonObject
        {
            ["items"]         = new JsonArray([.. items.Select(e => (JsonNode)e.DeepClone())]),
            ["nextSyncToken"] = string.Create(CultureInfo.InvariantCulture, $"sync-token-{_version}"),
        };
        return (200, page.ToJsonString(), null);
    }

    (int, string, string?) Insert(string calendarId, string body)
    {
        var ev = JsonNode.Parse(body)!.AsObject();
        var id = (string?)ev["id"] ?? Guid.NewGuid().ToString("N");
        if (Store(calendarId).ContainsKey(id))
        {
            return (409, Error(409, "duplicate"), null);
        }

        ev["id"]      = id;
        ev["status"] ??= "confirmed";
        ev["iCalUID"] = id + "@google.com";
        Store(calendarId)[id] = ev;
        Touch(calendarId, ev);
        return (200, ev.ToJsonString(), null);
    }

    (int, string, string?) Patch(string calendarId, string id, string? ifMatch, string body)
    {
        if (Existing(calendarId, id) is not { } ev)
        {
            return NotFound();
        }

        if (ifMatch is not null && ifMatch != (string?)ev["etag"])
        {
            return (412, Error(412, "conditionNotMet"), null);
        }

        Merge(ev, JsonNode.Parse(body)!.AsObject());
        Touch(calendarId, ev);
        return (200, ev.ToJsonString(), null);
    }

    (int, string, string?) Delete(string calendarId, string id, string? ifMatch)
    {
        if (Existing(calendarId, id) is not { } ev)
        {
            return NotFound();
        }

        if ((string?)ev["status"] == "cancelled")
        {
            return (410, Error(410, "deleted"), null);
        }

        if (ifMatch is not null && ifMatch != (string?)ev["etag"])
        {
            return (412, Error(412, "conditionNotMet"), null);
        }

        ev["status"] = "cancelled";
        Touch(calendarId, ev);
        return (204, "", null);
    }

    (int, string, string?) Move(string calendarId, string id, string destination)
    {
        var source = Store(calendarId);
        if (!source.TryGetValue(id, out var ev) || (string?)ev["status"] == "cancelled")
        {
            return NotFound();
        }

        source[id] = new JsonObject { ["id"] = id, ["status"] = "cancelled" };
        Touch(calendarId, source[id]);
        Store(destination)[id] = ev;
        Touch(destination, ev);
        return (200, ev.ToJsonString(), null);
    }

    (int, string, string?) Get(string calendarId, string id) =>
        Store(calendarId).TryGetValue(id, out var ev) ? (200, ev.ToJsonString(), null) : NotFound();

    // The stored event, or an instance of a stored series made into its own row (Google does the same on first write)
    JsonObject? Existing(string calendarId, string id)
    {
        var events = Store(calendarId);
        if (events.TryGetValue(id, out var ev))
        {
            return ev;
        }

        var cut = id.LastIndexOf('_');
        if (cut < 0 || !events.TryGetValue(id[..cut], out var master) || master["recurrence"] is null)
        {
            return null;
        }

        var stamp    = id[(cut + 1)..];
        var allDay   = stamp.Length == 8;
        var original = allDay
            ? DateTime.ParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture)
            : DateTime.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        var instance = master.DeepClone().AsObject();
        instance.Remove("recurrence");
        instance["id"]               = id;
        instance["recurringEventId"] = id[..cut];
        if (allDay)
        {
            var date = original.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            instance["originalStartTime"] = new JsonObject { ["date"] = date };
            instance["start"]             = new JsonObject { ["date"] = date };
            instance["end"]               = new JsonObject { ["date"] = original.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }
        else
        {
            var zone     = (string?)master["start"]?["timeZone"];
            var length   = DateTimeOffset.Parse((string)master["end"]!["dateTime"]!, CultureInfo.InvariantCulture) - DateTimeOffset.Parse((string)master["start"]!["dateTime"]!, CultureInfo.InvariantCulture);
            instance["originalStartTime"] = new JsonObject { ["dateTime"] = original.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["timeZone"] = zone };
            instance["start"]             = new JsonObject { ["dateTime"] = original.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["timeZone"] = zone };
            instance["end"]               = new JsonObject { ["dateTime"] = (original + length).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["timeZone"] = zone };
        }

        events[id] = instance;
        return instance;
    }

    void Touch(string calendarId, JsonObject ev)
    {
        ev["etag"]    = string.Create(CultureInfo.InvariantCulture, $"\"{++_etag}\"");
        ev["updated"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        _changes.Add((++_version, calendarId, (string)ev["id"]!));
    }

    Dictionary<string, JsonObject> Store(string calendarId)
    {
        if (!_events.TryGetValue(calendarId, out var events))
        {
            _events[calendarId] = events = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        }

        return events;
    }

    void Seed(string calendarId, string fixture)
    {
        foreach (var item in JsonNode.Parse(Read(fixture))!["items"]!.AsArray())
        {
            var ev = item!.AsObject();
            Store(calendarId)[(string)ev["id"]!] = ev.DeepClone().AsObject();
        }
    }

    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is null)
            {
                target.Remove(name);
            }
            else if (value is JsonObject child && target[name] is JsonObject existing)
            {
                Merge(existing, child);
            }
            else
            {
                target[name] = value.DeepClone();
            }
        }
    }

    static (int, string, string?) NotFound() => (404, Error(404, "notFound"), null);

    static string Error(int code, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $$$"""{"error":{"code":{{{code}}},"errors":[{"reason":"{{{reason}}}"}]}}""");

    static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        302 => "Found",
        409 => "Conflict",
        410 => "Gone",
        412 => "Precondition Failed",
        _   => "Not Found",
    };

    string Read(string name) => File.ReadAllText(Path.Combine(_fixtures, name));

    static async Task<(string Method, string Target, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(NetworkStream stream)
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

        var lines   = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var parts   = lines[0].Split(' ');
        var headers = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim());
        var contentLength = headers.TryGetValue("content-length", out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0;

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

        return (parts[0], parts[1], headers, Encoding.UTF8.GetString(buffer, bodyStart, Math.Max(0, length - bodyStart)));
    }
}
