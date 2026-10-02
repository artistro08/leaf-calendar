using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ZoneAbbreviationTests
{
    // Mid-July and mid-January, so each zone is on daylight time in one and standard time in the other
    static readonly DateTimeOffset July    = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset January = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    [Theory]
    [InlineData("America/New_York", "EDT", "EST")]
    [InlineData("America/Chicago", "CDT", "CST")]
    [InlineData("America/Denver", "MDT", "MST")]
    [InlineData("America/Los_Angeles", "PDT", "PST")]
    [InlineData("America/Anchorage", "AKDT", "AKST")]
    [InlineData("America/Halifax", "ADT", "AST")]
    [InlineData("America/St_Johns", "NDT", "NST")]
    [InlineData("Europe/London", "BST", "GMT")]
    [InlineData("Europe/Paris", "CEST", "CET")]
    [InlineData("Europe/Athens", "EEST", "EET")]
    public void For_DaylightZones_FollowTheClock(string id, string summer, string winter)
    {
        Assert.Equal(summer, ZoneAbbreviation.For(Zone(id), July));
        Assert.Equal(winter, ZoneAbbreviation.For(Zone(id), January));
    }

    [Theory]
    [InlineData("Australia/Sydney", "AEST", "AEDT")]
    [InlineData("Pacific/Auckland", "NZST", "NZDT")]
    public void For_SouthernZones_AreOnDaylightTimeInJanuary(string id, string july, string january)
    {
        Assert.Equal(july, ZoneAbbreviation.For(Zone(id), July));
        Assert.Equal(january, ZoneAbbreviation.For(Zone(id), January));
    }

    [Theory]
    [InlineData("America/Phoenix", "MST")]
    [InlineData("Pacific/Honolulu", "HST")]
    [InlineData("Asia/Tokyo", "JST")]
    [InlineData("Asia/Kolkata", "IST")]
    [InlineData("Asia/Singapore", "SGT")]
    [InlineData("Etc/UTC", "UTC")]
    public void For_ZonesWithoutDaylightTime_KeepOneName(string id, string expected)
    {
        Assert.Equal(expected, ZoneAbbreviation.For(Zone(id), July));
        Assert.Equal(expected, ZoneAbbreviation.For(Zone(id), January));
    }

    [Theory]
    [InlineData("Eastern Standard Time", "EDT")]
    [InlineData("Central Standard Time", "CDT")]
    [InlineData("GMT Standard Time", "BST")]
    [InlineData("Tokyo Standard Time", "JST")]
    public void For_WindowsIds_MapThroughIana(string windowsId, string expected) =>
        Assert.Equal(expected, ZoneAbbreviation.For(Zone(windowsId), July));

    [Theory]
    [InlineData("Asia/Kathmandu", "UTC+5:45")]
    [InlineData("Asia/Tehran", "UTC+3:30")]
    [InlineData("America/Bogota", "UTC−5")]
    public void For_NoKnownName_FallsBackToTheOffset(string id, string expected)
    {
        Assert.False(ZoneAbbreviation.TryGet(Zone(id), July, out _));
        Assert.Equal(expected, ZoneAbbreviation.For(Zone(id), July));
    }

    [Fact]
    public void For_DaylightChange_SwitchesAtTheInstant()
    {
        // US daylight time ends Nov 1 2026 at 2 AM local: 07:00 UTC in Chicago
        var chicago = Zone("America/Chicago");

        Assert.Equal("CDT", ZoneAbbreviation.For(chicago, new DateTimeOffset(2026, 11, 1, 6, 59, 0, TimeSpan.Zero)));
        Assert.Equal("CST", ZoneAbbreviation.For(chicago, new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void For_CustomZone_FallsBackToTheOffset()
    {
        var custom = TimeZoneInfo.CreateCustomTimeZone("Leaf/Test", TimeSpan.FromHours(3), "Test", "Test");

        Assert.Equal("UTC+3", ZoneAbbreviation.For(custom, July));
    }

    [Fact]
    public void For_Null_Throws() => Assert.Throws<ArgumentNullException>(() => ZoneAbbreviation.For(null!, July));
}
