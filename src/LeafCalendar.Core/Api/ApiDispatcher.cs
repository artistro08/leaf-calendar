using System.Collections.Frozen;
using System.Text.Json;
using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Api;

/// <summary>
/// Answers one line of the local API (local API spec 3.1, 4): reads the request, runs its method, and writes the reply.
/// </summary>
/// <remarks>
/// Nothing escapes: protocol problems get JSON-RPC codes, Leaf's own problems <see cref="ApiErrors.ServerError"/> with a
/// short code, and anything unexpected <see cref="ApiErrors.InternalError"/> with a fixed message (the exception's type
/// is logged, never its text). Requests are logged by method name only, as a breadcrumb; a name Leaf doesn't know is
/// logged as "unknown", so nothing the client wrote reaches the log.
/// </remarks>
public sealed class ApiDispatcher(ApiContext context, AppLog log)
{
    /// <summary>The API's version; it changes only when a method or field is removed or changes meaning.</summary>
    public const int ApiVersion = 1;

    // The methods there are; only these names reach the log, since a request's method is the client's own text
    private static readonly FrozenSet<string> Methods = new[]
    {
        "leaf.hello", "calendars.list", "events.list", "events.get", "events.create", "events.move", "meetings.join",
        "app.showDay", "app.showEvent", "scheduling.links", "scheduling.openTimes", "scheduling.timeLink",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The reply line for <paramref name="line"/>, or null for a notification.</summary>
    public async Task<string?> HandleAsync(string line, CancellationToken ct)
    {
        ApiRequest request;
        try
        {
            request = ApiMessage.Parse(line);
        }
        catch (ApiProtocolException ex)
        {
            return ApiMessage.Error(ApiMessage.TryReadId(line), ex.Code, ex.Message);
        }

        var method = Methods.Contains(request.Method) ? request.Method : "unknown";
        log.Trace("api.request", $"method={method}");
        try
        {
            var result = await RunAsync(request, ct);
            return request.Id is { } id ? ApiMessage.Result(id, result) : null;
        }
        catch (ApiProtocolException ex)
        {
            return Reply(request, ex.Code, ex.Message, null);
        }
        catch (ApiException ex)
        {
            return Reply(request, ApiErrors.ServerError, ex.Message, ex.Code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type only, never the text (it can carry event content)
            log.Info("api.failed", $"method={method} error={ex.GetType().Name}");
            return Reply(request, ApiErrors.InternalError, "Leaf couldn't do that.", null);
        }
    }

    private static string? Reply(ApiRequest request, int code, string message, string? dataCode) =>
        request.Id is { } id ? ApiMessage.Error(id, code, message, dataCode) : null;

    private Task<JsonElement> RunAsync(ApiRequest r, CancellationToken ct) => r.Method switch
    {
        "leaf.hello" => Task.FromResult(ApiCalendars.Hello(context, ApiParams.Read(r.Params, ApiJsonContext.Default.HelloParams))),
        "calendars.list" => Task.FromResult(ApiCalendars.List(context, ApiParams.Read(r.Params, ApiJsonContext.Default.NoParams))),
        "events.list" => Task.FromResult(ApiEvents.List(context, ApiParams.Read(r.Params, ApiJsonContext.Default.RangeParams))),
        "events.get" => Task.FromResult(ApiEvents.Get(context, ApiParams.Read(r.Params, ApiJsonContext.Default.IdParams))),
        "events.create" => Task.FromResult(ApiEvents.Create(context, ApiParams.Read(r.Params, ApiJsonContext.Default.CreateParams))),
        "events.move" => Task.FromResult(ApiEvents.Move(context, ApiParams.Read(r.Params, ApiJsonContext.Default.MoveParams))),
        "meetings.join" => ApiActions.JoinAsync(context, ApiParams.Read(r.Params, ApiJsonContext.Default.IdParams)),
        "app.showDay" => Task.FromResult(ApiActions.ShowDay(context, ApiParams.Read(r.Params, ApiJsonContext.Default.DayParams))),
        "app.showEvent" => Task.FromResult(ApiActions.ShowEvent(context, ApiParams.Read(r.Params, ApiJsonContext.Default.IdParams))),
        "scheduling.links" => ApiScheduling.LinksAsync(context, ApiParams.Read(r.Params, ApiJsonContext.Default.NoParams), ct),
        "scheduling.openTimes" => ApiScheduling.OpenTimesAsync(context, ApiParams.Read(r.Params, ApiJsonContext.Default.OpenTimesParams), ct),
        "scheduling.timeLink" => ApiScheduling.TimeLinkAsync(context, ApiParams.Read(r.Params, ApiJsonContext.Default.TimeLinkParams), ct),
        _ => throw new ApiProtocolException(ApiErrors.MethodNotFound, "Unknown method."),
    };
}
