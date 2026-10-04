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
    private static int Distance(string a, string b) =>
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
                var current = EventColors.Palette(accent, dark);
                var past = EventColors.Palette(accent, dark, past: true);
                var secondary = EventColors.Blend("#" + past.SecondaryText[3..], past.Fill, 1 - Convert.ToInt32(past.SecondaryText[1..3], 16) / 255.0);

                // The fade is clearly visible: fill and accent bar both moved a good way toward the surface (at least 25% of the old distance)
                Assert.True(Distance(past.Fill, surface) <= Distance(current.Fill, surface) * 0.75, $"{accent} dark={dark}: fill not faded enough");
                Assert.True(Distance(past.Accent, surface) <= Distance(current.Accent, surface) * 0.75, $"{accent} dark={dark}: accent bar not faded enough");
                Assert.True(EventColors.ContrastRatio(past.Text, past.Fill) >= 4.5, $"{accent} dark={dark} text");
                Assert.True(EventColors.ContrastRatio(secondary, past.Fill) >= 4.5, $"{accent} dark={dark} secondary");
            }
        }
    }

    // A Selected Card Is The Accent At Full Strength (even when past), And Its Text Still Meets 4.5:1, Both Themes
    [Fact]
    public void SelectedCards_AreFilledWithTheAccent_AndTextMeetsAA()
    {
        foreach (var dark in new[] { true, false })
        {
            foreach (var accent in EventColors.CalendarPalette.Concat(EventColors.EventColorNames.Select(c => EventColors.ResolveAccent(c.Id, "#039BE5"))))
            {
                foreach (var past in new[] { true, false })
                {
                    var selected = EventColors.Palette(accent, dark, past, selected: true);

                    // The fill is the accent, or as close to it as readable text allows
                    Assert.True(Distance(selected.Fill, accent) <= 120, $"{accent} dark={dark} past={past}: {selected.Fill} is far from the accent");
                    Assert.Equal(accent.ToUpperInvariant(), selected.Accent);
                    Assert.True(EventColors.ContrastRatio(selected.Text, selected.Fill) >= 4.5, $"{accent} dark={dark} past={past}");
                }
            }
        }
    }

    [Fact]
    public void SelectedCard_IsStrongerThanTheUnselectedOne()
    {
        foreach (var dark in new[] { true, false })
        {
            var surface = ChromeColors.Surface(dark);
            var unselected = EventColors.Palette("#039BE5", dark);
            var selected = EventColors.Palette("#039BE5", dark, selected: true);

            Assert.True(Distance(selected.Fill, surface) > Distance(unselected.Fill, surface), $"dark={dark}");
        }
    }

    [Theory]
    [InlineData("#039BE5")]
    [InlineData("#D50000")]
    [InlineData("#F6BF26")]
    public void SelectedCard_ReadableAccent_IsExactlyTheAccent(string accent) =>
        Assert.Equal(accent, EventColors.Palette(accent, dark: true, selected: true).Fill);

    [Fact]
    public void SelectedCard_BadHex_UsesTheDefaultColor() =>
        Assert.Equal(LeafCalendar.Core.Data.CalendarInfo.DefaultColor, EventColors.Palette("blue", dark: false, selected: true).Fill);
}
