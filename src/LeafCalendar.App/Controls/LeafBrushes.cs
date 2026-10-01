using System.Globalization;
using LeafCalendar.Core.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Brushes for code-built calendar visuals. Hex brushes are cached (UI thread only). Grid colors
/// are chosen per theme in code, because code-created elements can't use {ThemeResource}. In a
/// contrast theme every chrome brush and every card comes from the system's contrast colors instead.
/// </summary>
public static class LeafBrushes
{
    static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transparent but hit-testable.</summary>
    public static SolidColorBrush Transparent { get; } = new(Colors.Transparent);

    /// <summary>Current-time line (the highlight color in a contrast theme).</summary>
    public static SolidColorBrush NowLine => HighContrast ? SystemBrush(UIElementType.Highlight) : FromHex("#E5484D");

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
    public static SolidColorBrush GridLine(bool dark) => HighContrast ? SystemBrush(UIElementType.GrayText) : FromHex(ChromeColors.GridLine(dark));

    /// <summary>Half-hour lines.</summary>
    public static SolidColorBrush HalfHourLine(bool dark) => HighContrast ? SystemBrush(UIElementType.GrayText) : FromHex(ChromeColors.HalfHourLine(dark));

    /// <summary>Weekend column tint.</summary>
    public static SolidColorBrush WeekendFill(bool dark) => HighContrast ? SystemBrush(UIElementType.Window) : FromHex(ChromeColors.WeekendFill(dark));

    /// <summary>Secondary text (hour labels, weekday names).</summary>
    public static SolidColorBrush SecondaryText(bool dark) => HighContrast ? SystemBrush(UIElementType.WindowText) : FromHex(ChromeColors.SecondaryText(dark));

    /// <summary>Hover fill for rows (the theme's SubtleFillColorSecondary).</summary>
    public static SolidColorBrush Hover(bool dark) => HighContrast ? SystemBrush(UIElementType.Highlight) : FromHex(ChromeColors.Hover(dark));

    /// <summary>Opaque fill for the time grid's drag ghost (the theme's SolidBackgroundFillColorTertiary), so a card under it doesn't show through.</summary>
    public static SolidColorBrush GhostFill(bool dark) => HighContrast ? SystemBrush(UIElementType.Window) : FromHex(ChromeColors.GhostFill(dark));

    /// <summary>Primary text.</summary>
    public static SolidColorBrush PrimaryText(bool dark) => HighContrast ? SystemBrush(UIElementType.WindowText) : FromHex(ChromeColors.PrimaryText(dark));

    /// <summary>Differing fields in the conflict dialog (the theme's SystemFillColorCautionBackground).</summary>
    public static SolidColorBrush CautionBackground(bool dark) => HighContrast ? SystemBrush(UIElementType.Window) : FromHex(ChromeColors.CautionBackground(dark));

    /// <summary>Days outside the focused month.</summary>
    public static SolidColorBrush DimText(bool dark) => HighContrast ? SystemBrush(UIElementType.WindowText) : FromHex(ChromeColors.DimText(dark));

    /// <summary>
    /// The accent fill for a theme (what AccentFillColorDefaultBrush resolves to: the system accent's
    /// Light 2 shade in dark, Dark 1 in light). Looked up from the system rather than the app's resources,
    /// which follow the Windows theme even when Leaf is set to the other one.
    /// </summary>
    public static SolidColorBrush Accent(bool dark) => HighContrast ? SystemBrush(UIElementType.Highlight) : dark ? AccentDark.Value : AccentLight.Value;

    /// <summary>Text on <see cref="Accent"/> (TextOnAccentFillColorPrimary: black in dark, white in light).</summary>
    public static SolidColorBrush OnAccent(bool dark) => HighContrast ? SystemBrush(UIElementType.HighlightText) : FromHex(ChromeColors.OnAccent(dark));

