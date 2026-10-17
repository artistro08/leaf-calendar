using System.Globalization;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Sunsama;

/// <summary>
/// Reads the plain-text values Sunsama's MCP server sends: durations ("1 hours and 24 minutes"), local times without a
/// zone ("2026-10-16 10:50 AM") and task IDs.
/// </summary>
/// <remarks>Everything here comes from Sunsama and is untrusted: anything unreadable gives null, never an exception.</remarks>
public static partial class SunsamaText
{
    /// <summary>Reads a duration such as "1 hours and 24 minutes", "30 minutes" or "1 hour"; null when unreadable.</summary>
    /// <param name="text">The duration text from Sunsama.</param>
    /// <returns>The duration, or null when the text isn't one.</returns>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || DurationPattern().Match(text.Trim()) is not { Success: true } m)
        {
            return null;
        }

        var hasHours = m.Groups["h"].Success;
        var hasMinutes = m.Groups["m"].Success;

        // An Empty Match or a Dangling "and" Isn't a Duration
        if ((!hasHours && !hasMinutes) || (text.Contains(" and", StringComparison.Ordinal) && !(hasHours && hasMinutes)))
        {
            return null;
        }

        var hours = hasHours ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = hasMinutes ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromMinutes((hours * 60) + minutes);
    }

    /// <summary>
    /// Reads a local time such as "2026-10-16 10:50 AM" in <paramref name="zone"/>. A time that happens twice (fall back)
    /// is the first one; a time that doesn't exist (spring forward) moves forward by the gap. Null when unreadable.
    /// </summary>
    /// <param name="text">The local time text from Sunsama.</param>
    /// <param name="zone">The Sunsama account's time zone.</param>
    /// <returns>The instant in UTC, or null when the text isn't a time.</returns>
    public static DateTimeOffset? LocalTime(string? text, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!DateTime.TryParseExact(text?.Trim(), "yyyy-MM-dd h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        // Skipped Hour: move forward by the gap (the zone's DST delta)
        if (zone.IsInvalidTime(local))
        {
            var rule = zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= local && local <= r.DateEnd);
            local = local.Add(rule?.DaylightDelta ?? TimeSpan.FromHours(1));
        }

        // Repeated Hour: the first one has the larger (daylight) offset
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>True for Sunsama's task ID shape: 24 lowercase hex characters.</summary>
    /// <param name="id">A task ID from Sunsama.</param>
    /// <returns>True when the ID is safe to put in a link.</returns>
    public static bool IsTaskId(string? id) => id is { Length: 24 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    [GeneratedRegex(@"^(?:(?<h>\d{1,4}) hours?)?(?: and)?(?: ?(?<m>\d{1,4}) minutes?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
