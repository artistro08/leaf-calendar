using System.Net;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.Tests.Support;

/// <summary>A request the fake saw, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? Body)
{
    /// <summary>The <c>If-Match</c> header, or null.</summary>
    public string? IfMatch { get; init; }

    /// <summary>Returns a query string value, or null.</summary>
    public string? Query(string key) => QueryString.Parse(Uri.Query).GetValueOrDefault(key);

    /// <summary>Returns a form body value, or null.</summary>
    public string? Form(string key) => Body is null ? null : QueryString.Parse(Body).GetValueOrDefault(key);
}

/// <summary>
/// Fake Google. Routes are checked in the order they were added and the first match wins.
/// A route added with <c>once: true</c> is removed after it answers, so add one-shot routes before
/// permanent ones for the same URL. Unmatched requests get a 404.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    /// <summary>Every request sent through the fake, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Adds a route with a custom matcher and responder.</summary>
    public FakeHttpHandler On(Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> respond, bool once = false)
    {
        _routes.Add(new Route(match, respond, once));
        return this;
    }

    /// <summary>Adds a route matching a method and absolute URL prefix, answering with JSON.</summary>
    public FakeHttpHandler On(HttpMethod method, string urlPrefix, HttpStatusCode status, string body, bool once = false) =>
        On(r => r.Method == method && r.Uri.AbsoluteUri.StartsWith(urlPrefix, StringComparison.Ordinal), _ => Json(status, body), once);

    /// <summary>Base of <see cref="Respond"/>'s relative paths (Google Calendar API v3).</summary>
    public const string CalendarApi = "https://www.googleapis.com/calendar/v3/";

    /// <summary>The newest request (throws when none was sent).</summary>
    public RecordedRequest Last => Requests[^1];

    /// <summary>
    /// Adds a route matching a method and a Calendar API path exactly (relative to <see cref="CalendarApi"/>, escaped as
    /// sent, query ignored), answering with JSON.
    /// </summary>
    public FakeHttpHandler Respond(HttpMethod method, string relativePath, int status, string body, bool once = false) =>
        On(r => r.Method == method && r.Uri.GetLeftPart(UriPartial.Path) == CalendarApi + relativePath, _ => Json((HttpStatusCode)status, body), once);

    /// <summary>Adds a route answering every request with this method.</summary>
    public FakeHttpHandler RespondToAny(HttpMethod method, int status, string body) =>
        On(r => r.Method == method, _ => Json((HttpStatusCode)status, body));

    /// <summary>Adds a route that throws for every request not matched by an earlier route (e.g. offline).</summary>
    public FakeHttpHandler Throw(Exception error) =>
        On(_ => true, _ => throw error);

    /// <summary>Creates a JSON response.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter, body)
        {
            IfMatch = request.Headers.TryGetValues("If-Match", out var ifMatch) ? string.Join(",", ifMatch) : null,
        };
        Requests.Add(recorded);

        var route = _routes.FirstOrDefault(r => r.Match(recorded));
        if (route is null)
        {
            return Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"No fake route"}}""");
        }

        if (route.Once)
        {
            _routes.Remove(route);
        }

        return route.Respond(recorded);
    }

    private sealed record Route(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Respond, bool Once);
}
