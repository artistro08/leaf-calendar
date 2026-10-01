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
}
