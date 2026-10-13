using System.Globalization;

namespace LeafCalendar.Core.Scheduling;

/// <summary>The messages shown over the calendar after a pick is fitted to a link (spec section 11), one per fact.</summary>
public static class FitMessages
{
    /// <summary>
    /// The messages for <paramref name="summary"/>, in the order they're stacked: the split, with the stretched and removed
    /// times on new lines of the same message, then what wasn't open. Without a split, stretched and removed are their own.
    /// </summary>
    public static IReadOnlyList<string> For(FitSummary summary, string providerName)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(providerName);

        var c = CultureInfo.InvariantCulture;
        var messages = new List<string>();

        // Nothing Kept And Nothing Removed: none of it was bookable
        if (summary.Kept == 0 && summary.Removed == 0)
        {
            messages.Add("That time is not open per your availability. Pick a different time.");
            return messages;
        }

        if (summary.Kept > 0)
        {
            messages.Add(string.Create(c, $"Split into {summary.Kept} {(summary.Kept == 1 ? "time" : "times")} of {summary.Minutes} minutes."));
        }

        // Singular wording at one: "1 time", "its full", "half of it"
        if (summary.Stretched > 0)
        {
            var one = summary.Stretched == 1;
            messages.Add(one
                ? "Last time block stretched to match the available time slot."
                : string.Create(c, $"Stretched {summary.Stretched} times to their full {summary.Minutes} minutes, since you picked more than half of each."));
        }

        if (summary.Removed > 0)
        {
            var one = summary.Removed == 1;
            messages.Add(one
                ? "Last time block removed to match the available time slots."
                : string.Create(c, $"Removed {summary.Removed} times, since you picked half or less of each."));
        }

        // Split Plus Stretched Or Removed: one card, each on its own line
        if (summary.Kept > 0 && messages.Count > 1)
        {
            messages = [string.Join('\n', messages)];
        }

        if (summary.NotOpenMinutes > 0 && summary.Kept > 0)
        {
            messages.Add($"{Duration(summary.NotOpenMinutes)} wasn't open on {providerName}.");
        }

        return messages;
    }

    /// <summary>"45 minutes", "1 hour", "2 hours 30 minutes".</summary>
    public static string Duration(int minutes)
    {
        var c = CultureInfo.InvariantCulture;
        var hours = minutes / 60;
        var rest = minutes % 60;
        var hourText = hours == 0 ? "" : hours == 1 ? "1 hour" : string.Create(c, $"{hours} hours");
        var minuteText = rest == 0 ? "" : string.Create(c, $"{rest} minutes");
        return string.Join(" ", new[] { hourText, minuteText }.Where(s => s.Length > 0));
    }
}
