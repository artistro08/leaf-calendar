namespace LeafCalendar.Core.Views;

/// <summary>
/// The short names people know their time zones by ("CDT", "EST", "BST", "JST"), standard or daylight as the zone's
/// clock is at a given moment.
/// </summary>
/// <remarks>
/// Windows has no abbreviations (its zone names are "Central Standard Time" all year), so Leaf keeps its own for the
/// zones where one is in common use. A zone without one gets its UTC offset (<see cref="TimeZoneCatalog.OffsetLabel"/>),
/// so a label is never made up.
/// </remarks>
public static class ZoneAbbreviation
{
    // IANA ID → (standard, daylight); a zone without daylight saving repeats its standard name
    private static readonly Dictionary<string, (string Standard, string Daylight)> Names = Build(
        (("EST", "EDT"), ["America/New_York", "America/Detroit", "America/Toronto", "America/Montreal", "America/Nassau", "America/Indiana/Indianapolis", "America/Kentucky/Louisville", "America/Indianapolis", "US/Eastern"]),
        (("CST", "CDT"), ["America/Chicago", "America/Winnipeg", "America/Indiana/Knox", "America/Menominee", "America/Matamoros", "US/Central"]),
        (("CST", "CST"), ["America/Regina", "America/Mexico_City", "America/Monterrey", "America/Guatemala", "America/Costa_Rica", "America/El_Salvador", "America/Tegucigalpa", "America/Managua", "America/Belize"]),
        (("MST", "MDT"), ["America/Denver", "America/Edmonton", "America/Boise", "America/Ciudad_Juarez", "US/Mountain"]),
        (("MST", "MST"), ["America/Phoenix", "America/Hermosillo", "US/Arizona"]),
        (("PST", "PDT"), ["America/Los_Angeles", "America/Vancouver", "America/Tijuana", "US/Pacific"]),
        (("AKST", "AKDT"), ["America/Anchorage", "America/Juneau", "America/Nome", "America/Sitka", "US/Alaska"]),
        (("HST", "HST"), ["Pacific/Honolulu", "US/Hawaii"]),
        (("AST", "ADT"), ["America/Halifax", "America/Moncton", "America/Glace_Bay", "Atlantic/Bermuda"]),
        (("AST", "AST"), ["America/Puerto_Rico", "America/Santo_Domingo"]),
        (("NST", "NDT"), ["America/St_Johns"]),
        (("GMT", "BST"), ["Europe/London", "Europe/Belfast", "Europe/Guernsey", "Europe/Isle_of_Man", "Europe/Jersey"]),
        (("GMT", "IST"), ["Europe/Dublin"]),
        (("WET", "WEST"), ["Europe/Lisbon", "Atlantic/Canary", "Atlantic/Madeira", "Atlantic/Faroe"]),
        (("CET", "CEST"), ["Europe/Paris", "Europe/Berlin", "Europe/Madrid", "Europe/Amsterdam", "Europe/Brussels", "Europe/Rome", "Europe/Vienna", "Europe/Zurich", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen", "Europe/Warsaw", "Europe/Prague", "Europe/Budapest", "Europe/Belgrade", "Europe/Luxembourg", "Europe/Monaco", "Europe/Malta", "Europe/Zagreb", "Europe/Ljubljana", "Europe/Bratislava", "Europe/Sarajevo", "Europe/Skopje", "Europe/Tirane", "Europe/Andorra", "Europe/Gibraltar"]),
        (("EET", "EEST"), ["Europe/Athens", "Europe/Helsinki", "Europe/Kyiv", "Europe/Kiev", "Europe/Bucharest", "Europe/Sofia", "Europe/Riga", "Europe/Tallinn", "Europe/Vilnius", "Asia/Nicosia", "Europe/Chisinau"]),
        (("MSK", "MSK"), ["Europe/Moscow", "Europe/Simferopol"]),
        (("IST", "IDT"), ["Asia/Jerusalem", "Asia/Tel_Aviv"]),
        (("GST", "GST"), ["Asia/Dubai", "Asia/Muscat"]),
        (("PKT", "PKT"), ["Asia/Karachi"]),
        (("IST", "IST"), ["Asia/Kolkata", "Asia/Calcutta"]),
        (("ICT", "ICT"), ["Asia/Bangkok", "Asia/Ho_Chi_Minh", "Asia/Saigon"]),
        (("WIB", "WIB"), ["Asia/Jakarta"]),
        (("SGT", "SGT"), ["Asia/Singapore"]),
        (("HKT", "HKT"), ["Asia/Hong_Kong"]),
        (("PHT", "PHT"), ["Asia/Manila"]),
        (("JST", "JST"), ["Asia/Tokyo"]),
        (("KST", "KST"), ["Asia/Seoul"]),
        (("AWST", "AWST"), ["Australia/Perth"]),
        (("ACST", "ACDT"), ["Australia/Adelaide", "Australia/Broken_Hill"]),
        (("ACST", "ACST"), ["Australia/Darwin"]),
        (("AEST", "AEDT"), ["Australia/Sydney", "Australia/Melbourne", "Australia/Hobart", "Australia/Canberra", "Australia/ACT", "Australia/NSW", "Australia/Victoria"]),
        (("AEST", "AEST"), ["Australia/Brisbane", "Australia/Lindeman"]),
        (("NZST", "NZDT"), ["Pacific/Auckland"]),
        (("SAST", "SAST"), ["Africa/Johannesburg"]),
        (("BRT", "BRT"), ["America/Sao_Paulo"]),
        (("ART", "ART"), ["America/Argentina/Buenos_Aires", "America/Buenos_Aires"]),
        (("UTC", "UTC"), ["Etc/UTC", "UTC", "Etc/UCT", "Etc/Universal", "Etc/Zulu", "Etc/GMT", "GMT"]));

    /// <summary>
    /// "CDT", "CST", "BST", "JST"…, as <paramref name="zone"/>'s clock reads at <paramref name="at"/>; a zone without a
    /// common short name gets its offset ("UTC+5", "UTC−3:30").
    /// </summary>
    public static string For(TimeZoneInfo zone, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return TryGet(zone, at, out var name) ? name : TimeZoneCatalog.OffsetLabel(zone.GetUtcOffset(at));
    }

    /// <summary>True with the zone's short name at <paramref name="at"/> when it has one people know.</summary>
    public static bool TryGet(TimeZoneInfo zone, DateTimeOffset at, out string name)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (Names.TryGetValue(TimeZoneCatalog.IanaId(zone), out var names) || Names.TryGetValue(zone.Id, out names))
        {
            name = zone.IsDaylightSavingTime(at) ? names.Daylight : names.Standard;
            return true;
        }

        name = "";
        return false;
    }

    private static Dictionary<string, (string Standard, string Daylight)> Build(params ((string Standard, string Daylight) Names, string[] Ids)[] groups)
    {
        var map = new Dictionary<string, (string Standard, string Daylight)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (names, ids) in groups)
        {
            foreach (var id in ids)
            {
                map.Add(id, names);
            }
        }

        return map;
    }
}
