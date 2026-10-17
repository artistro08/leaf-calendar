using System.Net;
using System.Text;
using System.Text.Json;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SunsamaMcpClientTests : IDisposable
{
    private const string Mcp = "https://api.example.test/mcp";
    private readonly FakeHttpHandler _server = new();
    private int _sessions;

    public void Dispose() => _server.Dispose();

    private SunsamaMcpClient Create() => new(new HttpClient(_server), new Uri(Mcp), _ => Task.FromResult("at-1"));

    private static string Method(RecordedRequest r) => JsonDocument.Parse(r.Body!).RootElement.GetProperty("method").GetString()!;

    private static HttpResponseMessage Json(string body, string? session = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (session is not null)
        {
            response.Headers.Add("Mcp-Session-Id", session);
        }

        return response;
    }

    // The resource's JSON goes inside a JSON string, so its quotes and backslashes are escaped
    private static string ReadResult(int id, string innerJson) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"contents":[{"uri":"x","mimeType":"application/json","text":"{{{innerJson.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}}}"}]}}""";

    private void ServeSession(Func<RecordedRequest, HttpResponseMessage> read)
    {
        _server.On(r => r.Body is not null && Method(r) == "initialize", _ => Json("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""", $"s{++_sessions}"));
        _server.On(r => r.Body is not null && Method(r) == "notifications/initialized", _ => new HttpResponseMessage(HttpStatusCode.Accepted));
        _server.On(r => r.Body is not null && Method(r) == "resources/read", read);
    }

    [Fact]
    public async Task ReadResourceAsync_JsonReply_StartsSessionAndReturnsInnerJson()
    {
        ServeSession(r => Json(ReadResult(2, """{"tasks":[]}""")));

        using var doc = await Create().ReadResourceAsync("sunsama://tasks/2026-10-16", TestContext.Current.CancellationToken);

        Assert.True(doc.RootElement.TryGetProperty("tasks", out _));
        Assert.Equal(["initialize", "notifications/initialized", "resources/read"], _server.Requests.Select(Method));
        var read = _server.Requests.Last();
        Assert.Equal("s1", read.Header("Mcp-Session-Id"));
        Assert.Equal("at-1", read.BearerToken);
        Assert.Contains("text/event-stream", read.Header("Accept"), StringComparison.Ordinal);
        Assert.Equal("sunsama://tasks/2026-10-16", JsonDocument.Parse(read.Body!).RootElement.GetProperty("params").GetProperty("uri").GetString());
    }

    [Fact]
    public async Task ReadResourceAsync_SecondRead_ReusesTheSession()
    {
        ServeSession(r => Json(ReadResult(2, "{}")));
        var client = Create();

        (await client.ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken)).Dispose();
        (await client.ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(1, _sessions);
    }

    [Fact]
    public async Task ReadResourceAsync_SseReply_ReadsTheDataLine()
    {
        ServeSession(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {ReadResult(2, """{"user":{}}""")}\n\n", Encoding.UTF8, "text/event-stream"),
        });

        using var doc = await Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken);

        Assert.True(doc.RootElement.TryGetProperty("user", out _));
    }

    [Fact]
    public async Task ReadResourceAsync_SessionGone_StartsANewOneOnce()
    {
        var reads = 0;
        ServeSession(r => ++reads == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(ReadResult(2, "{}")));

        using var doc = await Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken);

        Assert.Equal(2, _sessions);
    }

    [Fact]
    public async Task ReadResourceAsync_SessionGoneTwice_ThrowsUnavailable()
    {
        ServeSession(r => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
        Assert.Equal(2, _sessions);
    }

    [Fact]
    public async Task ReadResourceAsync_Unauthorized_ThrowsAuth()
    {
        _server.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ReadResourceAsync_MayPass_ThrowsUnavailable(HttpStatusCode status)
    {
        _server.On(_ => true, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadResourceAsync_Offline_ThrowsUnavailable()
    {
        _server.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadResourceAsync_RpcError_ThrowsUnavailable()
    {
        ServeSession(r => Json("""{"jsonrpc":"2.0","id":2,"error":{"code":-32602,"message":"bad"}}"""));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":2,"result":{"contents":[{"text":"not json"}]}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":2,"result":{"contents":[]}}""")]
    [InlineData("<html>")]
    public async Task ReadResourceAsync_UnreadableReply_ThrowsUnavailable(string reply)
    {
        ServeSession(r => Json(reply));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }
}
