# Location Suggestions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Opt-in Google Maps place suggestions in the editor's Location box and through a `places.search` local API method, signed with the user's own Maps API key.

**Architecture:** A Core `PlaceSearch` (modeled on `ContactSearch`) owns the key (through `ITokenStore`) and the Places Autocomplete (New) call, and `IsOn(LeafSettings)` decides whether the feature is on. The App builds one in `LeafServices`; Settings › General saves the switch and key; the editor's Location box becomes an `AutoSuggestBox` fed by a small `PlaceSuggestions` helper; the local API reaches it through `IApiHost.Places`.

**Tech Stack:** C# 14, .NET 10, WinUI 3, Native AOT, `System.Text.Json` source generation, xunit v3, FlaUI.

**Spec:** `docs/superpowers/specs/2026-10-15-location-suggestions-design.md`

## Global Constraints

- Build has 0 warnings (`TreatWarningsAsErrors`); no trim/AOT warnings; JSON only through `GoogleJsonContext` / `ApiJsonContext`.
- Doc comments on every public member; namespaces match folders (`LeafCalendar.Core.Places`); CRLF line endings, edit files in place.
- The key goes only through `ITokenStore` (`"google-maps"`, `"api_key"`), only in the `X-Goog-Api-Key` header; never in a URL, never logged. Logs carry HTTP status only, never the query or results.
- Copy, verbatim (owner-approved 2026-10-15): "Location suggestions" · "Suggest places from Google Maps as you type a location. Needs a Maps API key with Places API (New) turned on." · "Maps API key" · "The key works." · "Google didn't accept this key. Check that Places API (New) and billing are turned on for it." · "Google Maps" · "Location suggestions are off." · "Location suggestions are off in Settings, or no Maps API key is saved."
- The app is always "Leaf Calendar"; US English; no commits to `main` (work on `feat/location-suggestions`, open a PR at the end).

## Review Focus

1. **Secrets file can't be read** (`InvalidDataException` from `GetProviderSecret`): suggestions are simply off, nothing throws, and the next call reads again. Test in Task 1.
2. **Hostile text from Google** (bidi overrides, control characters, very long names): cleaned and capped before it reaches the list or the event. Test in Task 1.
3. **Google answers 200 with junk JSON**: empty list, logged as `status=200`, no exception. Test in Task 1.
4. **The user keeps typing**: an older search's result never lands over a newer one (the `LatestSearch` + "text still in the box" check). Covered by the UI test in Task 6 typing in two steps.
5. **Key with surrounding spaces pasted from the console**: trimmed on save; a box cleared to empty removes the key. Test in Task 1.

---

### Task 1: Core `PlaceSearch`, models, endpoint, setting

**Files:**
- Create: `src/LeafCalendar.Core/Places/PlaceSearch.cs`
- Modify: `src/LeafCalendar.Core/Google/GoogleModels.cs` (add a PLACES section at the end)
- Modify: `src/LeafCalendar.Core/Google/GoogleJsonContext.cs` (two `[JsonSerializable]` lines)
- Modify: `src/LeafCalendar.Core/Google/GoogleEndpoints.cs` (add `PlacesApi`)
- Modify: `src/LeafCalendar.Core/Settings/LeafSettings.cs` (add `LocationSuggestions`)
- Modify: `src/LeafCalendar.Core/Auth/ITokenStore.cs` (provider-secret doc comments say "an integration's (a scheduling provider or Google Maps)")
- Test: `tests/LeafCalendar.Tests/PlaceSearchTests.cs`

**Interfaces:**
- Produces: `Place(string Name, string Address, string Text)`; `enum KeyCheck { Works, Refused, Unreachable }`; `PlaceSearch(HttpClient http, ITokenStore tokenStore, AppLog log, GoogleEndpoints? endpoints = null)` with `const int MaxResults = 5`, `const int MaxQuery = 100`, `bool HasKey`, `bool IsOn(LeafSettings)`, `void SaveKey(string? key)`, `Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken ct)`, `Task<KeyCheck> CheckKeyAsync(string key, CancellationToken ct)`, `TimeSpan Timeout { get; init; }`; `LeafSettings.LocationSuggestions`; `GoogleEndpoints.PlacesApi`.

- [ ] **Step 1: Write the failing tests** (`PlaceSearchTests.cs`)

```csharp
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
        $$$"""{"placePrediction":{"text":{"text":"{{{text}}}"},"structuredFormat":{"mainText":{"text":"{{{main}}}"},"secondaryText":{"text":"{{{secondary}}}"}}}}""";

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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*PlaceSearchTests"`
Expected: build error, `LeafCalendar.Core.Places` does not exist.

- [ ] **Step 3: Add the models** (end of `GoogleModels.cs`)

```csharp
// =========================================================================
// PLACES
// =========================================================================

/// <summary>A Places Autocomplete (New) request: what was typed, and the language to answer in.</summary>
public sealed class PlacesAutocompleteRequest
{
    /// <summary>The text typed so far.</summary>
    public string Input { get; set; } = "";

    /// <summary>A BCP-47 language for the answer (Windows' display language), or null for Google's default.</summary>
    public string? LanguageCode { get; set; }
}

/// <summary>A Places Autocomplete (New) answer (only the fields Leaf Calendar's field mask asks for).</summary>
public sealed class PlacesAutocompleteResponse
{
    /// <summary>Suggestions, best first (at most five).</summary>
    public List<PlacesSuggestion>? Suggestions { get; set; }
}

/// <summary>One suggestion; only place predictions are used.</summary>
public sealed class PlacesSuggestion
{
    /// <summary>A place, or null for a query prediction.</summary>
    public PlacePrediction? PlacePrediction { get; set; }
}

/// <summary>A suggested place: its full line, and the same line split into the name and the address.</summary>
public sealed class PlacePrediction
{
    /// <summary>The full line ("Blue Bottle Coffee, Mint Street, San Francisco, CA, USA").</summary>
    public PlacesText? Text { get; set; }

    /// <summary>The name and the address apart.</summary>
    public PlacesStructuredFormat? StructuredFormat { get; set; }
}

/// <summary>A prediction's name (<see cref="MainText"/>) and address (<see cref="SecondaryText"/>).</summary>
public sealed class PlacesStructuredFormat
{
    /// <summary>The place's name.</summary>
    public PlacesText? MainText { get; set; }

    /// <summary>The place's address.</summary>
    public PlacesText? SecondaryText { get; set; }
}

/// <summary>A piece of text in a Places answer.</summary>
public sealed class PlacesText
{
    /// <summary>The text.</summary>
    public string? Text { get; set; }
}
```

