using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class EventColorsTests
{
    [Theory]
    [InlineData("5", "#9fe1e7", "#F6BF26")]
    [InlineData("11", "#9fe1e7", "#D50000")]
    [InlineData(null, "#9fe1e7", "#9FE1E7")]
    [InlineData("99", "#9fe1e7", "#9FE1E7")]
    [InlineData(null, "not a color", "#4285F4")]
    public void ResolveAccent_ColorIdOrCalendar_Picks(string? colorId, string calendar, string expected)
    {
        Assert.Equal(expected, EventColors.ResolveAccent(colorId, calendar));
    }

    [Fact]
    public void Palette_EveryGoogleColorBothThemes_TextMeetsContrast()
    {
        var accents = EventColors.CalendarPalette.Concat(["#7986CB", "#33B679", "#8E24AA", "#E67C73", "#F6BF26", "#F4511E", "#039BE5", "#616161", "#3F51B5", "#0B8043", "#D50000"]);

        foreach (var accent in accents)
        {
            foreach (var dark in new[] { true, false })
            {
                var palette = EventColors.Palette(accent, dark);
                Assert.True(EventColors.ContrastRatio(palette.Text, palette.Fill) >= 4.5, $"{accent} dark={dark}");
            }
        }
    }

    [Fact]
    public void Blend_HalfWay_AveragesChannels()
    {
        Assert.Equal("#808080", EventColors.Blend("#FFFFFF", "#000000", 0.5));
    }

    [Fact]
    public void ContrastRatio_BlackOnWhite_Is21()
    {
        Assert.Equal(21, EventColors.ContrastRatio("#000000", "#FFFFFF"), 1);
    }
}
