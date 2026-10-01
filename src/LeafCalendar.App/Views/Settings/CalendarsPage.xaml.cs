using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
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
    SettingsContext _context = null!;
    List<AccountGroup> _groups = [];
    Flyout? _colorFlyout;

    /// <summary>Creates the page.</summary>
    public CalendarsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <summary>x:Bind helper: automation ID of a calendar's color button.</summary>
    public static string ColorId(CalendarInfo info) => $"CalendarColor_{info.Id}";

    /// <summary>x:Bind helper: automation ID of a calendar's show/hide switch.</summary>
    public static string VisibleId(CalendarInfo info) => $"CalendarVisible_{info.Id}";

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
        _context.Window.CalendarsChanged += OnCalendarsChanged;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Window.CalendarsChanged -= OnCalendarsChanged;

    void OnCalendarsChanged(object? sender, EventArgs e) => Rebuild();

    // Same accounts and calendars in the same order: update the rows in place, so focus and an open flyout stay put.
    // Otherwise (a sync added or removed one, or they were reordered) build the list again.
    void Rebuild()
    {
        var groups = _context.Calendar.CalendarGroups();
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AccountGroup.UpdateInPlace(_groups, groups))
        {
            return;
        }

        _colorFlyout?.Hide();
        _groups               = groups;
        GroupList.ItemsSource = groups;
    }

    // Only a real change counts: the switch also raises Toggled when the list is rebuilt
    void OnVisibleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: CalendarRow row } toggle && toggle.IsOn != row.IsVisible)
        {
            _context.Calendar.SetCalendarHidden(row.Info, hidden: !toggle.IsOn);
        }
    }

    // Palette Flyout: the 24 colors in rows of six, the current one outlined, then "Use Google's color"
    void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CalendarRow row } button)
        {
            return;
        }

        var calendar = _context.Calendar;
        var grid     = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 32, ItemHeight = 32 };
        var flyout   = new Flyout();
        _colorFlyout = flyout;
        foreach (var hex in EventColors.CalendarPalette)
        {
            var swatch = new Button
            {
                Width           = 26,
                Height          = 26,
                Padding         = new Thickness(0),
                CornerRadius    = new CornerRadius(13),
                Background      = LeafBrushes.FromHex(hex),
                BorderThickness = new Thickness(string.Equals(hex, row.Color, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
            };
            AutomationProperties.SetAutomationId(swatch, $"ColorSwatch_{hex[1..]}");
            AutomationProperties.SetName(swatch, hex);
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
}