In `GoogleJsonContext.cs`, after `FreeBusyResponse`:

```csharp
[JsonSerializable(typeof(PlacesAutocompleteRequest))]
[JsonSerializable(typeof(PlacesAutocompleteResponse))]
```

- [ ] **Step 4: Add `PlacesApi` to `GoogleEndpoints`**

Record gets a last parameter `Uri PlacesApi`; doc says "OAuth, Calendar, People, and Places requests". `Default` adds `new Uri("https://places.googleapis.com/v1/")`; `ForFake` adds `new Uri(root, "places/v1/")`. Grep `new GoogleEndpoints(` in `src` and `tests` and add the argument wherever it's constructed directly.

- [ ] **Step 5: Add the setting** (`LeafSettings.cs`, after `ShowWeekNumbers`)

```csharp
    /// <summary>Suggest places from Google Maps in the editor's Location box and through <c>places.search</c> (needs a saved Maps API key).</summary>
    public bool LocationSuggestions { get; init; }
```

- [ ] **Step 6: Write `PlaceSearch.cs`**

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;

namespace LeafCalendar.Core.Places;

/// <summary>A suggested place: its name, its address (may be empty), and the full line that goes into an event's location.</summary>
public sealed record Place(string Name, string Address, string Text);

/// <summary>What Google said about a Maps API key.</summary>
public enum KeyCheck
{
    /// <summary>Google answered a lookup with it.</summary>
    Works,

    /// <summary>Google refused it (not valid, Places API (New) off, or billing off).</summary>
    Refused,

    /// <summary>Google couldn't be reached, so nothing is known about the key.</summary>
    Unreachable,
}

/// <summary>
/// Location suggestions from Google Maps (Places Autocomplete (New)), signed with the user's own Maps API key.
/// </summary>
/// <remarks>
/// <para>
/// It owns the key: <see cref="SaveKey"/> keeps it in <see cref="ITokenStore"/> and in memory, and the first use reads it
/// once, so typing never decrypts the secrets file. Secrets that can't be read right now count as no key and are read
/// again next time. The key is sent only in the <c>X-Goog-Api-Key</c> header.
/// </para>
/// <para>
/// Only Autocomplete is called (no Place Details), so only Google's "Autocomplete Requests" SKU is billed. Results are
/// untrusted text, cleaned and capped. Nothing is stored; a failure is logged by HTTP status only, never the query, the
/// results, or the key.
/// </para>
/// </remarks>
/// <seealso href="https://developers.google.com/maps/documentation/places/web-service/place-autocomplete"/>
public sealed class PlaceSearch(HttpClient http, ITokenStore tokenStore, AppLog log, GoogleEndpoints? endpoints = null)
{
    /// <summary>Most suggestions returned (Google's own limit).</summary>
    public const int MaxResults = 5;

    /// <summary>Longest query searched.</summary>
    public const int MaxQuery = 100;

    private const int MaxText = 300;
    private const string ProviderId = "google-maps";
    private const string KeyName = "api_key";
    private const string FieldMask = "suggestions.placePrediction.text.text,suggestions.placePrediction.structuredFormat";

    // A lookup any working key answers
    private const string CheckQuery = "Eiffel Tower";

    // Relative path starts with "./" so the colon isn't read as a URI scheme
    private readonly Uri _autocomplete = new((endpoints ?? GoogleEndpoints.Default).PlacesApi, "./places:autocomplete");

    private readonly Lock _gate = new();
    private string? _key;
    private bool _keyLoaded;

    /// <summary>How long a lookup may take before it counts as failed, so a slow Google can't stall typing.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True when a Maps API key is saved.</summary>
    public bool HasKey => Key() is not null;

    /// <summary>True when location suggestions are turned on in <paramref name="settings"/> and a key is saved.</summary>
    public bool IsOn(LeafSettings settings) => settings.LocationSuggestions && HasKey;

    /// <summary>Saves the key, trimmed; an empty one removes the saved key.</summary>
    /// <exception cref="InvalidDataException">The secrets file can't be read right now; nothing was saved.</exception>
    public void SaveKey(string? key)
    {
        key = key?.Trim();
        lock (_gate)
        {
            if (string.IsNullOrEmpty(key))
            {
                tokenStore.RemoveProviderSecrets(ProviderId);
                _key = null;
            }
            else
            {
                tokenStore.SetProviderSecret(ProviderId, KeyName, key);
                _key = key;
            }

            _keyLoaded = true;
        }
    }

