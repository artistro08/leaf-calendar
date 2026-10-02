using System.Collections.ObjectModel;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace LeafCalendar.App.Views;

/// <summary>
/// Sidebar: a mini month that jumps the view, the calendars grouped by account (the checkbox shows or
/// hides, drag to reorder), and the Settings button in the bottom-left corner (colors live in
/// Settings › Calendars). Visibility, color, and order are Leaf-only; Google is not changed.
/// </summary>
public sealed partial class SidebarView : UserControl
{
    // Mini Month Rows (the seven columns share the sidebar's width evenly)
    const double MiniCellHeight   = 30;
    const double MiniHeaderHeight = 24;


    readonly TextBlock[] _weekdayLabels = new TextBlock[7];
    readonly Button[] _dayButtons = new Button[42];
    readonly TextBlock[] _dayLabels = new TextBlock[42];
    readonly Border[] _dayBands = new Border[42];
    readonly Style _dayStyle   = (Style)Application.Current.Resources["LeafMiniDayButtonStyle"];
    readonly Style _todayStyle = (Style)Application.Current.Resources["LeafMiniTodayButtonStyle"];

    // Calendar List (bound once and kept in step in place; each realized checkbox with the row and color it was painted for)
    readonly ObservableCollection<AccountGroup> _groups = [];
    readonly Dictionary<CheckBox, (CalendarRow Row, string Color)> _painted = [];
    readonly Dictionary<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem, CalendarRow> _rowItems = [];
    CalendarViewModel? _viewModel;
    DateOnly _miniMonth;

    /// <summary>Creates the sidebar.</summary>
    public SidebarView()
    {
        InitializeComponent();
        CalendarList.ItemsSource = _groups;
        ScrollIndicator.ShowOnHover(ContentScroll);
        BuildMiniMonth();
        ActualThemeChanged += (_, _) => RenderMiniMonth();

        // A Contrast Theme Turning On Or Off Redraws The Mini Month With The System's Colors (raised off the UI thread)
        Loaded   += (_, _) => LeafBrushes.ContrastChanged += OnContrastChanged;
        Unloaded += (_, _) => LeafBrushes.ContrastChanged -= OnContrastChanged;
    }

