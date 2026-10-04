using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class TimeZoneCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("NYC", "America/New_York")]
    [InlineData("sf", "America/Los_Angeles")]
    [InlineData("LON", "Europe/London")]
    [InlineData("tok", "Asia/Tokyo")]
    [InlineData("mumbai", "Asia/Kolkata")]
    public void Search_AliasOrCity_FindsZoneFirst(string query, string id)
    {
        Assert.Equal(id, TimeZoneCatalog.Search(query, Now)[0].Id);
    }

    [Fact]
    public void Search_Empty_ReturnsCuratedList()
    {
        var results = TimeZoneCatalog.Search("", Now);

        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.True(TimeZoneCatalog.IsKnown(r.Id)));
    }

    [Fact]
    public void Search_Nonsense_Empty() => Assert.Empty(TimeZoneCatalog.Search("zzqqxx", Now));

    [Theory]
    [InlineData("Europe/Kiev", "Kyiv")]
    [InlineData("Asia/Calcutta", "Kolkata")]
    [InlineData("Asia/Katmandu", "Kathmandu")]
    [InlineData("Asia/Rangoon", "Yangon")]
    [InlineData("America/Godthab", "Nuuk")]
    [InlineData("Asia/Saigon", "Ho Chi Minh City")]
    [InlineData("Atlantic/Faeroe", "Faroe")]
    [InlineData("Europe/Kyiv", "Kyiv")]
    public void CityFor_OldIanaSpelling_ShowsTodaysName(string id, string city) =>
        Assert.Equal(city, TimeZoneCatalog.CityFor(id));

    [Theory]
    [InlineData("Central Standard Time", "Chicago")]
    [InlineData("Eastern Standard Time", "New York")]
    [InlineData("India Standard Time", "Kolkata")]
    [InlineData("UTC", "UTC")]
    public void CityFor_WindowsId_ShowsTheCity(string id, string city) =>
        Assert.Equal(city, TimeZoneCatalog.CityFor(id));

    [Theory]
    [InlineData("Kiev", "Europe/Kiev", "Kyiv")]
    [InlineData("Kyiv", "Europe/Kiev", "Kyiv")]
    [InlineData("Godthab", "America/Godthab", "Nuuk")]
    public void Search_OldOrNewName_FindsTheRenamedZone(string query, string id, string city)
    {
        var first = TimeZoneCatalog.Search(query, Now)[0];

        Assert.Equal((id, city), (first.Id, first.City));
    }

    [Theory]
    [InlineData(0, 0, "UTC")]
    [InlineData(9, 0, "UTC+9")]
    [InlineData(-5, 0, "UTC−5")]
    [InlineData(5, 30, "UTC+5:30")]
    [InlineData(-3, -30, "UTC−3:30")]
    public void OffsetLabel_Formats(int hours, int minutes, string expected) =>
        Assert.Equal(expected, TimeZoneCatalog.OffsetLabel(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public void ShortLabel_CustomOrCity()
    {
        Assert.Equal("HQ", TimeZoneCatalog.ShortLabel(new ExtraTimeZone("Asia/Tokyo", "HQ")));
        Assert.Equal("Tokyo", TimeZoneCatalog.ShortLabel(new ExtraTimeZone("Asia/Tokyo", null)));
        Assert.Equal("Buenos Aires", TimeZoneCatalog.CityFor("America/Argentina/Buenos_Aires"));
    }

    [Theory]
    [InlineData("Eastern Standard Time", "America/New_York")]
    [InlineData("Tokyo Standard Time", "Asia/Tokyo")]
    [InlineData("Europe/London", "Europe/London")]
    [InlineData("UTC", "Etc/UTC")]
    public void IanaId_WindowsOrIana_GivesIana(string id, string expected) =>
        Assert.Equal(expected, TimeZoneCatalog.IanaId(TimeZoneInfo.FindSystemTimeZoneById(id)));

    [Fact]
    public void Choice_ToString_ShowsCityAndDetail()
    {
        var choice = TimeZoneCatalog.Search("tokyo", Now)[0];

        Assert.StartsWith("Tokyo (JST · ", choice.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void All_ListsEveryZoneByOffset_AsWindowsWritesThem()
    {
        var all = TimeZoneCatalog.All(Now).ToList();

        // More than the curated cities, each zone once, west to east
        Assert.True(all.Count > 30, $"Only {all.Count} zones.");
        Assert.Equal(all.Count, all.Select(z => z.Id).Distinct().Count());
        Assert.Contains(("Asia/Tokyo", "(UTC+09:00) Tokyo (JST)"), all);
        Assert.Contains(("Asia/Kolkata", "(UTC+05:30) Mumbai (IST)"), all);
        Assert.Contains(("America/New_York", "(UTC-04:00) New York (EDT)"), all);
        Assert.True(all.FindIndex(z => z.Id == "America/New_York") < all.FindIndex(z => z.Id == "Asia/Tokyo"));
    }

    [Fact]
    public void ListLabel_ZeroOffset_IsPlus() =>
        Assert.Equal("(UTC+00:00) UTC", TimeZoneCatalog.ListLabel("Etc/UTC", Now));

    [Theory]
    [InlineData("America/Chicago", "(UTC-05:00) Chicago (CDT)")]
    [InlineData("Europe/London", "(UTC+01:00) London (BST)")]
    [InlineData("Asia/Kathmandu", "(UTC+05:45) Kathmandu")]
    public void ListLabel_AddsTheShortNameWhenThereIsOne(string id, string expected) =>
        Assert.Equal(expected, TimeZoneCatalog.ListLabel(id, Now));

    [Fact]
    public void ListLabel_Winter_UsesTheStandardName() =>
        Assert.Equal("(UTC-06:00) Chicago (CST)", TimeZoneCatalog.ListLabel("America/Chicago", new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("America/Chicago", "America/Chicago", true)]
    [InlineData("Central Standard Time", "America/Chicago", true)]
    [InlineData("America/Chicago", "Central Standard Time", true)]
    [InlineData("America/New_York", "America/Chicago", false)]
    [InlineData("Asia/Kolkata", "India Standard Time", true)]       // Windows' own IANA name is Asia/Calcutta
    [InlineData("America/Toronto", "Eastern Standard Time", true)]  // Windows' own IANA name is America/New_York
    [InlineData("Mars/Olympus", "America/Chicago", false)]
    [InlineData(null, "America/Chicago", false)]
    public void IsSameZone_MatchesWindowsAndIanaIds(string? id, string zone, bool expected) =>
        Assert.Equal(expected, TimeZoneCatalog.IsSameZone(id, TimeZoneInfo.FindSystemTimeZoneById(zone)));
}