    /// <summary>
    /// Places matching <paramref name="query"/>, best first, at most <see cref="MaxResults"/>. An empty or too long
    /// query, no key, and every Google, network, timeout, or malformed-answer problem give an empty list.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled (the user kept typing).</exception>
    public async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken ct)
    {
        // Nothing To Search
        query = query.Trim();
        if (query.Length is 0 or > MaxQuery || Key() is not { } key)
        {
            return [];
        }

        var status = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);

        try
        {
            using var response = await SendAsync(key, query, limit.Token);
            status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return Clean(await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.PlacesAutocompleteResponse, limit.Token));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            status = ex is HttpRequestException { StatusCode: { } code } ? (int)code : status;
        }

        log.Info("places.search.failed", string.Create(CultureInfo.InvariantCulture, $"status={status}"));
        return [];
    }

    /// <summary>One lookup with <paramref name="key"/> (trimmed), for Settings to say whether Google accepts it.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<KeyCheck> CheckKeyAsync(string key, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);

        try
        {
            using var response = await SendAsync(key.Trim(), CheckQuery, limit.Token);
            return response.IsSuccessStatusCode ? KeyCheck.Works
                : response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden ? KeyCheck.Refused
                : KeyCheck.Unreachable;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            return KeyCheck.Unreachable;
        }
    }

    // The saved key, read once; unreadable secrets count as none and are read again next time
    private string? Key()
    {
        lock (_gate)
        {
            if (!_keyLoaded)
            {
                try
                {
                    _key = tokenStore.GetProviderSecret(ProviderId, KeyName);
                    _keyLoaded = true;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }

            return _key;
        }
    }

    // One autocomplete call; the key only in its header
    private async Task<HttpResponseMessage> SendAsync(string key, string query, CancellationToken ct)
    {
        var body = new PlacesAutocompleteRequest { Input = query, LanguageCode = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : null };
        using var request = new HttpRequestMessage(HttpMethod.Post, _autocomplete)
        {
            Content = JsonContent.Create(body, GoogleJsonContext.Default.PlacesAutocompleteRequest),
        };
        request.Headers.Add("X-Goog-Api-Key", key);
        request.Headers.Add("X-Goog-FieldMask", FieldMask);

        return await http.SendAsync(request, ct);
    }

    // Place predictions as one-line text, capped; one without a full line is dropped, and one without a name shows its line
    private static List<Place> Clean(PlacesAutocompleteResponse? page) =>
        [.. (page?.Suggestions ?? [])
            .Select(s => s.PlacePrediction)
            .OfType<PlacePrediction>()
            .Select(p => new Place(
                DisplayText.Clean(p.StructuredFormat?.MainText?.Text, MaxText),
                DisplayText.Clean(p.StructuredFormat?.SecondaryText?.Text, MaxText),
                DisplayText.Clean(p.Text?.Text, MaxText)))
            .Where(p => p.Text.Length > 0)
            .Select(p => p.Name.Length > 0 ? p : p with { Name = p.Text })
            .Take(MaxResults)];
}
```

- [ ] **Step 7: Update the `ITokenStore` doc comments** — the three provider-secret members say "an integration's (a scheduling provider or Google Maps)" in place of "a scheduling provider's".

- [ ] **Step 8: Run the tests, then the full logic suite and build**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*PlaceSearchTests"` → all pass.
Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Run the full logic suite → all pass.

- [ ] **Step 9: Commit**

```bash
git add src/LeafCalendar.Core tests/LeafCalendar.Tests/PlaceSearchTests.cs
git commit -m "feat(places): PlaceSearch looks up Google Maps places with the user's own Maps API key"
```

---

### Task 2: Local API `places.search`

**Files:**
- Create: `src/LeafCalendar.Core/Api/ApiPlaces.cs`
- Modify: `src/LeafCalendar.Core/Api/ApiErrors.cs` (add `NotEnabled`)
- Modify: `src/LeafCalendar.Core/Api/ApiJsonContext.cs` (records + `[JsonSerializable]`)
- Modify: `src/LeafCalendar.Core/Api/ApiDispatcher.cs` (method name + case)
- Modify: `src/LeafCalendar.Core/Api/ApiContext.cs` (`IApiHost.Places`)
- Modify: `tests/LeafCalendar.Tests/Support/ApiHarness.cs` (`FakeApiHost.Places`)
- Modify: `src/LeafCalendar.App/App.Api.cs` (`ApiHost.Places`; needs `LeafServices.Places` from Task 3, so add that property here first: see Task 3 Step 1)
- Modify: `docs/api.md`, `docs/superpowers/specs/2026-10-13-local-api-design.md`
- Test: `tests/LeafCalendar.Tests/ApiPlacesTests.cs`

**Interfaces:**
- Consumes: `PlaceSearch`, `PlaceSearch.IsOn`, `PlaceSearch.MaxQuery`, `PlaceSearch.SaveKey` (Task 1).
- Produces: `IApiHost.PlaceSearch? Places { get; }`; `ApiErrors.NotEnabled = "not_enabled"`; `PlacesParams(string? Query = null)`; `PlaceResult(string Name, string? Address, string Text)`.

- [ ] **Step 1: Write the failing tests** (`ApiPlacesTests.cs`)

```csharp
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
```

Note: check `ApiParams.Write`'s naming/null handling against `people.search`'s `"name":null` (its docs show null names are written). If `ApiJsonContext` omits nulls, change the expected JSON to match what `people.search` does and keep `address` consistent with it.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*ApiPlacesTests"` → build error (`FakeApiHost.Places` missing).

- [ ] **Step 3: Add the host member, error code, and records**

`ApiContext.cs`, in `IApiHost` after `Contacts`:

```csharp
    /// <summary>Google Maps place search (on only when <see cref="PlaceSearch.IsOn"/> says so for <see cref="Settings"/>), or null.</summary>
    PlaceSearch? Places { get; }
```

(add `using LeafCalendar.Core.Places;`)

`ApiErrors.cs`, after `Unavailable`:

