using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Editing;

/// <summary>
/// Converts between Google's event JSON and <see cref="EventDraft"/>, and builds the JSON Leaf sends.
/// </summary>
/// <remarks>
/// Reads use <see cref="JsonDocument"/>; building and merging use <see cref="JsonNode"/> (both AOT safe).
/// Local edits are applied with the same merge rules Google uses for <c>events.patch</c>: nested objects
/// merge, <c>null</c> removes a field, anything else replaces it. So the local copy matches what Google
/// will hold once the patch is sent.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/events"/>
public static class EventJson
{
    // Fields Google owns or that belong to the original event; never copied into a new one
    static readonly string[] GoogleOwned =
    [
        "id", "etag", "iCalUID", "recurringEventId", "originalStartTime", "htmlLink", "created", "updated", "sequence",
        "creator", "organizer", "hangoutLink", "conferenceData", "kind", "status", "attendeesOmitted", "privateCopy", "locked",
    ];

    // =========================================================================
    // READING
    // =========================================================================

    /// <summary>
    /// Reads the editable fields. <paramref name="start"/>, <paramref name="end"/>, and <paramref name="isAllDay"/>
    /// come from the on-screen instance (a series master's own times are its first instance).
    /// <paramref name="seriesRecurrence"/> is the master's lines when <paramref name="rawJson"/> is an exception.
    /// </summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static EventDraft ReadDraft(string accountId, string calendarId, string rawJson, DateTimeOffset start, DateTimeOffset end, bool isAllDay, IReadOnlyList<string>? seriesRecurrence = null)
    {
        var details = EventDetailsParser.Parse(rawJson);
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        return new EventDraft
        {
            AccountId           = accountId,
            CalendarId          = calendarId,
            Title               = Str(root, "summary") ?? "",
            Start               = start,
            End                 = end,
            IsAllDay            = isAllDay,
            TimeZone            = Str(Get(root, "start"), "timeZone"),
            Location            = Str(root, "location") ?? "",
            Description         = details.Description,
            ColorId             = Str(root, "colorId"),
            Guests              = Guests(root),
            UseDefaultReminders = Get(Get(root, "reminders"), "useDefault") is not { ValueKind: JsonValueKind.False },
            ReminderMinutes     = PopupMinutes(root),
            Recurrence          = seriesRecurrence ?? Lines(root),
            ConferenceUri       = details.ConferenceUri,
        };
    }

