using System.Globalization;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Data;

namespace LeafCalendar.Core.Views;

/// <summary>Colors for one event card: accent bar, fill, and readable text (hex; <see cref="SecondaryText"/> is #AARRGGBB).</summary>
public sealed record EventPalette(string Accent, string Fill, string Text, string SecondaryText);

/// <summary>
/// Google's color palettes and card color math. Fills are the accent mixed into the page surface,
/// and text is black or white, whichever contrasts more (always 4.5:1 or better for Google's colors).
/// </summary>
public static partial class EventColors
{
    /// <summary>How far a past card's colors (accent and fill) move toward the calendar surface; its text is not faded but picked against the faded fill.</summary>
    public const double PastFade = 0.45;

    const string DarkSurface  = "#202020";
    const string LightSurface = "#FFFFFF";
    const string DarkText     = "#1A1A1A";
    const string LightText    = "#FFFFFF";

    // Google Calendar event colors (colorId 1-11)
    static readonly Dictionary<string, string> EventColorIds = new(StringComparer.Ordinal)
    {
        ["1"]  = "#7986CB",
        ["2"]  = "#33B679",
        ["3"]  = "#8E24AA",
        ["4"]  = "#E67C73",
        ["5"]  = "#F6BF26",
        ["6"]  = "#F4511E",
        ["7"]  = "#039BE5",
        ["8"]  = "#616161",
        ["9"]  = "#3F51B5",
        ["10"] = "#0B8043",
        ["11"] = "#D50000",
    };

    /// <summary>Google's names for the 11 event colors, in colorId order (the editor's swatches and the event menu).</summary>
    public static IReadOnlyList<(string Id, string Name)> EventColorNames { get; } =
    [
        ("1", "Lavender"), ("2", "Sage"), ("3", "Grape"), ("4", "Flamingo"), ("5", "Banana"), ("6", "Tangerine"),
        ("7", "Peacock"), ("8", "Graphite"), ("9", "Blueberry"), ("10", "Basil"), ("11", "Tomato"),
    ];

    /// <summary>Google Calendar's 24 calendar colors, offered in the sidebar color picker.</summary>
    public static IReadOnlyList<string> CalendarPalette { get; } =
    [
        "#AC725E", "#D06B64", "#F83A22", "#FA573C", "#FF7537", "#FFAD46",
        "#42D692", "#16A765", "#7BD148", "#B3DC6C", "#FBE983", "#FAD165",
        "#92E1C0", "#9FE1E7", "#9FC6E7", "#4986E7", "#9A9CFF", "#B99AFF",
        "#C2C2C2", "#CABDBF", "#CCA6AC", "#F691B2", "#CD74E6", "#A47AE2",
    ];

    /// <summary>The event's own color when it has one, else its calendar's, else Google blue (uppercase hex).</summary>
    public static string ResolveAccent(string? colorId, string calendarColor)
    {
        if (colorId is not null && EventColorIds.TryGetValue(colorId, out var eventColor))
        {
            return eventColor;
        }

        return Hex().IsMatch(calendarColor) ? calendarColor.ToUpperInvariant() : CalendarInfo.DefaultColor;
    }

    /// <summary>
    /// Card colors for an accent in the dark or light theme; <paramref name="past"/> fades the accent and fill toward the
    /// calendar surface and picks text against the faded fill. A <paramref name="selected"/> card is filled with the
    /// accent itself, at full strength even when past, with text picked against it (a mid-tone accent is deepened or
    /// lightened just enough for its text to read at 4.5:1).
    /// </summary>
    public static EventPalette Palette(string accentHex, bool dark, bool past = false, bool selected = false)
    {
        var accent = Hex().IsMatch(accentHex) ? accentHex.ToUpperInvariant() : CalendarInfo.DefaultColor;
        var fill   = selected ? accent : Blend(accent, dark ? DarkSurface : LightSurface, dark ? 0.62 : 0.78);
        if (past && !selected)
        {
            accent = Blend(accent, ChromeColors.Surface(dark), PastFade);
            fill   = Blend(fill, ChromeColors.Surface(dark), PastFade);
        }

        var text   = ContrastRatio(LightText, fill) >= ContrastRatio(DarkText, fill) ? LightText : DarkText;

        // A Mid-Tone Accent Reaches 4.5:1 With Neither Text Color: deepen it (white text) or lighten it (dark text) until it does
        if (selected)
        {
            fill = Legible(fill, text);
        }

        return new EventPalette(accent, fill, text, (text == LightText ? "#F2" : "#D9") + text[1..]);
    }

    // The fill moved toward black (under white text) or white (under dark text) in small steps until the text reads at 4.5:1
    static string Legible(string fill, string text)
    {
        var toward = text == LightText ? "#000000" : "#FFFFFF";
        for (var step = 0; step < 20 && ContrastRatio(text, fill) < 4.5; step++)
        {
            fill = Blend(fill, toward, 0.05);
        }

        return fill;
    }

    /// <summary>Mixes two colors; <paramref name="backgroundAmount"/> 0 is all foreground, 1 all background.</summary>
    public static string Blend(string foregroundHex, string backgroundHex, double backgroundAmount)
    {
        var (fr, fg, fb) = Channels(foregroundHex);
        var (br, bg, bb) = Channels(backgroundHex);
        var t = Math.Clamp(backgroundAmount, 0, 1);

        static int Mix(int f, int b, double t) => (int)Math.Round(f * (1 - t) + b * t, MidpointRounding.AwayFromZero);

        return string.Create(CultureInfo.InvariantCulture, $"#{Mix(fr, br, t):X2}{Mix(fg, bg, t):X2}{Mix(fb, bb, t):X2}");
    }

    /// <summary>WCAG contrast ratio between two colors (1-21).</summary>
    public static double ContrastRatio(string hexA, string hexB)
    {
        var a = Luminance(hexA);
        var b = Luminance(hexB);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static double Luminance(string hex)
    {
        var (r, g, b) = Channels(hex);

        static double Linear(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    static (int R, int G, int B) Channels(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Hex();
}