```csharp
    /// <summary>Location suggestions are off in Settings, or no Maps API key is saved.</summary>
    public const string NotEnabled = "not_enabled";
```

`ApiJsonContext.cs`: next to `PeopleParams` / `PersonResult`:

```csharp
/// <summary><c>places.search</c> params.</summary>
public sealed record PlacesParams(string? Query = null);
```

```csharp
/// <summary>A suggested place: its name, its address (null when Google gave none), and the line to put in an event's <c>location</c>.</summary>
public sealed record PlaceResult(string Name, string? Address, string Text);
```

and `[JsonSerializable(typeof(PlacesParams))]`, `[JsonSerializable(typeof(List<PlaceResult>))]` after the people lines. Match the surrounding records' doc comment style.

`ApiHarness.cs` `FakeApiHost`, after `Contacts`:

```csharp
    /// <inheritdoc />
    public PlaceSearch? Places { get; set; }
```

- [ ] **Step 4: Write `ApiPlaces.cs`**

```csharp
using System.Text.Json;
using LeafCalendar.Core.Places;

namespace LeafCalendar.Core.Api;

/// <summary><c>places.search</c> (local API spec 4): the places the editor's Location box suggests, for an app's location field.</summary>
internal static class ApiPlaces
{
    // Off (or no key) is an error an app can hide its box on; Google's own failures are an empty list, as in the editor
    public static async Task<JsonElement> SearchAsync(ApiContext context, PlacesParams p, CancellationToken ct)
    {
        if (context.Host.Places is not { } places || !places.IsOn(context.Host.Settings))
        {
            throw new ApiException(ApiErrors.NotEnabled, "Location suggestions are off.");
        }

        var query = (p.Query ?? "").Trim();
        if (query.Length > PlaceSearch.MaxQuery)
        {
            throw ApiTime.Invalid("query is at most 100 characters.");
        }

        var found = await places.SearchAsync(query, ct);

        List<PlaceResult> result = [.. found.Select(f => new PlaceResult(f.Name, f.Address.Length > 0 ? f.Address : null, f.Text))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListPlaceResult);
    }
}
```

- [ ] **Step 5: Wire the dispatcher** — add `"places.search"` to `Methods` after `"people.search"`, and the case:

```csharp
        "places.search" => ApiPlaces.SearchAsync(context, ApiParams.Read(r.Params, ApiJsonContext.Default.PlacesParams), ct),
```

- [ ] **Step 6: App host** — `App.Api.cs` `ApiHost`, after `Contacts`:

```csharp
        public LeafCalendar.Core.Places.PlaceSearch? Places => app._services?.Places;
```

(`LeafServices.Places` is added in Task 3 Step 1; do that step first if building the App now.)

- [ ] **Step 7: Docs**

`docs/api.md`, after the `people.search` section:

````markdown
### places.search

Places to suggest for an event's location, as Leaf Calendar's Location box does, from Google Maps. Only when location suggestions are on in Settings › General with a Maps API key saved; otherwise the reply is a `not_enabled` error. At most 5, best first. `query` is at most 100 characters; an empty one gives an empty list. `address` is null when Google gives none. Put `text` in the event's `location`. If Google can't be reached or refuses the key, the list is empty.

Apps that show these results must show "Google Maps" with them, as Google's terms require.

```json
{"jsonrpc":"2.0","id":13,"method":"places.search","params":{"query":"blue bottle"}}
{"jsonrpc":"2.0","id":13,"result":[{"name":"Blue Bottle Coffee","address":"Mint Street, San Francisco, CA, USA","text":"Blue Bottle Coffee, Mint Street, San Francisco, CA, USA"}]}
```
````

Errors table, after `unavailable`: `| `not_enabled` | Location suggestions are off in Settings, or no Maps API key is saved. |`

`2026-10-13-local-api-design.md` method table, after `people.search`: `| `places.search` | `{ query? }` | `[ { name, address, text } ]`, the Location box's suggestions: `PlaceSearch.SearchAsync` (`IApiHost.Places`), only while `PlaceSearch.IsOn(Settings)`, else `not_enabled`. At most `PlaceSearch.MaxResults`; Google failures are an empty list. |` and add `not_enabled` wherever that spec lists error codes.

- [ ] **Step 8: Run tests and build**

`--filter-class "*ApiPlacesTests"` passes; full logic suite passes; `dotnet build LeafCalendar.slnx -c Debug` 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add src tests docs
git commit -m "feat(api): places.search gives apps the Location box's Google Maps suggestions"
```

---

### Task 3: App services and the Settings card

**Files:**
- Modify: `src/LeafCalendar.App/LeafServices.cs`
- Modify: `src/LeafCalendar.App/Views/Settings/GeneralPage.xaml` (Locations group)
- Modify: `src/LeafCalendar.App/Views/Settings/GeneralPage.xaml.cs`

**Interfaces:**
- Consumes: `PlaceSearch`, `SaveKey`, `HasKey`, `CheckKeyAsync`, `KeyCheck`, `LeafSettings.LocationSuggestions`, `GoogleEndpoints.PlacesApi`.
- Produces: `LeafServices.Places` (`PlaceSearch`); AutomationIds `PlacesExpander`, `PlacesSwitch`, `MapsKeyBox`, `MapsKeyCheckText`.

- [ ] **Step 1: `LeafServices.Places`** — after `Endpoints = …` in the constructor:

```csharp
        // Location Suggestions (the user's own Maps API key; not tied to the OAuth client, so it outlives a client change)
        Places = new PlaceSearch(_http, Tokens, Log, Endpoints);
```

and the property (after `Bookings`):

```csharp
    /// <summary>Google Maps place search for the editor's Location box and <c>places.search</c> (on only while <see cref="PlaceSearch.IsOn"/>).</summary>
    public PlaceSearch Places { get; }
