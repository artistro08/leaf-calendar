using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Api;

/// <summary>
/// Free times and saved times (local API spec 4): what Leaf Calendar's share panel does, for apps without a calendar grid. Free
/// times are your working hours minus your busy events; saved times are share groups, written and booked as the share
/// panel's Copy &amp; Save and Approve… do.
/// </summary>
internal static class ApiShares
{
    /// <summary>The most times <c>shares.create</c> takes.</summary>
    public const int MaxTimes = 50;

    private const int DefaultMinutes = 30;

    // The longest range availability.free reads
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    // =========================================================================
    // FREE TIMES
    // =========================================================================

    // Working hours on work days, from now on, minus events that show you busy; with a link, only its open times
    public static async Task<JsonElement> FreeAsync(ApiContext context, FreeParams p, CancellationToken ct)
    {
        var start = ApiTime.ReadInstant(p.Start) ?? throw ApiTime.Invalid("start must be a time with an offset.");
        var end = ApiTime.ReadInstant(p.End) ?? throw ApiTime.Invalid("end must be a time with an offset.");
        if (end <= start || end - start > MaxRange)
        {
            throw ApiTime.Invalid("end must be after start, at most 31 days later.");
        }

        var minutes = p.Minutes ?? DefaultMinutes;
        if (minutes is < 5 or > 1440)
        {
            throw ApiTime.Invalid("minutes must be 5 to 1,440.");
        }

        var zone = context.Host.Zone;
        var now = context.Time.GetUtcNow();
        var from = start > now ? start : now;
        var free = from < end ? Free(context, from, end, zone) : [];

        if (p.LinkId is not null)
        {
            var (link, provider) = await ApiScheduling.FindLinkAsync(context, p.LinkId, ct);
            var open = await ApiScheduling.OpenTimesAsync(context, link, provider, start, end, ct);
            free = BusyMath.Subtract(free, BusyMath.Subtract([new BusyRange(start, end)], open));
        }

        List<TimeRangeResult> result = [.. free
            .Where(f => (f.End - f.Start).TotalMinutes >= minutes)
            .Select(f => new TimeRangeResult(ApiTime.Write(f.Start, zone), ApiTime.Write(f.End, zone)))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListTimeRangeResult);
    }

    // Each work day's hours in [from, end), minus the events that show you busy (declined and free ones don't)
    private static IReadOnlyList<BusyRange> Free(ApiContext context, DateTimeOffset from, DateTimeOffset end, TimeZoneInfo zone)
    {
        var hours = context.Host.Settings.WorkingHours;
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);

        var work = new List<BusyRange>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (!hours.Days.Contains(day.DayOfWeek) || hours.EndMinute <= hours.StartMinute)
            {
                continue;
            }