    /// <summary>An overlaid person's color (block edge, chip dot); the highlight color in a contrast theme.</summary>
    public static SolidColorBrush Person(int index, bool dark) => HighContrast ? SystemBrush(UIElementType.Highlight) : FromHex(ChromeColors.Person(index, dark));

    /// <summary>An overlaid person's busy-block fill (their color at 20%); none in a contrast theme, where the edge carries it.</summary>
    public static SolidColorBrush PersonFill(int index, bool dark) => HighContrast ? Transparent : FromHex(ChromeColors.PersonFill(index, dark));

    /// <summary>The tint outside your working hours; none in a contrast theme, where a tint would cut contrast.</summary>
    public static SolidColorBrush OffHours(bool dark) => HighContrast ? Transparent : FromHex(ChromeColors.OffHours(dark));

    // =========================================================================
    // CONTRAST THEMES
    // =========================================================================

    static readonly AccessibilitySettings Accessibility = new();
    static readonly UISettings System = new();
    static readonly Dictionary<UIElementType, SolidColorBrush> SystemCache = [];
    static volatile bool _systemStale;
    static bool _wasHighContrast = Accessibility.HighContrast;

    /// <summary>True while Windows uses a contrast theme; every brush then comes from the system's contrast colors.</summary>
    public static bool HighContrast => Accessibility.HighContrast;

    /// <summary>Raised (on a background thread) when a contrast theme turns on or off, or its colors change.</summary>
    public static event EventHandler? ContrastChanged;

    // The System Brushes Are Read Again (on the UI thread, at the next use) When The Contrast Theme Or Its Colors Change.
    // UISettings.ColorValuesChanged, not AccessibilitySettings.HighContrastChanged: that one needs a CoreWindow, and
    // subscribing to it from a desktop app throws (it crashed Leaf at startup).
    static LeafBrushes() => System.ColorValuesChanged += (_, _) =>
    {
        var now = Accessibility.HighContrast;
        if (!now && !_wasHighContrast)
        {
            return;
        }

        _wasHighContrast = now;
        _systemStale     = true;
        ContrastChanged?.Invoke(null, EventArgs.Empty);
    };

    /// <summary>Card colors: Google's in normal themes; window, text, and highlight colors in a contrast theme (where past cards aren't faded).</summary>
    public static EventPalette CardPalette(string accentHex, bool dark, bool past = false, bool selected = false) =>
        HighContrast ? HighContrastPalette(selected) : EventColors.Palette(accentHex, dark, past);

    /// <summary>A card's border: 2 when selected, 1 when outlined or in a contrast theme (where the fill matches the window), else none.</summary>
    public static Thickness CardBorder(bool selected, bool outlined = false) => new(selected ? 2 : outlined || HighContrast ? 1 : 0);

    // A Contrast Card: The Window Color With A Text-Colored Bar And Border, Or The Highlight Pair When Selected
    static EventPalette HighContrastPalette(bool selected)
    {
        var fill = Hex(System.UIElementColor(selected ? UIElementType.Highlight : UIElementType.Window));
        var text = Hex(System.UIElementColor(selected ? UIElementType.HighlightText : UIElementType.WindowText));

        return new EventPalette(text, fill, text, "#FF" + text[1..]);
    }

    // A System Contrast Color As A Brush (UI thread only)
    static SolidColorBrush SystemBrush(UIElementType type)
    {
        if (_systemStale)
        {
            SystemCache.Clear();
            _systemStale = false;
        }

        if (!SystemCache.TryGetValue(type, out var brush))
        {
            brush             = new SolidColorBrush(System.UIElementColor(type));
            SystemCache[type] = brush;
        }

        return brush;
    }

    static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    // ponytail: read once; an accent color change while Leaf runs shows after a restart
    static readonly Lazy<SolidColorBrush> AccentDark  = new(() => new SolidColorBrush(new UISettings().GetColorValue(UIColorType.AccentLight2)));
    static readonly Lazy<SolidColorBrush> AccentLight = new(() => new SolidColorBrush(new UISettings().GetColorValue(UIColorType.AccentDark1)));

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
