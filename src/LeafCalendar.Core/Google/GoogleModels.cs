using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Google;

// =========================================================================
// OAUTH
// =========================================================================

/// <summary>Google token endpoint response.</summary>
public sealed class TokenResponse
{
    /// <summary>Short-lived access token.</summary>
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";

    /// <summary>Seconds until the access token expires.</summary>
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

    /// <summary>Long-lived refresh token; only present on first consent or rotation.</summary>
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

    /// <summary>Space-separated scopes actually granted.</summary>
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    /// <summary>Always "Bearer".</summary>
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
}

/// <summary>Google OAuth error body, e.g. <c>{"error":"invalid_grant"}</c>.</summary>
public sealed class OAuthErrorResponse
{
    /// <summary>OAuth error code.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>OpenID Connect user info.</summary>
public sealed class GoogleUserInfo
{
    /// <summary>Stable Google account ID; Leaf Calendar's account ID.</summary>
    public string Sub { get; set; } = "";

    /// <summary>Account email; used for Meet <c>authuser</c>.</summary>
    public string Email { get; set; } = "";

    /// <summary>Display name.</summary>
    public string? Name { get; set; }

    /// <summary>Avatar image address.</summary>
    public string? Picture { get; set; }

    /// <summary>Workspace domain (Google's <c>hd</c> claim); absent for personal accounts.</summary>
    public string? Hd { get; set; }
}

// =========================================================================
// API ERRORS
// =========================================================================

/// <summary>Google API error envelope: <c>{"error":{...}}</c>.</summary>
public sealed class ApiErrorEnvelope
{
    /// <summary>The error.</summary>
    public ApiError? Error { get; set; }
}

/// <summary>Google API error details.</summary>
public sealed class ApiError
{
    /// <summary>HTTP status code.</summary>
    public int Code { get; set; }

    /// <summary>Message from Google.</summary>
    public string? Message { get; set; }

    /// <summary>Individual errors with machine-readable reasons.</summary>
    public List<ApiErrorItem>? Errors { get; set; }

    /// <summary>Newer-style details (e.g. <c>SERVICE_DISABLED</c>); only <c>reason</c> is read.</summary>
    public List<ApiErrorItem>? Details { get; set; }
}

/// <summary>One Google API error item.</summary>
public sealed class ApiErrorItem
{
    /// <summary>Reason such as <c>rateLimitExceeded</c> or <c>fullSyncRequired</c>.</summary>
    public string? Reason { get; set; }

    /// <summary>Message from Google.</summary>
    public string? Message { get; set; }
}

// =========================================================================
// CALENDAR LIST
// =========================================================================

/// <summary>One page of <c>users/me/calendarList</c>.</summary>
public sealed class CalendarListPage
{
    /// <summary>Calendars on this page.</summary>
    public List<CalendarListEntry> Items { get; set; } = [];

    /// <summary>Token for the next page, if any.</summary>
    public string? NextPageToken { get; set; }
}

/// <summary>A calendar in the user's calendar list.</summary>
public sealed class CalendarListEntry
{
    /// <summary>Calendar ID (often an email address).</summary>
    public string Id { get; set; } = "";

    /// <summary>Calendar name.</summary>
    public string Summary { get; set; } = "";

    /// <summary>User's own name for the calendar.</summary>
    public string? SummaryOverride { get; set; }

    /// <summary>IANA time zone.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Hex background color.</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Hex foreground color.</summary>
    public string? ForegroundColor { get; set; }

    /// <summary><c>owner</c>, <c>writer</c>, <c>reader</c>, or <c>freeBusyReader</c>.</summary>
    public string AccessRole { get; set; } = "reader";

    /// <summary>True for the account's primary calendar.</summary>
    public bool Primary { get; set; }

    /// <summary>True when hidden in Google Calendar's list.</summary>
    public bool Hidden { get; set; }

    /// <summary>True when the calendar is ticked in Google Calendar's list (Google omits it when false).</summary>
    public bool Selected { get; set; }

    /// <summary>True when removed from the list.</summary>
    public bool Deleted { get; set; }

    /// <summary>Default reminders for events on this calendar.</summary>
    public List<ReminderOverride>? DefaultReminders { get; set; }
}

/// <summary>A reminder: method and minutes before start.</summary>
public sealed class ReminderOverride
{
    /// <summary><c>popup</c> or <c>email</c>.</summary>
    public string Method { get; set; } = "popup";

    /// <summary>Minutes before the event starts.</summary>
    public int Minutes { get; set; }
}

// =========================================================================
// EVENTS
// =========================================================================

/// <summary>One page of <c>calendars/{id}/events</c>. Items stay raw so Leaf Calendar stores Google's full JSON.</summary>
public sealed class EventsPage
{
    /// <summary>Raw event objects.</summary>
    public List<JsonElement> Items { get; set; } = [];

    /// <summary>Token for the next page, if any.</summary>
    public string? NextPageToken { get; set; }

    /// <summary>Token for the next incremental sync; only on the last page.</summary>
    public string? NextSyncToken { get; set; }

    /// <summary>Calendar time zone.</summary>
    public string? TimeZone { get; set; }
}

/// <summary>The event fields Leaf Calendar indexes. The rest stays in the raw JSON.</summary>
public sealed class GoogleEvent
{
    /// <summary>Event ID.</summary>
    public string Id { get; set; } = "";

    /// <summary><c>confirmed</c>, <c>tentative</c>, or <c>cancelled</c>.</summary>
    public string? Status { get; set; }

    /// <summary>Version tag for conflict detection.</summary>
    public string? Etag { get; set; }

