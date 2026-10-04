using LeafCalendar.App.Controls;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
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
    /// <summary>Width of the main window's panel.</summary>
    public const double PanelWidth = 400;

    /// <summary>
    /// Shows the sheet over <paramref name="owner"/>'s window until it's closed, in the theme <paramref name="owner"/>
    /// shows now (Settings › Shortcuts). Built in code from this method's own references (nothing read back).
    /// </summary>
    public static async Task ShowAsync(FrameworkElement owner, LeafSettings settings)
    {
        var (content, filter) = Build(owner, settings, 480);
        content.Width = 560;

        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Title = "Keyboard shortcuts",
            Content = content,
            CloseButtonText = "Close",
        };
        AutomationProperties.SetAutomationId(dialog, "ShortcutSheet");

        // A dialog is at most 548 wide (500 inside its padding), which cut the 560 content's key caps off: make room
        dialog.Resources["ContentDialogMaxWidth"] = 640d;
        dialog.Opened += (_, _) => filter.Focus(FocusState.Programmatic);

        await dialog.ShowAsync();
    }

    /// <summary>
    /// The main window's sheet: a floating card for the left side of the calendar view (like PowerToys' shortcut
    /// guide), with a title, a close button, the filter box, and the list filling the rest of its height. Esc or ?
    /// (even while typing in the filter) calls <paramref name="close"/>; so does the close button. The caller places it,
    /// gives it its shadow, and focuses the filter box (returned).
    /// </summary>
    public static (Border Panel, TextBox Filter) Panel(FrameworkElement owner, LeafSettings settings, Action close)
    {
        var (content, filter) = Build(owner, settings, double.PositiveInfinity);

        // Header (title and close button)
        var title = new TextBlock { Text = "Keyboard shortcuts", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
        var shut = new Button { Content = new FontIcon { Glyph = "", FontSize = 16 }, Style = (Style)Application.Current.Resources["LeafBareIconButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(shut, "Close");
        AutomationProperties.SetAutomationId(shut, "ShortcutSheetClose");
        ToolTipService.SetToolTip(shut, "Close (Esc)");
        shut.Click += (_, _) => close();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(title);
        header.Children.Add(shut);

        // Card (the header on top, the filter and list under it, filling the height)
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 1);
        layout.Children.Add(header);
        layout.Children.Add(content);

        var panel = new Border { Child = layout, Width = PanelWidth, Padding = new Thickness(16, 12, 4, 16), Style = (Style)Application.Current.Resources["LeafFloatingCardStyle"] };
        AutomationProperties.SetName(layout, "Keyboard shortcuts");
        AutomationProperties.SetAutomationId(layout, "ShortcutSheet");

        // Esc Or ? Closes It, Even While Typing In The Filter
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                close();
            }
        };

        // ? Typed Into The Filter (the character, whatever the keyboard layout; elsewhere ? is the page's own shortcut)
        filter.BeforeTextChanging += (_, e) =>
        {
            if (e.NewText.Contains('?', StringComparison.Ordinal))
            {
                e.Cancel = true;
                close();
            }
        };

        return (panel, filter);
    }

    // The filter box over the scrolling list (the list as tall as maxHeight, or filling a stretched parent)
    private static (Grid Content, TextBox Filter) Build(FrameworkElement owner, LeafSettings settings, double maxHeight)
    {
        // Colors For Code-Built Text: The Owner's Current Theme, Or The System's High Contrast Colors
        var colors = Colors.For(owner.ActualTheme == ElementTheme.Dark);

        // Filter Box Over A Scrolling List
        var filter = new TextBox { PlaceholderText = "Search shortcuts", Margin = new Thickness(0, 0, 12, 12) };
        AutomationProperties.SetName(filter, "Search shortcuts");
        AutomationProperties.SetAutomationId(filter, "ShortcutFilterBox");

        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = maxHeight, Padding = new Thickness(0, 0, 12, 0) };
        ScrollIndicator.ShowOnHover(scroll);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        content.Children.Add(filter);
        content.Children.Add(scroll);

        filter.TextChanged += (_, _) => Fill(list, filter.Text, settings, colors);
        Fill(list, "", settings, colors);

        // The Rows Again In The New Colors When The Theme Or A Contrast Theme Changes While The Sheet Is Open
        void Refill()
        {
            colors = Colors.For(content.ActualTheme == ElementTheme.Dark);
            Fill(list, filter.Text, settings, colors);
        }

        EventHandler onContrast = (_, _) => content.DispatcherQueue.TryEnqueue(Refill);
        content.ActualThemeChanged += (_, _) => Refill();
        content.Loaded += (_, _) => LeafBrushes.ContrastChanged += onContrast;
        content.Unloaded += (_, _) => LeafBrushes.ContrastChanged -= onContrast;
        return (content, filter);
    }
    // The matching rows by section, then the global shortcuts, then the footnote
    private static void Fill(StackPanel list, string query, LeafSettings settings, Colors colors)
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
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            Text = ShortcutCatalog.Footnote,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = colors.Secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
    }

    private static int IndexOf(ShortcutRow row)
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

    private static TextBlock Header(string text, bool first) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, first ? 0 : 16, 0, 4),
    };

    // The action on the left, the keys on the right, each on its own key cap (PowerToys' legend look)
    private static Grid Row(string action, string keys, string automationId, Colors colors)
    {
        var grid = new Grid { MinHeight = 32, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(grid, $"{action}: {keys}");
        AutomationProperties.SetAutomationId(grid, automationId);

        grid.Children.Add(new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        // "Off" (a global shortcut that isn't set) is a word, not a key
        UIElement legend = keys == "Off"
            ? new TextBlock { Text = keys, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = colors.Secondary, VerticalAlignment = VerticalAlignment.Center }
            : ShortcutLegend.Build(keys, colors.Secondary);
        Grid.SetColumn((FrameworkElement)legend, 1);
        grid.Children.Add(legend);

        return grid;
    }

    /// <summary>The sheet's code-built colors: LeafBrushes per theme, or the system's text color in high contrast (the key caps take theirs from their style).</summary>
    private sealed record Colors(Brush Secondary)
    {
        public static Colors For(bool dark) =>
            new AccessibilitySettings().HighContrast
                ? new(new SolidColorBrush(new UISettings().UIElementColor(UIElementType.WindowText)))
                : new(LeafBrushes.SecondaryText(dark));
    }
}