```

- [ ] **Step 2: XAML** — in the Locations group, after the "Open locations in" row, an expander built like Start with Windows:

```xml
                <!-- Location Suggestions (an expander, like Start with Windows: the switch in its header, the key inside) -->
                <Expander
                    x:Name="PlacesExpander"
                    Style="{StaticResource LeafSettingExpanderStyle}"
                    AutomationProperties.Name="Location suggestions"
                    AutomationProperties.AutomationId="PlacesExpander">
                    <Expander.Header>
                        <controls:SettingRow
                            Style="{StaticResource LeafExpanderHeaderRowStyle}"
                            Glyph="&#xE721;"
                            Header="Location suggestions"
                            Description="Suggest places from Google Maps as you type a location. Needs a Maps API key with Places API (New) turned on.">
                            <ToggleSwitch
                                x:Name="PlacesSwitch"
                                Style="{StaticResource LeafCardToggleSwitchStyle}"
                                Toggled="OnPlacesToggled"
                                AutomationProperties.Name="Location suggestions"
                                AutomationProperties.AutomationId="PlacesSwitch" />
                        </controls:SettingRow>
                    </Expander.Header>
                    <controls:SettingRow
                        Style="{StaticResource LeafExpanderItemRowStyle}"
                        BorderThickness="0"
                        Header="Maps API key">
                        <StackPanel Width="240" Spacing="4">
                            <PasswordBox
                                x:Name="MapsKeyBox"
                                LostFocus="OnMapsKeyLostFocus"
                                AutomationProperties.Name="Maps API key"
                                AutomationProperties.AutomationId="MapsKeyBox" />
                            <TextBlock
                                x:Name="MapsKeyCheckText"
                                Style="{StaticResource LeafSecondaryTextStyle}"
                                TextWrapping="Wrap"
                                Visibility="Collapsed"
                                AutomationProperties.LiveSetting="Polite"
                                AutomationProperties.AutomationId="MapsKeyCheckText" />
                        </StackPanel>
                    </controls:SettingRow>
                </Expander>
                <!-- /Location Suggestions -->
```

Run `XamlLintTests` after: the glyph, spacing (4), live region, and names must satisfy it.

- [ ] **Step 3: Code-behind**

In `Load()`, after `MapProviderBox.SelectedIndex = …`:

```csharp
        PlacesSwitch.IsOn = s.LocationSuggestions;
        MapsKeyBox.IsEnabled = s.LocationSuggestions;
```

In `OnNavigatedTo`, after `Load();`, show a saved key as dots without reading it into the page:

```csharp
        // A Saved Key Shows As Dots (its text never comes back into the page)
        MapsKeyBox.PlaceholderText = _context.Services.Places.HasKey ? "••••••••" : "";
```

Handlers in the "APPEARANCE, UPCOMING, AND LOCATIONS" section after `OnMapProviderChanged`:

```csharp
    private void OnPlacesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Turning On Opens The Key Row
        var on = PlacesSwitch.IsOn;
        MapsKeyBox.IsEnabled = on;
        PlacesExpander.IsExpanded |= on;
        _context.Save(s => s with { LocationSuggestions = on });
    }

    // A typed key saves when the box loses focus (an empty box leaves the saved key alone; Clear below removes it),
    // then one lookup says whether Google accepts it. Network trouble says nothing.
    private async void OnMapsKeyLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = MapsKeyBox.Password.Trim();
            if (key.Length == 0)
            {
                return;
            }

            var places = _context.Services.Places;
            places.SaveKey(key);
            MapsKeyBox.Password = "";
            MapsKeyBox.PlaceholderText = "••••••••";

            var check = await places.CheckKeyAsync(key, CancellationToken.None);
            MapsKeyCheckText.Text = check switch
            {
                KeyCheck.Works => "The key works.",
                KeyCheck.Refused => "Google didn't accept this key. Check that Places API (New) and billing are turned on for it.",
                _ => "",
            };
            MapsKeyCheckText.Visibility = MapsKeyCheckText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("places.key.save.failed", $"error={ex.GetType().Name}");
        }
    }
```

**Owner decision (2026-10-15):** the box shows the saved key (dots) with the stock peek button and a clear "x" button, right-aligned. Windows' stock `PasswordBox` has no clear button, so add `LeafPasswordBoxStyle` in `Styles/`: the stock `DefaultPasswordBoxStyle` template from the Windows App SDK's `generic.xaml`, with a second helper column holding the stock `DeleteButtonStyle` button (`x:Name="ClearButton"`, tooltip and `AutomationProperties.Name` "Clear", visible while the box has focus and text). Its click sets `Password = ""`. Since the box shows the saved key, an empty box on lost focus removes the key (`SaveKey(null)`), and the check line hides. `PlaceSearch` gains `string? SavedKey` (the key, for this box only), and `Load` sets `MapsKeyBox.Password = places.SavedKey ?? ""` once on navigation. The PlaceholderText lines above go.

Add `using LeafCalendar.Core.Places;`.

- [ ] **Step 4: Build** — `dotnet build LeafCalendar.slnx -c Debug` 0 warnings; `--filter-class "*XamlLintTests"` passes.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.App
git commit -m "feat(settings): a Location suggestions switch and Maps API key in Settings › General"
```

---

### Task 4: Editor Location box

