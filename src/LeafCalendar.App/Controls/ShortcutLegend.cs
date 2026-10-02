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
                    Content      = part.Text,
                    FontSize     = CapFontSize,
                    MinWidth     = CapMinWidth,
                    Padding      = new Thickness(8, 2, 8, 2),
                    CornerRadius = new CornerRadius(4),
                });
                continue;
            }

            var words = new TextBlock
            {
                Text              = part.Text,
                FontSize          = CapFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = wordBrush ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };

            AutomationProperties.SetAccessibilityView(words, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            legend.Children.Add(words);
        }

        return legend;
    }
}
