using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class LocalZoneWatcherTests
{
    static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    static readonly TimeZoneInfo Tokyo   = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [Fact]
    public void Check_SameZone_ReportsNoChange()
    {
        var clears  = 0;
        var watcher = new LocalZoneWatcher(() => Chicago, () => clears++);

        Assert.False(watcher.Check());
        Assert.Equal(1, clears);
        Assert.Same(Chicago, watcher.Zone);
    }

    [Fact]
    public void Check_NewZone_ReportsChangeOnce()
    {
        var current = Chicago;
        var watcher = new LocalZoneWatcher(() => current, () => { });

        current = Tokyo;

        Assert.True(watcher.Check());
        Assert.Same(Tokyo, watcher.Zone);
        Assert.False(watcher.Check());
    }

    [Fact]
    public void Check_SameIdWithoutDaylightSaving_ReportsChange()
    {
        var current = Chicago;
        var watcher = new LocalZoneWatcher(() => current, () => { });

        current = TimeZoneInfo.CreateCustomTimeZone(Chicago.Id, Chicago.BaseUtcOffset, Chicago.DisplayName, Chicago.StandardName);

        Assert.True(watcher.Check());
    }
}
