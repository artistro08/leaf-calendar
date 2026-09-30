using System.Globalization;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Alerts;

/// <summary>What clicking a notification (or one of its buttons) does.</summary>
public enum ToastAction
{
    /// <summary>Open the main window on the event.</summary>
    Open,

    /// <summary>Open the event's meeting link.</summary>
    Join,

    /// <summary>Reply Yes.</summary>
    Accept,

    /// <summary>Reply No.</summary>
    Decline,

    /// <summary>Reply Maybe.</summary>
    Maybe,

    /// <summary>Open the conflict dialog.</summary>
    ReviewConflicts,

    /// <summary>Open Settings &#x203A; Accounts.</summary>
    SignIn,
}

/// <summary>
/// A notification's activation arguments: the action, the Leaf profile it belongs to, and the event instance (account,
/// calendar, event ID, and start) or account it's about.
/// </summary>
/// <remarks>
/// <para>
/// Written as <c>key=value;key=value</c> with every value URL-escaped, so calendar IDs with <c>;</c>, <c>=</c>, or
/// <c>%</c> survive. <see cref="Parse"/> never throws: anything malformed, unknown, or missing a field its action needs
/// reads as null, because a notification's arguments come back from outside Leaf.
/// </para>
/// <para>
/// Only IDs travel here, never event text, email addresses, tokens, or a meeting URL. A Join click looks the event up
/// by its IDs and launches the link through the usual safety check at that moment.
/// </para>
/// </remarks>
public sealed record ToastArgs(ToastAction Action, string Profile, string? AccountId = null, string? CalendarId = null, string? EventId = null, DateTimeOffset? Start = null)
{
    const int MaxLength = 2048;

    // Largest instant DateTimeOffset can hold, in Unix milliseconds
    const long MaxUnixMs = 253402300799999;

    /// <summary>Arguments about one event instance.</summary>
    public static ToastArgs For(ToastAction action, string profile, CalendarOccurrence occurrence) =>
        new(action, profile, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId, occurrence.Start);

    /// <summary>The <c>key=value;...</c> text.</summary>
    public string Encode()
    {
        var parts = new List<string> { "action=" + Action, "profile=" + Uri.EscapeDataString(Profile) };
        Add(parts, "account", AccountId);
        Add(parts, "calendar", CalendarId);
        Add(parts, "event", EventId);
        Add(parts, "start", Start?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return string.Join(";", parts);
    }

    /// <summary>Reads <see cref="Encode"/>'s text; null when it's malformed or incomplete.</summary>
    public static ToastArgs? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }

        // Fields
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return null;
            }

            values[part[..equals]] = Uri.UnescapeDataString(part[(equals + 1)..]);
        }

        // Action (A Name, Never A Number) And Profile
        if (!values.TryGetValue("action", out var name) || !name.All(char.IsAsciiLetter) || !Enum.TryParse<ToastAction>(name, out var action)
            || !values.TryGetValue("profile", out var profile) || profile.Length == 0)
        {
            return null;
        }

        // Event Instance
        var account  = values.GetValueOrDefault("account");
        var calendar = values.GetValueOrDefault("calendar");
        var eventId  = values.GetValueOrDefault("event");
        DateTimeOffset? start = null;
        if (values.TryGetValue("start", out var ms))
        {
            if (!long.TryParse(ms, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) || unix > MaxUnixMs)
            {
                return null;
            }

            start = DateTimeOffset.FromUnixTimeMilliseconds(unix);
        }

        // What Each Action Needs
        var aboutEvent = action is ToastAction.Open or ToastAction.Join or ToastAction.Accept or ToastAction.Decline or ToastAction.Maybe;
        if ((aboutEvent && (account is null || calendar is null || eventId is null || start is null)) || (action == ToastAction.SignIn && account is null))
        {
            return null;
        }

        return new ToastArgs(action, profile, account, calendar, eventId, start);
    }

    static void Add(List<string> parts, string key, string? value)
    {
        if (value is not null)
        {
            parts.Add(key + "=" + Uri.EscapeDataString(value));
        }
    }
}
