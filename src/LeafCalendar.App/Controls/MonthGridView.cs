using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Month view.
/// </summary>
/// <remarks>
/// Week rows scroll vertically in a virtualized <see cref="ItemsRepeater"/> of about 10 years of weeks.
/// Six rows fill the screen. When scrolling stops, the view snaps to a row edge; the month containing
/// the middle of the third row becomes the "focused" month (the title, with other months' days
/// dimmed). Pagers jump a month. Navigation reports the new weeks right away.
/// </remarks>
public sealed partial class MonthGridView : Grid, IDisposable
{
    /// <summary>Smallest week row height.</summary>
    public const double MinRowHeight = 96;

    /// <summary>Chip height (one lane).</summary>
    public const double ChipHeight = 20;

    /// <summary>Space for the day number at the top of a cell.</summary>
    public const double DayNumberHeight = 26;

    const int WeeksEachSide = 260;

    readonly CalendarViewModel _vm;
    readonly Grid _weekdays = new() { Height = 32 };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ItemsRepeater _repeater = new() { Layout = new StackLayout { Orientation = Orientation.Vertical }, VerticalCacheLength = 2 };
    readonly HashSet<WeekRow> _rows = [];
    List<WeekItem> _weeks = [];
    bool _disposed;
    int _firstIndex;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public MonthGridView(CalendarViewModel vm)
    {
        _vm = vm;
        AutomationProperties.SetAutomationId(this, "MonthGrid");
        AutomationProperties.SetName(this, "Month grid");

        // Rows
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_weekdays);

        // Weeks
        _repeater.ItemTemplate = new WeekRowFactory(this);
        _repeater.ElementPrepared += (_, e) => _rows.Add((WeekRow)e.Element);
        _repeater.ElementClearing += (_, e) => _rows.Remove((WeekRow)e.Element);
        _scroll.Content = _repeater;
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        // Scrolling And Theme
        _scroll.ViewChanged += OnViewChanged;
        _scroll.SizeChanged += (_, _) => Relayout();
        ActualThemeChanged  += (_, _) =>
        {
            BuildWeekdayHeader();
            RenderAll();
        };

        // View Model
        _vm.OccurrencesChanged += OnOccurrencesChanged;
        _vm.LayoutChanged      += OnLayoutChanged;
        _vm.NavigateRequested  += OnNavigateRequested;

