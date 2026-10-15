# Location Suggestions Design

Leaf Calendar suggests places from Google Maps as you type an event's location, and offers the same search to apps through the local API. It's opt-in: it stays off until you turn it on in Settings and save your own Maps API key.

## Decisions

- **Opt-in, off by default.** With the setting off (or no key saved), nothing changes: Location is a plain text box and no request goes to Google Maps.
- **The user's own Maps API key,** not OAuth. Places API (New) only accepts the broad `cloud-platform` OAuth scope, so a key keeps sign-in and its permissions untouched. The key lives in the user's own Google Cloud project, so any Maps cost is theirs; the free tier (10,000 Autocomplete requests a month) covers normal use.
- **Autocomplete only.** No Place Details call: the suggestion's own text is what goes into the event. Only the "Autocomplete Requests" SKU is billed.
- **No prompts outside Settings.** The editor never shows a line or link about location suggestions. A key Google refuses shows up only as the key check in Settings.
- **Google's attribution rule.** Every suggestion list (the editor's, and any app's through the API) shows "Google Maps" with it.

## Setting

Settings › General gets one card:

- A `ToggleSwitch` titled "Location suggestions", described "Suggest places from Google Maps as you type a location. Needs a Maps API key with Places API (New) turned on." Saved in `LeafSettings.LocationSuggestions` (bool, default false).
- Inside the card's expander, a `PasswordBox` labeled "Maps API key", enabled while the switch is on. It shows the saved key as dots, with the stock reveal (peek) button and a clear ("x") button right-aligned; Windows' stock `PasswordBox` has no clear button, so `LeafPasswordBoxStyle` is the stock template plus the stock `DeleteButtonStyle` button TextBox uses. The key saves when the box loses focus, trimmed; an empty box deletes the saved key.
- Save (or Enter) stores the key, runs one test lookup and shows one line under the box: "Key is set up and active." or "Google didn't accept this key. Check that Places API (New) and billing are turned on for it." Network trouble shows nothing (the check is only about the key).

Location suggestions are **on** only when the switch is on and a key is saved.

## Secrets

The key is a secret. It goes only through `ITokenStore`, as `SetProviderSecret("google-maps", "api_key", key)` / `GetProviderSecret` / `RemoveProviderSecrets("google-maps")`, so it sits encrypted in `secrets.bin` next to the other secrets. The `ITokenStore` provider-secret doc comments change from "a scheduling provider's" to "an integration's (a scheduling provider or Google Maps)". The key is sent only in the `X-Goog-Api-Key` header, never in a URL, and never logged.

## Core: `Places/PlaceSearch.cs`

`PlaceSearch(HttpClient http, ITokenStore tokenStore, AppLog log, GoogleEndpoints? endpoints = null)` in `LeafCalendar.Core.Places`.

