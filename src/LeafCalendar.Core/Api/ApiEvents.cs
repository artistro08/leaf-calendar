using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Api;

/// <summary>The event methods: list, get, create, move (local API spec 4).</summary>
internal static class ApiEvents
{
    /// <summary>The most guests <c>events.create</c> takes.</summary>
    public const int MaxGuests = 100;

    private const int MaxTitleLength = 1024;
    private const int MaxLocationLength = 1024;
    private const int MaxDescriptionLength = 8192;

    // The longest range events.list reads
    private static readonly TimeSpan MaxListRange = TimeSpan.FromDays(366);

    // =========================================================================
    // READING
    // =========================================================================

    // Events overlapping [start, end) on the calendars shown in Leaf, as the calendar draws them
    public static JsonElement List(ApiContext context, RangeParams p)
    {
        var start = ApiTime.ReadInstant(p.Start) ?? throw ApiTime.Invalid("start must be a time with an offset.");
        var end = ApiTime.ReadInstant(p.End) ?? throw ApiTime.Invalid("end must be a time with an offset.");
        if (end <= start || end - start > MaxListRange)
        {
            throw ApiTime.Invalid("end must be after start, at most 366 days later.");
        }

        HashSet<(string, string)>? only = p.CalendarIds is null
            ? null
            : [.. p.CalendarIds.Select(id => ApiIds.ReadCalendar(id) ?? throw ApiTime.Invalid("calendarIds holds an ID that isn't a calendar's."))];

        var zone = context.Host.Zone;
        var from = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var to = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime).AddDays(1);

