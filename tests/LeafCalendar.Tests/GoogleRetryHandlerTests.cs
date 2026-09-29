using System.Net;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class GoogleRetryHandlerTests : IDisposable
{
    const string Url = "https://www.googleapis.com/calendar/v3/users/me/calendarList";

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    HttpClient CreateClient() => new(new GoogleRetryHandler(_time) { InnerHandler = _google });

    public void Dispose()
    {
        _google?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Drives fake time forward until the request finishes (bounded so a bug can't hang the run).
    async Task<HttpResponseMessage> SendWithTimeAsync(Task<HttpResponseMessage> send)
    {
        for (var i = 0; i < 100 && !send.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(40));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return await send;
    }

    [Fact]
    public async Task SendAsync_429Then503ThenOk_RetriesUntilSuccess()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.TooManyRequests, "{}", once: true);
        _google.On(HttpMethod.Get, Url, HttpStatusCode.ServiceUnavailable, "{}", once: true);
        _google.On(HttpMethod.Get, Url, HttpStatusCode.OK, """{"items":[]}""");

        using var response = await SendWithTimeAsync(CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, _google.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_RateLimit403_Retries()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.Forbidden, Fixture.Read("error-rate-limit.json"), once: true);
        _google.On(HttpMethod.Get, Url, HttpStatusCode.OK, """{"items":[]}""");

        using var response = await SendWithTimeAsync(CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_Forbidden403_ReturnsWithoutRetryAndBodyStillReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        _google.On(HttpMethod.Get, Url, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));

        using var response = await CreateClient().GetAsync(new Uri(Url), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(_google.Requests);
        Assert.Contains("forbidden", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_AlwaysFailing_StopsAtMaxAttempts()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.InternalServerError, "{}");

        using var response = await SendWithTimeAsync(CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(GoogleRetryHandler.MaxAttempts, _google.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_NotFound_DoesNotRetry()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.NotFound, "{}");

        using var response = await CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken);

        Assert.Single(_google.Requests);
    }
}
