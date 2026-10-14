using System.Text.Json;
using LeafCalendar.Core.Api;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiDispatcherTests : IDisposable
{
    private readonly ApiHarness _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Hello_ReturnsVersionsAndProfile()
    {
        var result = await _api.CallAsync("leaf.hello", """{"client":"Raycast"}""");

        Assert.Equal("0.1.400", result.GetProperty("leafVersion").GetString());
        Assert.Equal(1, result.GetProperty("apiVersion").GetInt32());
        Assert.Equal("default", result.GetProperty("profile").GetString());
    }

    [Fact]
    public async Task Hello_WithIconAndReturnUrl_IsAccepted()
    {
        var result = await _api.CallAsync("leaf.hello", $$"""{"client":"Raycast","icon":"{{ApiHelloTests.OnePixelPng}}","returnUrl":"raycast://extensions/leaf"}""");

        Assert.Equal(1, result.GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task UnknownMethod_IsMethodNotFound()
    {
        Assert.Equal(ApiErrors.MethodNotFound, (await _api.FailAsync("events.explode")).Code);
    }

    [Fact]
    public async Task UnknownParam_IsInvalidParams()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("leaf.hello", """{"surprise":1}""")).Code);
    }

    [Fact]
    public async Task WrongParamType_IsInvalidParams()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("leaf.hello", """{"client":5}""")).Code);
    }

    [Fact]
    public async Task Notification_GetsNoReply()
    {
        Assert.Null(await _api.Dispatcher.HandleAsync("""{"jsonrpc":"2.0","method":"leaf.hello"}""", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ParseError_RepliesWithNullId()
    {
        var reply = await _api.Dispatcher.HandleAsync("{oops", TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(reply!);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(ApiErrors.ParseError, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task CalendarsList_ListsShownCalendars_WithTheDefaultMarked()
    {
        var result = await _api.CallAsync("calendars.list");

        var calendars = result.EnumerateArray().ToList();
        Assert.Equal(2, calendars.Count);
        var primary = calendars.Single(c => c.GetProperty("name").GetString() == "leaf.tester@gmail.com");
        Assert.Equal("leaf.tester@gmail.com", primary.GetProperty("account").GetString());
        Assert.True(primary.GetProperty("canEdit").GetBoolean());
        Assert.True(primary.GetProperty("isDefault").GetBoolean());
        Assert.Equal(("109876543210", "leaf.tester@gmail.com"), ApiIds.ReadCalendar(primary.GetProperty("id").GetString()));
        Assert.False(calendars.Single(c => c.GetProperty("name").GetString() == "Family").GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Log_NeverHoldsAnUnknownMethodsText()
    {
        await _api.FailAsync("evil-method-text-from-the-client");
        await _api.CallAsync("leaf.hello");

        var log = File.ReadAllText(_api.LogPath);
        Assert.DoesNotContain("evil-method-text-from-the-client", log, StringComparison.Ordinal);
        Assert.Contains("method=leaf.hello", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replies_NeverCarrySecrets()
    {
        _api.Tokens.SetProviderSecret("calcom", "client_secret", "super-secret-value");

        var hello = await _api.CallAsync("leaf.hello");
        var calendars = await _api.CallAsync("calendars.list");

        Assert.DoesNotContain("super-secret-value", hello.GetRawText() + calendars.GetRawText(), StringComparison.Ordinal);
    }
}
