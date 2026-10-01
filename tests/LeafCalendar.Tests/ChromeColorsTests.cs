using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ChromeColorsTests
{
    // #AARRGGBB composited onto an opaque surface
    static string Flatten(string argb, string surface) =>
        argb.Length == 9 ? EventColors.Blend("#" + argb[3..], surface, 1 - Convert.ToInt32(argb[1..3], 16) / 255.0) : argb;

    // WCAG AA 4.5:1 plus a margin (4.7) For Text On The Calendar Surface, Both Themes
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_OnSurface_MeetsAA(bool dark)
    {
        var surface = ChromeColors.Surface(dark);
        foreach (var (name, color) in new[] { ("primary", ChromeColors.PrimaryText(dark)), ("secondary", ChromeColors.SecondaryText(dark)), ("dim", ChromeColors.DimText(dark)) })
        {
            var ratio = EventColors.ContrastRatio(Flatten(color, surface), surface);
            Assert.True(ratio >= 4.7, $"{name} text {color} on {surface}: {ratio:0.00}:1");
        }
    }

    // Busy-Block Titles Sit On A Person Fill Over The Surface
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_OnPersonFill_MeetsAA(bool dark)
    {
        var surface = ChromeColors.Surface(dark);
        for (var i = 0; i < ChromeColors.PersonCount; i++)
        {
            var fill  = Flatten(ChromeColors.PersonFill(i, dark), surface);
            var ratio = EventColors.ContrastRatio(Flatten(ChromeColors.PrimaryText(dark), fill), fill);
            Assert.True(ratio >= 4.5, $"primary text on person {i} fill {fill}: {ratio:0.00}:1");
        }
    }

    [Fact]
    public void Person_IndexesWrap() => Assert.Equal(ChromeColors.Person(0, dark: false), ChromeColors.Person(ChromeColors.PersonCount, dark: false));
}
