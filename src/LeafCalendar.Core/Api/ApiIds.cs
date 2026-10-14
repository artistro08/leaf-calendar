using System.Buffers.Text;
using System.Globalization;
using System.Text;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Api;

/// <summary>An event instance as an API ID names it: account, calendar, Google's event ID, and the instance's start.</summary>
public sealed record EventKey(string AccountId, string CalendarId, string EventId, DateTimeOffset Start);

/// <summary>
/// The local API's opaque IDs (local API spec 4): calendars carry their account, events their calendar and start, links
/// their provider. Each is base64url of its parts joined by newlines after a one-letter kind, so it is URL-safe and an ID
/// of one kind never reads as another. Reading never throws: anything malformed reads as null.
/// </summary>
public static class ApiIds
{
    private const int MaxLength = 2048;

    // Largest instant DateTimeOffset can hold, in Unix milliseconds
    private const long MaxUnixMs = 253402300799999;

    /// <summary>A calendar's ID.</summary>
    public static string Calendar(string accountId, string calendarId) => Pack("c", accountId, calendarId);

    /// <summary>An event instance's ID.</summary>
    public static string Event(CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return Event(occurrence.AccountId, occurrence.CalendarId, occurrence.EventId, occurrence.Start);
    }

    /// <summary>An event instance's ID.</summary>
    public static string Event(string accountId, string calendarId, string eventId, DateTimeOffset start) =>
        Pack("e", accountId, calendarId, eventId, start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>A scheduling link's ID.</summary>
    public static string Link(string providerId, string linkId) => Pack("l", providerId, linkId);

    /// <summary>Saved times' ID (a share group).</summary>
    public static string Share(long groupId) => Pack("s", groupId.ToString(CultureInfo.InvariantCulture));

    /// <summary>The share group a <see cref="Share"/> ID names, or null.</summary>
    public static long? ReadShare(string? id) =>
        Unpack(id, "s", 1) is { } p && long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var groupId) ? groupId : null;

    /// <summary>The calendar a <see cref="Calendar"/> ID names, or null.</summary>
    public static (string AccountId, string CalendarId)? ReadCalendar(string? id) =>
        Unpack(id, "c", 2) is { } p ? (p[0], p[1]) : null;

    /// <summary>The event instance an <see cref="Event(CalendarOccurrence)"/> ID names, or null.</summary>
    public static EventKey? ReadEvent(string? id) =>
        Unpack(id, "e", 4) is { } p && long.TryParse(p[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) && Math.Abs(ms) <= MaxUnixMs
            ? new EventKey(p[0], p[1], p[2], DateTimeOffset.FromUnixTimeMilliseconds(ms))
            : null;

    /// <summary>The scheduling link a <see cref="Link"/> ID names, or null.</summary>
    public static (string ProviderId, string LinkId)? ReadLink(string? id) =>
        Unpack(id, "l", 2) is { } p ? (p[0], p[1]) : null;

    private static string Pack(string kind, params string[] parts)
    {
        if (parts.Any(p => string.IsNullOrEmpty(p) || p.Contains('\n', StringComparison.Ordinal)))
        {
            throw new ArgumentException("ID parts must be non-empty and on one line.", nameof(parts));
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(kind + "\n" + string.Join('\n', parts)));
    }

    private static string[]? Unpack(string? id, string kind, int count)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            return null;
        }

        var parts = Encoding.UTF8.GetString(bytes).Split('\n');
        return parts.Length == count + 1 && parts[0] == kind && parts.Skip(1).All(p => p.Length > 0) ? parts[1..] : null;
    }
}
