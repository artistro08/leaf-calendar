using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Brushes for code-built calendar visuals. Hex brushes are cached (UI thread only). Grid colors
/// are chosen per theme in code, because code-created elements can't use {ThemeResource}.
/// </summary>
public static class LeafBrushes
{
    static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transparent but hit-testable.</summary>
    public static SolidColorBrush Transparent { get; } = new(Colors.Transparent);

    /// <summary>Current-time line.</summary>
    public static SolidColorBrush NowLine { get; } = new(Color.FromArgb(255, 0xE5, 0x48, 0x4D));

    /// <summary>A brush for <c>#RRGGBB</c> or <c>#AARRGGBB</c> (invalid input gives Google blue).</summary>
    public static SolidColorBrush FromHex(string hex)
    {
        if (Cache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(Parse(hex));
        Cache[hex] = brush;
        return brush;
    }

    /// <summary>Hour and day divider lines.</summary>
    public static SolidColorBrush GridLine(bool dark) => FromHex(dark ? "#1FFFFFFF" : "#1A000000");

    /// <summary>Half-hour lines.</summary>
    public static SolidColorBrush HalfHourLine(bool dark) => FromHex(dark ? "#0DFFFFFF" : "#0A000000");

    /// <summary>Weekend column tint.</summary>
    public static SolidColorBrush WeekendFill(bool dark) => FromHex(dark ? "#08FFFFFF" : "#06000000");

    /// <summary>Secondary text (hour labels, weekday names).</summary>
    public static SolidColorBrush SecondaryText(bool dark) => FromHex(dark ? "#C5FFFFFF" : "#9E000000");

    /// <summary>Primary text.</summary>
    public static SolidColorBrush PrimaryText(bool dark) => FromHex(dark ? "#FFFFFFFF" : "#E4000000");

    /// <summary>Days outside the focused month.</summary>
    public static SolidColorBrush DimText(bool dark) => FromHex(dark ? "#5DFFFFFF" : "#5C000000");

    /// <summary>The system accent brush.</summary>
    public static Brush Accent => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    /// <summary>Text on the accent brush.</summary>
    public static Brush OnAccent => (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];

    static Color Parse(string hex)
    {
        var digits = hex.TrimStart('#');
        if (digits.Length == 6)
        {
            digits = "FF" + digits;
        }

        if (digits.Length != 8 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return Color.FromArgb(255, 0x42, 0x85, 0xF4);
        }

        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }
}
