using System.Globalization;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Api;

/// <summary>
/// Times as the local API writes and reads them (local API spec 4): ISO 8601 instants that always carry an offset, and
/// plain dates for all-day events (end exclusive, as Google does). A time without an offset is refused rather than guessed.
/// </summary>
public static partial class ApiTime
{
    /// <summary>Reads an instant with an offset (<c>Z</c> or <c>±hh:mm</c>), or null.</summary>
    public static DateTimeOffset? ReadInstant(string? text) =>
        text is not null && InstantPattern().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;

    /// <summary>Reads a <c>yyyy-MM-dd</c> date, or null.</summary>
    public static DateOnly? ReadDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>
    /// Reads a start and end: two dates (all-day, as UTC midnights like <see cref="Events.CalendarOccurrence"/>) or two
    /// instants. The end must be after the start.
    /// </summary>
    /// <exception cref="ApiProtocolException">Missing, mixed, without an offset, or backwards (<see cref="ApiErrors.InvalidParams"/>).</exception>
    public static (DateTimeOffset Start, DateTimeOffset End, bool AllDay) ReadSpan(string? start, string? end)
    {
        if (ReadDate(start) is { } firstDay && ReadDate(end) is { } lastDay)
        {
            var from = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var to = new DateTimeOffset(lastDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return to > from ? (from, to, true) : throw Invalid("end must be after start (all-day ends are exclusive).");
        }

        if (ReadInstant(start) is { } s && ReadInstant(end) is { } e)
        {
            return e > s ? (s, e, false) : throw Invalid("end must be after start.");
        }

        throw Invalid("start and end must both be dates (yyyy-MM-dd) or both be times with an offset.");
    }

    /// <summary>An instant as <c>yyyy-MM-ddTHH:mm:ss±hh:mm</c> in <paramref name="zone"/>.</summary>
    public static string Write(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>A date as <c>yyyy-MM-dd</c>.</summary>
    public static string Write(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An invalid-params error with a message for the client.</summary>
    internal static ApiProtocolException Invalid(string message) => new(ApiErrors.InvalidParams, message);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPattern();
}
