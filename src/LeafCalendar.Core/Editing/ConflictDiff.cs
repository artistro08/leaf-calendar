using System.Text.Json;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Editing;

/// <summary>One row of the conflict dialog: a field's local ("mine") and Google value, and whether they differ.</summary>
public sealed record FieldComparison(string Field, string Mine, string Google, bool Differs);

/// <summary>
/// Lines up the local and Google versions of a conflicted event field by field, for the side-by-side dialog
/// (spec 5.5). Values are plain display text, worded as the editor and details panel show them; a missing version reads "Deleted here" or "Google's copy isn't available".
/// </summary>
public static class ConflictDiff
{
    private static readonly string[] Fields = ["Title", "When", "Location", "Description", "Guests", "Repeats", "Color", "Reminder", "Show as", "Visibility", "Video call"];

    /// <summary>The rows, in <see cref="Fields"/> order, with times shown in <paramref name="zone"/>.</summary>
    public static IReadOnlyList<FieldComparison> Compare(string? localJson, string? googleJson, TimeZoneInfo zone, bool use24h)
    {
        var mine = Describe(localJson, "Deleted here", zone, use24h);
        var google = Describe(googleJson, "Google's copy isn't available", zone, use24h);

        return [.. Fields.Select((field, i) => new FieldComparison(field, mine[i], google[i], mine[i] != google[i]))];
    }

    private static string[] Describe(string? json, string deleted, TimeZoneInfo zone, bool use24h)
    {
        if (json is null)
        {
            return [deleted, .. Enumerable.Repeat("", Fields.Length - 1)];
        }

        var details = EventDetailsParser.Parse(json);
        var draft = EventJson.ReadDraft("", "", json, default, default, isAllDay: false);
        var ev = JsonSerializer.Deserialize(json, GoogleJsonContext.Default.GoogleEvent);

        return
        [
            details.Title,
            When(ev, zone, use24h),
            details.Location ?? "",
            details.Description,
            string.Join(", ", draft.Guests.Select(g => g.Email)),
            string.Join("\n", draft.Recurrence),
            draft.ColorId is { } color ? $"Color {color}" : "Calendar color",
            Reminders(draft),
            draft.IsFree ? "Free" : "Busy",
            draft.Visibility switch { "public" => "Public", "private" or "confidential" => "Private", _ => "Default visibility" },
            EditorConference.Text(draft.HasConference, draft.HasConference, details.ConferenceUri),
        ];
    }

    // As the editor reads them: "Use calendar default", or the popup times shortest first ("10 min, 1 hr")
    private static string Reminders(EventDraft draft) =>
        draft.UseDefaultReminders ? "Use calendar default"
            : draft.ReminderMinutes.Count == 0 ? "None"
            : string.Join(", ", draft.ReminderMinutes.Select(ReminderTimes.Label));

    private static string When(GoogleEvent? ev, TimeZoneInfo zone, bool use24h)
    {
        if (ev?.Start?.Date is { } date)
        {
            var last = (ev.End?.Date ?? date.AddDays(1)).AddDays(-1);
            return last > date ? $"{TimeLabels.LongDate(date)} – {TimeLabels.LongDate(last)} · All day" : $"{TimeLabels.LongDate(date)} · All day";
        }

        if (ev?.Start?.DateTime is not { } start || ev.End?.DateTime is not { } end)
        {
            return "";
        }

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        return $"{TimeLabels.LongDate(day)} · {TimeLabels.Range(start, end, zone, use24h)}";
    }
}