        FocusMonth = ViewNavigator.MonthStartOf(_vm.PeriodStart);
        BuildWeeks(_vm.PeriodStart);
        _firstIndex = WeekIndexOf(FocusMonth);
    }

    /// <summary>The view model.</summary>
    public CalendarViewModel ViewModel => _vm;

    /// <summary>Week row height.</summary>
    public double RowHeight { get; private set; } = 120;

    /// <summary>Day column width.</summary>
    public double ColumnWidth => Math.Max(40, (_scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth) / ViewNavigator.VisibleColumnCount(Core.Settings.CalendarViewMode.Month, 0, _vm.Settings.ShowWeekends));

    /// <summary>The month the title shows.</summary>
    public DateOnly FocusMonth { get; private set; }

    /// <summary>True in the dark theme.</summary>
    public bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>The visible days of the week starting <paramref name="weekStart"/>.</summary>
    public IReadOnlyList<DateOnly> ColumnDates(DateOnly weekStart) =>
        [.. Enumerable.Range(0, 7).Select(weekStart.AddDays).Where(d => _vm.Settings.ShowWeekends || !ViewNavigator.IsWeekend(d))];

    /// <summary>Scrolls so the month containing <paramref name="date"/> fills the view.</summary>
    public void ScrollToDate(DateOnly date, bool animate)
    {
        var index = WeekIndexOf(ViewNavigator.MonthStartOf(date));
        if (index < 0)
        {
            BuildWeeks(date);
            index = WeekIndexOf(ViewNavigator.MonthStartOf(date));
        }

        _firstIndex = index;
        _scroll.ChangeView(null, index * RowHeight, null, !animate);
        ReportVisible();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _vm.OccurrencesChanged -= OnOccurrencesChanged;
        _vm.LayoutChanged      -= OnLayoutChanged;
        _vm.NavigateRequested  -= OnNavigateRequested;
    }

    // =========================================================================
    // LAYOUT
    // =========================================================================

    void BuildWeeks(DateOnly around)
    {
        var start = ViewNavigator.WeekStartOf(around, _vm.Settings.WeekStart);
        _weeks = [.. Enumerable.Range(-WeeksEachSide, WeeksEachSide * 2 + 1).Select(i => new WeekItem(start.AddDays(i * 7)))];
        _repeater.ItemsSource = _weeks;
    }

    int WeekIndexOf(DateOnly date)
    {
        var weekStart = ViewNavigator.WeekStartOf(date, _vm.Settings.WeekStart);
        return _weeks.FindIndex(w => w.WeekStart == weekStart);
    }

    // Resizes rows for the viewport, then keeps the same first week (reading _firstIndex when the
    // queued scroll runs, so a navigation that lands in between wins)
    void Relayout()
    {
        var viewport = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight;
        if (_disposed || viewport <= 0)
        {
            return;
        }

        RowHeight = Math.Max(MinRowHeight, viewport / 6);
        BuildWeekdayHeader();
        _repeater.InvalidateMeasure();
        RenderAll();

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            _scroll.ChangeView(null, _firstIndex * RowHeight, null, true);
            ReportVisible();
        });
    }

    void BuildWeekdayHeader()
    {
        _weekdays.Children.Clear();
        _weekdays.ColumnDefinitions.Clear();

        var dates = ColumnDates(ViewNavigator.WeekStartOf(_vm.Today, _vm.Settings.WeekStart));
        for (var c = 0; c < dates.Count; c++)
        {
            _weekdays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var name = new TextBlock { Text = TimeLabels.WeekdayShort(dates[c]), FontSize = 12, Margin = new Thickness(10, 8, 0, 0), Foreground = LeafBrushes.SecondaryText(IsDark) };
            SetColumn(name, c);
            _weekdays.Children.Add(name);
        }
    }

    void RenderAll()
    {
        foreach (var row in _rows)
        {
            row.Render();
        }
    }

    void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            return;
        }

        // Snap To A Row
        var index  = (int)Math.Round(_scroll.VerticalOffset / RowHeight);
        var target = index * RowHeight;
        if (Math.Abs(target - _scroll.VerticalOffset) > 0.5)
        {
            _scroll.ChangeView(null, target, null, false);
            return;
        }

        _firstIndex = index;
        ReportVisible();
    }

    void ReportVisible()
    {
        if (_disposed)
        {
            return;
        }

        _firstIndex = Math.Clamp(_firstIndex, 0, _weeks.Count - 1);
        var first   = _weeks[_firstIndex].WeekStart;
        var focus   = _weeks[Math.Min(_firstIndex + 2, _weeks.Count - 1)].WeekStart.AddDays(3);
        var month   = ViewNavigator.MonthStartOf(focus);

        if (month != FocusMonth)
        {
            FocusMonth = month;
            RenderAll();
        }

        _vm.OnViewScrolled(first, first.AddDays(42), focus);
    }

    // =========================================================================
    // VIEW MODEL EVENTS
    // =========================================================================

    void OnOccurrencesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RenderAll();
    }

    void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        BuildWeeks(FocusMonth);
        _firstIndex = WeekIndexOf(FocusMonth);
        Relayout();
    }

    void OnNavigateRequested(object? sender, DateOnly date)
    {
        if (_disposed)
        {
            return;
        }

        ScrollToDate(date, animate: true);
    }

    // =========================================================================
    // RECYCLING
    // =========================================================================

    /// <summary>A week in the list (a class, so WinRT can hold it).</summary>
    public sealed class WeekItem(DateOnly weekStart)
    {
        /// <summary>First day of the week.</summary>
        public DateOnly WeekStart { get; } = weekStart;
    }

    sealed partial class WeekRowFactory(MonthGridView owner) : IElementFactory
    {
        readonly Stack<WeekRow> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var row = _pool.Count > 0 ? _pool.Pop() : new WeekRow(owner);
            row.Bind(((WeekItem)args.Data).WeekStart);
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => _pool.Push((WeekRow)args.Element);
    }
}