**Files:**
- Create: `src/LeafCalendar.App/Views/PlaceSuggestions.cs`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs` (add `SearchPlacesAsync`; it sits with the other editor suggestion searches)
- Modify: `src/LeafCalendar.App/Views/EventEditorView.xaml` (Location)
- Modify: `src/LeafCalendar.App/Views/EventEditorView.xaml.cs`

**Interfaces:**
- Consumes: `LeafServices.Places`, `PlaceSearch.IsOn`, `PlaceSearch.SearchAsync`, `Place`, `LatestSearch<T>`, `CalendarViewModel.Fire`.
- Produces: `CalendarViewModel.SearchPlacesAsync(string text, CancellationToken ct)`; `PlaceSuggestion(string Name, string Address, bool IsAttribution)`; AutomationId `EditorLocation` on the `AutoSuggestBox`.

- [ ] **Step 1: View model search** (`CalendarViewModel.People.cs`, after `SearchPeopleAsync`)

```csharp
    /// <summary>
    /// Google Maps places for the editor's Location box, or none while location suggestions are off
    /// (<see cref="PlaceSearch.IsOn"/>), checked on every search so a Settings change applies at the next keystroke.
    /// </summary>
    public async Task<IReadOnlyList<Place>> SearchPlacesAsync(string text, CancellationToken ct) =>
        _services.Places.IsOn(Settings) ? await _services.Places.SearchAsync(text, ct) : [];
```

- [ ] **Step 2: `PlaceSuggestions.cs`** (mirrors `ContactSuggestions`)

```csharp
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Places;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>A row in the Location box's list: a place's name and address, or the "Google Maps" attribution at the end.</summary>
public sealed record PlaceSuggestion(string Name, string Address, bool IsAttribution);

/// <summary>
/// Google Maps suggestions for the editor's Location box: they wait until typing pauses, only the newest search lands,
/// and a result for text no longer in the box is dropped. The list ends with "Google Maps", as Google requires; picking
/// that row does nothing. While location suggestions are off no list ever opens, so the box works as a plain text box.
/// </summary>
internal sealed class PlaceSuggestions : IDisposable
{
    private readonly AutoSuggestBox _box;
    private readonly CalendarViewModel _vm;
    private readonly DispatcherQueueTimer _timer;
    private readonly LatestSearch<IReadOnlyList<Place>> _search = new();
    private readonly List<(PlaceSuggestion View, Place? Place)> _shown = [];

    /// <summary>Suggests places in <paramref name="box"/> as you type in it.</summary>
    public PlaceSuggestions(AutoSuggestBox box, CalendarViewModel vm)
    {
        _box = box;
        _vm = vm;

        // Suggestions Wait Until Typing Pauses
        _timer = box.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => vm.Fire(SuggestAsync, "places.suggest.failed");
        box.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _timer.Stop();
                _timer.Start();
            }
        };
    }

    /// <summary>The place behind a suggestion the box raised (found by reference in the list shown), or null (also for the attribution row).</summary>
    public Place? Find(object? suggestion) =>
        suggestion is null ? null : _shown.Find(s => ReferenceEquals(s.View, suggestion)).Place;

    /// <summary>Drops a search waiting for a pause or still running, so its suggestions never show.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _search.Cancel();
    }

    /// <summary>Cancels any search.</summary>
    public void Dispose() => Cancel();

    private async Task SuggestAsync()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0 || await _search.RunAsync(ct => _vm.SearchPlacesAsync(text, ct)) is not { } places || _box.Text.Trim() != text)
        {
            return;
        }

        _shown.Clear();
        if (places.Count > 0)
        {
            _shown.AddRange(places.Select(p => (new PlaceSuggestion(p.Name, p.Address, false), (Place?)p)));
            _shown.Add((new PlaceSuggestion("Google Maps", "", true), null));
        }

        _box.ItemsSource = _shown.Select(s => s.View).ToList();
        _box.IsSuggestionListOpen = _shown.Count > 0;
    }
}
```

- [ ] **Step 3: XAML** — replace the Location `TextBox` line with:

```xml
                <!-- Location (Google Maps suggestions while they're on in Settings; otherwise a plain box) -->
                <AutoSuggestBox
                    x:Name="LocationBox"
                    Header="Location"
                    UpdateTextOnSelect="False"
                    Text="{x:Bind Editor.Location, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                    QuerySubmitted="OnLocationQuerySubmitted"
                    Visibility="{x:Bind Editor.ShowLocation, Mode=OneWay}"
                    AutomationProperties.AutomationId="EditorLocation">
                    <AutoSuggestBox.ItemTemplate>
                        <DataTemplate x:DataType="local:PlaceSuggestion">
                            <StackPanel Padding="0,4">
                                <TextBlock Text="{x:Bind Name}" TextTrimming="CharacterEllipsis" Visibility="{x:Bind local:PlaceSuggestion.Shows(IsAttribution, false)}" />
                                <TextBlock Style="{StaticResource LeafSecondaryTextStyle}" Text="{x:Bind Address}" TextTrimming="CharacterEllipsis" Visibility="{x:Bind local:PlaceSuggestion.Shows(IsAttribution, false)}" />
                                <TextBlock Style="{StaticResource LeafSecondaryTextStyle}" HorizontalAlignment="Right" Text="{x:Bind Name}" Visibility="{x:Bind local:PlaceSuggestion.Shows(IsAttribution, true)}" />
                            </StackPanel>
                        </DataTemplate>
                    </AutoSuggestBox.ItemTemplate>
                </AutoSuggestBox>
```

Add to `PlaceSuggestion` a static helper used by the template:

```csharp
public sealed record PlaceSuggestion(string Name, string Address, bool IsAttribution)
{
    /// <summary>Visible when <paramref name="isAttribution"/> matches <paramref name="want"/> (the template's place rows or attribution row).</summary>
    public static Microsoft.UI.Xaml.Visibility Shows(bool isAttribution, bool want) =>
        isAttribution == want ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}
```

Check the editor's existing `xmlns` for the `LeafCalendar.App.Views` namespace (add `xmlns:local="using:LeafCalendar.App.Views"` if missing). A row's automation name: set `AutomationProperties.Name` on the `StackPanel` to `{x:Bind Name}` so the UI test (and Narrator) reads the place's name.

- [ ] **Step 4: Code-behind** — create the helper where the view gets its owner (next to where the guest timer is set up; the owner is `_owner`), dispose it where the view lets go of the owner, and handle the pick:

```csharp
    // Google Maps suggestions for the Location box (made once the owner is known)
    private PlaceSuggestions? _places;
```

```csharp
        _places?.Dispose();
        _places = new PlaceSuggestions(LocationBox, owner);
```

```csharp
    // A picked place puts its full line in Location; Enter on typed text keeps the text. The pick is found by reference
    // in our own list (never cast back from WinRT); the "Google Maps" row does nothing.
    private void OnLocationQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _places?.Cancel();
        if (Editor is { } editor && _places?.Find(args.ChosenSuggestion) is { } place)
        {
            editor.Location = place.Text;
        }

        sender.IsSuggestionListOpen = false;
    }