    void OnContrastChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RenderMiniMonth);

    /// <summary>The share-availability button was clicked.</summary>
    public event EventHandler? ShareAvailabilityRequested;

    /// <summary>x:Bind helper: automation ID of a calendar's visibility checkbox.</summary>
    public static string ToggleId(CalendarInfo info) => $"CalendarToggle_{info.Id}";

    /// <summary>Connects the sidebar to the page's view model.</summary>
    public void Attach(CalendarViewModel viewModel)
    {
        _viewModel = viewModel;

        _viewModel.CalendarsChanged      += OnCalendarsChanged;
        _viewModel.AccountFoldingChanged += OnCalendarsChanged;
        _viewModel.PropertyChanged       += OnViewModelPropertyChanged;
        _viewModel.LayoutChanged         += OnLayoutChanged;

        UpdateCalendarList();
        ShowMonthOf(ViewNavigator.MiniMonthAnchor(_viewModel.Mode, _viewModel.PeriodStart, _viewModel.VisibleColumns, _viewModel.Today));
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.CalendarsChanged      -= OnCalendarsChanged;
            _viewModel.AccountFoldingChanged -= OnCalendarsChanged;
            _viewModel.PropertyChanged       -= OnViewModelPropertyChanged;
            _viewModel.LayoutChanged         -= OnLayoutChanged;
        }

        _viewModel = null;
    }

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateCalendarList();

    /// <summary>Dims the mini month, the calendar list, and the footer's icons while the window isn't the active one.</summary>
    public void SetWindowActive(bool active)
    {
        var opacity = active ? 1 : CalendarPage.InactiveOpacity;
        MiniMonth.Opacity     = opacity;
        ContentScroll.Opacity = opacity;
        Footer.Opacity        = opacity;
    }

    // A new day, week start, or time zone moves today's circle and the weekday names
    void OnLayoutChanged(object? sender, EventArgs e) => RenderMiniMonth();

    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.PeriodStart) && _viewModel is not null)
        {
            ShowMonthOf(ViewNavigator.MiniMonthAnchor(_viewModel.Mode, _viewModel.PeriodStart, _viewModel.VisibleColumns, _viewModel.Today));
        }
    }

    // Every change lands here (a sync, a checkbox, a color, a reorder, an account added or removed). Rows are matched by
    // calendar ID and updated in place, so a checkbox only checks or unchecks, and only calendars that came or went animate.
    void UpdateCalendarList()
    {
        if (_viewModel is null)
        {
            return;
        }

        AccountGroup.Sync(_groups, _viewModel.CalendarGroups());

        // Forget Checkboxes Whose Calendar Left (a removed calendar or account), Repaint Those Whose Color Changed
        var shown = _groups.SelectMany(g => g.Calendars).ToHashSet();
        foreach (var (box, painted) in _painted.ToList())
        {
            if (!shown.Contains(painted.Row))
            {
                _painted.Remove(box);
            }
            else if (!string.Equals(painted.Row.Color, painted.Color, StringComparison.OrdinalIgnoreCase))
            {
                Paint(box, painted.Row);
            }
        }

        // A Renamed Calendar's Row Reads Its New Name (and rows whose calendar left are forgotten)
        foreach (var (item, row) in _rowItems.ToList())
        {
            if (!shown.Contains(row))
            {
                _rowItems.Remove(item);
                continue;
            }

            AutomationProperties.SetName(item, row.Name);
        }
    }

    // =========================================================================
    // MINI MONTH
    // =========================================================================

    // Seven even columns: a weekday row, then six weeks of day buttons
    void BuildMiniMonth()
    {
        for (var c = 0; c < 7; c++)
        {
            MiniMonthDays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        MiniMonthDays.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MiniHeaderHeight) });
        for (var r = 0; r < 6; r++)
        {
            MiniMonthDays.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MiniCellHeight) });
        }

        // Weekday Names
        for (var c = 0; c < 7; c++)
        {
            var label = new TextBlock
            {
                FontSize            = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, c);
            MiniMonthDays.Children.Add(label);
            _weekdayLabels[c] = label;
        }

        // Days (each on a band cell that joins into one strip across the visible days)
        for (var i = 0; i < 42; i++)
        {
            var band = new Border { Height = 28 };
            Grid.SetRow(band, 1 + i / 7);
            Grid.SetColumn(band, i % 7);
            MiniMonthDays.Children.Add(band);
            _dayBands[i] = band;

            var label  = new TextBlock();
            var button = new Button { Style = _dayStyle, Content = label };
            _dayLabels[i] = label;
            button.Click += OnMiniDayClick;
            Grid.SetRow(button, 1 + i / 7);
            Grid.SetColumn(button, i % 7);
            MiniMonthDays.Children.Add(button);
            _dayButtons[i] = button;
        }
    }

    void ShowMonthOf(DateOnly date)
    {
        _miniMonth = ViewNavigator.MonthStartOf(date);
        RenderMiniMonth();
    }

    // Today on an accent circle, the visible days on a soft band, other months' days dimmed
    void RenderMiniMonth()
    {
        if (_miniMonth == default)
        {
            return;
        }

        var dark      = ActualTheme == ElementTheme.Dark;
        var weekStart = _viewModel?.Settings.WeekStart ?? DayOfWeek.Sunday;
        var today     = _viewModel?.Today ?? DateOnly.FromDateTime(DateTime.Today);
        var first     = ViewNavigator.WeekStartOf(_miniMonth, weekStart);
        var shown     = _viewModel is { Mode: not Core.Settings.CalendarViewMode.Month } vm ? (Start: vm.PeriodStart, End: vm.PeriodStart.AddDays(vm.VisibleColumns)) : default;

        MiniMonthTitle.Text = ViewNavigator.MonthTitle(_miniMonth);

        for (var c = 0; c < 7; c++)
        {
            _weekdayLabels[c].Text       = TimeLabels.WeekdayShort(first.AddDays(c))[..2];
            _weekdayLabels[c].Foreground = LeafBrushes.SecondaryText(dark);
        }

        for (var i = 0; i < 42; i++)
        {
            var date    = first.AddDays(i);
            var button  = _dayButtons[i];
            var text    = _dayLabels[i];
            var isToday = date == today;

            text.Text       = date.Day.ToString(System.Globalization.CultureInfo.InvariantCulture);
            text.FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal;
            text.Opacity    = isToday || date.Month == _miniMonth.Month ? 1 : 0.45;
            button.Tag      = date.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

            // Visible-Days Band (rounded where the strip starts and ends, or wraps to the next row)
            var inBand   = date >= shown.Start && date < shown.End;
            var column   = i % 7;
            var roundL   = date == shown.Start || column == 0 ? 14 : 0;
            var roundR   = date.AddDays(1) == shown.End || column == 6 ? 14 : 0;
            var band     = _dayBands[i];
            band.Background   = inBand ? LeafBrushes.FromHex(dark ? "#14FFFFFF" : "#0F000000") : null;
            band.CornerRadius = new CornerRadius(roundL, roundR, roundR, roundL);

            // Today (an accent circle from its style, in the theme's own accent and on-accent colors). Other days
            // carry their text color themselves, so no hover or press state can repaint the number.
            button.Style = isToday ? _todayStyle : _dayStyle;
            if (isToday)
            {
                text.ClearValue(TextBlock.ForegroundProperty);
            }
            else
            {
                text.Foreground = LeafBrushes.PrimaryText(dark);
            }

            AutomationProperties.SetAutomationId(button, $"MiniDay_{date:yyyy-MM-dd}");
            AutomationProperties.SetName(button, TimeLabels.LongDate(date));
        }
    }

    void OnMiniMonthPreviousClick(object sender, RoutedEventArgs e) => ShowMonthOf(_miniMonth.AddMonths(-1));

    void OnMiniMonthNextClick(object sender, RoutedEventArgs e) => ShowMonthOf(_miniMonth.AddMonths(1));

    void OnMiniDayClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: string tag })
        {
            return;
        }

        _viewModel.NavigateTo(DateOnly.ParseExact(tag, "O", System.Globalization.CultureInfo.InvariantCulture));
    }

    // An account header folds its calendars away or shows them again (Settings › Calendars follows)
    void OnAccountHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: AccountGroup group })
        {
            _viewModel.SetAccountExpanded(group.AccountId, !group.IsExpanded);
        }
    }

    void OnVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is CheckBox { Tag: CalendarRow row } box)
        {
            _viewModel.SetCalendarHidden(row.Info, hidden: box.IsChecked != true);
        }
    }

    // Color Each Checkbox With Its Calendar's Color: only the 20 px box's own fill and stroke. Setting the
    // CheckBox's Background paints its whole 32 px-tall root grid, which bled past the box when unchecked.
    void OnCalendarRowChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Grid { Children: [CheckBox box, ..] })
        {
            return;
        }

        // A recycled row no longer belongs to its calendar
        if (args.InRecycleQueue || args.Item is not CalendarRow row)
        {
            _painted.Remove(box);
            _rowItems.Remove(args.ItemContainer);
            args.ItemContainer.ContextFlyout = null;
            return;
        }

        // Narrator Reads The Row (the list item Tab lands on) By The Calendar's Name
        _rowItems[args.ItemContainer] = row;
        AutomationProperties.SetName(args.ItemContainer, row.Name);

        Paint(box, row);
        args.ItemContainer.ContextFlyout = MenuFor(row);
    }

    // =========================================================================
    // CALENDAR MENU
    // =========================================================================

    // The right-click menu of one row; the row is captured here, so nothing is read back from the item
    MenuFlyout MenuFor(CalendarRow row)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Rename…", "", "CalendarMenu_Rename", () =>
        {
            if (_viewModel is { } vm)
            {
                vm.Fire(() => RenameCalendarDialog.RenameAsync(this, vm, row.Info), "calendar.rename.failed");
            }
        }));
        menu.Items.Add(MenuItem("Show upcoming events", "", "CalendarMenu_Upcoming", () => _viewModel?.ShowUpcomingFor(row.Info)));
        menu.Items.Add(MenuItem("Change color…", "", "CalendarMenu_Color", () => _viewModel?.OpenSettings?.Invoke(SettingsSection.Calendars)));
        return menu;
    }

    static MenuFlyoutItem MenuItem(string text, string glyph, string automationId, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => click();
        return item;
    }

    void Paint(CheckBox box, CalendarRow row)
    {
        _painted[box] = (row, row.Color);

        // Hover and press lighten the color like Fluent's accent (90% and 80%), so the row hover shows on a checked box too
        var brush   = LeafBrushes.FromHex(row.Color);
        var hover   = new SolidColorBrush(brush.Color) { Opacity = 0.9 };
        var pressed = new SolidColorBrush(brush.Color) { Opacity = 0.8 };
        foreach (var (key, value) in new[]
        {
            ("CheckBoxCheckBackgroundFillChecked", brush),
            ("CheckBoxCheckBackgroundFillCheckedPointerOver", hover),
            ("CheckBoxCheckBackgroundFillCheckedPressed", pressed),
            ("CheckBoxCheckBackgroundStrokeChecked", brush),
            ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", hover),
            ("CheckBoxCheckBackgroundStrokeCheckedPressed", pressed),
            ("CheckBoxCheckBackgroundStrokeUnchecked", brush),
            ("CheckBoxCheckBackgroundStrokeUncheckedPointerOver", brush),
            ("CheckBoxCheckBackgroundStrokeUncheckedPressed", brush),
        })
        {
            box.Resources[key] = value;
        }

        // The unchecked outline reads its brush once, when the template is applied, so re-apply it
        box.Template = null;
        box.ClearValue(Control.TemplateProperty);

        // A re-applied template starts in no visual state, and drew every box with the indeterminate dash until the
        // pointer passed over it: put it in its real state now, or as soon as it's back on screen
        if (box.IsLoaded && box.ApplyTemplate())
        {
            ShowCheckBoxHover(box, hover: false);
        }
        else
        {
            box.Loaded -= OnRepaintedBoxLoaded;
            box.Loaded += OnRepaintedBoxLoaded;
        }
    }

    static void OnRepaintedBoxLoaded(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        box.Loaded -= OnRepaintedBoxLoaded;
        box.ApplyTemplate();
        ShowCheckBoxHover(box, hover: false);
    }

    // =========================================================================
    // ROW HOVER
    // =========================================================================

    // One hover for the whole row: its background fades in, and the checkbox shows its own hover state (the
    // checkbox only knows about the pointer over itself, so the row drives it). Enter and exit bubble up from
    // the checkbox and its name too, so an exit only counts once the pointer is really outside the row.
    void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => SetRowHover(sender, hover: true);

    void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row)
        {
            return;
        }

        var at = e.GetCurrentPoint(row).Position;
        SetRowHover(sender, hover: at.X >= 0 && at.Y >= 0 && at.X < row.ActualWidth && at.Y < row.ActualHeight);
    }

    static void SetRowHover(object sender, bool hover)
    {
        if (sender is not Grid { Children: [CheckBox box, ..] } row)
        {
            return;
        }

        row.Background = hover ? LeafBrushes.Hover(row.ActualTheme == ElementTheme.Dark) : LeafBrushes.Transparent;
        ShowCheckBoxHover(box, hover);
    }

    static void ShowCheckBoxHover(CheckBox box, bool hover) =>
        VisualStateManager.GoToState(box, (box.IsChecked == true ? "Checked" : "Unchecked") + (hover ? "PointerOver" : "Normal"), true);

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

    void OnSettingsClick(object sender, RoutedEventArgs e) => _viewModel?.OpenSettings?.Invoke(SettingsSection.General);

    void OnShareClick(object sender, RoutedEventArgs e) => ShareAvailabilityRequested?.Invoke(this, EventArgs.Empty);
}
