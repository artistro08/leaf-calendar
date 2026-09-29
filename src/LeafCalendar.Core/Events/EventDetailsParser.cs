using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Events;

/// <summary>Reads display details from an event's raw Google JSON (AOT safe: <see cref="JsonDocument"/> only).</summary>
public static partial class EventDetailsParser
{
    /// <summary>Title shown when Google has none.</summary>
    public const string NoTitle = "(No title)";

    const int MaxDescriptionLength = 10_000;

    // Input bound applied before any regex runs, so a huge invite can't stall the UI thread
    const int MaxHtmlInputLength = MaxDescriptionLength * 4;

    /// <summary>Parses one event.</summary>
    /// <remarks>Valid JSON of an unexpected shape (a non-object root, or fields of the wrong type) reads as missing fields.</remarks>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static EventDetails Parse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var title = String(root, "summary") is { Length: > 0 } summary ? summary : NoTitle;

        return new EventDetails(
            title,
            String(root, "location"),
            String(root, "description") is { } html ? HtmlToText(html) : "",
            KindOf(String(root, "eventType")),
            SelfResponse(root),
            String(root, "colorId"),
            ConferenceUri(root),
            String(root, "transparency") == "transparent",
            Get(root, "attendees") is { ValueKind: JsonValueKind.Array } guests ? guests.GetArrayLength() : 0,
            Get(root, "organizer") is { } organizer ? String(organizer, "email") : null);
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

        var text = ListItemOpen().Replace(html, "• ");
        text = LineBreak().Replace(text, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = RepeatedNewlines().Replace(text, "\n").Trim();

        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] + "…" : text;
    }

    static EventKind KindOf(string? eventType) => eventType switch
    {
        "focusTime"       => EventKind.FocusTime,
        "outOfOffice"     => EventKind.OutOfOffice,
        "birthday"        => EventKind.Birthday,
        "workingLocation" => EventKind.WorkingLocation,
        _                 => EventKind.Default,
    };

    static ResponseStatus SelfResponse(JsonElement root)
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
                    "declined"    => ResponseStatus.Declined,
                    "tentative"   => ResponseStatus.Tentative,
                    "needsAction" => ResponseStatus.NeedsAction,
                    _             => ResponseStatus.Accepted,
                };
            }
        }

        return ResponseStatus.Accepted;
    }

    // Only https links count; anything else from an invite is ignored
    static Uri? ConferenceUri(JsonElement root)
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

        return Https(String(root, "hangoutLink"));
    }

    static Uri? Https(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    // Property lookup that tolerates non-object parents
    static JsonElement? Get(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } parent && parent.TryGetProperty(name, out var value) ? value : null;

    static string? String(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    [GeneratedRegex(@"<\s*li\b[^<>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemOpen();

    [GeneratedRegex(@"<\s*br\s*/?\s*>|<\s*/\s*(p|div|li|ul|ol|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex RepeatedNewlines();
}