```

- [ ] **Step 5: Build and lint** — 0 warnings; `XamlLintTests` pass; full logic suite pass.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.App
git commit -m "feat(editor): the Location box suggests Google Maps places while location suggestions are on"
```

---

### Task 5: Docs and the live test

**Files:**
- Modify: `PRIVACY.md`
- Modify: `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (editor Location line, ~381)
- Create: `tests/LeafCalendar.LiveTests/LivePlacesTests.cs`
- Modify: `tests/LeafCalendar.LiveTests/README.md`

- [ ] **Step 1: `PRIVACY.md`** — a new section after "Scheduling (Cal.com)":

```markdown
## Location Suggestions (Google Maps)

This only applies if you turn on Location suggestions in Settings › General and save a Maps API key. Until you do, Leaf Calendar never contacts Google Maps.

- **What's sent:** the text you type into an event's location, with your Maps API key, to Google's Places API, so Google can suggest places. Google's own terms and privacy policy cover what Google does with it.
- **What's kept:** nothing from the suggestions, except the place you pick, which is saved in the event like any location you type.
- **Your key** is stored encrypted on your PC with your other Leaf Calendar secrets. Turn the switch off to stop lookups.
```

Add "Location suggestions go to Google's Places API, with your own Maps API key, only while you have them turned on." to the Google bullet in "Where Leaf Calendar Sends Data", and the key to the list in "What Leaf Calendar Keeps On Your PC" next to the other secrets.

These are new user-facing words: quote them to the owner in the final report for approval.

- [ ] **Step 2: Main spec** — after "- Location, opened in Google Maps or Bing Maps (setting).":

```markdown
  - Location suggestions from Google Maps (opt-in, Settings › General, with the user's own Maps API key). See `2026-10-15-location-suggestions-design.md`.
```

- [ ] **Step 3: Live test** (`LivePlacesTests.cs`)

```csharp
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Places;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.LiveTests;

public class LivePlacesTests
{
    [Fact]
    public async Task Search_RealKey_FindsAPlace()
    {
        var key = Environment.GetEnvironmentVariable("LEAF_LIVE_MAPS_KEY");
        if (string.IsNullOrEmpty(key))
        {
            Assert.Skip("Set LEAF_LIVE_MAPS_KEY to a Maps API key with Places API (New) turned on.");
        }

        using var logs = new TempFolder();
        using var http = new HttpClient();
        var search = new PlaceSearch(http, new InMemoryTokenStore(), new AppLog(logs.Path, TimeProvider.System));
        search.SaveKey(key);

        Assert.Equal(KeyCheck.Works, await search.CheckKeyAsync(key, TestContext.Current.CancellationToken));
        Assert.NotEmpty(await search.SearchAsync("Eiffel Tower", TestContext.Current.CancellationToken));
    }
}
```

Check that `LeafCalendar.LiveTests` references the test support types (`TempFolder`, `InMemoryTokenStore`); if not, use what its own `Support/` folder has. Add one README line under its setup section: "`LEAF_LIVE_MAPS_KEY`: a Maps API key with Places API (New) turned on, for `LivePlacesTests`."

- [ ] **Step 4: Build; commit**

```bash
git add PRIVACY.md docs tests/LeafCalendar.LiveTests
git commit -m "docs(places): privacy, spec and a live test for location suggestions"
```

---

### Task 6: Fake Google route and UI tests

**Files:**
- Modify: `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`
- Create: `tests/LeafCalendar.UITests/LocationSuggestionsTests.cs`

**Interfaces:**
- Consumes: AutomationIds `PlacesExpander`, `PlacesSwitch`, `MapsKeyBox`, `MapsKeyCheckText`, `EditorLocation`, `SuggestionsList`; launch flags `--fake-google`, `--start-date`.

- [ ] **Step 1: Fake route** — in `HandleAsync`, route `/places/` before `Route(...)` (it needs the key header):

```csharp
                    (status, content, location) = target.StartsWith("/calcom/", StringComparison.Ordinal)
                        ? await RouteCalComAsync(method, target, headers.GetValueOrDefault("authorization"), body)
                        : target.StartsWith("/places/", StringComparison.Ordinal)
                            ? Places(method, target, headers.GetValueOrDefault("x-goog-api-key"), body)
                            : Route(method, target, headers.GetValueOrDefault("if-match"), body);
```

and a PLACES section:

```csharp
    // =========================================================================
    // PLACES (Google Maps Autocomplete (New))
    // =========================================================================

    /// <summary>The Maps API key the fake accepts; any other is refused with 400, as Google does.</summary>
    public const string MapsKey = "fake-maps-key";

    // Two places for any input starting with "blue", nothing otherwise
    private static (int, string, string?) Places(string method, string target, string? key, string body)
    {
        if (method != "POST" || new Uri(new Uri("http://x"), target).AbsolutePath != "/places/v1/places:autocomplete")
        {
            return NotFound();
        }

        if (key != MapsKey)
        {
            return (400, Error(400, "API key not valid. Please pass a valid API key."), null);
        }

        var input = JsonNode.Parse(body)?["input"]?.GetValue<string>() ?? "";
        if (!input.StartsWith("blue", StringComparison.OrdinalIgnoreCase))
        {
            return (200, "{}", null);
        }

        return (200, """
            {"suggestions":[
              {"placePrediction":{"text":{"text":"Blue Bottle Coffee, Mint Street, San Francisco, CA, USA"},"structuredFormat":{"mainText":{"text":"Blue Bottle Coffee"},"secondaryText":{"text":"Mint Street, San Francisco, CA, USA"}}}},
              {"placePrediction":{"text":{"text":"Blue Note, West 3rd Street, New York, NY, USA"},"structuredFormat":{"mainText":{"text":"Blue Note"},"secondaryText":{"text":"West 3rd Street, New York, NY, USA"}}}}
            ]}
            """, null);
    }
```

Match the real `Error(...)` and `NotFound()` helper signatures in the file; `Places` is static only if those helpers are.

- [ ] **Step 2: UI tests** (`LocationSuggestionsTests.cs`) — reuse `GuestDirectoryTests`' patterns (`Edit`, `Type`, `Suggestions`, `WaitForSuggestion`, `CreateNew`), copied into this class (they are private there). Open Settings with the same helper `SettingsPagesTests` uses (read it and reuse its open-Settings call).

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class LocationSuggestionsTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // (Edit, Type, Suggestions, WaitForSuggestion, CreateNew: as in GuestDirectoryTests)

    // Settings › General: turns location suggestions on and saves a key (Tab moves focus off the box, which saves it)
    private static void TurnOn(LeafApp leaf, string key)
    {
        // open Settings › General with SettingsPagesTests' helper
        leaf.WaitFor("PlacesSwitch").AsToggleButton().Toggle();
        var box = leaf.WaitFor("MapsKeyBox").AsTextBox();
        box.Focus();
        Keyboard.Type(key);
        Keyboard.Press(VirtualKeyShort.TAB);
    }

    private static string CheckText(LeafApp leaf) =>
        Retry.WhileEmpty(() => leaf.WaitFor("MapsKeyCheckText").Name, TimeSpan.FromSeconds(10)).Result ?? "";

    [Fact]
    public void Settings_GoodKey_SaysItWorks()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);

        Assert.Equal("The key works.", CheckText(leaf));
    }

    [Fact]
    public void Settings_BadKey_SaysGoogleRefusedIt()
    {
        using var leaf = Launch();
        TurnOn(leaf, "wrong-key");

        Assert.Equal("Google didn't accept this key. Check that Places API (New) and billing are turned on for it.", CheckText(leaf));
    }

    [Fact]
    public void Editor_On_SuggestsPlaces_AndPickFillsLocation()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);
        // back to the calendar (title bar back button, as SettingsPagesTests does)
        CreateNew(leaf, "Coffee");

        // Two steps, so a stale first answer can't land over the newer one
        Type(leaf, "EditorLocation", "bl");
        Keyboard.Type("ue b");

        WaitForSuggestion(leaf, "Google Maps");
        WaitForSuggestion(leaf, "Blue Bottle Coffee").Click();

        Assert.Equal("Blue Bottle Coffee, Mint Street, San Francisco, CA, USA", Edit(leaf, "EditorLocation").Text);
    }

    [Fact]
    public void Editor_Off_NeverAsksGoogle()
    {
        using var leaf = Launch();
        CreateNew(leaf, "Coffee");

        Type(leaf, "EditorLocation", "blue");
        Thread.Sleep(1_000);

        Assert.Empty(Suggestions(leaf));
        Assert.DoesNotContain(_google.Requests, r => r.Contains("/places/", StringComparison.Ordinal));
        Assert.Equal("blue", Edit(leaf, "EditorLocation").Text);
    }
}
```

Fill in the Settings open/close calls with the real helpers from `SettingsPagesTests`/`SettingsTests` before running (read them; don't invent). If a fixed `Thread.Sleep` is against the suite's habits, use `Retry` for 1 s on "no suggestions" instead.

- [ ] **Step 3: Install and run** — `pwsh tools/sign-install.ps1`, then:
`dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*LocationSuggestionsTests"`, then the affected classes `*SettingsPagesTests`, `*GuestDirectoryTests`, `*EditingTests`, `*AccessibilityTests`, `*LocalApiTests`. All pass.

- [ ] **Step 4: Commit**

```bash
git add tests/LeafCalendar.UITests
git commit -m "test(places): UI tests for the Maps key check and Location suggestions"
```

---

### Task 7: Verify, review, PR

- [ ] **Step 1:** `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Full logic suite → all pass. `pwsh tools/publish-aot.ps1` (AOT/trim warnings: none) and `pwsh tools/check-package.ps1` pass.
- [ ] **Step 2:** Code review of the branch diff against this plan, the spec, and `CLAUDE.md` (security rules: key handling and logging; AOT rules; doc comments; copy). Fix what it finds; rerun affected tests.
- [ ] **Step 3:** `pwsh tools/sign-install.ps1` so the owner runs the optimized build.
- [ ] **Step 4:** Push `feat/location-suggestions` and open a PR to `main` with a plain summary, test results, and the new user-facing wording quoted. No PR comments, no status changes.
