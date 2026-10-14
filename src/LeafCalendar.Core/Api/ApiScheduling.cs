using System.Text.Json;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Core.Api;

/// <summary>The scheduling methods: links, open times, and a booking link for one time (local API spec 4), through the signed-in providers.</summary>
internal static class ApiScheduling
{
    // The most guests a booking link carries
    private const int MaxGuests = 20;

    // The longest range scheduling.openTimes reads
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    public static async Task<JsonElement> LinksAsync(ApiContext context, NoParams _, CancellationToken ct)
    {
        RequireConnected(context);
        var links = await context.Scheduling.GetLinksAsync(ct);
        List<LinkResult> result = [.. links.Select(l => new LinkResult(ApiIds.Link(l.ProviderId, l.Id), context.Scheduling.Get(l.ProviderId)?.Name ?? l.ProviderId, l.Name, l.Minutes, l.AllowsGuests))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListLinkResult);
    }

    public static async Task<JsonElement> OpenTimesAsync(ApiContext context, OpenTimesParams p, CancellationToken ct)
    {
        var start = ApiTime.ReadInstant(p.Start) ?? throw ApiTime.Invalid("start must be a time with an offset.");
        var end = ApiTime.ReadInstant(p.End) ?? throw ApiTime.Invalid("end must be a time with an offset.");
        if (end <= start || end - start > MaxRange)
        {
            throw ApiTime.Invalid("end must be after start, at most 31 days later.");
        }

        var (link, provider) = await FindLinkAsync(context, p.LinkId, ct);
        var times = await OpenTimesAsync(context, link, provider, start, end, ct);
        List<TimeRangeResult> result = [.. times.Select(t => new TimeRangeResult(ApiTime.Write(t.Start, context.Host.Zone), ApiTime.Write(t.End, context.Host.Zone)))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListTimeRangeResult);
    }

    /// <summary>A link's bookable times between two instants; a refused sign-in marks the provider as needing reconnecting.</summary>
    /// <exception cref="ApiException">No provider can answer (<see cref="ApiErrors.NotConnected"/>), or it couldn't be reached (<see cref="ApiErrors.Unavailable"/>).</exception>
    internal static async Task<IReadOnlyList<BusyRange>> OpenTimesAsync(ApiContext context, SchedulingLink link, ISchedulingProvider provider, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (provider is not IOpenTimesSource source)
        {
            throw NotConnected();
        }

        try
        {
            return await source.GetOpenTimesAsync(link.Id, start.ToUniversalTime(), end.ToUniversalTime(), ct);
        }
        catch (SchedulingAuthException)
        {
            context.Scheduling.MarkNeedsReconnect(provider.Id);
            throw NotConnected();
        }
        catch (SchedulingUnavailableException)
        {
            throw Unavailable();
        }
    }

    public static async Task<JsonElement> TimeLinkAsync(ApiContext context, TimeLinkParams p, CancellationToken ct)
    {
        var start = ApiTime.ReadInstant(p.Start) ?? throw ApiTime.Invalid("start must be a time with an offset.");
        if ((p.Guests?.Count ?? 0) > MaxGuests)
        {
            throw ApiTime.Invalid("guests holds more than 20 people.");
        }

        List<Contact> guests = [.. (p.Guests ?? []).Select(g =>
            new Contact((g.Name ?? "").Trim(), ContactSearch.ValidEmail(g.Email) ?? throw ApiTime.Invalid("guests holds an address that isn't an email address.")))];

        var (link, provider) = await FindLinkAsync(context, p.LinkId, ct);
        if (provider is not ITimeLinkBuilder builder)
        {
            throw NotConnected();
        }

        var url = builder.TimeLink(link, start.ToUniversalTime(), context.Host.Zone, guests);
        return ApiParams.Write(new UrlResult(url.AbsoluteUri), ApiJsonContext.Default.UrlResult);
    }

    /// <summary>The link a link ID names, among the signed-in provider's current links.</summary>
    /// <exception cref="ApiException">No provider is signed in (<see cref="ApiErrors.NotConnected"/>), or it has no such link (<see cref="ApiErrors.NotFound"/>).</exception>
    internal static async Task<(SchedulingLink Link, ISchedulingProvider Provider)> FindLinkAsync(ApiContext context, string? id, CancellationToken ct)
    {
        var (providerId, linkId) = ApiIds.ReadLink(id) ?? throw ApiTime.Invalid("linkId isn't a scheduling link ID.");
        RequireConnected(context);
        var provider = context.Scheduling.Get(providerId) ?? throw NotConnected();
        var links = await context.Scheduling.GetLinksAsync(ct);
        if (context.Scheduling.StateOf(providerId) != ProviderState.SignedIn)
        {
            throw NotConnected();
        }

        var link = links.FirstOrDefault(l => l.ProviderId == providerId && l.Id == linkId)
            ?? throw new ApiException(ApiErrors.NotFound, "No scheduling link with that ID.");
        return (link, provider);
    }

    private static void RequireConnected(ApiContext context)
    {
        if (context.Scheduling.Connected.Count == 0)
        {
            throw NotConnected();
        }
    }

    internal static ApiException NotConnected() => new(ApiErrors.NotConnected, "No scheduling provider is connected.");

    internal static ApiException Unavailable() => new(ApiErrors.Unavailable, "The scheduling provider couldn't be reached. Try again later.");
}
