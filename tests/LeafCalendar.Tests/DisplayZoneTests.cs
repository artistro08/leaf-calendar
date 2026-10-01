using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class DisplayZoneTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Resolve_TravelBeatsPrimaryBeatsWindows()
    {
        Assert.Equal("Asia/Tokyo", DisplayZone.Resolve("Asia/Tokyo", "Europe/London", NewYork).Id);
        Assert.Equal("Europe/London", DisplayZone.Resolve(null, "Europe/London", NewYork).Id);
        Assert.Same(NewYork, DisplayZone.Resolve(null, null, NewYork));
    }

    [Fact]
    public void Resolve_UnknownIds_FallThrough() =>
        Assert.Same(NewYork, DisplayZone.Resolve("Mars/Base", "Moon/Crater", NewYork));

    [Fact]
    public void ShouldOfferSwitch_OnlyWithAPinnedZoneThatDiffers()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        Assert.True(DisplayZone.ShouldOfferSwitch("America/New_York", prompt: true, pacific));
        Assert.False(DisplayZone.ShouldOfferSwitch("America/New_York", prompt: false, pacific));
        Assert.False(DisplayZone.ShouldOfferSwitch(null, prompt: true, pacific));                  // following Windows
        Assert.False(DisplayZone.ShouldOfferSwitch("America/Los_Angeles", prompt: true, pacific));  // already there
        Assert.False(DisplayZone.ShouldOfferSwitch("America/Los_Angeles", prompt: true,
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")));                      // a Windows ID for the same zone
    }

    [Fact]
    public void Describe_CityAndOffset() =>
        Assert.Equal("Tokyo time (UTC+9)", DisplayZone.Describe(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Describe_WindowsId_UsesTheCity() =>
        Assert.Equal("New York time (UTC−4)", DisplayZone.Describe(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
}
