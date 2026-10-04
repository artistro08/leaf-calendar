using System.Text.Json.Nodes;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Editing;

/// <summary>
/// Your changes to a calendar's entry in your Google calendar list: its name (Google's <c>summaryOverride</c>, which
/// Google Calendar shows too) and its default reminders.
/// </summary>
/// <remarks>
/// Google holds these values. Leaf sends the patch first and mirrors the result locally only after Google accepts it,
/// so an offline or refused change leaves everything as it was.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/calendarList/patch"/>
public static class CalendarEdits
{
    /// <summary>Longest name kept.</summary>
    public const int MaxName = 100;

    /// <summary>Most default reminders Google keeps.</summary>
    public const int MaxReminders = 5;

    // Google's range for reminder minutes (four weeks)
    private const int MaxMinutes = 40_320;

    /// <summary>
    /// A typed name as plain text: control, bidi, and other hidden characters removed, trimmed, and cut at
    /// <see cref="MaxName"/>. Null when nothing is left, which means "use Google's name".
    /// </summary>
    public static string? CleanName(string? text)
    {
        var name = ContactSearch.Plain(text, MaxName);
        return name.Length == 0 ? null : name;
    }

    /// <summary>The patch that renames the calendar, or (null) goes back to Google's name.</summary>
    public static string RenamePatch(string? name) =>
        new JsonObject { ["summaryOverride"] = name is null ? null : JsonValue.Create(name) }.ToJsonString();

    /// <summary>
    /// The patch that sets the calendar's default popup reminders: 0 to 40,320 minutes, no repeats, earliest first, at
    /// most five. Leaf edits popups only, so the other reminders in <paramref name="storedJson"/> (Google's
    /// <c>defaultReminders</c> as stored, such as email ones) are sent back unchanged after them; unreadable entries are left out.
    /// </summary>
    public static string RemindersPatch(IEnumerable<int> minutes, string? storedJson = null)
    {
        var reminders = new JsonArray();
        var others = OtherReminders(storedJson).ToList();
        foreach (var m in minutes.Where(m => m is >= 0 and <= MaxMinutes).Distinct().Order().Take(Math.Max(0, MaxReminders - others.Count)))
        {
            reminders.Add((JsonNode)new JsonObject { ["method"] = "popup", ["minutes"] = m });
        }

        foreach (var (method, m) in others)
        {
            reminders.Add((JsonNode)new JsonObject { ["method"] = method, ["minutes"] = m });
        }

        return new JsonObject { ["defaultReminders"] = reminders }.ToJsonString();
    }

    // The stored reminders that aren't popups, with a known method and minutes in Google's range
    private static IEnumerable<(string Method, int Minutes)> OtherReminders(string? storedJson)
    {
        JsonArray? stored;
        try
        {
            stored = string.IsNullOrEmpty(storedJson) ? null : JsonNode.Parse(storedJson) as JsonArray;
        }
        catch (System.Text.Json.JsonException)
        {
            stored = null;
        }

        return (stored ?? [])
            .OfType<JsonObject>()
            .Select(r => (Method: (r["method"] as JsonValue)?.TryGetValue<string>(out var method) == true ? method : null, Minutes: (r["minutes"] as JsonValue)?.TryGetValue<int>(out var m) == true ? m : -1))
            .Where(r => r.Method is "email" && r.Minutes is >= 0 and <= MaxMinutes)
            .Select(r => (r.Method!, r.Minutes))
            .Distinct();
    }
}
