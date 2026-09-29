using System.Net;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.Tests.Support;

/// <summary>A request the fake saw, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? Body)
{
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
    readonly List<Route> _routes = [];

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

    /// <summary>Creates a JSON response.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body     = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter, body);
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

    sealed record Route(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Respond, bool Once);
}
