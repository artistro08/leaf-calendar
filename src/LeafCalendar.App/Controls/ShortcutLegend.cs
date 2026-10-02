using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>
/// A written shortcut drawn like PowerToys' shortcut legends: every key on its own key cap (<see cref="KeyVisual"/>),
/// side by side, with the words between keys ("or", "then", "/", "drag") as plain secondary text.
/// </summary>
public static class ShortcutLegend
{
    // Caption-sized caps, 4 apart, with room for a one-letter key to read as a square
    const double CapFontSize = 12;
    const double CapMinWidth = 24;
    const double Gap         = 4;

    /// <summary>
    /// What a key cap shows for a key as the cheat sheet writes it: Shift, Enter, Backspace, Win, and the arrows as their
    /// virtual keys, so the cap draws their glyph (like the shortcut picker), and every other key as its name.
    /// </summary>
    public static object CapContent(string key) => key switch
    {
        "Shift"             => (int)Windows.System.VirtualKey.Shift,
        "Enter"             => (int)Windows.System.VirtualKey.Enter,
        "Backspace"         => (int)Windows.System.VirtualKey.Back,
        "Win"               => (int)Windows.System.VirtualKey.LeftWindows,
        "Left" or "←"       => (int)Windows.System.VirtualKey.Left,
        "Right" or "→"      => (int)Windows.System.VirtualKey.Right,
        "Up" or "↑"         => (int)Windows.System.VirtualKey.Up,
        "Down" or "↓"       => (int)Windows.System.VirtualKey.Down,
        _                   => key,
    };

    /// <summary>
    /// The legend for <paramref name="shortcut"/> (as the cheat sheet writes it), read by Narrator as the shortcut itself.
    /// <paramref name="wordBrush"/> colors the words between keys (the theme's secondary text when null).
    /// </summary>
    public static StackPanel Build(string shortcut, Brush? wordBrush = null)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(legend, shortcut);

        foreach (var part in ShortcutKeys.Parse(shortcut))
        {
            if (part.IsKey)
            {
                legend.Children.Add(new KeyVisual
                {
                    Content          = CapContent(part.Text),
                    RenderKeyAsGlyph = true,
                    FontSize         = CapFontSize,
                    MinWidth         = CapMinWidth,
                    Padding          = new Thickness(8, 2, 8, 2),
                    CornerRadius     = new CornerRadius(4),
                });
                continue;
            }

            // The Words: the given color, else the theme's secondary text (a ThemeResource style, so it follows Leaf's theme, not Windows')
            var words = new TextBlock
            {
                Text              = part.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.NoWrap,
            };
            if (wordBrush is null)
            {
                words.Style = (Style)Application.Current.Resources["LeafSecondaryTextStyle"];
            }
            else
            {
                words.Foreground = wordBrush;
            }

            words.FontSize = CapFontSize;

            AutomationProperties.SetAccessibilityView(words, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            legend.Children.Add(words);
        }

        return legend;
    }
}
