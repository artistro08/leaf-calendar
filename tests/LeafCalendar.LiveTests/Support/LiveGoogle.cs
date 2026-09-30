using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LeafCalendar.LiveTests.Support;

/// <summary>
/// Raw Google calls that live tests use to arrange and check data.
/// </summary>
/// <remarks>
/// Safety: the account is a real one, so every write is refused unless it targets a calendar this instance created.
/// </remarks>
public sealed class LiveGoogle(LiveAccount live)
{
    static readonly Uri BaseUri = new("https://www.googleapis.com/calendar/v3/");

    readonly HashSet<string> _owned = [];

    /// <summary>The calendars this instance created.</summary>
    public IReadOnlyCollection<string> Owned => _owned;

    /// <summary>Refuses to go on unless <paramref name="calendarId"/> was created by this instance.</summary>
    public void RequireOwned(string calendarId)
    {
        if (!_owned.Contains(calendarId))
        {
            throw new InvalidOperationException("Live tests may only write to a calendar they created in this run.");
        }
    }

    /// <summary>Creates a "Leaf live test &lt;guid&gt;" calendar and returns its ID.</summary>
    public async Task<string> CreateTestCalendarAsync(CancellationToken ct)
    {
        var body = new JsonObject { ["summary"] = $"Leaf live test {Guid.NewGuid():N}" };
        var created = await SendAsync(HttpMethod.Post, "calendars", body, ct);
        var id      = created!["id"]!.GetValue<string>();
        _owned.Add(id);
        return id;
    }

    /// <summary>Deletes a calendar.</summary>
    public Task DeleteCalendarAsync(string calendarId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"calendars/{Uri.EscapeDataString(calendarId)}", null, ct);

    /// <summary>Creates a one-hour event tomorrow and returns its ID.</summary>
    public async Task<string> InsertEventAsync(string calendarId, string summary, CancellationToken ct)
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(15), TimeSpan.Zero);
        var body = new JsonObject
        {
            ["summary"] = summary,
            ["start"]   = new JsonObject { ["dateTime"] = start.ToString("O") },
            ["end"]     = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("O") },
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Changes an event's title.</summary>
    public Task PatchSummaryAsync(string calendarId, string eventId, string summary, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", new JsonObject { ["summary"] = summary }, ct);

    /// <summary>Deletes an event.</summary>
    public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", null, ct);

    /// <summary>Creates a repeating event with a start in <paramref name="timeZone"/> and returns its ID.</summary>
    public async Task<string> InsertRecurringEventAsync(string calendarId, string startLocal, string endLocal, string timeZone, string[] recurrence, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["summary"]    = "Leaf live recurrence",
            ["start"]      = new JsonObject { ["dateTime"] = startLocal, ["timeZone"] = timeZone },
            ["end"]        = new JsonObject { ["dateTime"] = endLocal, ["timeZone"] = timeZone },
            ["recurrence"] = new JsonArray([.. recurrence.Select(r => (JsonNode)r)]),
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Returns Google's own occurrence starts (UTC) for a repeating event.</summary>
    public async Task<List<DateTimeOffset>> ListInstanceStartsAsync(string calendarId, string eventId, CancellationToken ct)
    {
        var page = await SendAsync(HttpMethod.Get, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}/instances?maxResults=250", null, ct);

        return [.. page!["items"]!.AsArray().Select(i => DateTimeOffset.Parse(i!["start"]!["dateTime"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime())];
    }

    /// <summary>Google's current copy of an event, or null when it's gone.</summary>
    public async Task<JsonNode?> GetEventAsync(string calendarId, string eventId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await live.Services.AccessTokens.GetAccessTokenAsync(live.AccountId, ct));

        using var response = await LiveAccount.Http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Creates a one-hour event tomorrow on <paramref name="organizerCalendarId"/> that invites <paramref name="guestCalendarId"/>,
    /// so the guest calendar gets its own copy it can reply to, and returns its ID.
    /// </summary>
    /// <remarks>
    /// Inviting the organizer's own calendar doesn't work: Google marks that guest as the organizer, and organizers don't reply.
    /// </remarks>
    public async Task<string> InsertInviteAsync(string organizerCalendarId, string guestCalendarId, string summary, CancellationToken ct)
    {
        // Guard: The Guest Calendar Gets A Copy, So It Must Be One This Run Created Too
        RequireOwned(guestCalendarId);

        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(16), TimeSpan.Zero);
        var body = new JsonObject
        {
            ["summary"]   = summary,
            ["start"]     = new JsonObject { ["dateTime"] = start.ToString("O") },
            ["end"]       = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("O") },
            ["attendees"] = new JsonArray(new JsonObject { ["email"] = guestCalendarId }),
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(organizerCalendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Sets a calendar's default popup reminder (in the account's calendar list) to <paramref name="minutes"/>.</summary>
    public async Task SetDefaultRemindersAsync(string calendarId, int minutes, CancellationToken ct)
    {
        // Guard: Only A Calendar This Run Created (the path starts with users/me, so the generic guard can't see it)
        RequireOwned(calendarId);

        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(BaseUri, $"users/me/calendarList/{Uri.EscapeDataString(calendarId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await live.Services.AccessTokens.GetAccessTokenAsync(live.AccountId, ct));
        request.Content = JsonContent.Create(new JsonObject
        {
            ["defaultReminders"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = minutes }),
        });

        using var response = await LiveAccount.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates a one-hour event at <paramref name="start"/> with the given <c>reminders</c> object and returns its ID.</summary>
    public async Task<string> InsertEventWithRemindersAsync(string calendarId, DateTimeOffset start, JsonObject reminders, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["summary"]   = "Leaf live reminders",
            ["start"]     = new JsonObject { ["dateTime"] = start.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
            ["end"]       = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
            ["reminders"] = reminders,
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        // Guard: Writes Only Go To Calendars This Run Created
        if (method != HttpMethod.Get && path != "calendars")
        {
            RequireOwned(Uri.UnescapeDataString(path.Split('/')[1]));
        }

        using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await live.Services.AccessTokens.GetAccessTokenAsync(live.AccountId, ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await LiveAccount.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }
}
