using System.Net;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Places;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiPlacesTests : IDisposable
{
    private const string Url = "https://places.googleapis.com/v1/places:autocomplete";

    private readonly ApiHarness _api = new();
    private readonly TempFolder _logs = new();

    public ApiPlacesTests()
    {
        _api.Host.Places = new PlaceSearch(new HttpClient(_api.Http, disposeHandler: false), _api.Tokens, new AppLog(_logs.Path, _api.Time));
    }

    public void Dispose()
    {
        _api.Dispose();
        _logs.Dispose();
    }

    private void TurnOn()
    {
        _api.Host.Settings = _api.Host.Settings with { LocationSuggestions = true };
        _api.Host.Places!.SaveKey("AIza-test-key");
    }

    [Fact]
    public async Task Search_Off_IsNotEnabled()
    {
        _api.Host.Places!.SaveKey("AIza-test-key");

        var error = await _api.FailAsync("places.search", """{"query":"blue"}""");

        Assert.Equal(ApiErrors.NotEnabled, error.DataCode);
        Assert.Equal("Location suggestions are off.", error.Message);
    }

    [Fact]
    public async Task Search_OnWithoutKey_IsNotEnabled()
    {
        _api.Host.Settings = _api.Host.Settings with { LocationSuggestions = true };

        Assert.Equal(ApiErrors.NotEnabled, (await _api.FailAsync("places.search", """{"query":"blue"}""")).DataCode);
    }

    [Fact]
    public async Task Search_NoPlaceSearch_IsNotEnabled()
    {
        _api.Host.Places = null;

        Assert.Equal(ApiErrors.NotEnabled, (await _api.FailAsync("places.search", """{"query":"blue"}""")).DataCode);
    }

    [Fact]
    public async Task Search_On_ReturnsPlaces_AddressNullWhenEmpty()
    {
        TurnOn();
        _api.Http.On(HttpMethod.Post, Url, HttpStatusCode.OK, """
            {"suggestions":[
              {"placePrediction":{"text":{"text":"Blue Bottle Coffee, Mint Street, San Francisco, CA, USA"},"structuredFormat":{"mainText":{"text":"Blue Bottle Coffee"},"secondaryText":{"text":"Mint Street, San Francisco, CA, USA"}}}},
              {"placePrediction":{"text":{"text":"Japan"},"structuredFormat":{"mainText":{"text":"Japan"}}}}
            ]}
            """);

        var result = await _api.CallAsync("places.search", """{"query":"blue"}""");

        Assert.Equal(
            """[{"name":"Blue Bottle Coffee","address":"Mint Street, San Francisco, CA, USA","text":"Blue Bottle Coffee, Mint Street, San Francisco, CA, USA"},{"name":"Japan","address":null,"text":"Japan"}]""",
            result.GetRawText());
    }

    [Fact]
    public async Task Search_EmptyQuery_IsEmpty()
    {
        TurnOn();

        Assert.Equal(0, (await _api.CallAsync("places.search", "{}")).GetArrayLength());
    }

    [Fact]
    public async Task Search_TooLong_IsInvalid()
    {
        TurnOn();

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("places.search", $$"""{"query":"{{new string('a', 101)}}"}""")).Code);
    }

    [Fact]
    public async Task Search_GoogleFails_IsEmpty()
    {
        TurnOn();
        _api.Http.On(HttpMethod.Post, Url, HttpStatusCode.Forbidden, "{}");

        Assert.Equal(0, (await _api.CallAsync("places.search", """{"query":"blue"}""")).GetArrayLength());
    }
}
