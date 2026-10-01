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

    // Sum of per-channel differences between two #RRGGBB colors
    static int Distance(string a, string b) =>
        Enumerable.Range(0, 3).Sum(i => Math.Abs(Convert.ToInt32(a.Substring(1 + 2 * i, 2), 16) - Convert.ToInt32(b.Substring(1 + 2 * i, 2), 16)));

    // Past Cards Are Clearly Faded (fill closer to the surface) And Their Text Still Meets 4.5:1 On The Faded Fill, Both Themes
    [Fact]
    public void PastCards_FadedFill_IsLighterAndTextMeetsAA()
    {
        foreach (var dark in new[] { true, false })
        {
            var surface = ChromeColors.Surface(dark);
            foreach (var accent in EventColors.CalendarPalette.Concat(EventColors.EventColorNames.Select(c => EventColors.ResolveAccent(c.Id, "#039BE5"))))
            {
                var current   = EventColors.Palette(accent, dark);
                var past      = EventColors.Palette(accent, dark, past: true);
                var secondary = EventColors.Blend("#" + past.SecondaryText[3..], past.Fill, 1 - Convert.ToInt32(past.SecondaryText[1..3], 16) / 255.0);

                Assert.True(Distance(past.Fill, surface) < Distance(current.Fill, surface), $"{accent} dark={dark}: fill not closer to surface");
                Assert.True(EventColors.ContrastRatio(past.Text, past.Fill) >= 4.5, $"{accent} dark={dark} text");
                Assert.True(EventColors.ContrastRatio(secondary, past.Fill) >= 4.5, $"{accent} dark={dark} secondary");
            }
        }
    }
}

