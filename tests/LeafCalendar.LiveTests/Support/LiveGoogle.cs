using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LeafCalendar.LiveTests.Support;

/// <summary>
/// Raw Google calls that live tests use to arrange data. The app doesn't need these yet.
/// </summary>
public sealed class LiveGoogle(LiveAccount live)
{
    static readonly Uri BaseUri = new("https://www.googleapis.com/calendar/v3/");

    /// <summary>Creates a "Leaf Test &lt;timestamp&gt;" calendar and returns its ID.</summary>
    public async Task<string> CreateTestCalendarAsync(CancellationToken ct)
    {
        var body = new JsonObject { ["summary"] = $"Leaf Test {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}" };
        var created = await SendAsync(HttpMethod.Post, "calendars", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Deletes a calendar.</summary>
    public Task DeleteCalendarAsync(string calendarId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"calendars/{Uri.EscapeDataString(calendarId)}", null, ct);

    /// <summary>Creates a one-hour event tomorrow and returns its ID.</summary>
    public async Task<string> InsertEventAsync(string calendarId, string summary, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(15);
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

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
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
