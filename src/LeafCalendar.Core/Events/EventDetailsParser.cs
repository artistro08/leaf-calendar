using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Events;

/// <summary>Reads display details from an event's raw Google JSON (AOT safe: <see cref="JsonDocument"/> only).</summary>
public static partial class EventDetailsParser
{
    /// <summary>Title shown when Google has none.</summary>
    public const string NoTitle = "(No title)";

    private const int MaxDescriptionLength = 10_000;

    // Input bound applied before any regex runs, so a huge invite can't stall the UI thread
    private const int MaxHtmlInputLength = MaxDescriptionLength * 4;

    /// <summary>Parses one event. Pass <paramref name="includeDescription"/> false to skip the description cleanup (it reads as empty).</summary>
    /// <remarks>Valid JSON of an unexpected shape (a non-object root, or fields of the wrong type) reads as missing fields.</remarks>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static EventDetails Parse(string rawJson, bool includeDescription = true)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        // A title with nothing to see once cleaned for display (only spaces or invisible characters) reads as no title
        var title = String(root, "summary") is { } summary && Tray.DisplayText.Clean(summary, int.MaxValue).Length > 0 ? summary : NoTitle;

        return new EventDetails(
            title,
            String(root, "location"),
            includeDescription && String(root, "description") is { } html ? HtmlToText(html) : "",
            KindOf(String(root, "eventType")),
            SelfResponse(root),
            String(root, "colorId"),
            ConferenceUri(root, includeDescription),
            String(root, "transparency") == "transparent",
            Get(root, "attendees") is { ValueKind: JsonValueKind.Array } guests ? guests.GetArrayLength() : 0,
            Get(root, "organizer") is { } organizer ? String(organizer, "email") : null,
            VisibilityOf(String(root, "visibility")),
            HasOtherGuests(root));
    }

    /// <summary>
    /// Turns Google's description HTML into plain text. Line-breaking tags become newlines, list items
    /// become bullets, every other tag is dropped, entities are decoded, and consecutive newlines collapse to one.
    /// The result is capped at 10,000 characters plus an ellipsis.
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (html.Length > MaxHtmlInputLength)
        {
            html = html[..MaxHtmlInputLength];
        }

        var text = ListItemOpen().Replace(html, "\n• ");
        text = LineBreak().Replace(text, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = RepeatedNewlines().Replace(text, "\n").Trim();

        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] + "…" : text;
    }

    // Only Google's documented values pass; anything else (including other casing) is the default
    private static string VisibilityOf(string? value) => value is "public" or "private" or "confidential" ? value : "default";

    private static EventKind KindOf(string? eventType) => eventType switch
    {
        "focusTime" => EventKind.FocusTime,
        "outOfOffice" => EventKind.OutOfOffice,
        "birthday" => EventKind.Birthday,
        "workingLocation" => EventKind.WorkingLocation,
        _ => EventKind.Default,
    };

    // Anyone on the guest list but you and the rooms
    private static bool HasOtherGuests(JsonElement root) =>
        Get(root, "attendees") is { ValueKind: JsonValueKind.Array } attendees
        && attendees.EnumerateArray().Any(a => Get(a, "self") is not { ValueKind: JsonValueKind.True } && Get(a, "resource") is not { ValueKind: JsonValueKind.True });

    private static ResponseStatus SelfResponse(JsonElement root)
    {
        if (Get(root, "attendees") is not { ValueKind: JsonValueKind.Array } attendees)
        {
            return ResponseStatus.Accepted;
        }

        foreach (var attendee in attendees.EnumerateArray())
        {
            if (Get(attendee, "self") is { ValueKind: JsonValueKind.True })
            {
                return String(attendee, "responseStatus") switch
                {
                    "declined" => ResponseStatus.Declined,
                    "tentative" => ResponseStatus.Tentative,
                    "needsAction" => ResponseStatus.NeedsAction,
                    _ => ResponseStatus.Accepted,
                };
            }
        }

        return ResponseStatus.Accepted;
    }

    // Only https links count; Google's conference data first, then a meeting link pasted in the location, then
    // (when the description is being read anyway) one in the description. Occurrence loads skip the description.
    private static Uri? ConferenceUri(JsonElement root, bool includeDescription)
    {
        if (Get(Get(root, "conferenceData"), "entryPoints") is { ValueKind: JsonValueKind.Array } entryPoints)
        {
            foreach (var entry in entryPoints.EnumerateArray())
            {
                if (String(entry, "entryPointType") == "video" && Https(String(entry, "uri")) is { } video)
                {
                    return video;
                }
            }
        }

        return Https(String(root, "hangoutLink"))
            ?? LinkSafety.FindMeetingLink(String(root, "location"))
            ?? (includeDescription ? LinkSafety.FindMeetingLink(DecodedDescription(root)) : null);
    }

    // Description HTML with entities decoded ("&amp;" back to "&"), bounded first, so pasted links keep all their parameters
    private static string? DecodedDescription(JsonElement root)
    {
        var html = String(root, "description");
        return html is null ? null : WebUtility.HtmlDecode(html.Length > MaxHtmlInputLength ? html[..MaxHtmlInputLength] : html);
    }

    // An https link without a user name (one can pose as a host: https://meet.google.com@evil.example opens evil.example)
    private static Uri? Https(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;

    // Property lookup that tolerates non-object parents
    private static JsonElement? Get(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } parent && parent.TryGetProperty(name, out var value) ? value : null;

    private static string? String(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // As in DescriptionFormatter, it's a tag only when the name ends at a space, ">", or "/>": a link or address a
    // plain-text invite put in angle brackets ("<https://a.example/x>", "<li@a.example>") is text
    [GeneratedRegex(@"<\s*li(?=\s|/?>)[^<>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemOpen();

    // A line break, a block's opening tag (it starts on a new line), or a closing tag that ends a line
    [GeneratedRegex(@"<\s*(br|p|div|h[1-6])(?=\s|/?>)[^<>]*>|<\s*/\s*(p|div|li|ul|ol|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    // Any other tag, and comments, declarations, and processing instructions
    [GeneratedRegex(@"<\s*/?[a-zA-Z][a-zA-Z0-9]*(?:[:-][a-zA-Z][a-zA-Z0-9]*)*(?=\s|/?>)[^<>]*>|<[!?][^<>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex RepeatedNewlines();
}
