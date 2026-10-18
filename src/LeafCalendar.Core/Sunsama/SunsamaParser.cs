using System.Globalization;
using System.Text.Json;

namespace LeafCalendar.Core.Sunsama;

/// <summary>
/// Turns the JSON of Sunsama's <c>sunsama://me</c>, <c>sunsama://tasks/{day}</c> and <c>sunsama://calendar/events/{day}</c>
/// resources into <see cref="SunsamaProfile"/> and <see cref="SunsamaTask"/> values (spec 2026-10-16, section 4.3).
/// </summary>
/// <remarks>
/// Reads <see cref="JsonElement"/>s by hand, so a missing field or one of the wrong kind skips that value instead of
/// throwing: a task shows what it can. Everything read is untrusted text.
/// </remarks>
public static class SunsamaParser
{
    /// <summary>Reads the signed-in user: email, time zone and Google calendar IDs.</summary>
    /// <param name="me">The <c>sunsama://me</c> JSON.</param>
    /// <returns>The profile, or null when there's no user or the time zone isn't known to Windows.</returns>
    public static SunsamaProfile? Profile(JsonElement me)
    {
        if (Member(me, "user") is not { ValueKind: JsonValueKind.Object } user
            || Str(user, "timezone") is not { } zoneId
            || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
        {
            return null;
        }

        var google = Items(user, "calendars")
            .Where(c => Str(c, "provider") == "google-calendar")
            .Select(c => Str(c, "id"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return new SunsamaProfile(Str(user, "email") ?? "", zone, google);
    }

    /// <summary>Reads one day's tasks, resolving each task's timebox and imported-from events against the day's events.</summary>
    /// <param name="tasks">The <c>sunsama://tasks/{day}</c> JSON.</param>
    /// <param name="events">The <c>sunsama://calendar/events/{day}</c> JSON.</param>
    /// <param name="profile">The signed-in user, for the zone and the Google calendar IDs.</param>
    /// <returns>Every task with an ID, in Sunsama's order.</returns>
    public static IReadOnlyList<SunsamaTask> Day(JsonElement tasks, JsonElement events, SunsamaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // The Day's Timed Events, by ID
        var byId = new Dictionary<string, SunsamaEventRef>(StringComparer.Ordinal);
        foreach (var e in Items(events, "events"))
        {
            if (Str(e, "eventId") is { } id
                && Str(e, "calendarId") is { } calendar
                && Instant(Str(Member(e, "start"), "dateTime")) is { } start
                && Instant(Str(Member(e, "end"), "dateTime")) is { } end
                && end > start)
            {
                byId.TryAdd(id, new SunsamaEventRef(id, calendar, Str(e, "title") ?? "", start, end, profile.GoogleCalendarIds.Contains(calendar)));
            }
        }

        var result = new List<SunsamaTask>();
        foreach (var t in Items(tasks, "tasks"))
        {
            if (Str(t, "_id") is not { } id)
            {
                continue;
            }

            var projected = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var entry in Items(t, "projectedTimeEntries"))
            {
                if (SunsamaText.LocalTime(Str(entry, "startDate"), profile.Zone) is not { } start
                    || SunsamaText.LocalTime(Str(entry, "endDate"), profile.Zone) is not { } end)
                {
                    continue;
                }

                // A start moved out of a skipped hour (spring forward) can meet its end: keep the wall-clock length
                if (end <= start && WallLength(entry) is { } length && length > TimeSpan.Zero)
                {
                    end = start + length;
                }

                if (end > start)
                {
                    projected.Add((start, end));
                }
            }

            // Timeboxes, Then the Event the Task Was Imported From
            var eventIds = Items(t, "timeboxEventIds")
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Append(Str(Member(Member(t, "importedFrom"), "identifier"), "sunsamaId"));
            var tied = eventIds
                .Select(e => e is not null && byId.TryGetValue(e, out var found) ? found : null)
                .OfType<SunsamaEventRef>()
                .DistinctBy(e => e.EventId)
                .ToList();

            var tracked = Minutes(Str(Member(t, "actualTimeSpent"), "total"));
            result.Add(new SunsamaTask(
                id,
                Str(t, "title") ?? "",
                Str(t, "channel") ?? "",
                Minutes(Str(t, "timeEstimate")),
                tracked is 0 ? null : tracked,
                Member(t, "completed")?.ValueKind == JsonValueKind.True,
                projected,
                tied));
        }

        return result;
    }

    // The length on the clock between an entry's start and end text, ignoring the zone
    private static TimeSpan? WallLength(JsonElement entry) =>
        DateTime.TryParseExact(Str(entry, "startDate"), "yyyy-MM-dd h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
        && DateTime.TryParseExact(Str(entry, "endDate"), "yyyy-MM-dd h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            ? to - from
            : null;

    private static int? Minutes(string? text) => SunsamaText.Duration(text) is { } d ? (int)d.TotalMinutes : null;

    private static DateTimeOffset? Instant(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value.ToUniversalTime() : null;

    private static JsonElement? Member(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) ? value : null;

    private static string? Str(JsonElement? obj, string name) =>
        Member(obj, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // Items of Any Kind: the readers above give null for a wrong kind, so callers skip them
    private static List<JsonElement> Items(JsonElement obj, string name) =>
        Member(obj, name) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];
}