    /// <summary>iCalendar UID shared by copies of the same event.</summary>
    [JsonPropertyName("iCalUID")] public string? ICalUid { get; set; }

    /// <summary>Start time or date.</summary>
    public EventDateTime? Start { get; set; }

    /// <summary>End time or date (exclusive).</summary>
    public EventDateTime? End { get; set; }

    /// <summary>RRULE/EXRULE/RDATE/EXDATE lines for a recurring master.</summary>
    public List<string>? Recurrence { get; set; }

    /// <summary>Master event ID when this is a single occurrence.</summary>
    public string? RecurringEventId { get; set; }

    /// <summary>The occurrence's original start when this is a single occurrence.</summary>
    public EventDateTime? OriginalStartTime { get; set; }

    /// <summary>Last modification time.</summary>
    public DateTimeOffset? Updated { get; set; }
}

/// <summary>Either an all-day <see cref="Date"/> or a timed <see cref="DateTime"/>.</summary>
public sealed class EventDateTime
{
    /// <summary>All-day date.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>Timed start/end with offset.</summary>
    public DateTimeOffset? DateTime { get; set; }

    /// <summary>IANA time zone the event was created in.</summary>
    public string? TimeZone { get; set; }
}

// =========================================================================
// PEOPLE
// =========================================================================

/// <summary>A People API search answer (<c>people:searchContacts</c> and <c>otherContacts:search</c>).</summary>
public sealed class PeopleSearchResponse
{
    /// <summary>Matches, best first.</summary>
    public List<PeopleSearchResult>? Results { get; set; }
}

/// <summary>One match.</summary>
public sealed class PeopleSearchResult
{
    /// <summary>The matched person.</summary>
    public PeoplePerson? Person { get; set; }
}

/// <summary>The fields Leaf Calendar asks for (<c>readMask=names,emailAddresses</c>).</summary>
public sealed class PeoplePerson
{
    /// <summary>Names; the first is the primary one.</summary>
    public List<PeopleName>? Names { get; set; }

    /// <summary>Email addresses.</summary>
    public List<PeopleEmail>? EmailAddresses { get; set; }
}

/// <summary>A person's name.</summary>
public sealed class PeopleName
{
    /// <summary>Name as Google shows it.</summary>
    public string? DisplayName { get; set; }
}

/// <summary>A person's email address.</summary>
public sealed class PeopleEmail
{
    /// <summary>The address.</summary>
    public string? Value { get; set; }
}

/// <summary><c>people:searchDirectoryPeople</c> answer.</summary>
public sealed class DirectorySearchResponse
{
    /// <summary>Matching people in your Workspace directory.</summary>
    public List<PeoplePerson>? People { get; set; }
}

// =========================================================================
// FREE/BUSY
// =========================================================================

/// <summary>One busy stretch.</summary>
public sealed record BusyRange(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Free/busy for one calendar or person: <see cref="Error"/> is Google's reason (e.g. <c>notFound</c>) when it had none to give.</summary>
public sealed record FreeBusyResult(IReadOnlyList<BusyRange> Busy, string? Error);

/// <summary><c>freeBusy.query</c> body.</summary>
public sealed class FreeBusyRequest
{
    /// <summary>RFC 3339 UTC start.</summary>
    public string TimeMin { get; set; } = "";

    /// <summary>RFC 3339 UTC end.</summary>
    public string TimeMax { get; set; } = "";

    /// <summary>Calendars or people.</summary>
    public List<FreeBusyItem> Items { get; set; } = [];
}

/// <summary>One calendar or address to query.</summary>
public sealed class FreeBusyItem
{
    /// <summary>Calendar ID or email.</summary>
    public string Id { get; set; } = "";
}

/// <summary><c>freeBusy.query</c> answer.</summary>
public sealed class FreeBusyResponse
{
    /// <summary>Per ID.</summary>
    public Dictionary<string, FreeBusyCalendar>? Calendars { get; set; }
}

/// <summary>One ID's answer.</summary>
public sealed class FreeBusyCalendar
{
    /// <summary>Busy stretches.</summary>
    public List<FreeBusyPeriod>? Busy { get; set; }

    /// <summary>Why there's no answer.</summary>
    public List<ApiErrorItem>? Errors { get; set; }
}

/// <summary>A busy stretch as Google sends it.</summary>
public sealed class FreeBusyPeriod
{
    /// <summary>Start.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>End.</summary>
    public DateTimeOffset End { get; set; }
}

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

    /// <summary>Place types to suggest only (<c>["(cities)"]</c> for cities), or null for any place.</summary>
    public List<string>? IncludedPrimaryTypes { get; set; }

    /// <summary>
    /// The search session these requests and the Place Details call that ends it share (Google bills the session's
    /// suggestions as one), or null.
    /// </summary>
    public string? SessionToken { get; set; }
}

/// <summary>A Place Details (New) answer with only its time zone (the field mask asks for nothing else).</summary>
public sealed class PlaceTimeZoneResponse
{
    /// <summary>The place's time zone, or null.</summary>
    public PlaceTimeZone? TimeZone { get; set; }
}

/// <summary>A place's time zone.</summary>
public sealed class PlaceTimeZone
{
    /// <summary>The IANA time zone ID ("America/Chicago").</summary>
    public string? Id { get; set; }
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

/// <summary>A suggested place: its ID, its full line, and the same line split into the name and the address.</summary>
public sealed class PlacePrediction
{
    /// <summary>The place's ID (for a Place Details call).</summary>
    public string? PlaceId { get; set; }

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
