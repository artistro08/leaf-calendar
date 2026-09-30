using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Core.Google;

/// <summary>
/// Google Calendar REST API v3 calls Leaf needs for sync and for sending edits.
/// </summary>
/// <remarks>
/// Events are listed with <c>singleEvents=false</c> so repeating events arrive as one master plus
/// exceptions, and with <c>showDeleted=true</c> so deletions arrive during incremental sync. A 401
/// drops the cached access token and retries once; a 410 means the sync token expired.
/// Writes send <c>If-Match</c> with the ETag the edit was made on, so a change made on Google in the meantime
/// comes back as 412 (<see cref="PreconditionFailedException"/>) instead of being overwritten.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/events/list"/>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/sync"/>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/version-resources"/>
public sealed class GoogleCalendarClient(HttpClient http, AccessTokenProvider tokens, GoogleEndpoints? endpoints = null)
{
    readonly Uri _baseUri = (endpoints ?? GoogleEndpoints.Default).CalendarApi;

    /// <summary>Returns every calendar in the account's list (all pages).</summary>
    public async Task<IReadOnlyList<CalendarListEntry>> ListCalendarsAsync(string accountId, CancellationToken ct)
    {
        var calendars = new List<CalendarListEntry>();
        string? pageToken = null;

        do
        {
            var path = "users/me/calendarList?maxResults=250&showHidden=true";
            if (pageToken is not null)
            {
                path += "&pageToken=" + Uri.EscapeDataString(pageToken);
            }

            var page = await GetAsync(accountId, path, GoogleJsonContext.Default.CalendarListPage, ct);
            calendars.AddRange(page.Items);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return calendars;
    }

    /// <summary>Returns one page of events (full sync when <paramref name="syncToken"/> is null).</summary>
    /// <exception cref="SyncTokenExpiredException">Google needs a full sync.</exception>
    public Task<EventsPage> ListEventsAsync(string accountId, string calendarId, string? syncToken, string? pageToken, CancellationToken ct)
    {
        var path = new StringBuilder($"calendars/{Uri.EscapeDataString(calendarId)}/events?maxResults=2500&showDeleted=true&singleEvents=false");

        if (syncToken is not null)
        {
            path.Append("&syncToken=").Append(Uri.EscapeDataString(syncToken));
        }

        if (pageToken is not null)
        {
            path.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        return GetAsync(accountId, path.ToString(), GoogleJsonContext.Default.EventsPage, ct);
    }

    // =========================================================================
    // WRITES (outbox)
    // =========================================================================

    /// <summary>Creates an event with Leaf's own ID and returns Google's JSON for it.</summary>
    /// <exception cref="ArgumentException"><paramref name="eventJson"/> has no <c>id</c>; Leaf always makes its own so a retry can't duplicate.</exception>
    /// <exception cref="DuplicateEventException">The ID exists already (an earlier try got through).</exception>
    public async Task<string> InsertEventAsync(string accountId, string calendarId, string eventJson, bool sendUpdates, CancellationToken ct)
    {
        // Client-Generated IDs Are Required
        if (GoogleJson.TryParse(eventJson, GoogleJsonContext.Default.GoogleEvent)?.Id is not { Length: > 0 })
        {
            throw new ArgumentException("The event JSON must carry a non-empty id.", nameof(eventJson));
        }

        using var response = await SendAsync(accountId, HttpMethod.Post, $"{EventsPath(calendarId)}?sendUpdates={Updates(sendUpdates)}", eventJson, null, ct);
        return await ReadEventAsync(response, ct, isInsert: true);
    }

    /// <summary>Changes the fields in <paramref name="patchJson"/> and returns Google's JSON.</summary>
    /// <param name="ifMatch">The ETag the edit was made on; null for an instance Google hasn't stored as its own row yet.</param>
    /// <exception cref="PreconditionFailedException">Google's copy changed since that ETag.</exception>
    /// <exception cref="EventGoneException">The event was deleted on Google.</exception>
    public async Task<string> PatchEventAsync(string accountId, string calendarId, string eventId, string patchJson, string? ifMatch, bool sendUpdates, CancellationToken ct)
    {
        using var response = await SendAsync(accountId, HttpMethod.Patch, $"{EventPath(calendarId, eventId)}?sendUpdates={Updates(sendUpdates)}", patchJson, ifMatch, ct);
        return await ReadEventAsync(response, ct);
    }

    /// <summary>Deletes an event (or cancels one instance of a series).</summary>
    /// <exception cref="PreconditionFailedException">Google's copy changed since <paramref name="ifMatch"/>.</exception>
    /// <exception cref="EventGoneException">It's already gone.</exception>
    public async Task DeleteEventAsync(string accountId, string calendarId, string eventId, string? ifMatch, bool sendUpdates, CancellationToken ct)
    {
        using var response = await SendAsync(accountId, HttpMethod.Delete, $"{EventPath(calendarId, eventId)}?sendUpdates={Updates(sendUpdates)}", null, ifMatch, ct);
        await ReadEventAsync(response, ct);
    }

    /// <summary>Moves an event to another calendar of the same account and returns Google's JSON.</summary>
    /// <exception cref="EventGoneException">The event was deleted on Google.</exception>
    public async Task<string> MoveEventAsync(string accountId, string calendarId, string eventId, string destinationCalendarId, bool sendUpdates, CancellationToken ct)
    {
        var path = $"{EventPath(calendarId, eventId)}/move?destination={Uri.EscapeDataString(destinationCalendarId)}&sendUpdates={Updates(sendUpdates)}";
        using var response = await SendAsync(accountId, HttpMethod.Post, path, null, null, ct);
        return await ReadEventAsync(response, ct);
    }

    /// <summary>Google's current JSON for an event, or null when it no longer exists.</summary>
    public async Task<string?> GetEventAsync(string accountId, string calendarId, string eventId, CancellationToken ct)
    {
        using var response = await SendAsync(accountId, HttpMethod.Get, EventPath(calendarId, eventId), null, null, ct);
        try
        {
            return await ReadEventAsync(response, ct);
        }
        catch (EventGoneException)
        {
            return null;
        }
    }

    // =========================================================================
    // HTTP
    // =========================================================================

    static string EventsPath(string calendarId) => $"calendars/{Uri.EscapeDataString(calendarId)}/events";

    static string EventPath(string calendarId, string eventId) => $"{EventsPath(calendarId)}/{Uri.EscapeDataString(eventId)}";

    static string Updates(bool sendUpdates) => sendUpdates ? "all" : "none";

    // Maps write statuses to Leaf's exceptions; success returns the body ("" for 204).
    // A 409 means "that ID exists" only for an insert; elsewhere it stays a GoogleApiException (a permanent refusal)
    static async Task<string> ReadEventAsync(HttpResponseMessage response, CancellationToken ct, bool isInsert = false)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.PreconditionFailed:
                throw new PreconditionFailedException();
            case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                throw new EventGoneException();
            case HttpStatusCode.Conflict when isInsert:
                throw new DuplicateEventException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleJson.ToExceptionAsync(response, ct);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    async Task<T> GetAsync<T>(string accountId, string relativePath, JsonTypeInfo<T> info, CancellationToken ct)
    {
        using var response = await SendAsync(accountId, HttpMethod.Get, relativePath, null, null, ct);

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new SyncTokenExpiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleJson.ToExceptionAsync(response, ct);
        }

        return await response.Content.ReadFromJsonAsync(info, ct)
            ?? throw new InvalidDataException("Google returned an empty response.");
    }

    // One call with a fresh access token; a 401 drops the cached token and retries once
    async Task<HttpResponseMessage> SendAsync(string accountId, HttpMethod method, string relativePath, string? body, string? ifMatch, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, relativePath);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(accountId, ct));

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            // Google's ETags are quoted strings; sent exactly as Google gave them
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            var response = await http.SendAsync(request, ct);

            // Expired Access Token: Refresh Once
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                tokens.Forget(accountId);
                continue;
            }

            return response;
        }
    }
}
