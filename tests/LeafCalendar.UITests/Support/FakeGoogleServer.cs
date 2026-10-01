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
    const string PrimaryId      = "leaf.tester@gmail.com";
    const string FamilyId       = "family123@group.calendar.google.com";
    const string EventsPrefix   = "/calendar/v3/calendars/";
    const string CalendarScopes = "openid https://www.googleapis.com/auth/calendar";
    const string ContactsScopes = "https://www.googleapis.com/auth/contacts.readonly https://www.googleapis.com/auth/contacts.other.readonly";

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

    /// <summary>A calendar served as read-only (<c>accessRole: reader</c>), or null (set before launching).</summary>
    public string? ReadOnlyCalendarId { get; set; }

    /// <summary>A calendar left out of the calendar list, as if it was deleted or unsubscribed on Google, or null.</summary>
    public string? DroppedCalendarId { get; set; }

    /// <summary>When true, every connection is dropped without an answer (Leaf sees a network failure).</summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Whether the account's grant includes the contacts scopes. When false, token refreshes list only <c>openid</c> and
    /// calendar. A new sign-in (authorization code exchange) sets it back to true.
    /// </summary>
    public bool ContactsGranted { get; set; } = true;

    /// <summary>When true, contact searches get Google's "People API is turned off" refusal (<c>403 accessNotConfigured</c>).</summary>
    public bool PeopleApiDisabled { get; set; }

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

                // A bug in the fake answers 500 with the exception type, so a failing test says why
                int status;
                string content;
                string? location;
                try
                {
                    (status, content, location) = Route(method, target, headers.GetValueOrDefault("if-match"), body);
                }
                catch (Exception ex)
                {
                    (status, content, location) = (500, Error(500, ex.GetType().Name), null);
                }

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
            // A new sign-in grants everything token-response.json lists, contacts included
            if (QueryString.Parse(body).GetValueOrDefault("grant_type") == "authorization_code")
            {
                ContactsGranted = true;
                return (200, Read("token-response.json"), null);
            }

            // Refresh: the scope string says which grants the account holds
            var refresh = JsonNode.Parse(Read("token-refresh.json"))!;
            refresh["scope"] = ContactsGranted ? $"{CalendarScopes} {ContactsScopes}" : CalendarScopes;
            return (200, refresh.ToJsonString(), null);
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
            var list  = JsonNode.Parse(Read(ManyCalendars ? "calendar-list-many.json" : "calendar-list.json"))!;
            var items = list["items"]!.AsArray();
            foreach (var item in items.OfType<JsonObject>().Where(c => (string?)c["id"] == DroppedCalendarId).ToList())
            {
                items.Remove(item);
            }

            foreach (var item in list["items"]!.AsArray().OfType<JsonObject>().Where(c => (string?)c["id"] == ReadOnlyCalendarId))
            {
                item["accessRole"] = "reader";
            }

            return (200, list.ToJsonString(), null);
        }

        // Contacts
        if (method == "GET" && path.StartsWith("/people/v1/", StringComparison.Ordinal))
        {
            return People(path, query);
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
                    "POST" => Insert(calendarId, body, ConferenceEnabled(rawQuery)),
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
                "PATCH"  => Patch(calendarId, id, ifMatch, body, ConferenceEnabled(rawQuery)),
                "DELETE" => Delete(calendarId, id, ifMatch),
                _        => NotFound(),
            };
        }
    }

    // =========================================================================
    // PEOPLE
    // =========================================================================

    // Google's contact search: a case-insensitive prefix of any word of the name, or of the address.
    // An empty query is the warmup request and matches nothing, as on Google.
    (int, string, string?) People(string path, IReadOnlyDictionary<string, string> query)
    {
        var fixture = path switch
        {
            "/people/v1/people:searchContacts" => "contacts-search.json",
            "/people/v1/otherContacts:search"  => "other-contacts-search.json",
            _                                  => null,
        };

        if (fixture is null)
        {
            return NotFound();
        }

        if (PeopleApiDisabled)
        {
            return (403, Error(403, "accessNotConfigured"), null);
        }

        var text    = query.GetValueOrDefault("query") ?? "";
        var results = JsonNode.Parse(Read(fixture))!["results"]!.AsArray()
            .Where(r => text.Length > 0 && Matches(r!["person"]!, text))
            .Select(r => r!.DeepClone());

        return (200, new JsonObject { ["results"] = new JsonArray([.. results]) }.ToJsonString(), null);
    }

    static bool Matches(JsonNode person, string text)
    {
        var names  = person["names"]?.AsArray().Select(n => (string?)n!["displayName"] ?? "") ?? [];
        var emails = person["emailAddresses"]?.AsArray().Select(e => (string?)e!["value"] ?? "") ?? [];

        return names.SelectMany(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(emails)
            .Any(w => w.StartsWith(text, StringComparison.OrdinalIgnoreCase));
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

    (int, string, string?) Insert(string calendarId, string body, bool conference)
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
        if (ev.ContainsKey("conferenceData"))
        {
            ApplyConference(ev, ev["conferenceData"], conference);
        }

        Store(calendarId)[id] = ev;
        Touch(calendarId, ev);
        return (200, ev.ToJsonString(), null);
    }

    (int, string, string?) Patch(string calendarId, string id, string? ifMatch, string body, bool conference)
    {
        if (Existing(calendarId, id) is not { } ev)
        {
            return NotFound();
        }

        if (ifMatch is not null && ifMatch != (string?)ev["etag"])
        {
            return (412, Error(412, "conditionNotMet"), null);
        }

        // Video Call Changes Are Handled Apart From The Merge
        var patch      = JsonNode.Parse(body)!.AsObject();
        var hasConfRaw = patch.ContainsKey("conferenceData");
        var confValue  = patch["conferenceData"];
        patch.Remove("conferenceData");
        Merge(ev, patch);
        if (hasConfRaw)
        {
            ApplyConference(ev, confValue, conference);
        }

        Touch(calendarId, ev);
        return (200, ev.ToJsonString(), null);
    }

    // Google reads conferenceData only with conferenceDataVersion=1
    static bool ConferenceEnabled(string rawQuery) =>
        rawQuery.TrimStart('?').Split('&').Contains("conferenceDataVersion=1", StringComparer.Ordinal);

    // A createRequest becomes a Meet link, null removes it, stored conferenceData stays as given, and without version 1 the field is ignored
    static void ApplyConference(JsonObject ev, JsonNode? value, bool enabled)
    {
        ev.Remove("conferenceData", out _);
        if (!enabled)
        {
            return;
        }

        if (value is null)
        {
            ev.Remove("hangoutLink");
        }
        else if ((string?)value["createRequest"]?["requestId"] is { } requestId)
        {
            var code = requestId.Length > 4 ? requestId[..4] : requestId;
            var link = "https://meet.google.com/fake-" + code;
            ev["hangoutLink"]    = link;
            ev["conferenceData"] = new JsonObject
            {
                ["conferenceId"] = "fake-" + code,
                ["entryPoints"]  = new JsonArray(new JsonObject { ["entryPointType"] = "video", ["uri"] = link }),
            };
        }
        else
        {
            ev["conferenceData"] = value.DeepClone();
        }
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

    /// <summary>Puts an event on the fake Google (tests seed extra events with it before launching Leaf).</summary>
    public void AddEvent(string calendarId, JsonObject body)
    {
        lock (_gate)
        {
            if (Insert(calendarId, body.ToJsonString(), conference: true).Item1 != 200)
            {
                throw new InvalidOperationException("The fake Google refused the seeded event (is its ID already used?).");
            }
        }
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
        403 => "Forbidden",
        409 => "Conflict",
        410 => "Gone",
        412 => "Precondition Failed",
        500 => "Internal Server Error",
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
