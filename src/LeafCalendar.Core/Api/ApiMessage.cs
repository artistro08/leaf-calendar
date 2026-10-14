using System.Text;
using System.Text.Json;

namespace LeafCalendar.Core.Api;

/// <summary>One JSON-RPC 2.0 request. <see cref="Id"/> is null for a notification (no reply); a JSON <c>null</c> id is a <see cref="JsonValueKind.Null"/> element.</summary>
public sealed record ApiRequest(JsonElement? Id, string Method, JsonElement Params);

/// <summary>
/// Reads and writes the local API's JSON-RPC 2.0 messages, one per line (local API spec 3.3).
/// </summary>
/// <remarks>
/// Batches aren't supported. <c>params</c> must be an object (or left out, which reads as <c>{}</c>). Error messages are
/// fixed English sentences, never exception text or anything from the request.
/// </remarks>
/// <seealso href="https://www.jsonrpc.org/specification"/>
public static class ApiMessage
{
    /// <summary>The longest line Leaf Calendar reads (1 MB); a longer one closes the connection.</summary>
    public const int MaxLineBytes = 1_048_576;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 32 };
    private static readonly JsonElement EmptyObject = ParseElement("{}");

    /// <summary>Reads one request.</summary>
    /// <exception cref="ApiProtocolException">The line isn't JSON, isn't a JSON-RPC 2.0 request, or its params aren't an object.</exception>
    public static ApiRequest Parse(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line, Options);
        }
        catch (JsonException)
        {
            throw new ApiProtocolException(ApiErrors.ParseError, "Parse error.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                throw new ApiProtocolException(ApiErrors.InvalidRequest, "Batches aren't supported.");
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ApiProtocolException(ApiErrors.InvalidRequest, "Invalid request.");
            }

            // Id: A String, A Number, Or Null; Left Out For A Notification
            JsonElement? id = null;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                {
                    throw new ApiProtocolException(ApiErrors.InvalidRequest, "Invalid request.");
                }

                id = idElement.Clone();
            }

            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
                || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(method.GetString()))
            {
                throw new ApiProtocolException(ApiErrors.InvalidRequest, "Invalid request.");
            }

            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : EmptyObject;
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new ApiProtocolException(ApiErrors.InvalidParams, "Invalid params.");
            }

            return new ApiRequest(id, method.GetString()!, parameters);
        }
    }

    /// <summary>A success reply.</summary>
    public static string Result(JsonElement id, JsonElement result) => Write(w =>
    {
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WritePropertyName("result");
        result.WriteTo(w);
    });

    /// <summary>An error reply; <paramref name="dataCode"/> (Leaf Calendar's short code) goes under <c>data.code</c>.</summary>
    public static string Error(JsonElement? id, int code, string message, string? dataCode = null) => Write(w =>
    {
        w.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(w);
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        if (dataCode is not null)
        {
            w.WriteStartObject("data");
            w.WriteString("code", dataCode);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    });

    /// <summary>The request's id when the line is a readable request, else null.</summary>
    public static JsonElement? TryReadId(string line)
    {
        try
        {
            return Parse(line).Id;
        }
        catch (ApiProtocolException)
        {
            return null;
        }
    }

    /// <summary>The reply to a request from an app that isn't (or is no longer) approved.</summary>
    public static string NotApproved(string line) =>
        Error(TryReadId(line), ApiErrors.ServerError, "You didn't allow this app to connect to Leaf Calendar.", ApiErrors.NotApproved);

    /// <summary>The reply to a first <c>leaf.hello</c> with <c>askOnLink</c> from an app that isn't approved yet.</summary>
    public static string ApprovalNeeded(string line) =>
        Error(TryReadId(line), ApiErrors.ServerError, "Open leaf-calendar://connect?code=<your askOnLink code> so Leaf Calendar asks to allow this app.", ApiErrors.ApprovalNeeded);

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            body(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
