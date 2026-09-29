using System.Globalization;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>A time zone offered in search. <see cref="ToString"/> is what the search box shows.</summary>
public sealed record TimeZoneChoice(string Id, string City, string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{City} ({Detail})";
}

/// <summary>
/// Finds time zones by city, IANA ID, Windows name, or common abbreviation (NYC, SF, LON...).
/// </summary>
/// <remarks>
/// A curated list of major cities comes first. Every other zone Windows knows is added from its
/// primary IANA ID. Only zones this PC can resolve are offered.
/// </remarks>
public static class TimeZoneCatalog
{
    static readonly (string Id, string City, string[] Aliases)[] Cities =
    [
        ("America/New_York", "New York", ["NYC", "NY", "EST", "EDT", "ET", "Eastern", "Boston", "Miami"]),
        ("America/Chicago", "Chicago", ["CHI", "CST", "CDT", "CT", "Central", "Dallas", "Houston"]),
        ("America/Denver", "Denver", ["DEN", "MST", "MDT", "MT", "Mountain"]),
        ("America/Phoenix", "Phoenix", ["PHX", "Arizona"]),
        ("America/Los_Angeles", "Los Angeles", ["LA", "SF", "San Francisco", "Seattle", "PST", "PDT", "PT", "Pacific"]),
        ("America/Anchorage", "Anchorage", ["AKST", "Alaska"]),
        ("Pacific/Honolulu", "Honolulu", ["HST", "Hawaii"]),
        ("America/Toronto", "Toronto", ["YYZ"]),
        ("America/Vancouver", "Vancouver", ["YVR"]),
        ("America/Mexico_City", "Mexico City", ["CDMX"]),
        ("America/Sao_Paulo", "São Paulo", ["SAO", "Sao Paulo"]),
        ("Europe/London", "London", ["LON", "UK", "GMT", "BST"]),
        ("Europe/Dublin", "Dublin", ["DUB"]),
        ("Europe/Paris", "Paris", ["PAR", "CET", "CEST"]),
        ("Europe/Berlin", "Berlin", ["BER"]),
        ("Europe/Madrid", "Madrid", ["MAD"]),
        ("Europe/Amsterdam", "Amsterdam", ["AMS"]),
        ("Europe/Athens", "Athens", ["EET"]),
        ("Asia/Kolkata", "Mumbai", ["Delhi", "Bangalore", "IST", "India"]),
        ("Asia/Tokyo", "Tokyo", ["TYO", "JST"]),
        ("Asia/Singapore", "Singapore", ["SIN", "SGT"]),
        ("Asia/Hong_Kong", "Hong Kong", ["HK", "HKT"]),
        ("Asia/Shanghai", "Shanghai", ["Beijing", "China"]),
        ("Asia/Seoul", "Seoul", ["KST"]),
        ("Asia/Dubai", "Dubai", ["GST", "UAE"]),
        ("Australia/Sydney", "Sydney", ["SYD", "AEST", "AEDT", "Melbourne"]),
        ("Australia/Perth", "Perth", ["AWST"]),
        ("Pacific/Auckland", "Auckland", ["AKL", "NZST", "NZDT", "New Zealand"]),
        ("Africa/Johannesburg", "Johannesburg", ["SAST"]),
        ("Etc/UTC", "UTC", ["GMT", "Coordinated Universal Time", "Z"]),
    ];

    static readonly Lazy<IReadOnlyList<Entry>> AllEntries = new(BuildEntries);

    /// <summary>True when this PC can resolve <paramref name="id"/>.</summary>
    public static bool IsKnown(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    /// <summary>Best matches for <paramref name="query"/> (curated cities when empty).</summary>
    public static IReadOnlyList<TimeZoneChoice> Search(string query, DateTimeOffset now, int max = 20)
    {
        var q = query.Trim();

        return AllEntries.Value
            .Select((entry, order) => (entry, order, rank: Rank(entry, q)))
            .Where(x => x.rank < int.MaxValue)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => new TimeZoneChoice(x.entry.Id, x.entry.City, Detail(x.entry.Id, now)))
            .ToList();
    }

    /// <summary>"UTC", "UTC+9", "UTC−5", "UTC+5:30" (with a real minus sign).</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        var sign = offset < TimeSpan.Zero ? "−" : "+";
        var abs  = offset.Duration();

        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}:{abs.Minutes:00}");
    }

    /// <summary>A zone's city name (curated, else the last part of the IANA ID).</summary>
    public static string CityFor(string id)
    {
        var curated = Array.Find(Cities, c => c.Id == id);
        return curated.City ?? id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
    }

    /// <summary>The column label: the custom label, else the city.</summary>
    public static string ShortLabel(ExtraTimeZone zone) => zone.Label ?? CityFor(zone.Id);

    static string Detail(string id, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return $"{OffsetLabel(zone.GetUtcOffset(now))} · {zone.StandardName}";
    }

    static int Rank(Entry entry, string query)
    {
        if (query.Length == 0)
        {
            return entry.Curated ? 0 : int.MaxValue;
        }

        if (entry.Aliases.Any(a => a.Equals(query, StringComparison.OrdinalIgnoreCase)) || entry.City.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (entry.City.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return entry.Curated ? 1 : 2;
        }

        if (entry.City.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return entry.Curated ? 3 : 4;
        }

        return int.MaxValue;
    }

    static List<Entry> BuildEntries()
    {
        var entries = Cities.Where(c => IsKnown(c.Id)).Select(c => new Entry(c.Id, c.City, c.Aliases, Curated: true)).ToList();
        var ids     = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && IsKnown(iana) && ids.Add(iana))
            {
                entries.Add(new Entry(iana, CityFor(iana), [zone.DisplayName, zone.StandardName], Curated: false));
            }
        }

        return entries;
    }

    sealed record Entry(string Id, string City, string[] Aliases, bool Curated);
}
