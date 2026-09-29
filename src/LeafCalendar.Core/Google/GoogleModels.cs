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
    /// <summary>Stable Google account ID; Leaf's account ID.</summary>
    public string Sub { get; set; } = "";

    /// <summary>Account email; used for Meet <c>authuser</c>.</summary>
    public string Email { get; set; } = "";

    /// <summary>Display name.</summary>
    public string? Name { get; set; }

    /// <summary>Avatar image address.</summary>
    public string? Picture { get; set; }
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

/// <summary>One page of <c>calendars/{id}/events</c>. Items stay raw so Leaf stores Google's full JSON.</summary>
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

/// <summary>The event fields Leaf indexes. The rest stays in the raw JSON.</summary>
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
