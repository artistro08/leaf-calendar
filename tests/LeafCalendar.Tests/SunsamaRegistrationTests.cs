using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SunsamaRegistrationTests : IDisposable
{
    private const string Register = "https://api.example.test/oauth/register";
    private readonly FakeHttpHandler _server = new();

    public void Dispose() => _server.Dispose();

    private Task<string> Run() =>
        SunsamaRegistration.RegisterAsync(new HttpClient(_server), new Uri(Register), "http://127.0.0.1:53818/sunsama/callback", TestContext.Current.CancellationToken);

    [Fact]
    public async Task RegisterAsync_Accepted_ReturnsClientIdAndSendsPublicClientFields()
    {
        _server.On(HttpMethod.Post, Register, HttpStatusCode.Created, """{"client_id":"abc-123"}""");

        Assert.Equal("abc-123", await Run());

        using var body = JsonDocument.Parse(Assert.Single(_server.Requests).Body!);
        var root = body.RootElement;
        Assert.Equal("Leaf Calendar", root.GetProperty("client_name").GetString());
        Assert.Equal("none", root.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("http://127.0.0.1:53818/sunsama/callback", root.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal(["authorization_code", "refresh_token"], root.GetProperty("grant_types").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal("read offline_access", root.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RegisterAsync_Refused_ThrowsAuth(HttpStatusCode status)
    {
        _server.On(HttpMethod.Post, Register, status, """{"error":"invalid_redirect_uri"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(Run);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task RegisterAsync_MayPass_ThrowsUnavailable(HttpStatusCode status)
    {
        _server.On(HttpMethod.Post, Register, status, "{}");

        await Assert.ThrowsAsync<SchedulingUnavailableException>(Run);
    }

    [Fact]
    public async Task RegisterAsync_Unreadable_ThrowsUnavailable()
    {
        _server.On(HttpMethod.Post, Register, HttpStatusCode.Created, "<html>");

        await Assert.ThrowsAsync<SchedulingUnavailableException>(Run);
    }

    [Fact]
    public async Task RegisterAsync_Offline_ThrowsUnavailable()
    {
        _server.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(Run);
    }

    [Fact]
    public async Task RegisterAsync_NoClientId_ThrowsAuth()
    {
        _server.On(HttpMethod.Post, Register, HttpStatusCode.Created, """{"client_name":"x"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(Run);
    }
}
