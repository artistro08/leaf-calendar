using LeafCalendar.App.Controls;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace LeafCalendar.App.Views;

/// <summary>
/// The keyboard shortcut cheat sheet (? in the main window, or Settings › Shortcuts): spec 8.7's tables with a filter
/// box, then the two global shortcuts as set, then the Ctrl+wheel note.
/// </summary>
public static class ShortcutSheet
{
    const double CaptionSize = 12;

    /// <summary>
    /// Shows the sheet over <paramref name="owner"/>'s window until it's closed, in the theme <paramref name="owner"/>
    /// shows now. Built in code from this method's own references (nothing read back).
    /// </summary>
    public static async Task ShowAsync(FrameworkElement owner, LeafSettings settings)
    {
        // Colors For Code-Built Text: The Owner's Current Theme, Or The System's High Contrast Colors
        var colors = Colors.For(owner.ActualTheme == ElementTheme.Dark);

        // Filter Box Over A Scrolling List
        var filter = new TextBox { PlaceholderText = "Search shortcuts", Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(filter, "Search shortcuts");
        AutomationProperties.SetAutomationId(filter, "ShortcutFilterBox");

        var list   = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 480, Padding = new Thickness(0, 0, 16, 0) };
        ScrollIndicator.ShowOnHover(scroll);

        var content = new StackPanel { Width = 560 };
        content.Children.Add(filter);
        content.Children.Add(scroll);

        filter.TextChanged += (_, _) => Fill(list, filter.Text, settings, colors);
        Fill(list, "", settings, colors);

        var dialog = new ContentDialog
        {
            XamlRoot        = owner.XamlRoot,
            Title           = "Keyboard shortcuts",
            Content         = content,
            CloseButtonText = "Close",
        };
        AutomationProperties.SetAutomationId(dialog, "ShortcutSheet");
        dialog.Opened += (_, _) => filter.Focus(FocusState.Programmatic);

        await dialog.ShowAsync();
    }

    // The matching rows by section, then the global shortcuts, then the footnote
    static void Fill(StackPanel list, string query, LeafSettings settings, Colors colors)
    {
        list.Children.Clear();

        // In-App Sections
        foreach (var section in ShortcutCatalog.Sections)
        {
            var rows = ShortcutCatalog.Filter(query).Where(r => r.Section == section).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            list.Children.Add(Header(section, first: list.Children.Count == 0));
            foreach (var row in rows)
            {
                list.Children.Add(Row(row.Action, row.Keys, $"ShortcutRow_{IndexOf(row)}", colors));
            }
        }

        // Anywhere In Windows (the global shortcuts as set; an empty one is off)
        (string Action, string Keys)[] global =
        [
            ("Join meeting", string.IsNullOrEmpty(settings.JoinShortcut) ? "Off" : settings.JoinShortcut),
            ("Show or hide the tray flyout", string.IsNullOrEmpty(settings.FlyoutShortcut) ? "Off" : settings.FlyoutShortcut),
        ];
        var words   = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = global.Where(g => words.All(w => g.Action.Contains(w, StringComparison.OrdinalIgnoreCase) || g.Keys.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count > 0)
        {
            list.Children.Add(Header("Anywhere in Windows", first: list.Children.Count == 0));
            for (var i = 0; i < matches.Count; i++)
            {
                list.Children.Add(Row(matches[i].Action, matches[i].Keys, $"GlobalShortcutRow_{i}", colors));
            }
        }

        // Footnote
        list.Children.Add(new TextBlock
        {
            Text         = ShortcutCatalog.Footnote,
            FontSize     = CaptionSize,
            Foreground   = colors.Secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 16, 0, 0),
        });
    }

    static int IndexOf(ShortcutRow row)
    {
        for (var i = 0; i < ShortcutCatalog.Rows.Count; i++)
        {
            if (ReferenceEquals(ShortcutCatalog.Rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    static TextBlock Header(string text, bool first) => new()
    {
        Text       = text,
        FontWeight = FontWeights.SemiBold,
        Margin     = new Thickness(0, first ? 0 : 16, 0, 4),
    };

    // The action on the left, the keys on the right in a key cap
    static Grid Row(string action, string keys, string automationId, Colors colors)
    {
        var grid = new Grid { MinHeight = 32, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(grid, $"{action}: {keys}");
        AutomationProperties.SetAutomationId(grid, automationId);

        grid.Children.Add(new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        var cap = new Border
        {
            Child             = new TextBlock { Text = keys, FontSize = CaptionSize, Foreground = colors.CapText },
            Background        = colors.CapFill,
            BorderBrush       = colors.CapStroke,
            BorderThickness   = new Thickness(1),
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cap, 1);
        grid.Children.Add(cap);

        return grid;
    }

    /// <summary>The sheet's code-built colors: LeafBrushes per theme, or the system's button colors in high contrast.</summary>
    sealed record Colors(Brush CapFill, Brush CapStroke, Brush CapText, Brush Secondary)
    {
        public static Colors For(bool dark)
        {
            if (!new AccessibilitySettings().HighContrast)
            {
                return new(LeafBrushes.Hover(dark), LeafBrushes.GridLine(dark), LeafBrushes.PrimaryText(dark), LeafBrushes.SecondaryText(dark));
            }

            var ui   = new UISettings();
            var face = new SolidColorBrush(ui.UIElementColor(UIElementType.ButtonFace));
            var text = new SolidColorBrush(ui.UIElementColor(UIElementType.ButtonText));
            return new(face, text, text, new SolidColorBrush(ui.UIElementColor(UIElementType.WindowText)));
        }
    }
}