        using var conn = context.Database.Open();
        List<EventResult> result = [.. OccurrenceQuery.Load(conn, from, to, zone, context.Host.Settings.ShowDeclined)
            .Where(o => o.EndIn(zone) > start && o.StartIn(zone) < end)
            .Where(o => only is null || only.Contains((o.AccountId, o.CalendarId)))
            .Select(o => Summary(conn, o, zone))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListEventResult);
    }

    // One instance with its description (plain text) and guests (rooms left out)
    public static JsonElement Get(ApiContext context, IdParams p)
    {
        using var conn = context.Database.Open();
        var o = Find(conn, context, p.Id);
        List<Guest> guests;
        try
        {
            guests = [.. context.Editor.Load(o).Guests];
        }
        catch (InvalidOperationException)
        {
            throw NotFound();
        }

        var summary = Summary(conn, o, context.Host.Zone);
        var result = new EventDetailResult(
            summary.Id, summary.CalendarId, summary.Title, summary.Start, summary.End, summary.AllDay, summary.Location,
            summary.Response, summary.Color, summary.MeetingLink, Details(conn, o, includeDescription: true)?.Description ?? "",
            [.. guests.Where(g => !g.IsResource).Select(g => new GuestResult(g.Name, g.Email, Response(g.Response), g.IsOrganizer))]);
        return ApiParams.Write(result, ApiJsonContext.Default.EventDetailResult);
    }

    // =========================================================================
    // EDITS (through the editor and the outbox, like the app)
    // =========================================================================

    // A new event on the chosen calendar, else where new events go; invitations follow notifyGuests (default on), like Save and quiet save
    public static JsonElement Create(ApiContext context, CreateParams p)
    {
        if (p.Title is null || p.Title.Length > MaxTitleLength)
        {
            throw ApiTime.Invalid("title is required, at most 1,024 characters.");
        }

        if ((p.Location?.Length ?? 0) > MaxLocationLength || (p.Description?.Length ?? 0) > MaxDescriptionLength)
        {
            throw ApiTime.Invalid("location is at most 1,024 characters and description at most 8,192.");
        }

        var (start, end, allDay) = ApiTime.ReadSpan(p.Start, p.End);
        var guests = Guests(p.Guests);

        CalendarInfo target;
        using (var conn = context.Database.Open())
        {
            var accounts = AccountStore.GetAll(conn);
            if (accounts.Count == 0)
            {
                throw new ApiException(ApiErrors.NotSignedIn, "No Google account is signed in.");
            }

            var calendars = CalendarStore.GetAll(conn);
            var settings = context.Host.Settings;
            if (p.CalendarId is not null)
            {
                var (accountId, calendarId) = ApiIds.ReadCalendar(p.CalendarId) ?? throw ApiTime.Invalid("calendarId isn't a calendar ID.");
                target = calendars.FirstOrDefault(c => c.AccountId == accountId && c.Id == calendarId && c.IsVisible)
                    ?? throw new ApiException(ApiErrors.NotFound, "No calendar with that ID.");
                if (target.AccessRole is not ("owner" or "writer"))
                {
                    throw new ApiException(ApiErrors.ReadOnly, "You can't change that.");
                }
            }
            else
            {
                target = DefaultCalendar.Pick(calendars, accounts.Select(a => a.Id).ToHashSet(), settings.DefaultCalendar, mainAccountId: settings.MainAccountId)
                    ?? throw new ApiException(ApiErrors.ReadOnly, "You can't change that.");
            }
        }

        var draft = new EventDraft
        {
            AccountId = target.AccountId,
            CalendarId = target.Id,
            Title = p.Title,
            Start = start,
            End = end,
            IsAllDay = allDay,
            TimeZone = allDay ? null : TimeZoneCatalog.IanaId(context.Host.Zone),
            Location = p.Location ?? "",
            Description = Html(p.Description),
            Guests = [.. guests.Select(email => new Guest(email))],
            HasConference = p.Meet ?? false,
        };
        var id = context.Editor.Create(draft, sendUpdates: p.NotifyGuests ?? true);
        return ApiParams.Write(new IdResult(ApiIds.Event(target.AccountId, target.Id, id, start)), ApiJsonContext.Default.IdResult);
    }

    // A drag: one scope for a repeating event, guests told; the new ID finds the moved instance (a split series has none yet)
    public static JsonElement Move(ApiContext context, MoveParams p)
    {
        var scope = Scope(p.Scope);
        var (start, end, allDay) = ApiTime.ReadSpan(p.Start, p.End);

        CalendarOccurrence o;
        using (var conn = context.Database.Open())
        {
            o = Find(conn, context, p.Id);
        }

        if (!context.Editor.Permissions(o).CanEdit)
        {
            throw new ApiException(ApiErrors.ReadOnly, "You can't change that.");
        }

        // A single event ignores the scope, so it still gets its new ID. "Following" on a series has none: a split leaves the
        // old series' earlier instances, and one of them may start at the new time, so Relocate could name the wrong event
        context.Editor.Move([new EventMove(o, start, end, allDay)], scope, sendUpdates: true);
        if (scope == EditScope.Following && o.RecurringEventId is not null)
        {
            return ApiParams.Write(new IdResult(null), ApiJsonContext.Default.IdResult);
        }

        using (var conn = context.Database.Open())
        {
            return ApiParams.Write(new IdResult(Relocate(conn, context, o, start) is { } moved ? ApiIds.Event(moved) : null), ApiJsonContext.Default.IdResult);
        }
    }

    // The editor's Save: only the fields given change (guests are the whole list: those kept keep their answers, rooms stay),
    // one scope for a repeating event, guests told unless notifyGuests is false; the new ID finds the saved instance
    public static JsonElement Update(ApiContext context, UpdateParams p)
    {
        var scope = Scope(p.Scope);
        if (p.Title is { Length: > MaxTitleLength } || (p.Location?.Length ?? 0) > MaxLocationLength || (p.Description?.Length ?? 0) > MaxDescriptionLength)
        {
            throw ApiTime.Invalid("title and location are at most 1,024 characters and description at most 8,192.");
        }

        if ((p.Start is null) != (p.End is null))
        {
            throw ApiTime.Invalid("start and end change together.");
        }

        var span = p.Start is null ? ((DateTimeOffset, DateTimeOffset, bool)?)null : ApiTime.ReadSpan(p.Start, p.End);
        var guests = p.Guests is null ? null : Guests(p.Guests);

        CalendarOccurrence o;
        CalendarInfo? target = null;
        using (var conn = context.Database.Open())
        {
            o = Find(conn, context, p.Id);
            if (p.CalendarId is not null)
            {
                var (accountId, calendarId) = ApiIds.ReadCalendar(p.CalendarId) ?? throw ApiTime.Invalid("calendarId isn't a calendar ID.");
                target = CalendarStore.GetAll(conn).FirstOrDefault(c => c.AccountId == accountId && c.Id == calendarId && c.IsVisible)
                    ?? throw new ApiException(ApiErrors.NotFound, "No calendar with that ID.");
                if (target.AccessRole is not ("owner" or "writer"))
                {
                    throw new ApiException(ApiErrors.ReadOnly, "You can't change that.");
                }
            }
        }

        if (!context.Editor.Permissions(o).CanEdit)
        {
            throw new ApiException(ApiErrors.ReadOnly, "You can't change that.");
        }

        EventDraft before;
        try
        {
            before = context.Editor.Load(o);
        }
        catch (InvalidOperationException)
        {
            throw NotFound();
        }

        var after = before with
        {
            AccountId = target?.AccountId ?? before.AccountId,
            CalendarId = target?.Id ?? before.CalendarId,
            Title = p.Title ?? before.Title,
            Location = p.Location ?? before.Location,
            Description = p.Description is null ? before.Description : Html(p.Description),
            HasConference = p.Meet ?? before.HasConference,
            Guests = guests is null ? before.Guests : [
                .. before.Guests.Where(g => g.IsResource),
                .. guests.Select(email => before.Guests.FirstOrDefault(g => !g.IsResource && string.Equals(g.Email, email, StringComparison.OrdinalIgnoreCase)) ?? new Guest(email))],
        };

        if (span is var (start, end, allDay))
        {
            after = after with
            {
                Start = start,
                End = end,
                IsAllDay = allDay,
                TimeZone = allDay ? before.TimeZone : before.TimeZone ?? context.Editor.LocalZoneId,
            };
        }

        // Nothing Changed: Nothing To Send
        if (Same(before, after))
        {
            return ApiParams.Write(new IdResult(ApiIds.Event(o)), ApiJsonContext.Default.IdResult);
        }

        try
        {
            context.Editor.Save(o, before, after, scope, sendUpdates: p.NotifyGuests ?? true);
        }
        catch (ArgumentException)
        {
            throw ApiTime.Invalid("Only scope all can move a repeating event to another calendar.");
        }

        if (scope == EditScope.Following && o.RecurringEventId is not null)
        {
            return ApiParams.Write(new IdResult(null), ApiJsonContext.Default.IdResult);
        }

        using (var conn = context.Database.Open())
        {
            var saved = Relocate(conn, context, o, after.Start, after.AccountId, after.CalendarId);
            return ApiParams.Write(new IdResult(saved is null ? null : ApiIds.Event(saved)), ApiJsonContext.Default.IdResult);
        }
    }

    // Same fields the API changes (guest lists by address, in order)
    private static bool Same(EventDraft a, EventDraft b) =>
        a.AccountId == b.AccountId && a.CalendarId == b.CalendarId && a.Title == b.Title && a.Location == b.Location
        && a.Description == b.Description && a.HasConference == b.HasConference && a.Start == b.Start && a.End == b.End
        && a.IsAllDay == b.IsAllDay && a.Guests.Select(g => g.Email).SequenceEqual(b.Guests.Select(g => g.Email), StringComparer.OrdinalIgnoreCase);

    // A delete, as in Leaf Calendar: one scope for a repeating event, held for the undo window, guests told unless notifyGuests is false
    public static JsonElement Delete(ApiContext context, DeleteParams p)
    {
        var scope = Scope(p.Scope);
        CalendarOccurrence o;
        using (var conn = context.Database.Open())
        {
            o = Find(conn, context, p.Id);
        }

        if (!context.Editor.Permissions(o).CanEdit)
        {
            throw new ApiException(ApiErrors.ReadOnly, "You can't change that.");
        }

        context.Editor.Delete([o], scope, sendUpdates: p.NotifyGuests ?? true);
        return ApiParams.Write(new EmptyResult(), ApiJsonContext.Default.EmptyResult);
    }

    // this (the default), following, or all
    private static EditScope Scope(string? scope) => scope switch
    {
        null or "this" => EditScope.This,
        "following" => EditScope.Following,
        "all" => EditScope.All,
        _ => throw ApiTime.Invalid("scope must be this, following, or all."),
    };

    // Where a moved instance landed: same calendar, same event or series, starting at the new time (a moved instance of a
    // series gets Google's {series}_{start} ID, so the old ID can't find it)
    private static CalendarOccurrence? Relocate(SqliteConnection conn, ApiContext context, CalendarOccurrence before, DateTimeOffset start) =>
        Relocate(conn, context, before, start, before.AccountId, before.CalendarId);

    // The same, on the calendar the event now lives on (an update can move it)
    private static CalendarOccurrence? Relocate(SqliteConnection conn, ApiContext context, CalendarOccurrence before, DateTimeOffset start, string accountId, string calendarId)
    {
        var zone = context.Host.Zone;
        var series = before.RecurringEventId ?? before.EventId;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        return OccurrenceQuery.Load(conn, day.AddDays(-1), day.AddDays(2), zone, includeDeclined: true, keepSharedCopies: true)
            .FirstOrDefault(o => o.AccountId == accountId && o.CalendarId == calendarId && o.Start == start
                && (o.EventId == before.EventId || o.EventId == series || o.RecurringEventId == series));
    }

    // Guests as the editor checks them: valid addresses only, repeats (any case) dropped, at most MaxGuests
    private static List<string> Guests(List<string>? emails)
    {
        var result = new List<string>();
        foreach (var raw in emails ?? [])
        {
            var email = ContactSearch.ValidEmail(raw) ?? throw ApiTime.Invalid("guests holds an address that isn't an email address.");
            if (!result.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(email);
            }
        }

        return result.Count <= MaxGuests ? result : throw ApiTime.Invalid("guests holds more than 100 addresses.");
    }

    // Plain text to Leaf's description HTML: every character escaped, each line its own line
    private static string Html(string? text) =>
        string.IsNullOrEmpty(text) ? "" : DescriptionHtml.Normalize(string.Join("<br>", text.ReplaceLineEndings("\n").Split('\n').Select(WebUtility.HtmlEncode)));

    // =========================================================================
    // SHARED
    // =========================================================================

    /// <summary>The instance an event ID names, as it is now (it may have moved since the ID was made).</summary>
    public static CalendarOccurrence Find(SqliteConnection conn, ApiContext context, string? id)
    {
        var key = ApiIds.ReadEvent(id) ?? throw ApiTime.Invalid("id isn't an event ID.");
        return OccurrenceLookup.Find(conn, key.AccountId, key.CalendarId, key.EventId, key.Start, context.Host.Zone) ?? throw NotFound();
    }

    /// <summary>Your answer as the API writes it.</summary>
    public static string Response(ResponseStatus response) => response switch
    {
        ResponseStatus.Accepted => "accepted",
        ResponseStatus.Tentative => "tentative",
        ResponseStatus.Declined => "declined",
        _ => "needsAction",
    };

    public static ApiException NotFound() => new(ApiErrors.NotFound, "No event with that ID.");

    private static EventResult Summary(SqliteConnection conn, CalendarOccurrence o, TimeZoneInfo zone)
    {
        var details = Details(conn, o, includeDescription: false);
        return new EventResult(
            ApiIds.Event(o),
            ApiIds.Calendar(o.AccountId, o.CalendarId),
            o.Title,
            o.IsAllDay ? ApiTime.Write(o.AllDayStart) : ApiTime.Write(o.Start, zone),
            o.IsAllDay ? ApiTime.Write(o.AllDayEnd) : ApiTime.Write(o.End, zone),
            o.IsAllDay,
            details?.Location,
            Response(o.SelfResponse),
            EventColors.ResolveAccent(o.ColorId, o.CalendarColor),
            JoinPicker.KnownHost(details?.ConferenceUri)?.AbsoluteUri);
    }

    // The stored event's details, or null when it's gone or unreadable (the description only when asked: a year's list doesn't need it)
    private static EventDetails? Details(SqliteConnection conn, CalendarOccurrence o, bool includeDescription)
    {
        if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
        {
            return null;
        }

        try
        {
            return EventDetailsParser.Parse(stored.RawJson, includeDescription);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