            var dayStart = DragMath.Instant(day, hours.StartMinute, zone);
            var dayEnd = DragMath.Instant(day, hours.EndMinute, zone);
            dayStart = dayStart < from ? from : dayStart;
            dayEnd = dayEnd > end ? end : dayEnd;
            if (dayEnd > dayStart)
            {
                work.Add(new BusyRange(dayStart, dayEnd));
            }
        }

        using var conn = context.Database.Open();
        var busy = OccurrenceQuery.Load(conn, firstDay.AddDays(-1), lastDay.AddDays(2), zone, includeDeclined: false)
            .Where(o => !o.IsFree && o.SelfResponse != ResponseStatus.Declined)
            .Select(o => new BusyRange(o.StartIn(zone), o.EndIn(zone)));
        return BusyMath.Subtract(work, busy);
    }

    // =========================================================================
    // SAVED TIMES
    // =========================================================================

    // Copy & Save: the times saved as a group, and the text Copy would put on the clipboard (with a booking link per time
    // on a link, split by its length and its open times when they can be read)
    public static async Task<JsonElement> CreateAsync(ApiContext context, ShareCreateParams p, CancellationToken ct)
    {
        if (p.Times is not { Count: > 0 and <= MaxTimes })
        {
            throw ApiTime.Invalid("times holds 1 to 50 times.");
        }

        var times = BusyMath.Merge(p.Times.Select(t => ReadTime(t)));
        if (times[0].End <= context.Time.GetUtcNow())
        {
            throw ApiTime.Invalid("Each time must end in the future.");
        }
        if ((p.Title?.Length ?? 0) > ShareGroupStore.MaxTitleLength || (p.Message?.Length ?? 0) > AvailabilityText.MaxMessageLength)
        {
            throw ApiTime.Invalid("title is at most 100 characters and message at most 2,000.");
        }

        if ((p.Guests?.Count ?? 0) > ShareGroupStore.MaxGuests)
        {
            throw ApiTime.Invalid("guests holds more than 50 people.");
        }

        List<Contact> guests = [.. (p.Guests ?? []).Select(g =>
            new Contact((g.Name ?? "").Trim(), ContactSearch.ValidEmail(g.Email) ?? throw ApiTime.Invalid("guests holds an address that isn't an email address.")))];

        var settings = context.Host.Settings;
        var zone = context.Host.Zone;
        var message = p.Message ?? settings.ShareMessage;
        var saved = Data.ShareLink.None;
        string text;
        string? html = null;
        if (p.LinkId is not null)
        {
            var (link, provider) = await ApiScheduling.FindLinkAsync(context, p.LinkId, ct);
            if (provider is not ITimeLinkBuilder builder)
            {
                throw ApiScheduling.NotConnected();
            }

            // Open Times Not Readable (not reachable, backing off, refused, or not offered): split by the link's length instead, as Copy does offline
            IReadOnlyList<BusyRange>? open;
            try
            {
                open = await ApiScheduling.OpenTimesAsync(context, link, provider, times[0].Start, times[^1].End, ct);
            }
            catch (ApiException ex) when (ex.Code is ApiErrors.Unavailable or ApiErrors.NotConnected)
            {
                open = null;
            }

            var linked = Scheduling.ShareLinks.Build(builder, link, times, open, guests, message, settings.BookingLinkNote, zone, settings.Use24HourTime);
            (text, html) = (linked.Plain, linked.Html);
            saved = new Data.ShareLink(link.ProviderId, link.Id, link.BookingUrl.AbsoluteUri, link.Minutes);
        }
        else
        {
            text = AvailabilityText.Compose(message, AvailabilityText.Format(times, zone, settings.Use24HourTime));
        }

        long id;
        using (var conn = context.Database.Open())
        {
            id = ShareGroupStore.Insert(conn, p.Title ?? "", message, TimeZoneCatalog.IanaId(zone), times, context.Time.GetUtcNow(), guests, saved);
        }

        context.Host.SharesChanged();
        return ApiParams.Write(new ShareTextResult(ApiIds.Share(id), text, html), ApiJsonContext.Default.ShareTextResult);
    }

    // Every group with a time left, oldest first, its passed times left out
    public static JsonElement List(ApiContext context, NoParams _)
    {
        using var conn = context.Database.Open();
        var zone = context.Host.Zone;
        List<ShareResult> result = [.. ShareGroupStore.GetAll(conn, context.Time.GetUtcNow()).Select(g => new ShareResult(
            ApiIds.Share(g.Id),
            g.Title,
            g.Message,
            [.. g.Slots.Select(s => new TimeRangeResult(ApiTime.Write(s.Start, zone), ApiTime.Write(s.End, zone)))],
            [.. g.Guests.Select(c => new ShareGuestResult(c.Name.Length > 0 ? c.Name : null, c.Email))],
            g.ProviderId.Length > 0 ? new ShareLinkResult(ApiIds.Link(g.ProviderId, g.LinkId), g.LinkMinutes) : null))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListShareResult);
    }

    // Approve…: plain times become an event with the group's title and guests; times on a link are booked on the provider
    // (which puts the event on your calendar). Either way the group is deleted once booked
    public static async Task<JsonElement> BookAsync(ApiContext context, ShareBookParams p, CancellationToken ct)
    {
        var id = ApiIds.ReadShare(p.Id) ?? throw ApiTime.Invalid("id isn't a saved times ID.");
        var start = ApiTime.ReadInstant(p.Start) ?? throw ApiTime.Invalid("start must be a time with an offset.");
        var group = Find(context, id);

        if (group.ProviderId.Length > 0)
        {
            await BookOnLinkAsync(context, group, start, ct);
            return ApiParams.Write(new IdResult(null), ApiJsonContext.Default.IdResult);
        }

        var end = ApiTime.ReadInstant(p.End) ?? throw ApiTime.Invalid("end must be a time with an offset.");
        if (end <= start || !group.Slots.Any(s => s.Start <= start && end <= s.End))
        {
            throw ApiTime.Invalid("start and end must fall inside one of the saved times.");
        }

        var zone = context.Host.Zone;
        var title = DisplayText.Clean(group.Title, ShareGroupStore.MaxTitleLength);
        var created = ApiEvents.Create(context, new CreateParams(
            Title: title.Length > 0 ? title : ShareGroupStore.GenericTitle,
            Start: ApiTime.Write(start, zone),
            End: ApiTime.Write(end, zone),
            Guests: group.Guests.Count > 0 ? [.. group.Guests.Select(g => g.Email)] : null));
        Delete(context, id);
        return created;
    }

    // Books start on the group's link for its guests (the first is the booker; the rest only when the link takes them),
    // then deletes the group, telling the booking check it's ours until the group is gone
    private static async Task BookOnLinkAsync(ApiContext context, ShareGroup group, DateTimeOffset start, CancellationToken ct)
    {
        if (group.Guests.Count == 0)
        {
            throw ApiTime.Invalid("Booking on a link needs a guest to book for.");
        }

        var (link, provider) = await ApiScheduling.FindLinkAsync(context, ApiIds.Link(group.ProviderId, group.LinkId), ct, anyLink: true);
        if (provider is not IBookingCreator creator)
        {
            throw ApiScheduling.NotConnected();
        }

        // The Link's Whole Length Fits In One Saved Time
        var end = start + TimeSpan.FromMinutes(link.Minutes);
        if (!group.Slots.Any(s => s.Start <= start && end <= s.End))
        {
            throw ApiTime.Invalid("start and the link's length must fall inside one of the saved times.");
        }

        if (context.Scheduling.IsWaiting(provider.Id))
        {
            throw ApiScheduling.Unavailable();
        }

        var guests = link.AllowsGuests ? group.Guests : [.. group.Guests.Take(1)];
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(group.ZoneId, out var shared) ? shared : context.Host.Zone;
        context.Bookings?.BeginBooking(group.Id);
        try
        {
            await creator.BookAsync(link, start.ToUniversalTime(), guests, zone, ct);
            Delete(context, group.Id);
        }
        catch (SchedulingSlotTakenException)
        {
            throw new ApiException(ApiErrors.Taken, "That time isn't open anymore.");
        }
        catch (SchedulingAuthException)
        {
            context.Scheduling.MarkNeedsReconnect(provider.Id);
            throw ApiScheduling.NotConnected();
        }
        catch (SchedulingUnavailableException ex)
        {
            throw ApiScheduling.Unavailable(context, provider.Id, ex);
        }
        finally
        {
            context.Bookings?.EndBooking(group.Id);
        }
    }

    public static JsonElement Delete(ApiContext context, IdParams p)
    {
        var id = ApiIds.ReadShare(p.Id) ?? throw ApiTime.Invalid("id isn't a saved times ID.");
        Find(context, id);
        Delete(context, id);
        return ApiParams.Write(new EmptyResult(), ApiJsonContext.Default.EmptyResult);
    }

    // =========================================================================
    // SHARED
    // =========================================================================

    private static ShareGroup Find(ApiContext context, long id)
    {
        using var conn = context.Database.Open();
        return ShareGroupStore.GetAll(conn, context.Time.GetUtcNow()).FirstOrDefault(g => g.Id == id)
            ?? throw new ApiException(ApiErrors.NotFound, "No saved times with that ID.");
    }

    private static void Delete(ApiContext context, long id)
    {
        using (var conn = context.Database.Open())
        {
            ShareGroupStore.Delete(conn, id);
        }

        context.Host.SharesChanged();
    }

    // One {start, end} time, both with an offset, end after start
    private static BusyRange ReadTime(TimeParam? time)
    {
        var start = ApiTime.ReadInstant(time?.Start);
        var end = ApiTime.ReadInstant(time?.End);
        return start is { } s && end is { } e && e > s ? new BusyRange(s, e) : throw ApiTime.Invalid("Each time needs a start and a later end, both with an offset.");
    }
}
