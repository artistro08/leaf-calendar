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
/// Finds time zones by city, IANA ID, Windows name, or common abbreviation (NYC, SF, LON...), forgiving small typos.
/// </summary>
/// <remarks>
/// A curated list of major cities comes first. Every other zone Windows knows is added from its
/// primary IANA ID. Only zones this PC can resolve are offered. A query of 4 to 6 letters also matches a word of a city,
/// alias or Windows name one letter off ("Tokoy"), and 7 or more letters two off ("Pheonix"); those rank below every
/// exact and part-of-a-name match.
/// </remarks>
public static class TimeZoneCatalog
{
    private static readonly (string Id, string City, string[] Aliases)[] Cities =
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

    // IANA IDs with an old spelling of their city (IANA's "backward" names, which Windows still hands out), by today's name
    private static readonly Dictionary<string, string> RenamedCities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Kiev"] = "Kyiv",
        ["Asia/Calcutta"] = "Kolkata",
        ["Asia/Katmandu"] = "Kathmandu",
        ["Asia/Rangoon"] = "Yangon",
        ["America/Godthab"] = "Nuuk",
        ["Asia/Saigon"] = "Ho Chi Minh City",
        ["Asia/Ho_Chi_Minh"] = "Ho Chi Minh City",
        ["Asia/Dacca"] = "Dhaka",
        ["Asia/Thimbu"] = "Thimphu",
        ["Asia/Ulan_Bator"] = "Ulaanbaatar",
        ["Asia/Ashkhabad"] = "Ashgabat",
        ["Asia/Macao"] = "Macau",
        ["Asia/Ujung_Pandang"] = "Makassar",
        ["Atlantic/Faeroe"] = "Faroe",
        ["Pacific/Enderbury"] = "Kanton",
        ["Pacific/Truk"] = "Chuuk",
        ["Pacific/Ponape"] = "Pohnpei",
    };

    private static readonly Lazy<IReadOnlyList<Entry>> AllEntries = new(BuildEntries);

    /// <summary>A zone's IANA ID (what Google wants), converting a Windows ID such as "Eastern Standard Time".</summary>
    public static string IanaId(TimeZoneInfo zone) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    /// <summary>
    /// A zone's Windows ID, converting an IANA ID. Windows maps several IANA IDs onto one zone ("Asia/Kolkata" and
    /// "Asia/Calcutta" are both "India Standard Time"), so this is the ID to compare zones by.
    /// </summary>
    internal static string WindowsId(TimeZoneInfo zone) =>
        zone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : zone.Id;

    /// <summary>True when this PC can resolve <paramref name="id"/>.</summary>
    public static bool IsKnown(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    /// <summary>
    /// Best matches for <paramref name="query"/> (curated cities when empty). With <paramref name="typos"/> off, only exact
    /// and part-of-a-name matches, so a caller can list its own matches between those and the typo matches.
    /// </summary>
    public static IReadOnlyList<TimeZoneChoice> Search(string query, DateTimeOffset now, int max = 20, bool typos = true)
    {
        var q = query.Trim();

        return AllEntries.Value
            .Select((entry, order) => (entry, order, rank: Rank(entry, q, typos)))
            .Where(x => x.rank < int.MaxValue)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => new TimeZoneChoice(x.entry.Id, x.entry.City, Detail(x.entry.Id, now)))
            .ToList();
    }

    /// <summary>
    /// Every zone this PC knows (the curated cities and one per Windows zone), as dropdown rows: "(UTC-05:00) New York (EST)",
    /// ordered by their offset at <paramref name="now"/>, then city.
    /// </summary>
    public static IReadOnlyList<(string Id, string Label)> All(DateTimeOffset now) =>
        [.. AllEntries.Value
            .Select(e => (e.Id, Zone: TimeZoneInfo.FindSystemTimeZoneById(e.Id), e.City))
            .Select(x => (x.Id, x.Zone, Offset: x.Zone.GetUtcOffset(now), x.City))
            .OrderBy(x => x.Offset)
            .ThenBy(x => x.City, StringComparer.CurrentCulture)
            .Select(x => (x.Id, ListLabel(x.Zone, x.Offset, x.City, now)))];

    /// <summary>
    /// A zone's dropdown row, as Windows writes them, with the short name people know it by when it has one:
    /// "(UTC+05:30) Mumbai (IST)", "(UTC-05:00) Chicago (CDT)", "(UTC+05:45) Kathmandu".
    /// </summary>
    public static string ListLabel(string id, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return ListLabel(zone, zone.GetUtcOffset(now), CityFor(id), now);
    }

    private static string ListLabel(TimeZoneInfo zone, TimeSpan offset, string city, DateTimeOffset now)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var label = string.Create(CultureInfo.InvariantCulture, $"(UTC{sign}{abs.Hours:00}:{abs.Minutes:00}) {city}");

        // "UTC" Names Itself Already
        return ZoneAbbreviation.TryGet(zone, now, out var name) && !string.Equals(name, city, StringComparison.Ordinal) ? $"{label} ({name})" : label;
    }

    /// <summary>
    /// True when <paramref name="id"/> (an IANA or Windows ID) is <paramref name="zone"/> itself: the zone you're in, which
    /// zone lists offer but don't let you pick again.
    /// </summary>
    public static bool IsSameZone(string? id, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return id is not null
            && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var other)
            && string.Equals(WindowsId(other), WindowsId(zone), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"UTC", "UTC+9", "UTC−5", "UTC+5:30" (with a real minus sign).</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        var sign = offset < TimeSpan.Zero ? "−" : "+";
        var abs = offset.Duration();

        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}:{abs.Minutes:00}");
    }

    /// <summary>
    /// A zone's city name (curated, else today's name for a renamed IANA ID, else the last part of the IANA ID). A Windows
    /// ID ("Central Standard Time", what TimeZoneInfo.Local gives) is converted to its IANA ID first. Only the name shown
    /// changes: the ID, which settings store and search matches too, stays as Windows gives it.
    /// </summary>
    public static string CityFor(string id)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
        {
            id = iana;
        }

        var curated = Array.Find(Cities, c => c.Id == id);
        return curated.City ?? RenamedCities.GetValueOrDefault(id) ?? id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
    }

    /// <summary>The column label: the custom label, else the city.</summary>
    public static string ShortLabel(ExtraTimeZone zone) => zone.Label ?? CityFor(zone.Id);

    private static string Detail(string id, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return $"{ZoneAbbreviation.For(zone, now)} · {zone.StandardName}";
    }

    private static int Rank(Entry entry, string query, bool typos)
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

        // A Small Typo In A Whole Word, Closer Ones First
        var allowed = !typos ? 0 : query.Length switch { < 4 => 0, <= 6 => 1, _ => 2 };
        var closest = allowed == 0 ? int.MaxValue : entry.Words.Min(w => TypoDistance(query, w, allowed));

        return closest <= allowed ? (closest * 2) + (entry.Curated ? 3 : 4) : int.MaxValue;
    }

    // The optimal string alignment distance (letters added, dropped, changed, or two side by side swapped), ignoring case;
    // anything past max reads as max + 1
    private static int TypoDistance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return Math.Min(d[a.Length, b.Length], max + 1);
    }

    // The whole words of a zone's city, aliases and Windows names, for typo matching
    private static string[] WordsOf(string id, string city, IEnumerable<string> aliases)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return [.. new[] { city, zone.StandardName, zone.DaylightName, zone.DisplayName }
            .Concat(aliases)
            .SelectMany(s => s.Split((char[])[' ', '(', ')', '-', '/', '_', ',', '.', '+', ':'], StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Any(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<Entry> BuildEntries()
    {
        var entries = Cities.Where(c => IsKnown(c.Id)).Select(c => new Entry(c.Id, c.City, c.Aliases, WordsOf(c.Id, c.City, c.Aliases), Curated: true)).ToList();
        var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && IsKnown(iana) && ids.Add(iana))
            {
                var city = CityFor(iana);
                entries.Add(new Entry(iana, city, [zone.DisplayName, zone.StandardName], WordsOf(iana, city, []), Curated: false));
            }
        }

        return entries;
    }

    private sealed record Entry(string Id, string City, string[] Aliases, string[] Words, bool Curated);
}
