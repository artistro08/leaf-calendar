using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Day, week, and N-day view.
/// </summary>
/// <remarks>
/// The layout is a 2×2 grid. Top left is the corner (zone labels, week number, all-day expand).
/// Top right is the day headers and the all-day row. Bottom left is the hour gutter. Bottom right is
/// the day columns. The day columns live in an <see cref="ItemsRepeater"/> over a <see cref="DayStrip"/>
/// of about 8 years, so only the visible days plus one screen on each side are built, and they're
/// recycled as you scroll. The header and gutter follow the body's scrolling (they never scroll on
/// their own, so they can't fight the body's animations). When scrolling stops, the view snaps to a
/// day edge and tells the view model which days are showing (that loads data and sets the title).
/// Navigation reports the new days right away. Pagers and "today" scroll with animation.
/// </remarks>
public sealed partial class TimeGridView : Grid, IDisposable
{
    /// <summary>Day header height.</summary>
    public const double DayHeaderHeight = 52;

    /// <summary>All-day lane height.</summary>
    public const double AllDayLaneHeight = 22;

    /// <summary>Lanes shown before "expand".</summary>
    public const int MaxCollapsedLanes = 3;

    /// <summary>Width of one time-zone column in the gutter.</summary>
    public const double ZoneColumnWidth = 56;

    // ponytail: ~8 years of days; rebuild the strip around the target date if someone scrolls past the ends
    const int StripDaysEachSide = 1500;

