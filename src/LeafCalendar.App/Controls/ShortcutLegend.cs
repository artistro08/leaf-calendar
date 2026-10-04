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
    private const double CapFontSize = 12;
    private const double CapMinWidth = 24;
    private const double Gap = 4;

    // Keys whose mark is a speck at Caption size (". , ; : ' ` -"): drawn larger, with the cap's vertical padding given
    // up so the cap stays as tall as its neighbors (16 of text either way)
    private const string SmallMarks = ".,;:'`-";
    private const double MarkFontSize = 16;

    /// <summary>
    /// What a key cap shows for a key as the cheat sheet writes it: Enter, Backspace, and Win as their virtual keys, so
    /// the cap draws their glyph (like the shortcut picker); the arrows as words ("Left", never a chevron that reads as
    /// &lt; or &gt;); and every other key, Shift included, as its name.
    /// </summary>
    public static object CapContent(string key) => key switch
    {
        "Enter" => (int)Windows.System.VirtualKey.Enter,
        "Backspace" => (int)Windows.System.VirtualKey.Back,
        "Win" => (int)Windows.System.VirtualKey.LeftWindows,
        "←" => "Left",
        "→" => "Right",
        "↑" => "Up",
        "↓" => "Down",
        _ => key,
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
                // Only Virtual Keys Draw As Glyphs (a key named "Left" would otherwise turn into a chevron)
                var content = CapContent(part.Text);
                var mark = part.Text.Length == 1 && SmallMarks.Contains(part.Text[0], StringComparison.Ordinal);
                legend.Children.Add(new KeyVisual
                {
                    Content = content,
                    RenderKeyAsGlyph = content is int,
                    FontSize = mark ? MarkFontSize : CapFontSize,
                    FontWeight = mark ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    MinWidth = CapMinWidth,
                    Padding = mark ? new Thickness(8, 0, 8, 0) : new Thickness(8, 2, 8, 2),
                    CornerRadius = new CornerRadius(4),
                });
                continue;
            }

            // The Words: the given color, else the theme's secondary text (a ThemeResource style, so it follows Leaf's theme, not Windows')
            var words = new TextBlock
            {
                Text = part.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
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
