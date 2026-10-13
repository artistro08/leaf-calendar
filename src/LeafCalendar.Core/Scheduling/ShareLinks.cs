using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Scheduling;

/// <summary>The copied text for times that each carry a booking link.</summary>
/// <param name="Plain">Plain text, one "time: link" line per time.</param>
/// <param name="Html">Rich text, where each time is the link text.</param>
/// <param name="GuestsDropped">True when the link takes no extra guests but more than one was chosen, so only the first is in the links.</param>
public sealed record LinkedTimes(string Plain, string Html, bool GuestsDropped);

/// <summary>Builds the copied text where each proposed time links to its booking page.</summary>
public static class ShareLinks
{
    /// <summary>The line Copy adds after booking links until you change it in Settings › Scheduling.</summary>
    public const string DefaultNote = "You can schedule time via the links or just let me know what works for you.";

    // The first default (owner, 2026-10-13, before the wording fix), replaced where it was saved unchanged
    internal const string FirstDefaultNote = "You schedule time via the links or just let me know what works for you";

    /// <summary>Longest note kept.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>Splits the saved ranges into bookable times and writes them out with a link each.</summary>
    /// <param name="builder">Makes the booking link for a time.</param>
    /// <param name="link">The scheduling link the times are booked on.</param>
    /// <param name="ranges">The saved ranges.</param>
    /// <param name="open">The link's bookable times, or null when they couldn't be fetched.</param>
    /// <param name="guests">The people to fill into each booking link.</param>
    /// <param name="message">The share message, or null.</param>
    /// <param name="note">The line added on its own line at the end (Settings › Scheduling); blank adds nothing.</param>
    /// <param name="zone">The zone whose clock the reader uses.</param>
    /// <param name="use24Hour">"14:00–16:00" instead of "2–4 PM".</param>
    /// <returns>The plain and rich versions and whether extra guests were left out.</returns>
    public static LinkedTimes Build(
        ITimeLinkBuilder builder,
        SchedulingLink link,
        IReadOnlyList<BusyRange> ranges,
        IReadOnlyList<BusyRange>? open,
        IReadOnlyList<Contact> guests,
        string? message,
        string? note,
        TimeZoneInfo zone,
        bool use24Hour)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(guests);

        var pieces = ranges.SelectMany(r => SlotFitting.Split(r, open, link.Minutes)).ToList();
        var texts = AvailabilityText.FormatTimes(pieces, zone, use24Hour);
        var pairs = pieces.Select((p, i) => (Text: texts[i], Link: builder.TimeLink(link, p.Start, zone, guests))).ToList();

        // Colons Are Safe In A Query, So The Pasted Text Keeps The Time Readable
        var plain = AvailabilityText.Compose(message, string.Join("\r\n", pairs.Select(p => $"{p.Text}: {p.Link.AbsoluteUri.Replace("%3A", ":", StringComparison.Ordinal)}")));
        var html = AvailabilityText.ComposeHtml(message, pairs);

        // The Note, On Its Own Line At The End
        var line = (note ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (line.Length > 0)
        {
            plain += "\r\n" + line.Replace("\n", "\r\n", StringComparison.Ordinal);
            html += "<br>" + System.Net.WebUtility.HtmlEncode(line).Replace("\n", "<br>", StringComparison.Ordinal);
        }

        return new(plain, html, !link.AllowsGuests && guests.Count > 1);
    }
}
