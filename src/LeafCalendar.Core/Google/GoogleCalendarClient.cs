using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Core.Google;

/// <summary>
/// Google Calendar REST API v3 calls Leaf needs for sync.
/// </summary>
/// <remarks>
/// Events are listed with <c>singleEvents=false</c> so repeating events arrive as one master plus
/// exceptions, and with <c>showDeleted=true</c> so deletions arrive during incremental sync. A 401
/// drops the cached access token and retries once; a 410 means the sync token expired.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/events/list"/>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/sync"/>
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

    async Task<T> GetAsync<T>(string accountId, string relativePath, JsonTypeInfo<T> info, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, relativePath);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(accountId, ct));

            using var response = await http.SendAsync(request, ct);

            // Expired Access Token: Refresh Once
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                tokens.Forget(accountId);
                continue;
            }

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
    }
}