- `PlaceSearch` owns the key: `SaveKey(string? key)` writes or removes it through `ITokenStore` and keeps it in memory; the first read loads it once (secrets that can't be read right now count as no key and are read again next time), so typing never decrypts `secrets.bin`.
- `IsOn(LeafSettings settings)` is true when `settings.LocationSuggestions` is on and a key is saved. The editor and the API both use it.

- `Place(string Name, string Address, string Text)`: `Name` is `structuredFormat.mainText.text`, `Address` is `structuredFormat.secondaryText.text` (may be empty), `Text` is `text.text`, the full line put into the event's location.
- `SearchAsync(string query, CancellationToken ct)` → `IReadOnlyList<Place>`:
  - Trimmed query; empty or over 100 characters returns `[]` without a request.
  - No saved key returns `[]` without a request.
  - `POST {PlacesApi}places:autocomplete` with `X-Goog-Api-Key`, `X-Goog-FieldMask: suggestions.placePrediction.text.text,suggestions.placePrediction.structuredFormat`, and body `{"input": query, "languageCode": <Windows display language>}`.
  - At most 5 places (Google's own limit, also enforced here).
  - Untrusted text: each field goes through `DisplayText.Clean` and is capped at 300 characters; a prediction with no usable `Text` is dropped.
  - Every failure (HTTP error, network, timeout of 5 s, malformed JSON) returns `[]` and logs `places.search.failed` with the HTTP status only. Never the query, results, or key. Only the caller's own cancellation throws.
- `CheckKeyAsync(string key, CancellationToken ct)` → `KeyCheck` (`Works`, `Refused`, `Unreachable`): one autocomplete for a fixed query with the given key. 2xx is `Works`; 400/403 is `Refused`; anything else is `Unreachable`.
- JSON through `GoogleJsonContext` (source-generated), records `PlacesAutocompleteRequest` / `PlacesAutocompleteResponse`.
- `GoogleEndpoints` gains `PlacesApi` (`https://places.googleapis.com/v1/`, fake: `{root}places/v1/`).

## App

- `LeafServices` builds one `PlaceSearch` at startup (it doesn't depend on the OAuth client, so it isn't in `GoogleServices`) and exposes it as `Places`.
- **Editor:** the Location `TextBox` becomes an `AutoSuggestBox` (`AutomationId` `EditorLocation`, same as today). With location suggestions off it never opens a list, so it looks and works exactly like the old text box. Suggestions wait 250 ms after typing pauses, only the newest search lands (`LatestSearch`), and a result for text no longer in the box is dropped. Each row shows the name with the address under it in secondary text. The list ends with a non-selectable "Google Maps" row. Picking a place sets Location to its `Text`. Typed text is kept as-is when nothing is picked.
- Turning the setting off or removing the key takes effect from the next keystroke.

## Local API: `places.search`

- Params `PlacesParams(string? Query)`; result `List<PlaceResult(string Name, string? Address, string Text)>` (`Address` null when empty).
- `Query` over 100 characters → `ApiTime.Invalid("query is at most 100 characters.")`, as `people.search` does. Empty query → `[]`.
- Location suggestions off or no key → `ApiException("not_enabled", "Location suggestions are off.")`.
- Google failures → `[]` (as `people.search` does for contacts).
- `IApiHost` gains `PlaceSearch? Places { get; }`, next to `Contacts`; the method checks `Places.IsOn(Host.Settings)`. `ApiDispatcher` adds the method name and case.
- `docs/api.md`: a `places.search` section (with "Apps that show these results must show "Google Maps" with them, as Google's terms require.") and the error row `not_enabled` — "Location suggestions are off in Settings, or no Maps API key is saved."

## Privacy

`PRIVACY.md` gains: when location suggestions are on, the text you type into an event's location is sent to Google Maps (Places API) with your API key, under Google's terms; nothing from it is stored except the place you pick, saved in the event. The key is stored encrypted like the other secrets and removed by clearing the box.

## Copy (owner-approved 2026-10-15)

- "Location suggestions"
- "Suggest places from Google Maps as you type a location. Needs a Maps API key with Places API (New) turned on."
- "Maps API key"
- "Key is set up and active."
- "Google didn't accept this key. Check that Places API (New) and billing are turned on for it."
- "Google Maps" (attribution; Google's required text, never localized)
- "Location suggestions are off." (API error message)
- "Location suggestions are off in Settings, or no Maps API key is saved." (`not_enabled` in `docs/api.md`)

## Testing

- **Logic tests** (`PlaceSearchTests`, fake `HttpMessageHandler`): request shape (POST, header key, field mask, body), mapping, the 5 cap, cleaning of control/bidi characters, dropped empty predictions, no request for empty/long query or no key, every failure returns `[]`, logs carry no query or key, key never in the URL, `CheckKeyAsync` mapping.
- **Logic tests** (`ApiPlacesTests`): `not_enabled` when off, mapping and null address, long query refused, Google failure returns `[]`.
- **Settings:** `LeafSettings` round-trip with `LocationSuggestions`.
- **UI test** (`LocationSuggestionsTests`): the fake Google server answers `places/v1/places:autocomplete`; with the setting on and a key, typing shows suggestions and the "Google Maps" row, and picking one fills Location; with it off, Location is a plain box. Plus the key check line in Settings.
- **Live test:** one autocomplete with a real key, skipped until `LEAF_LIVE_MAPS_KEY` is set.

## Out of Scope

Location bias, Place Details, maps or photos, session tokens (no Details call, so they don't change billing), and suggestions in the details panel.
