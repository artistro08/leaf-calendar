using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Editing;

/// <summary>The editor's video call line.</summary>
public static class EditorConference
{
    /// <summary>
    /// "Google Meet link is added when you save" for a call being added, "Video call: {host}" for the one the event
    /// already has (or a meeting link found in its location or description), else "No video call".
    /// </summary>
    public static string Text(bool hadConference, bool hasConference, Uri? conferenceUri)
    {
        // Being Added (Google makes the link on save)
        if (hasConference && !hadConference)
        {
            return "Google Meet link is added when you save";
        }

        // Being Removed
        if (hadConference && !hasConference)
        {
            return "No video call";
        }

        // The Host In Its ASCII Form, Like The Details Panel (a look-alike host can't pass for the real one)
        if (conferenceUri is { } uri)
        {
            return LinkSafety.TryIdnHost(uri, out var host) ? $"Video call: {host}" : "Video call";
        }

        return hasConference ? "Video call" : "No video call";
    }
}
