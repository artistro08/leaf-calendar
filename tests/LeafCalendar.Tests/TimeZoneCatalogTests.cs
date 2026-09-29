using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class TimeZoneCatalogTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void Choice_ToString_ShowsCityAndDetail()
    {
        var choice = TimeZoneCatalog.Search("tokyo", Now)[0];

        Assert.StartsWith("Tokyo (UTC+9", choice.ToString(), StringComparison.Ordinal);
    }
}
