using System.Collections.ObjectModel;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Calendars: every calendar, grouped by account, with its color (Google's 24-color palette, or back to
/// Google's own color) and whether Leaf shows it. Leaf-only: Google Calendar isn't changed.
/// </summary>
public sealed partial class CalendarsPage : Page
{
    private SettingsContext _context = null!;
    private readonly ObservableCollection<AccountGroup> _groups = [];
    private Flyout? _colorFlyout;
    private CalendarRow? _colorRow;

    /// <summary>Creates the page.</summary>
    public CalendarsPage()
    {
        InitializeComponent();
        GroupList.ItemsSource = _groups;
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <summary>x:Bind helper: automation ID of a calendar's color button.</summary>
    public static string ColorId(CalendarInfo info) => $"CalendarColor_{info.Id}";

    /// <summary>x:Bind helper: automation ID of a calendar's show/hide switch.</summary>
    public static string VisibleId(CalendarInfo info) => $"CalendarVisible_{info.Id}";

    /// <summary>x:Bind helper: automation ID of a calendar's "More options" button.</summary>
    public static string MoreId(CalendarInfo info) => $"CalendarMore_{info.Id}";

    /// <summary>x:Bind helper: the "More options" button's accessible name.</summary>
    public static string MoreName(string calendar) => $"More options for {calendar}";

    /// <summary>x:Bind helper: the show/hide switch's accessible name ("Show Family").</summary>
    public static string ShowName(string calendar) => $"Show {calendar}";

    /// <summary>x:Bind helper: automation ID of an account's expander.</summary>
    public static string ExpanderId(string accountId) => $"CalendarsExpander_{accountId}";

    /// <summary>x:Bind helper: the color button's accessible name.</summary>
    public static string ColorName(string calendar) => $"Color for {calendar}";

    /// <summary>x:Bind helper: where the calendar's color comes from.</summary>
    public static string ColorSource(CalendarInfo info) => info.LeafColor is null ? "Google's color" : "Custom color";

    /// <summary>x:Bind helper: the swatch brush for a hex color.</summary>
    public static SolidColorBrush BrushFor(string hex) => LeafBrushes.FromHex(hex);

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Host.CalendarsChanged += OnCalendarsChanged;
        _context.Calendar.AccountFoldingChanged += OnCalendarsChanged;
        _context.Host.Closed += OnHostClosed;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Host.CalendarsChanged -= OnCalendarsChanged;
        _context.Calendar.AccountFoldingChanged -= OnCalendarsChanged;
        _context.Host.Closed -= OnHostClosed;
    }

    // Settings Left On This Page (back to the calendar, or the window closed): the page isn't navigated from, so the
    // app-lifetime view model's event is let go here, or it would keep the whole Settings view alive
    private void OnHostClosed(object? sender, EventArgs e)
    {
        _context.Host.Closed -= OnHostClosed;
        _context.Calendar.AccountFoldingChanged -= OnCalendarsChanged;
    }

    private void OnCalendarsChanged(object? sender, EventArgs e) => Rebuild();

    // An account's expander folds its calendars away or shows them again (the sidebar follows); a change that came from
    // the calendar (the expander following the bound value) is already there and does nothing
    private void OnAccountExpanding(Expander sender, ExpanderExpandingEventArgs args) => SetExpanded(sender, true);

    private void OnAccountCollapsed(Expander sender, ExpanderCollapsedEventArgs args) => SetExpanded(sender, false);

    private void SetExpanded(Expander expander, bool expanded)
    {
        if (expander.Tag is AccountGroup group && group.IsExpanded != expanded)
        {
            _context.Calendar.SetAccountExpanded(group.AccountId, expanded);
        }
    }

    // Rows are matched by calendar ID and updated in place, so focus and an open flyout stay put; only calendars that
    // came or went are added or removed. A color flyout whose calendar went away closes.
    private void Rebuild()
    {
        var groups = _context.Calendar.CalendarGroups();
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountGroup.Sync(_groups, groups);

        if (_colorRow is not null && !_groups.Any(g => g.Calendars.Contains(_colorRow)))
        {
            _colorFlyout?.Hide();
        }
    }

    // Only a real change counts: the switch also raises Toggled when the list is rebuilt
    private void OnVisibleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: CalendarRow row } toggle && toggle.IsOn != row.IsVisible)
        {
            _context.Calendar.SetCalendarHidden(row.Info, hidden: !toggle.IsOn);
        }
    }

    // Palette Flyout: the 24 colors in rows of six, the current one outlined, then "Use Google's color"
    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CalendarRow row } button)
        {
            return;
        }

        var calendar = _context.Calendar;
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 32, ItemHeight = 32 };
        var flyout = new Flyout();
        _colorFlyout = flyout;
        _colorRow = row;
        foreach (var (hex, colorName) in EventColors.CalendarColorNames)
        {
            var color = LeafBrushes.FromHex(hex).Color;
            var ring = LeafBrushes.PrimaryText(ActualTheme == ElementTheme.Dark);
            var current = string.Equals(hex, row.Color, StringComparison.OrdinalIgnoreCase);
            var name = current ? $"{colorName}, current" : colorName;
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(color),
                BorderBrush = ring,
                BorderThickness = new Thickness(current ? 2 : 0),
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
                calendar.SetCalendarColor(row.Info, hex);
            };
            grid.Children.Add(swatch);
        }

        var reset = new HyperlinkButton { Content = "Use Google's color", Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(reset, "ColorReset");
        reset.Click += (_, _) =>
        {
            flyout.Hide();
            calendar.SetCalendarColor(row.Info, null);
        };

        flyout.Content = new StackPanel { Children = { grid, reset } };
        flyout.ShowAt(button);
    }

    // =========================================================================
    // MORE OPTIONS
    // =========================================================================

    // Rename, order, and default reminders; Move up and Move down are off at the ends of the account's list
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CalendarRow row } button)
        {
            return;
        }

        var calendar = _context.Calendar;
        var siblings = _groups.FirstOrDefault(g => g.Calendars.Contains(row))?.Calendars;
        var index = siblings?.IndexOf(row) ?? -1;
        var menu = new MenuFlyout();

        menu.Items.Add(MenuItem("Rename…", "CalendarMenu_Rename", true, () => calendar.Fire(() => RenameCalendarDialog.RenameAsync(this, calendar, row.Info), "calendar.rename.failed")));
        menu.Items.Add(MenuItem("Move up", "CalendarMenu_MoveUp", index > 0, () => calendar.MoveCalendar(row.Info, -1)));
        menu.Items.Add(MenuItem("Move down", "CalendarMenu_MoveDown", siblings is not null && index >= 0 && index < siblings.Count - 1, () => calendar.MoveCalendar(row.Info, 1)));
        menu.Items.Add(MenuItem("Default reminders…", "CalendarMenu_Reminders", true, () => calendar.Fire(() => EditRemindersAsync(row.Info), "calendar.reminders.failed")));
        menu.ShowAt(button);
    }

    private static MenuFlyoutItem MenuItem(string text, string automationId, bool enabled, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => click();
        return item;
    }

    // Default Reminders Dialog: up to five dropdowns of reminder times, each with a remove button, then "Add reminder"
    private async Task EditRemindersAsync(CalendarInfo info)
    {
        var minutes = ReminderTimes.Choices(_context.Calendar.DefaultRemindersOf(info));
        var labels = minutes.Select(ReminderTimes.Label).ToList();
        var boxes = new List<ComboBox>();
        var rows = new StackPanel { Spacing = 4 };

        // Empty State (secondary text while there are no reminders)
        var empty = new TextBlock { Text = "No default reminders.", Style = (Style)Application.Current.Resources["LeafSecondaryTextStyle"] };
        AutomationProperties.SetAutomationId(empty, "RemindersEmpty");

        // Add Reminder (a plain link like the editor's: no fill on hover or press)
        var add = new HyperlinkButton { Content = "Add reminder" };
        add.Resources["HyperlinkButtonBackgroundPointerOver"] = LeafBrushes.Transparent;
        add.Resources["HyperlinkButtonBackgroundPressed"] = LeafBrushes.Transparent;
        AutomationProperties.SetAutomationId(add, "AddReminderButton");

        // Rows Keep Their Own References; Their IDs Follow Their Place
        void Renumber()
        {
            for (var i = 0; i < boxes.Count; i++)
            {
                AutomationProperties.SetAutomationId(boxes[i], string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ReminderRow_{i}"));
                AutomationProperties.SetName(boxes[i], string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Reminder {i + 1}"));
            }

            add.IsEnabled = boxes.Count < CalendarEdits.MaxReminders;
            empty.Visibility = boxes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            rows.Visibility = boxes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void AddRow(int value)
        {
            var box = new ComboBox { ItemsSource = labels, SelectedIndex = Math.Max(0, minutes.ToList().IndexOf(value)), HorizontalAlignment = HorizontalAlignment.Stretch };
            var remove = new Button { Style = (Style)Application.Current.Resources["LeafIconButtonStyle"], Content = new FontIcon { Glyph = "", FontSize = 12 } };
            var line = new Grid { ColumnSpacing = 4, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            AutomationProperties.SetName(remove, "Remove reminder");
            ToolTipService.SetToolTip(remove, "Remove reminder");
            Grid.SetColumn(remove, 1);
            line.Children.Add(box);
            line.Children.Add(remove);
            remove.Click += (_, _) =>
            {
                boxes.Remove(box);
                rows.Children.Remove(line);
                Renumber();
            };

            boxes.Add(box);
            rows.Children.Add(line);
            Renumber();
        }

        foreach (var m in _context.Calendar.DefaultRemindersOf(info).Order().Take(CalendarEdits.MaxReminders))
        {
            AddRow(m);
        }

        add.Click += (_, _) => AddRow(10);
        Renumber();

        var description = new TextBlock { Text = "Google uses these for new events on this calendar.", TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Default reminders",
            Content = new StackPanel { Spacing = 8, Children = { description, empty, rows, add } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        CtrlEnter.Submits(dialog);

        // Focus Starts On The First Reminder (or Add reminder with none), Without The Ring Windows Gave Add reminder
        dialog.Opened += (_, _) => ((Control?)boxes.FirstOrDefault() ?? add).Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _context.Calendar.SetCalendarRemindersAsync(info, [.. boxes.Where(b => b.SelectedIndex >= 0).Select(b => minutes[b.SelectedIndex])]);
    }
}
