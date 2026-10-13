using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// Cal.com, through its v2 REST API: your links (event types), their bookable times, bookings made on them, booking a
/// time, and the booking page links Leaf copies.
/// </summary>
/// <remarks>
/// Every call asks <paramref name="accessToken"/> for a fresh OAuth access token (it refreshes quietly when needed; a
/// refused refresh throws <see cref="SchedulingAuthException"/> before anything is sent) and sends it as
/// <c>Authorization: Bearer</c> to <paramref name="apiBase"/> only (<c>https://api.cal.com/</c>, or the UI tests' fake),
/// never in a URL. Each endpoint pins its <c>cal-api-version</c>. Names are cleaned and capped, and a link whose booking
/// page isn't https or fails <see cref="LinkSafety.CanLaunch"/> is left out.
/// </remarks>
/// <seealso href="https://cal.com/docs/api-reference/v2/introduction"/>
public sealed class CalComProvider(HttpClient http, Func<CancellationToken, Task<string>> accessToken, Uri apiBase)
    : ISchedulingProvider, ILinkSource, IOpenTimesSource, ITimeLinkBuilder, IBlockLinkBuilder, IBookingSource, IBookingCreator
{
    /// <summary>The provider's ID.</summary>
    public const string ProviderId = "calcom";

    /// <summary>The longest link or attendee name kept.</summary>
    public const int MaxName = 100;

    private const string EventTypesVersion = "2024-06-14";
    private const string SlotsVersion = "2024-09-04";
    private const string BookingsVersion = "2024-08-13";

    /// <summary>Cal.com's hosted API.</summary>
    public static Uri DefaultApiBase { get; } = new("https://api.cal.com/");

    /// <summary>Cal.com's OAuth endpoints, scopes and fixed loopback redirect (registered on your OAuth client).</summary>
    public static SchedulingOAuth OAuth { get; } = new(
        new Uri("https://app.cal.com/auth/oauth2/authorize"),
        new Uri("https://api.cal.com/v2/auth/oauth2/token"),
        ["EVENT_TYPE_READ", "BOOKING_READ", "BOOKING_WRITE", "SCHEDULE_READ"],
        53817,
        "/calcom/callback",
        new Uri("https://app.cal.com/settings/developer/oauth"));

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string Name => "Cal.com";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct)
    {
        var page = await SendAsync(HttpMethod.Get, "v2/event-types", EventTypesVersion, null, CalComJsonContext.Default.CalComEnvelopeListCalComEventType, ct);
        var links = new List<SchedulingLink>();
        foreach (var t in page.Data ?? [])
        {
            if (t.Id is not { } id || t.Hidden == true || t.LengthInMinutes is not ({ } minutes and > 0)
                || !Uri.TryCreate(t.BookingUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !LinkSafety.CanLaunch(url))
            {
                continue;
            }

            var name = DisplayText.Clean(t.Title, MaxName);
            if (name.Length == 0)
            {
                continue;
            }

            links.Add(new SchedulingLink(ProviderId, id.ToString(CultureInfo.InvariantCulture), name, minutes, url, AllowsGuests: t.DisableGuests != true));
        }

        return links;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BusyRange>> GetOpenTimesAsync(string linkId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        var query = $"v2/slots?eventTypeId={Uri.EscapeDataString(linkId)}&start={Iso(startUtc)}&end={Iso(endUtc)}&format=range";
        var page = await SendAsync(HttpMethod.Get, query, SlotsVersion, null, CalComJsonContext.Default.CalComEnvelopeDictionaryStringListCalComSlot, ct);
        return [.. (page.Data ?? [])
            .SelectMany(day => day.Value ?? [])
            .Where(s => s.Start is { } start && s.End is { } end && end > start)
            .Select(s => new BusyRange(s.Start!.Value.ToUniversalTime(), s.End!.Value.ToUniversalTime()))
            .OrderBy(s => s.Start)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchedulingBooking>> GetBookingsAsync(string linkId, DateTimeOffset createdAfterUtc, CancellationToken ct)
    {
        var query = $"v2/bookings?eventTypeId={Uri.EscapeDataString(linkId)}&afterCreatedAt={Iso(createdAfterUtc)}&status=upcoming&take=100";
        var page = await SendAsync(HttpMethod.Get, query, BookingsVersion, null, CalComJsonContext.Default.CalComEnvelopeListCalComBooking, ct);
        return [.. (page.Data ?? []).Select(ToBooking).OfType<SchedulingBooking>()];
    }

    /// <inheritdoc />
    public async Task<SchedulingBooking> BookAsync(SchedulingLink link, DateTimeOffset startUtc, IReadOnlyList<Contact> guests, TimeZoneInfo zone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(guests);
        ArgumentNullException.ThrowIfNull(zone);
        if (guests.Count == 0 || !long.TryParse(link.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var eventTypeId))
        {
            throw new ArgumentException("A booking needs a guest and a Cal.com link.", nameof(guests));
        }

        var booker = guests[0];
        var request = new CalComBookingRequest(
            Iso(startUtc),
            eventTypeId,
            new CalComBookingAttendee(booker.Name.Length > 0 ? booker.Name : booker.Email, booker.Email, TimeZoneCatalog.IanaId(zone)),
            link.AllowsGuests && guests.Count > 1 ? [.. guests.Skip(1).Select(g => g.Email)] : null);
        var body = JsonContent.Create(request, CalComJsonContext.Default.CalComBookingRequest);

        var page = await SendAsync(HttpMethod.Post, "v2/bookings", BookingsVersion, body, CalComJsonContext.Default.CalComEnvelopeCalComBooking, ct);
        return (page.Data is { } data ? ToBooking(data) : null) ?? throw new SchedulingUnavailableException("Cal.com's booking answer was incomplete.");
    }

    /// <inheritdoc />
    public Uri TimeLink(SchedulingLink link, DateTimeOffset startUtc, TimeZoneInfo zone, IReadOnlyList<Contact> guests)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(zone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startUtc, zone).DateTime);
        return Build(link, [("date", Day(day)), ("slot", Iso(startUtc))], guests);
    }

    /// <inheritdoc />
    public Uri BlockLink(SchedulingLink link, DateOnly day, IReadOnlyList<Contact> guests)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Build(link, [("date", Day(day))], guests);
    }

    // The booking page plus query values (escaped), then the booker and, when allowed, the other guests
    private static Uri Build(SchedulingLink link, List<(string Key, string Value)> values, IReadOnlyList<Contact> guests)
    {
        ArgumentNullException.ThrowIfNull(guests);
        if (guests.Count > 0)
        {
            if (guests[0].Name.Length > 0)
            {
                values.Add(("name", guests[0].Name));
            }

            values.Add(("email", guests[0].Email));
            if (link.AllowsGuests)
            {
                values.AddRange(guests.Skip(1).Select(g => ("guests", g.Email)));
            }
        }

        var query = string.Join("&", values.Select(v => v.Key + "=" + Uri.EscapeDataString(v.Value)));
        var url = link.BookingUrl.AbsoluteUri;
        return new Uri(url + (link.BookingUrl.Query.Length > 0 ? "&" : "?") + query);
    }

    private static SchedulingBooking? ToBooking(CalComBooking b) =>
        b.Uid is { Length: > 0 } uid && b.Start is { } start && b.CreatedAt is { } created
            ? new SchedulingBooking(uid, start.ToUniversalTime(), created.ToUniversalTime(), DisplayText.Clean(b.Attendees?.FirstOrDefault()?.Name, MaxName))
            : null;

    // One request: token and version headers, status mapped to Leaf's three failures, body read through the source-generated context
    private async Task<T> SendAsync<T>(HttpMethod method, string relative, string version, HttpContent? content, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        var token = await accessToken(ct);
        using var request = new HttpRequestMessage(method, new Uri(apiBase, relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("cal-api-version", version);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SchedulingUnavailableException("Cal.com couldn't be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SchedulingUnavailableException("Cal.com didn't answer in time.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new SchedulingAuthException("Cal.com refused the access token.");
                case HttpStatusCode.Conflict when method == HttpMethod.Post:
                    throw new SchedulingSlotTakenException("That time isn't open on Cal.com anymore.");
                case HttpStatusCode.BadRequest when method == HttpMethod.Post && IsTakenMessage(text):
                    throw new SchedulingSlotTakenException("That time isn't open on Cal.com anymore.");
                case (HttpStatusCode)429:
                    throw new SchedulingUnavailableException("Cal.com asked Leaf to slow down.", response.Headers.RetryAfter?.Delta);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SchedulingUnavailableException(string.Create(CultureInfo.InvariantCulture, $"Cal.com answered {(int)response.StatusCode}."));
            }

            try
            {
                return JsonSerializer.Deserialize(text, type) ?? throw new SchedulingUnavailableException("Cal.com's answer was empty.");
            }
            catch (JsonException ex)
            {
                throw new SchedulingUnavailableException("Cal.com's answer couldn't be read.", ex);
            }
        }
    }

    // Cal.com answers a booking for a taken time with 400 and this wording (checked against a live account in Task 19)
    private static bool IsTakenMessage(string body) =>
        body.Contains("already has booking", StringComparison.OrdinalIgnoreCase) || body.Contains("not available", StringComparison.OrdinalIgnoreCase);

    private static string Iso(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