    readonly CalendarViewModel _vm;
    readonly ScrollViewer _headerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ScrollViewer _gutterScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ScrollViewer _bodyScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, ZoomMode = ZoomMode.Disabled };
    // One row of fixed-size cells: positions are exact (index × width), unlike StackLayout's estimates,
    // which drift by weeks when jumping deep into the strip after the column width changes
    readonly UniformGridLayout _headerLayout = new() { Orientation = Orientation.Vertical, MaximumRowsOrColumns = 1 };
    readonly UniformGridLayout _bodyLayout = new() { Orientation = Orientation.Vertical, MaximumRowsOrColumns = 1 };
    readonly ItemsRepeater _headerRepeater = new() { HorizontalCacheLength = 2 };
    readonly ItemsRepeater _bodyRepeater = new() { HorizontalCacheLength = 2 };
    readonly Grid _headerContent = new();
    readonly AllDayCanvas _allDay;
    readonly TimeZoneGutter _gutter;
    readonly StackPanel _zoneLabels = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBlock _weekNumber = new() { FontSize = 11, Margin = new Thickness(8, 6, 0, 0) };
    readonly Button _allDayExpand = new() { Padding = new Thickness(4), Background = LeafBrushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    readonly DispatcherQueueTimer _clock;
    readonly HashSet<DayColumn> _columns = [];
    readonly HashSet<DayHeaderCell> _headers = [];
    DayStrip _strip = null!;
    bool _allDayExpanded;
    bool _disposed;
    bool _initialized;
    int _firstIndex;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public TimeGridView(CalendarViewModel vm)
    {
        _vm     = vm;
        _allDay = new AllDayCanvas(this);
        _gutter = new TimeZoneGutter(this);
        AutomationProperties.SetAutomationId(this, "TimeGrid");
        AutomationProperties.SetName(this, "Time grid");

        // Rows And Columns
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Corner
        Corner = new Grid();
        Corner.Children.Add(_weekNumber);
        Corner.Children.Add(_zoneLabels);
        Corner.Children.Add(_allDayExpand);
        _allDayExpand.Content = new FontIcon { Glyph = "", FontSize = 10 };
        AutomationProperties.SetAutomationId(_allDayExpand, "AllDayExpand");
        AutomationProperties.SetName(_allDayExpand, "Show all all-day events");
        _allDayExpand.Click += (_, _) =>
        {
            _allDayExpanded = !_allDayExpanded;
            RenderAllDay();
        };
        Children.Add(Corner);

        // Header (day names + all-day row)
        _headerRepeater.Layout = _headerLayout;
        _bodyRepeater.Layout   = _bodyLayout;
        _headerRepeater.ItemTemplate = new DayHeaderFactory(this);
        _headerRepeater.ElementPrepared += (_, e) => _headers.Add((DayHeaderCell)e.Element);
        _headerRepeater.ElementClearing += (_, e) => _headers.Remove((DayHeaderCell)e.Element);
        _headerContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayHeaderHeight) });
        _headerContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _headerContent.Children.Add(_headerRepeater);
        SetRow(_allDay, 1);
        _headerContent.Children.Add(_allDay);
        _headerScroll.Content = _headerContent;
        SetColumn(_headerScroll, 1);
        Children.Add(_headerScroll);

        // Gutter
        _gutterScroll.Content = _gutter;
        SetRow(_gutterScroll, 1);
        Children.Add(_gutterScroll);

        // Body
        _bodyRepeater.ItemTemplate = new DayColumnFactory(this);
        _bodyRepeater.ElementPrepared += (_, e) => _columns.Add((DayColumn)e.Element);
        _bodyRepeater.ElementClearing += (_, e) => _columns.Remove((DayColumn)e.Element);
        _bodyScroll.Content = _bodyRepeater;
        SetRow(_bodyScroll, 1);
        SetColumn(_bodyScroll, 1);
        Children.Add(_bodyScroll);

        // Scroll Sync And Snapping
        _bodyScroll.ViewChanging += OnBodyViewChanging;
        _bodyScroll.ViewChanged  += OnBodyViewChanged;
        _bodyScroll.SizeChanged  += (_, _) => Relayout();

        // View Model
        _vm.OccurrencesChanged    += OnOccurrencesChanged;
        _vm.LayoutChanged         += OnLayoutChanged;
        _vm.NavigateRequested     += OnNavigateRequested;
        _vm.ScrollToTimeRequested += OnScrollToTimeRequested;
        ActualThemeChanged        += (_, _) => RenderRealized();

        // Now Line Clock
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromMinutes(1);
        _clock.Tick += (_, _) => RenderToday();
        _clock.Start();

        BuildStrip(_vm.PeriodStart);
    }

    /// <summary>The top-left corner above the gutter.</summary>
    public Grid Corner { get; }

    /// <summary>The view model.</summary>
    public CalendarViewModel ViewModel => _vm;

    /// <summary>Width of one day column.</summary>
    public double ColumnWidth { get; private set; } = 120;

    /// <summary>Height of one hour.</summary>
    public double HourHeight => _vm.Settings.HourHeight;

    /// <summary>Height of the 24-hour body.</summary>
    public double BodyHeight => HourHeight * 24;

    /// <summary>True in the dark theme.</summary>
    public bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>Scrolls so <paramref name="date"/> is the first visible column.</summary>
    public void ScrollToDate(DateOnly date, bool animate)
    {
        if (!_strip.Contains(date))
        {
            BuildStrip(date);
        }

        _firstIndex = _strip.IndexOf(date);
        _bodyScroll.ChangeView(_firstIndex * ColumnWidth, null, null, !animate);
        ReportVisible();
    }

    /// <summary>Scrolls vertically so <paramref name="instant"/> sits a third of the way down.</summary>
    public void ScrollToTime(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _vm.Zone);
        var y     = local.TimeOfDay.TotalHours * HourHeight - _bodyScroll.ViewportHeight / 3;
        _bodyScroll.ChangeView(null, Math.Max(0, y), null, false);
    }

    /// <summary>Repaints every built column, header, the all-day row, and the gutter.</summary>
    public void RenderRealized()
    {
        foreach (var column in _columns)
        {
            column.Render();
        }

        foreach (var header in _headers)
        {
            header.Bind(header.Date);
        }

        RenderAllDay();
        RenderCorner();
        _gutter.Render(_strip[_firstIndex]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _clock.Stop();
        _vm.OccurrencesChanged    -= OnOccurrencesChanged;
        _vm.LayoutChanged         -= OnLayoutChanged;
        _vm.NavigateRequested     -= OnNavigateRequested;
        _vm.ScrollToTimeRequested -= OnScrollToTimeRequested;
    }

    // =========================================================================
    // LAYOUT
    // =========================================================================

    void BuildStrip(DateOnly around)
    {
        _strip = new DayStrip(around, StripDaysEachSide, StripDaysEachSide, skipWeekends: !_vm.Settings.ShowWeekends);
        var items = Enumerable.Range(0, _strip.Count).Select(i => new DayItem(_strip[i])).ToList();
        _headerRepeater.ItemsSource = items;
        _bodyRepeater.ItemsSource   = items;
        _firstIndex = _strip.IndexOf(around);
    }

    // Resizes columns for the viewport, then keeps the same first day (reading _firstIndex when the
    // queued scroll runs, so a navigation that lands in between wins)
    void Relayout()
    {
        var viewport = _bodyScroll.ViewportWidth > 0 ? _bodyScroll.ViewportWidth : _bodyScroll.ActualWidth;
        if (_disposed || viewport <= 0)
        {
            return;
        }

        ColumnWidth = Math.Max(48, viewport / _vm.VisibleColumns);
        _bodyRepeater.Height        = BodyHeight;
        _headerRepeater.Height      = DayHeaderHeight;
        _bodyLayout.MinItemWidth    = ColumnWidth;
        _bodyLayout.MinItemHeight   = BodyHeight;
        _headerLayout.MinItemWidth  = ColumnWidth;
        _headerLayout.MinItemHeight = DayHeaderHeight;
        _bodyRepeater.InvalidateMeasure();
        _headerRepeater.InvalidateMeasure();
        RenderRealized();

        // First Layout: also jump to 7:30 AM
        double? top = _initialized ? null : Math.Max(0, 7.5 * HourHeight - 20);
        _initialized = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            _bodyScroll.ChangeView(_firstIndex * ColumnWidth, top, null, true);
            ReportVisible();
        });
    }

    void OnBodyViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        _headerScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
        _gutterScroll.ChangeView(null, e.NextView.VerticalOffset, null, true);
    }

    void OnBodyViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            return;
        }

        // Snap To A Day Edge
        var index  = (int)Math.Round(_bodyScroll.HorizontalOffset / ColumnWidth);
        var target = index * ColumnWidth;
        if (Math.Abs(target - _bodyScroll.HorizontalOffset) > 0.5)
        {
            _bodyScroll.ChangeView(target, null, null, false);
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

        var first = _strip[_firstIndex];
        var after = _firstIndex + _vm.VisibleColumns >= _strip.Count ? _strip.Last.AddDays(1) : _strip[_firstIndex + _vm.VisibleColumns];

        _vm.OnViewScrolled(first, after);
        RenderAllDay();
        RenderCorner();
        _gutter.Render(first);
    }

    void RenderAllDay()
    {
        var count    = _vm.VisibleColumns;
        var maxLanes = _allDayExpanded ? int.MaxValue : MaxCollapsedLanes;
        _allDay.Render(_strip, _firstIndex - count, count * 3, maxLanes);

        var lanes = Math.Min(_allDay.LaneCount, maxLanes);
        _allDay.Height = lanes * AllDayLaneHeight + 4;
        _allDay.Width  = _strip.Count * ColumnWidth;
        _allDayExpand.Visibility = _allDay.LaneCount > MaxCollapsedLanes ? Visibility.Visible : Visibility.Collapsed;
        Corner.Height = DayHeaderHeight + _allDay.Height;
    }

    void RenderCorner()
    {
        var dark = IsDark;
        _zoneLabels.Children.Clear();

        var zones = new List<(string Id, string Label)> { ("Local", TimeZoneCatalog.OffsetLabel(_vm.Zone.GetUtcOffset(_vm.Now))) };
        zones.AddRange(_vm.Settings.TimeZones.Select(z => (z.Id, TimeZoneCatalog.ShortLabel(z))));

        foreach (var (id, label) in zones)
        {
            var text = new TextBlock { Text = label, FontSize = 10, Width = ZoneColumnWidth - 8, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = LeafBrushes.SecondaryText(dark), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(text, $"ZoneLabel_{id}");
            _zoneLabels.Children.Add(text);
        }

        Corner.Width = zones.Count * ZoneColumnWidth;
        _weekNumber.Text       = _vm.Settings.ShowWeekNumbers ? string.Create(CultureInfo.InvariantCulture, $"W{ViewNavigator.WeekNumber(_strip[_firstIndex])}") : "";
        _weekNumber.Foreground = LeafBrushes.SecondaryText(dark);
    }

    void RenderToday()
    {
        foreach (var column in _columns.Where(c => c.Date == _vm.Today))
        {
            column.Render();
        }
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

        RenderRealized();
    }

    void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_strip.SkipsWeekends == _vm.Settings.ShowWeekends)
        {
            BuildStrip(_strip[_firstIndex]);
        }

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

    void OnScrollToTimeRequested(object? sender, DateTimeOffset instant) => ScrollToTime(instant);

    // =========================================================================
    // RECYCLING
    // =========================================================================

    /// <summary>An item in the strip (a class, so WinRT can hold it).</summary>
    public sealed class DayItem(DateOnly date)
    {
        /// <summary>The day.</summary>
        public DateOnly Date { get; } = date;
    }

    sealed partial class DayColumnFactory(TimeGridView owner) : IElementFactory
    {
        readonly Stack<DayColumn> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var column = _pool.Count > 0 ? _pool.Pop() : new DayColumn(owner);
            column.Bind(((DayItem)args.Data).Date);
            return column;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => _pool.Push((DayColumn)args.Element);
    }

    sealed partial class DayHeaderFactory(TimeGridView owner) : IElementFactory
    {
        readonly Stack<DayHeaderCell> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var cell = _pool.Count > 0 ? _pool.Pop() : new DayHeaderCell(owner);
            cell.Bind(((DayItem)args.Data).Date);
            return cell;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => _pool.Push((DayHeaderCell)args.Element);
    }
}
