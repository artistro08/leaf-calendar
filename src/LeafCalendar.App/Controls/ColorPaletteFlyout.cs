using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The calendar palette as a flyout: the 24 colors in rows of six, the current one outlined, then a link back to the
/// default color. Used by Settings › Calendars and the Sunsama card in Settings › Accounts.
/// </summary>
public static class ColorPaletteFlyout
{
    /// <summary>Builds the flyout; picking a color or the link closes it first.</summary>
    /// <param name="current">The color in use (outlined when it's in the palette).</param>
    /// <param name="dark">True in the dark theme (the outline follows the text color).</param>
    /// <param name="pick">Runs with the picked color's hex.</param>
    /// <param name="resetText">The link's text ("Use Google's color").</param>
    /// <param name="resetId">The link's automation ID.</param>
    /// <param name="reset">Runs when the link is clicked.</param>
    /// <returns>The flyout, ready to show.</returns>
    public static Flyout Create(string current, bool dark, Action<string> pick, string resetText, string resetId, Action reset)
    {
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentNullException.ThrowIfNull(reset);

        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 32, ItemHeight = 32 };
        var flyout = new Flyout();
        foreach (var (hex, colorName) in EventColors.CalendarColorNames)
        {
            var color = LeafBrushes.FromHex(hex).Color;
            var ring = LeafBrushes.PrimaryText(dark);
            var isCurrent = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var name = isCurrent ? $"{colorName}, current" : colorName;
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(color),
                BorderBrush = ring,
                BorderThickness = new Thickness(isCurrent ? 2 : 0),
            };

            // Hover And Press Tint The Color Instead Of Replacing It (the current one's ring stays too)
            swatch.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(color) { Opacity = 0.8 };
            swatch.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(color) { Opacity = 0.6 };
            swatch.Resources["ButtonBorderBrushPointerOver"] = ring;
            swatch.Resources["ButtonBorderBrushPressed"] = ring;
            AutomationProperties.SetAutomationId(swatch, $"ColorSwatch_{hex[1..]}");
            AutomationProperties.SetName(swatch, name);
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                flyout.Hide();
                pick(hex);
            };
            grid.Children.Add(swatch);
        }

        var link = new HyperlinkButton { Content = resetText, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(link, resetId);
        link.Click += (_, _) =>
        {
            flyout.Hide();
            reset();
        };

        flyout.Content = new StackPanel { Children = { grid, link } };
        return flyout;
    }
}
