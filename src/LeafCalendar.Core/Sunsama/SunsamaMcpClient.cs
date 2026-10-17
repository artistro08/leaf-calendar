using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Core.Sunsama;

/// <summary>
/// A small client for Sunsama's MCP server that only reads resources (spec 2026-10-16, section 4.1).
/// </summary>
/// <remarks>
/// MCP's streamable HTTP transport is JSON-RPC 2.0 posted to one URL; the server answers with plain JSON or a short
/// server-sent event stream, and keeps a session by the <c>Mcp-Session-Id</c> header. Leaf Calendar sends
/// <c>initialize</c>, <c>notifications/initialized</c> and then <c>resources/read</c>, and nothing that changes anything.
/// A session the server forgot (404) is started again once. One call runs at a time; the gate is never disposed, so a
/// call still on its way when a sign-out drops the client finishes or fails normally. See
/// <see href="https://modelcontextprotocol.io/specification/2025-06-18/basic/transports">MCP transports</see>.
/// </remarks>
/// <param name="http">The shared HTTP client.</param>
/// <param name="endpoint">The MCP server URL.</param>
/// <param name="accessToken">Gives a current access token (refreshing as needed).</param>
public sealed class SunsamaMcpClient(HttpClient http, Uri endpoint, Func<CancellationToken, Task<string>> accessToken) : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private bool _initialized;
    private int _nextId;
    private volatile bool _ended;

    /// <summary>
    /// Reads one resource (for example <c>sunsama://tasks/2026-10-16</c>) and returns the JSON inside its first content's
    /// text. The caller disposes the document.
    /// </summary>
    /// <param name="uri">The resource URI.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The resource's JSON.</returns>
    /// <exception cref="SchedulingAuthException">Sunsama refused the access token (401, 403).</exception>
    /// <exception cref="SchedulingUnavailableException">Offline, a timeout, 429, another error, or an unreadable answer.</exception>
    public async Task<JsonDocument> ReadResourceAsync(string uri, CancellationToken ct)
    {
        ThrowIfEnded();
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfEnded();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await EnsureSessionAsync(ct);
                var (gone, reply) = await PostAsync("resources/read", w => w.WriteString("uri", uri), notification: false, ct);
                if (gone)
                {
                    ResetSession();
                    continue;
                }

                using (reply)
                {
                    return Contents(reply!);
                }
            }

            throw new SchedulingUnavailableException("Sunsama kept ending the session.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the session, so the next read starts a new one (after a sign-in, or when the server forgot it).</summary>
    public void ResetSession()
    {
        _sessionId = null;
        _initialized = false;
    }

    /// <summary>
    /// Ends this client (a sign-out or a new sign-in): a call already running finishes, and every call waiting or
    /// arriving after it throws <see cref="SchedulingUnavailableException"/>.
    /// </summary>
    /// <remarks>The gate isn't disposed, so callers already waiting on it aren't stranded (as in <see cref="ProviderTokens"/>).</remarks>
    public void Dispose() => _ended = true;

    private void ThrowIfEnded()
    {
        if (_ended)
        {
            throw new SchedulingUnavailableException("This Sunsama sign-in has ended.");
        }
    }

    // Starts a session: initialize, keep the session ID, then the initialized notification
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        var (_, reply) = await PostAsync(
            "initialize",
            w =>
            {
                w.WriteString("protocolVersion", ProtocolVersion);
                w.WriteStartObject("capabilities");
                w.WriteEndObject();
                w.WriteStartObject("clientInfo");
                w.WriteString("name", "Leaf Calendar");
                w.WriteString("version", "1.0");
                w.WriteEndObject();
            },
            notification: false,
            ct);
        reply?.Dispose();
        _initialized = true;
        try
        {
            await PostAsync("notifications/initialized", null, notification: true, ct);
        }
        catch
        {
            // Half-Started Session: start over next time
            ResetSession();
            throw;
        }
    }

    // Posts one JSON-RPC message. Returns gone=true for a 404 on a known session; otherwise the reply (null for a
    // notification). Maps every other failure to the scheduling exceptions.
    private async Task<(bool Gone, JsonDocument? Reply)> PostAsync(string method, Action<Utf8JsonWriter>? writeParams, bool notification, CancellationToken ct)
    {
        var id = notification ? (int?)null : ++_nextId;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(Message(method, id, writeParams)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_initialized)
        {
            request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        }

        if (_sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", _sessionId);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SchedulingUnavailableException("Sunsama couldn't be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SchedulingUnavailableException("Sunsama didn't answer in time.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new SchedulingAuthException("Sunsama refused the access token.");
                case HttpStatusCode.NotFound when _sessionId is not null:
                    return (true, null);
                case (HttpStatusCode)429:
                    throw new SchedulingUnavailableException("Sunsama asked Leaf Calendar to slow down.", response.Headers.RetryAfter?.Delta);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SchedulingUnavailableException(string.Create(CultureInfo.InvariantCulture, $"Sunsama's MCP server answered {(int)response.StatusCode}."));
            }

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var session))
            {
                _sessionId = session.FirstOrDefault();
            }

            if (id is not { } expected)
            {
                return (false, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var reply = response.Content.Headers.ContentType?.MediaType == "text/event-stream" ? FromEvents(body, expected) : Parse(body);
            if (reply.RootElement.ValueKind != JsonValueKind.Object || reply.RootElement.TryGetProperty("error", out _))
            {
                reply.Dispose();
                throw new SchedulingUnavailableException("Sunsama's MCP server answered with an error.");
            }

            return (false, reply);
        }
    }

    // The JSON-RPC message; a notification has no id
    private static byte[] Message(string method, int? id, Action<Utf8JsonWriter>? writeParams)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            if (id is { } value)
            {
                json.WriteNumber("id", value);
            }

            json.WriteString("method", method);
            if (writeParams is not null)
            {
                json.WriteStartObject("params");
                writeParams(json);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    // An event stream's reply: the last data message with this call's id, else the last one that reads
    private static JsonDocument FromEvents(string body, int id)
    {
        JsonDocument? best = null;
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line[5..].Trim());
            }
            catch (JsonException)
            {
                continue;
            }

            var matches = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var got)
                && got.ValueKind == JsonValueKind.Number
                && got.TryGetInt32(out var n)
                && n == id;
            if (matches || best is null)
            {
                best?.Dispose();
                best = doc;
            }
            else
            {
                doc.Dispose();
            }
        }

        return best ?? throw new SchedulingUnavailableException("Sunsama's event stream had no reply.");
    }

    private static JsonDocument Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new SchedulingUnavailableException("Sunsama's answer couldn't be read.", ex);
        }
    }

    // result.contents[0].text, which holds the resource's own JSON as a string
    private static JsonDocument Contents(JsonDocument reply)
    {
        var root = reply.RootElement;
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("contents", out var contents)
            && contents.ValueKind == JsonValueKind.Array
            && contents.GetArrayLength() > 0
            && contents[0].ValueKind == JsonValueKind.Object
            && contents[0].TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return Parse(text.GetString()!);
        }

        throw new SchedulingUnavailableException("Sunsama's resource answer had no contents.");
    }
}
