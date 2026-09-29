using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// Sidebar: a mini month that jumps the view, the calendars grouped by account (checkbox shows or
/// hides, the name opens a color picker, drag to reorder), the booking pages link, and Accounts.
/// Visibility, color, and order are Leaf-only; Google is not changed.
/// </summary>
public sealed partial class SidebarView : UserControl
{
    CalendarViewModel? _viewModel;
    Action? _openAccounts;
    bool _updatingMiniMonth;

    /// <summary>Creates the sidebar.</summary>
    public SidebarView() => InitializeComponent();

    /// <summary>x:Bind helper: a brush for a hex color.</summary>
    public static SolidColorBrush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>x:Bind helper: automation ID of a calendar's visibility checkbox.</summary>
    public static string ToggleId(CalendarInfo info) => $"CalendarToggle_{info.Id}";

    /// <summary>x:Bind helper: automation ID of a calendar's name/color button.</summary>
    public static string ColorId(CalendarInfo info) => $"CalendarColor_{info.Id}";

    /// <summary>Connects the sidebar to the page's view model.</summary>
    public void Attach(CalendarViewModel viewModel, Action openAccounts)
    {
        _viewModel    = viewModel;
        _openAccounts = openAccounts;

        _viewModel.CalendarsChanged += OnCalendarsChanged;
        _viewModel.PropertyChanged  += OnViewModelPropertyChanged;

        MiniMonth.FirstDayOfWeek = (Windows.Globalization.DayOfWeek)(int)_viewModel.Settings.WeekStart;
        Rebuild();
        ShowMonthOf(_viewModel.PeriodStart);
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.CalendarsChanged -= OnCalendarsChanged;
            _viewModel.PropertyChanged  -= OnViewModelPropertyChanged;
        }

        _viewModel    = null;
        _openAccounts = null;
    }

    void OnCalendarsChanged(object? sender, EventArgs e) => Rebuild();

    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.PeriodStart) && _viewModel is not null)
        {
            ShowMonthOf(_viewModel.PeriodStart);
        }
    }

    void Rebuild()
    {
        if (_viewModel is null)
        {
            return;
        }

        CalendarList.ItemsSource = _viewModel.Calendars
            .GroupBy(c => c.AccountId)
            .Select(g => new AccountGroup(_viewModel.AccountEmails.GetValueOrDefault(g.Key, g.Key), g.Select(c => new CalendarRow(c))))
            .ToList();
    }

    void ShowMonthOf(DateOnly date)
    {
        _updatingMiniMonth = true;
        MiniMonth.SetDisplayDate(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)));
        _updatingMiniMonth = false;
    }

    void OnMiniMonthSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_updatingMiniMonth || _viewModel is null || args.AddedDates.Count == 0)
        {
            return;
        }

        _viewModel.NavigateTo(DateOnly.FromDateTime(args.AddedDates[0].Date));
        sender.SelectedDates.Clear();
    }

    void OnVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is CheckBox { Tag: CalendarRow row } box)
        {
            _viewModel.SetCalendarHidden(row.Info, hidden: box.IsChecked != true);
        }
    }

    void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: CalendarRow row } button)
        {
            return;
        }

        // Palette Flyout
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 32, ItemHeight = 32 };
        var flyout = new Flyout();
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
                _viewModel.SetCalendarColor(row.Info, hex);
            };
            grid.Children.Add(swatch);
        }

        var reset = new HyperlinkButton { Content = "Use Google's color", Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(reset, "ColorReset");
        reset.Click += (_, _) =>
        {
            flyout.Hide();
            _viewModel.SetCalendarColor(row.Info, null);
        };

        flyout.Content = new StackPanel { Children = { grid, reset } };
        flyout.ShowAt(button);
    }

    void OnCalendarsReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_viewModel is null || sender.ItemsSource is not IEnumerable<CalendarRow> rows)
        {
            return;
        }

        var list = rows.ToList();
        if (list.Count > 0)
        {
            _viewModel.ReorderCalendars(list[0].Info.AccountId, [.. list.Select(r => r.Info.Id)]);
        }
    }

    void OnAccountsClick(object sender, RoutedEventArgs e) => _openAccounts?.Invoke();
}
