using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Editing;

/// <summary>
/// Event IDs Leaf Calendar makes itself.
/// </summary>
/// <remarks>
/// New events get a random 128-bit ID in lowercase base32hex (Google allows <c>a-v</c> and <c>0-9</c>,
/// 5 to 1024 characters), so retrying a create after a dropped connection can't make a duplicate (spec 5.4).
/// A single instance of a repeating event has Google's ID <c>{series}_{UTC start}</c>, which Leaf Calendar
/// computes to edit or cancel an instance Google hasn't sent as its own row. A series split with "this and following"
/// has the ID <c>{original}_R{start}</c>, but its instances keep the original's name.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/events/insert"/>
public static partial class EventIds
{
    private const string Base32Hex = "0123456789abcdefghijklmnopqrstuv";

    /// <summary>A new random event ID (26 characters).</summary>
    public static string NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var value = new UInt128(BitConverter.ToUInt64(bytes[..8]), BitConverter.ToUInt64(bytes[8..]));

        // Five Bits Per Character
        Span<char> chars = stackalloc char[26];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Base32Hex[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    /// <summary>
    /// Google's ID for one instance of a series: <c>{series}_{yyyyMMddTHHmmssZ}</c>, or <c>{series}_{yyyyMMdd}</c> (the
    /// local calendar date) when all-day. A split series' <c>_R{start}</c> ending isn't part of it.
    /// </summary>
    public static string InstanceId(string masterId, DateTimeOffset originalStart, bool isAllDay)
    {
        var series = SplitSuffix().Replace(masterId, "");
        return isAllDay
            ? string.Create(CultureInfo.InvariantCulture, $"{series}_{originalStart.DateTime:yyyyMMdd}")
            : string.Create(CultureInfo.InvariantCulture, $"{series}_{originalStart.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
    }

    [GeneratedRegex(@"_R\d{8}(T\d{6}Z?)?$")]
    private static partial Regex SplitSuffix();
}
