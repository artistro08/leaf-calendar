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

    /// <summary>The saved key, or null, for the Settings box that shows it (behind its peek button).</summary>
    public string? SavedKey => Key();

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