    /// <summary>The event's recurrence lines (empty for a single event).</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static IReadOnlyList<string> RecurrenceOf(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return Lines(doc.RootElement);
    }

    /// <summary>True when you may change the event: an owner or writer calendar, and you organize it, guests may modify it, or it has no guests.</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static bool CanEdit(string rawJson, string accessRole)
    {
        if (accessRole is not ("owner" or "writer"))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (Get(root, "organizer") is not { } organizer || Flag(organizer, "self"))
        {
            return true;
        }

        return Flag(root, "guestsCanModify") || Get(root, "attendees") is not { ValueKind: JsonValueKind.Array };
    }

    /// <summary>True when you're a guest (not the organizer), so Yes / No / Maybe applies.</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static bool CanRespond(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (Get(root, "organizer") is { } organizer && Flag(organizer, "self"))
        {
            return false;
        }

        return Get(root, "attendees") is { ValueKind: JsonValueKind.Array } attendees
            && attendees.EnumerateArray().Any(a => Flag(a, "self"));
    }

    // =========================================================================
    // BUILDING
    // =========================================================================

    /// <summary>The body for <c>events.insert</c>.</summary>
    public static JsonObject BuildCreate(string id, EventDraft draft)
    {
        var body = new JsonObject
        {
            ["id"]      = id,
            ["summary"] = draft.Title,
            ["start"]   = Time(draft.Start, draft.IsAllDay, draft.TimeZone, clearOther: false),
            ["end"]     = Time(draft.End, draft.IsAllDay, draft.TimeZone, clearOther: false),
        };

        if (draft.Location.Length > 0)
        {
            body["location"] = draft.Location;
        }

        if (draft.Description.Length > 0)
        {
            body["description"] = TextToHtml(draft.Description);
        }

        if (draft.ColorId is not null)
        {
            body["colorId"] = draft.ColorId;
        }

        if (draft.Guests.Count > 0)
        {
            body["attendees"] = Attendees(draft.Guests);
        }

        body["reminders"] = Reminders(draft);

        if (draft.Recurrence.Count > 0)
        {
            body["recurrence"] = Strings(draft.Recurrence);
        }

        return body;
    }

    /// <summary>The body for <c>events.patch</c>: only the fields that differ. Switching between all-day and timed clears the other form.</summary>
    public static JsonObject BuildPatch(EventDraft before, EventDraft after)
    {
        var patch = new JsonObject();

        if (before.Title != after.Title)
        {
            patch["summary"] = after.Title;
        }

        // Times
        if (before.Start != after.Start || before.End != after.End || before.IsAllDay != after.IsAllDay || before.TimeZone != after.TimeZone)
        {
            var switched = before.IsAllDay != after.IsAllDay;
            patch["start"] = Time(after.Start, after.IsAllDay, after.TimeZone, switched);
            patch["end"]   = Time(after.End, after.IsAllDay, after.TimeZone, switched);
        }

        if (before.Location != after.Location)
        {
            patch["location"] = after.Location;
        }

        if (before.Description != after.Description)
        {
            patch["description"] = TextToHtml(after.Description);
        }

        if (before.ColorId != after.ColorId)
        {
            patch["colorId"] = after.ColorId;
        }

        if (!before.Guests.SequenceEqual(after.Guests))
        {
            patch["attendees"] = Attendees(after.Guests);
        }

        if (before.UseDefaultReminders != after.UseDefaultReminders || !before.ReminderMinutes.SequenceEqual(after.ReminderMinutes))
        {
            patch["reminders"] = Reminders(after);
        }

        if (!before.Recurrence.SequenceEqual(after.Recurrence))
        {
            patch["recurrence"] = Strings(after.Recurrence);
        }

        return patch;
    }

    /// <summary>Applies a patch to stored JSON the way Google will (merge objects, remove nulls, replace the rest).</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static string ApplyPatch(string rawJson, JsonObject patch)
    {
        var target = Parse(rawJson);
        Merge(target, patch);
        return target.ToJsonString();
    }

    /// <summary>A created event's body as Leaf stores it before Google answers.</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static string AsLocal(string createdJson)
    {
        var local = Parse(createdJson);
        local["status"] = "confirmed";
        return local.ToJsonString();
    }

    /// <summary>One instance of a series as its own row (Google makes the same row once an instance is changed).</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static string MaterializeInstance(string masterJson, string instanceId, DateTimeOffset originalStart, bool isAllDay, DateTimeOffset start, DateTimeOffset end)
    {
        var instance = Parse(masterJson);
        var zone     = (string?)(instance["start"] as JsonObject)?["timeZone"];
        var masterId = (string?)instance["id"];

        instance.Remove("recurrence");
        instance.Remove("etag");
        instance.Remove("htmlLink");
        instance["id"]                = instanceId;
        instance["recurringEventId"]  = masterId;
        instance["originalStartTime"] = Time(originalStart, isAllDay, zone, clearOther: false);
        instance["start"]             = Time(start, isAllDay, zone, clearOther: false);
        instance["end"]               = Time(end, isAllDay, zone, clearOther: false);

        return instance.ToJsonString();
    }

    /// <summary>The row that hides one canceled instance of a series (what Google sends for it).</summary>
    public static string CancelledInstance(string masterId, string instanceId, DateTimeOffset originalStart, bool isAllDay, string? timeZone) =>
        new JsonObject
        {
            ["id"]                = instanceId,
            ["status"]            = "cancelled",
            ["recurringEventId"]  = masterId,
            ["originalStartTime"] = Time(originalStart, isAllDay, timeZone, clearOther: false),
        }.ToJsonString();

    /// <summary>A copy of an event for <c>events.insert</c> under <paramref name="newId"/>, without the fields Google owns.</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static string CloneForCreate(string rawJson, string newId, bool keepRecurrence = true)
    {
        var clone = Parse(rawJson);
        foreach (var name in GoogleOwned)
        {
            clone.Remove(name);
        }

        if (!keepRecurrence)
        {
            clone.Remove("recurrence");
        }

        clone["id"] = newId;
        return clone.ToJsonString();
    }

    /// <summary>The event with new start and end (replacing both objects).</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static string WithTimes(string rawJson, DateTimeOffset start, DateTimeOffset end, bool isAllDay, string? timeZone)
    {
        var ev = Parse(rawJson);
        ev["start"] = Time(start, isAllDay, timeZone, clearOther: false);
        ev["end"]   = Time(end, isAllDay, timeZone, clearOther: false);
        return ev.ToJsonString();
    }

    /// <summary>The event with the signed-in user's reply and note set (other guests untouched).</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static string WithResponse(string rawJson, ResponseStatus response, string? note)
    {
        var ev = Parse(rawJson);
        if (ev["attendees"] is JsonArray attendees)
        {
            foreach (var attendee in attendees.OfType<JsonObject>().Where(IsSelf))
            {
                attendee["responseStatus"] = ResponseText(response);
                if (note is { Length: > 0 })
                {
                    attendee["comment"] = note;
                }
                else
                {
                    attendee.Remove("comment");
                }
            }
        }

        return ev.ToJsonString();
    }

    /// <summary><c>{"attendees": [...]}</c> from the event, for sending a reply.</summary>
    /// <exception cref="JsonException">The JSON is invalid or not an object.</exception>
    public static JsonObject AttendeesPatch(string rawJson) =>
        new() { ["attendees"] = Parse(rawJson)["attendees"]?.DeepClone() ?? new JsonArray() };

    /// <summary>
    /// A new event whose title contains "birthday" becomes a yearly all-day event on its local date (spec 7.2).
    /// A draft that already repeats, or has another title, comes back unchanged.
    /// </summary>
    public static EventDraft ApplyBirthdayRule(EventDraft draft, TimeZoneInfo zone)
    {
        if (draft.Recurrence.Count > 0 || !draft.Title.Contains("birthday", StringComparison.OrdinalIgnoreCase))
        {
            return draft;
        }

        var day   = draft.IsAllDay ? DateOnly.FromDateTime(draft.Start.UtcDateTime) : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(draft.Start, zone).DateTime);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return draft with { IsAllDay = true, Start = start, End = start.AddDays(1), Recurrence = ["RRULE:FREQ=YEARLY"] };
    }

    /// <summary>Plain text as Google description HTML: escaped, with line breaks as <c>&lt;br&gt;</c> (WinUI text boxes use <c>\r</c>).</summary>
    public static string TextToHtml(string text) =>
        WebUtility.HtmlEncode(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Replace("\n", "<br>", StringComparison.Ordinal);

    /// <summary>Google's word for a reply.</summary>
    public static string ResponseText(ResponseStatus response) => response switch
    {
        ResponseStatus.Accepted  => "accepted",
        ResponseStatus.Tentative => "tentative",
        ResponseStatus.Declined  => "declined",
        _                        => "needsAction",
    };

    // =========================================================================
    // INTERNALS
    // =========================================================================

    static JsonObject Parse(string rawJson) =>
        JsonNode.Parse(rawJson) as JsonObject ?? throw new JsonException("The event JSON is not an object.");

    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is null)
            {
                target.Remove(name);
                continue;
            }

            if (value is JsonObject child && target[name] is JsonObject existing)
            {
                Merge(existing, child);
                continue;
            }

            target[name] = value.DeepClone();
        }
    }

    // All-day: {"date"}; timed: {"dateTime" in the event's zone, "timeZone"}. clearOther nulls the other form when switching.
    static JsonObject Time(DateTimeOffset instant, bool isAllDay, string? timeZone, bool clearOther)
    {
        if (isAllDay)
        {
            var date = new JsonObject { ["date"] = instant.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            if (clearOther)
            {
                date["dateTime"] = null;
                date["timeZone"] = null;
            }

            return date;
        }

        var local = FindZone(timeZone) is { } zone ? TimeZoneInfo.ConvertTime(instant, zone) : instant;
        var time  = new JsonObject { ["dateTime"] = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) };
        if (timeZone is not null)
        {
            time["timeZone"] = timeZone;
        }

        if (clearOther)
        {
            time["date"] = null;
        }

        return time;
    }

    static JsonArray Attendees(IReadOnlyList<Guest> guests)
    {
        var array = new JsonArray();
        foreach (var guest in guests)
        {
            var attendee = new JsonObject { ["email"] = guest.Email, ["responseStatus"] = ResponseText(guest.Response) };
            if (guest.Optional)
            {
                attendee["optional"] = true;
            }

            if (guest.Comment is { Length: > 0 })
            {
                attendee["comment"] = guest.Comment;
            }

            // Read-only to Google (ignored there), but local rendering reads them
            if (guest.IsSelf)
            {
                attendee["self"] = true;
            }

            if (guest.IsOrganizer)
            {
                attendee["organizer"] = true;
            }

            array.Add((JsonNode)attendee);
        }

        return array;
    }

    // ponytail: custom reminders are popups only (they become Windows notifications); email reminders set elsewhere are dropped when reminders are edited
    static JsonObject Reminders(EventDraft draft)
    {
        var reminders = new JsonObject { ["useDefault"] = draft.UseDefaultReminders };
        if (!draft.UseDefaultReminders)
        {
            var overrides = new JsonArray();
            foreach (var minutes in draft.ReminderMinutes)
            {
                overrides.Add((JsonNode)new JsonObject { ["method"] = "popup", ["minutes"] = minutes });
            }

            reminders["overrides"] = overrides;
        }

        return reminders;
    }

    static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        return array;
    }

    static bool IsSelf(JsonObject attendee) =>
        attendee["self"] is JsonValue self && self.TryGetValue<bool>(out var isSelf) && isSelf;

    static List<Guest> Guests(JsonElement root)
    {
        if (Get(root, "attendees") is not { ValueKind: JsonValueKind.Array } attendees)
        {
            return [];
        }

        var guests = new List<Guest>();
        foreach (var attendee in attendees.EnumerateArray())
        {
            if (Str(attendee, "email") is { Length: > 0 } email)
            {
                guests.Add(new Guest(email, Flag(attendee, "optional"), ParseResponse(Str(attendee, "responseStatus")), Str(attendee, "comment"), Flag(attendee, "self"), Flag(attendee, "organizer")));
            }
        }

        return guests;
    }

    static IReadOnlyList<int> PopupMinutes(JsonElement root)
    {
        if (Get(Get(root, "reminders"), "overrides") is not { ValueKind: JsonValueKind.Array } overrides)
        {
            return [];
        }

        return [.. overrides.EnumerateArray()
            .Where(o => Str(o, "method") == "popup" && Get(o, "minutes") is { ValueKind: JsonValueKind.Number })
            .Select(o => Get(o, "minutes")!.Value.TryGetInt32(out var m) ? m : 0)
            .Order()];
    }

    static IReadOnlyList<string> Lines(JsonElement root) =>
        Get(root, "recurrence") is { ValueKind: JsonValueKind.Array } lines
            ? [.. lines.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!)]
            : [];

    /// <summary>A reply from Google's word for it (anything unknown reads as "not answered").</summary>
    public static ResponseStatus ParseResponse(string? value) => value switch
    {
        "accepted"  => ResponseStatus.Accepted,
        "tentative" => ResponseStatus.Tentative,
        "declined"  => ResponseStatus.Declined,
        _           => ResponseStatus.NeedsAction,
    };

    static TimeZoneInfo? FindZone(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    // Property lookup that tolerates non-object parents (invites come from anyone)
    static JsonElement? Get(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } parent && parent.TryGetProperty(name, out var value) ? value : null;

    static string? Str(JsonElement? element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    static bool Flag(JsonElement element, string name) => Get(element, name) is { ValueKind: JsonValueKind.True };
}
