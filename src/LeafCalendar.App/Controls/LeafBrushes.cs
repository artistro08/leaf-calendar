using System.Globalization;
using LeafCalendar.Core.Views;
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
    public static SolidColorBrush GridLine(bool dark) => FromHex(ChromeColors.GridLine(dark));

    /// <summary>Half-hour lines.</summary>
    public static SolidColorBrush HalfHourLine(bool dark) => FromHex(ChromeColors.HalfHourLine(dark));

    /// <summary>Weekend column tint.</summary>
    public static SolidColorBrush WeekendFill(bool dark) => FromHex(ChromeColors.WeekendFill(dark));

    /// <summary>Secondary text (hour labels, weekday names).</summary>
    public static SolidColorBrush SecondaryText(bool dark) => FromHex(ChromeColors.SecondaryText(dark));

    /// <summary>Hover fill for rows (the theme's SubtleFillColorSecondary).</summary>
    public static SolidColorBrush Hover(bool dark) => FromHex(ChromeColors.Hover(dark));

    /// <summary>Opaque fill for the time grid's drag ghost (the theme's SolidBackgroundFillColorTertiary), so a card under it doesn't show through.</summary>
    public static SolidColorBrush GhostFill(bool dark) => FromHex(ChromeColors.GhostFill(dark));

    /// <summary>Primary text.</summary>
    public static SolidColorBrush PrimaryText(bool dark) => FromHex(ChromeColors.PrimaryText(dark));

    /// <summary>Differing fields in the conflict dialog (the theme's SystemFillColorCautionBackground).</summary>
    public static SolidColorBrush CautionBackground(bool dark) => FromHex(ChromeColors.CautionBackground(dark));

    /// <summary>Days outside the focused month.</summary>
    public static SolidColorBrush DimText(bool dark) => FromHex(ChromeColors.DimText(dark));

    /// <summary>
    /// The accent fill for a theme (what AccentFillColorDefaultBrush resolves to: the system accent's
    /// Light 2 shade in dark, Dark 1 in light). Looked up from the system rather than the app's resources,
    /// which follow the Windows theme even when Leaf is set to the other one.
    /// </summary>
    public static SolidColorBrush Accent(bool dark) => dark ? AccentDark.Value : AccentLight.Value;

    /// <summary>Text on <see cref="Accent"/> (TextOnAccentFillColorPrimary: black in dark, white in light).</summary>
    public static SolidColorBrush OnAccent(bool dark) => FromHex(ChromeColors.OnAccent(dark));

    /// <summary>An overlaid person's color (block edge, chip dot); the highlight color in a contrast theme.</summary>
    public static SolidColorBrush Person(int index, bool dark) => IsHighContrast ? Highlight : FromHex(ChromeColors.Person(index, dark));

    /// <summary>An overlaid person's busy-block fill (their color at 20%); none in a contrast theme, where the edge carries it.</summary>
    public static SolidColorBrush PersonFill(int index, bool dark) => IsHighContrast ? Transparent : FromHex(ChromeColors.PersonFill(index, dark));

    // Contrast Themes Use The System's Own Colors Instead Of Tints
    static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    static bool IsHighContrast => Accessibility.HighContrast;

    // ponytail: read once, like the accent below
    static readonly SolidColorBrush Highlight = new(new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight));

    // ponytail: read once; an accent color change while Leaf runs shows after a restart
    static readonly Lazy<SolidColorBrush> AccentDark  = new(() => new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2)));
    static readonly Lazy<SolidColorBrush> AccentLight = new(() => new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark1)));

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
