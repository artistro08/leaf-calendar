using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Places;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class PlaceSearchTests : IDisposable
{
    private const string Url = "https://places.googleapis.com/v1/places:autocomplete";
    private const string Key = "AIza-test-key";

    private static readonly string Rlo = char.ConvertFromUtf32(0x202E);

    private readonly FakeHttpHandler _google = new();
    private readonly InMemoryTokenStore _store = new();
    private readonly TempFolder _logs = new();
    private readonly AppLog _log;

    public PlaceSearchTests()
    {
        _log = new AppLog(_logs.Path, new FakeTimeProvider());
    }

    public void Dispose()
    {
        _google.Dispose();
        _logs.Dispose();
    }

    private PlaceSearch Create(ITokenStore? store = null) => new(new HttpClient(_google, disposeHandler: false), store ?? _store, _log);

    private PlaceSearch CreateWithKey()
    {
        var search = Create();
        search.SaveKey(Key);
        return search;
    }

    private static string Prediction(string main, string secondary, string text) =>
        "{\"placePrediction\":{\"text\":{\"text\":\"" + text + "\"},\"structuredFormat\":{\"mainText\":{\"text\":\"" + main + "\"},\"secondaryText\":{\"text\":\"" + secondary + "\"}}}}";

    private string ReadLog() => File.Exists(_log.FilePath) ? File.ReadAllText(_log.FilePath) : "";

    [Fact]
    public async Task Search_PostsInputWithKeyAndFieldMaskHeaders()
    {
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, "{}");

        await CreateWithKey().SearchAsync("  blue bottle ", TestContext.Current.CancellationToken);

        var request = Assert.Single(_google.Requests);
        Assert.Equal(Url, request.Uri.AbsoluteUri);
        Assert.Equal(Key, request.Header("X-Goog-Api-Key"));
        Assert.Equal("suggestions.placePrediction.text.text,suggestions.placePrediction.structuredFormat", request.Header("X-Goog-FieldMask"));
        Assert.Contains("\"input\":\"blue bottle\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, request.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_MapsPredictions()
    {
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, $$"""{"suggestions":[{{Prediction("Blue Bottle Coffee", "Mint Street, San Francisco, CA, USA", "Blue Bottle Coffee, Mint Street, San Francisco, CA, USA")}}]}""");

        var places = await CreateWithKey().SearchAsync("blue", TestContext.Current.CancellationToken);

        Assert.Equal([new Place("Blue Bottle Coffee", "Mint Street, San Francisco, CA, USA", "Blue Bottle Coffee, Mint Street, San Francisco, CA, USA")], places);
    }

    [Fact]
    public async Task Search_CleansUntrustedText_DropsEmpty_AndCapsAtFive()
    {
        var rows = new List<string>
        {
            Prediction($"{Rlo}Evil\\u0007Cafe", "", $"{Rlo}Evil Cafe, Main St"),
            """{"queryPrediction":{"text":{"text":"just a query"}}}""",
            Prediction("No text", "x", ""),
        };
        rows.AddRange(Enumerable.Range(1, 8).Select(i => Prediction($"P{i}", "", $"P{i}, Town")));
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, $$"""{"suggestions":[{{string.Join(',', rows)}}]}""");

        var places = await CreateWithKey().SearchAsync("e", TestContext.Current.CancellationToken);

        Assert.Equal(PlaceSearch.MaxResults, places.Count);
        Assert.Equal(new Place("Evil Cafe", "", "Evil Cafe, Main St"), places[0]);
        Assert.Equal("P1, Town", places[1].Text);
    }

    [Fact]
    public async Task Search_LongText_IsCapped()
    {
        var name = new string('L', 5_000);
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, $$"""{"suggestions":[{{Prediction(name, name, name)}}]}""");

        var place = Assert.Single(await CreateWithKey().SearchAsync("l", TestContext.Current.CancellationToken));

        Assert.True(place.Name.Length <= 300 && place.Address.Length <= 300 && place.Text.Length <= 300);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_NoQuery_SendsNothing(string query)
    {
        Assert.Empty(await CreateWithKey().SearchAsync(query, TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task Search_TooLong_SendsNothing()
    {
        Assert.Empty(await CreateWithKey().SearchAsync(new string('a', 101), TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task Search_NoKey_SendsNothing()
    {
        Assert.Empty(await Create().SearchAsync("blue", TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"API key not valid.\"}}")]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\"}}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Search_GoogleFails_IsEmpty_AndLogsStatusOnly(HttpStatusCode status, string body)
    {
        _google.On(HttpMethod.Post, Url, status, body);

        var places = await CreateWithKey().SearchAsync("secret place", TestContext.Current.CancellationToken);

        Assert.Empty(places);
        var log = ReadLog();
        Assert.Contains($"places.search.failed status={(int)status}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret place", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_NetworkFails_IsEmpty()
    {
        _google.Throw(new HttpRequestException("down"));

        Assert.Empty(await CreateWithKey().SearchAsync("blue", TestContext.Current.CancellationToken));
        Assert.Contains("places.search.failed status=0", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_CallerCancels_Throws()
    {
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, "{}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateWithKey().SearchAsync("blue", cts.Token));
    }

    [Fact]
    public void SaveKey_TrimsAndStores_EmptyRemoves()
    {
        var search = Create();

        search.SaveKey($"  {Key} ");
        Assert.Equal(Key, _store.GetProviderSecret("google-maps", "api_key"));
        Assert.True(search.HasKey);

        search.SaveKey("   ");
        Assert.Null(_store.GetProviderSecret("google-maps", "api_key"));
        Assert.False(search.HasKey);
    }

    [Fact]
    public void HasKey_ReadsTheSavedKeyOnce()
    {
        _store.SetProviderSecret("google-maps", "api_key", Key);

        Assert.True(Create().HasKey);
    }

    [Fact]
    public void HasKey_SecretsUnreadable_IsFalse_ThenReadsAgain()
    {
        var store = new FlakyStore { Fail = true };
        var search = Create(store);

        Assert.False(search.HasKey);

        store.Fail = false;
        Assert.True(search.HasKey);
    }

    [Fact]
    public void SavedKey_IsTheKeyForTheSettingsBox()
    {
        var search = CreateWithKey();

        Assert.Equal(Key, search.SavedKey);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void IsOn_NeedsTheSwitchAndAKey(bool on, bool key, bool expected)
    {
        var search = key ? CreateWithKey() : Create();

        Assert.Equal(expected, search.IsOn(new LeafSettings { LocationSuggestions = on }));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void TimeZonesOn_NeedsBothSwitchesAndAKey(bool places, bool zones, bool key, bool expected)
    {
        var search = key ? CreateWithKey() : Create();

        Assert.Equal(expected, search.TimeZonesOn(new LeafSettings { LocationSuggestions = places, TimeZoneSuggestions = zones }));
    }

    [Fact]
    public async Task SearchCities_PostsCitiesOnly_InTheSession()
    {
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, "{}");

        await CreateWithKey().SearchCitiesAsync(" aus ", "session-1", TestContext.Current.CancellationToken);

        var request = Assert.Single(_google.Requests);
        Assert.Equal(Key, request.Header("X-Goog-Api-Key"));
        Assert.Equal("suggestions.placePrediction.placeId,suggestions.placePrediction.structuredFormat", request.Header("X-Goog-FieldMask"));
        Assert.Contains("\"input\":\"aus\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"includedPrimaryTypes\":[\"(cities)\"]", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"sessionToken\":\"session-1\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCities_MapsPredictions_DropsOnesWithoutAnId()
    {
        _google.On(HttpMethod.Post, Url, HttpStatusCode.OK, """
            {"suggestions":[
              {"placePrediction":{"placeId":"id-austin","structuredFormat":{"mainText":{"text":"‮Austin"},"secondaryText":{"text":"TX, USA"}}}},
              {"placePrediction":{"structuredFormat":{"mainText":{"text":"No id"}}}},
              {"placePrediction":{"placeId":"id-nameless"}}
            ]}
            """);

        var cities = await CreateWithKey().SearchCitiesAsync("aus", "s", TestContext.Current.CancellationToken);

        Assert.Equal([new City("Austin", "TX, USA", "id-austin")], cities);
    }

    [Fact]
    public async Task SearchCities_NoKey_SendsNothing()
    {
        Assert.Empty(await Create().SearchCitiesAsync("aus", "s", TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task TimeZoneOf_GetsOnlyTheZone_InTheSession()
    {
        _google.On(HttpMethod.Get, "https://places.googleapis.com/v1/places/", HttpStatusCode.OK, """{"timeZone":{"id":"America/Chicago"}}""");

        var zone = await CreateWithKey().TimeZoneOfAsync("id-austin", "session-1", TestContext.Current.CancellationToken);

        Assert.Equal("America/Chicago", zone);
        var request = Assert.Single(_google.Requests);
        Assert.Equal("https://places.googleapis.com/v1/places/id-austin?sessionToken=session-1", request.Uri.AbsoluteUri);
        Assert.Equal(Key, request.Header("X-Goog-Api-Key"));
        Assert.Equal("timeZone", request.Header("X-Goog-FieldMask"));
    }

    [Fact]
    public async Task TimeZoneOf_EscapesThePlaceId()
    {
        _google.On(HttpMethod.Get, "https://places.googleapis.com/", HttpStatusCode.OK, "{}");

        await CreateWithKey().TimeZoneOfAsync("../calendar?x=1", "s", TestContext.Current.CancellationToken);

        Assert.StartsWith("https://places.googleapis.com/v1/places/..%2Fcalendar%3Fx%3D1?", _google.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"timeZone":{"id":"Mars/Olympus_Mons"}}""")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    public async Task TimeZoneOf_NoKnownZone_IsNull(HttpStatusCode status, string body)
    {
        _google.On(HttpMethod.Get, "https://places.googleapis.com/v1/places/", status, body);

        Assert.Null(await CreateWithKey().TimeZoneOfAsync("id", "s", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Key, ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeZoneOf_NoKey_SendsNothing()
    {
        Assert.Null(await Create().TimeZoneOfAsync("id", "s", TestContext.Current.CancellationToken));
        Assert.Empty(_google.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, KeyCheck.Works)]
    [InlineData(HttpStatusCode.BadRequest, KeyCheck.Refused)]
    [InlineData(HttpStatusCode.Forbidden, KeyCheck.Refused)]
    [InlineData(HttpStatusCode.ServiceUnavailable, KeyCheck.Unreachable)]
    public async Task CheckKey_MapsTheAnswer(HttpStatusCode status, KeyCheck expected)
    {
        _google.On(HttpMethod.Post, Url, status, "{}");

        Assert.Equal(expected, await Create().CheckKeyAsync(" other-key ", TestContext.Current.CancellationToken));
        Assert.Equal("other-key", _google.Last.Header("X-Goog-Api-Key"));
    }

    [Fact]
    public async Task CheckKey_NetworkFails_IsUnreachable()
    {
        _google.Throw(new HttpRequestException("down"));

        Assert.Equal(KeyCheck.Unreachable, await Create().CheckKeyAsync(Key, TestContext.Current.CancellationToken));
    }

    // Provider secrets that can't be read while Fail is set (as secrets.bin locked by another process)
    private sealed class FlakyStore : ITokenStore
    {
        private readonly InMemoryTokenStore _inner = new();

        public FlakyStore() => _inner.SetProviderSecret("google-maps", "api_key", Key);

        public bool Fail { get; set; }

        public string? GetProviderSecret(string providerId, string key) =>
            Fail ? throw new InvalidDataException("unreadable") : _inner.GetProviderSecret(providerId, key);

        public void SetProviderSecret(string providerId, string key, string value) => _inner.SetProviderSecret(providerId, key, value);

        public void RemoveProviderSecrets(string providerId) => _inner.RemoveProviderSecrets(providerId);

        public OAuthClientCredentials? GetClientCredentials() => _inner.GetClientCredentials();

        public void SetClientCredentials(OAuthClientCredentials credentials) => _inner.SetClientCredentials(credentials);

        public void ClearClientCredentials() => _inner.ClearClientCredentials();

        public string? GetRefreshToken(string accountId) => _inner.GetRefreshToken(accountId);

        public void SetRefreshToken(string accountId, string refreshToken) => _inner.SetRefreshToken(accountId, refreshToken);

        public void RemoveRefreshToken(string accountId) => _inner.RemoveRefreshToken(accountId);

        public IReadOnlyList<string> GetAccountIds() => _inner.GetAccountIds();
    }
}
