using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class WindowSizeTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void PlaceIn_ScalesAndCentersOnTheWorkArea()
    {
        var place = WindowSize.MainDefault.PlaceIn(0, 0, 2560, 1400, 1.25);

        Assert.Equal((1596, 1018), (place.Width, place.Height));
        Assert.Equal(((2560 - 1596) / 2, (1400 - 1018) / 2), (place.X, place.Y));
    }

    [Fact]
    public void PlaceIn_NeverPassesTheWorkArea()
    {
        var place = new WindowSize(4000, 3000).PlaceIn(100, 40, 1920, 1040, 1.5);

        Assert.Equal((100, 40, 1920, 1040), place);
    }

    [Theory]
    [InlineData(double.NaN, 600)]
    [InlineData(800, double.PositiveInfinity)]
    [InlineData(0, 600)]
    [InlineData(800, -1)]
    public void Clean_DamagedSize_IsNull(double width, double height) =>
        Assert.Null(new WindowSize(width, height).Clean());

    [Fact]
    public void Settings_RememberBothWindows()
    {
        using var conn = _db.Database.Open();
        SettingsStore.Save(conn, new LeafSettings { MainWindowSize = new(1400, 900, Maximized: true), SettingsWindowSize = new(1000, 700) });

        var loaded = SettingsStore.Load(conn);

        Assert.Equal(new WindowSize(1400, 900, true), loaded.MainWindowSize);
        Assert.Equal(new WindowSize(1000, 700), loaded.SettingsWindowSize);
    }

    [Fact]
    public void Settings_DamagedSize_FallsBackToTheDefault()
    {
        var settings = new LeafSettings { MainWindowSize = new(double.NaN, 900) }.Normalize();

        Assert.Null(settings.MainWindowSize);
    }
}
