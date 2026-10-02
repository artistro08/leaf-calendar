using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Month view.
/// </summary>
/// <remarks>
/// Week rows scroll vertically in a virtualized <see cref="ItemsRepeater"/> of about 10 years of weeks.
/// Six rows fill the screen, each a whole number of screen pixels tall so row edges are whole-pixel
/// scroll offsets and text stays sharp. The month containing the middle of the third row is the
/// "focused" month (the title, with other months' days dimmed); while you scroll by hand it follows
/// each new top week, and when scrolling stops the view snaps to a row edge. Pagers jump a month.
/// Navigation reports the new weeks right away, then runs one scroll to the exact target and ignores
/// the offsets it passes on the way.
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
    readonly ScrollViewer _scroll = new() { HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    // One column of fixed-size rows: positions are exact (index × height), unlike StackLayout's estimates,
    // which put rows off by part of a row after the row height changes
    readonly UniformGridLayout _layout = new() { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 1 };
    readonly ItemsRepeater _repeater = new() { VerticalCacheLength = 2 };
    readonly HashSet<WeekRow> _rows = [];
    List<WeekItem> _weeks = [];
    bool _disposed;
    bool _renderDeferred;
    int _firstIndex;
    int _reportedIndex = -1;
    double _layoutWidth;

    // The week a navigation is scrolling to; until the body lands there, offsets on the way are ignored
    int? _pendingIndex;

    // The XamlRoot this view listens to for scale changes (kept: it's already gone when a closing window unloads the view)
    XamlRoot? _root;

    // An animated scroll is running; jumps wait for it (see TimeGridView: a jump doesn't cancel it)
    bool _animating;

    // Drag ghost over the week rows (never takes hits)
    readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    readonly Border _ghost = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed };
    readonly TextBlock _ghostLabel = new() { FontSize = 11, Margin = new Thickness(6, 2, 4, 0), Text = "+ Copy" };

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
        _repeater.Layout       = _layout;
        _repeater.ItemTemplate = new WeekRowFactory(this);
        _repeater.ElementPrepared += (_, e) => _rows.Add((WeekRow)e.Element);
        _repeater.ElementClearing += (_, e) => _rows.Remove((WeekRow)e.Element);
        _scroll.Content = _repeater;
        ScrollIndicator.ShowOnHover(_scroll);
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        // Scrolling And Theme
        // No Scroll Anchoring: this view keeps its own first week (anchoring shifts the offset when rows change height)
        _scroll.VerticalAnchorRatio = double.NaN;

        _scroll.ViewChanged   += OnViewChanged;
        _scroll.SizeChanged   += (_, _) => Relayout(force: false);
        _repeater.SizeChanged += (_, _) => RetryPendingScroll();
        _scroll.DirectManipulationStarted += (_, _) => _pendingIndex = null;
        Loaded                += (_, _) =>
        {
            BuildWeekdayHeader();
            (_root = XamlRoot).Changed += OnXamlRootChanged;
            LeafBrushes.ContrastChanged += OnContrastChanged;
        };
        Unloaded              += (_, _) =>
        {
            _root?.Changed -= OnXamlRootChanged;
            LeafBrushes.ContrastChanged -= OnContrastChanged;
        };
        ActualThemeChanged    += (_, _) =>
        {
            BuildWeekdayHeader();
            RenderAll();
        };

        // View Model
        _vm.OccurrencesChanged += OnOccurrencesChanged;
        _vm.LayoutChanged      += OnLayoutChanged;
        _vm.NavigateRequested  += OnNavigateRequested;

        // Built On The Period A View Switch Is Heading To (else it would scroll there from the old one)
        var start  = _vm.SwitchingTo ?? _vm.PeriodStart;
        FocusMonth = ViewNavigator.MonthStartOf(start);
        BuildWeeks(start);
        _firstIndex = WeekIndexOf(FocusMonth);

        // Dragging Chips Between Days
        _ghost.Child = _ghostLabel;
        _dragLayer.Children.Add(_ghost);
        _dragLayer.Children.Add(_box);
        SetRow(_dragLayer, 1);
        Children.Add(_dragLayer);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnDragMoved), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnDragReleased), handledEventsToo: true);
        PointerCaptureLost += (_, _) => CancelDrag();
        PointerCanceled    += (_, _) => CancelDrag();

        // Shift+Press On An Empty Cell Starts A Selection Box (chips, day numbers, and "more" buttons aren't the row itself)
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnBoxPressed), handledEventsToo: false);
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

        // Report the destination first; data that loads now paints once the scroll lands
        _firstIndex   = index;
        _pendingIndex = index;
        ReportVisible();
        ScrollToIndex(index, animate);
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
        _reportedIndex        = -1;
    }

    int WeekIndexOf(DateOnly date)
    {
        var weekStart = ViewNavigator.WeekStartOf(date, _vm.Settings.WeekStart);
        return _weeks.FindIndex(w => w.WeekStart == weekStart);
    }

    // Sizes the rows for the viewport and keeps the same first week. Resizing the window calls this for
    // every step of the drag, so rows are only repainted when their height or the column width changed.
    void Relayout(bool force)
    {
        var viewport = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight;
        if (_disposed || viewport <= 0)
        {
            return;
        }

        // Whole-Pixel Rows
        var scale  = XamlRoot?.RasterizationScale ?? 1;
        var height = Math.Max(MinRowHeight, Math.Floor(viewport * scale / 6) / scale);
        var width  = ColumnWidth;
        if (!force && height == RowHeight && width == _layoutWidth)
        {
            return;
        }

        RowHeight             = height;
        _layoutWidth          = width;
        _layout.MinItemHeight = height;
        _layout.MinItemWidth  = width * ViewNavigator.VisibleColumnCount(Core.Settings.CalendarViewMode.Month, 0, _vm.Settings.ShowWeekends);
        _repeater.InvalidateMeasure();
        RenderAll();

        // Keep The Same First Week (right away, so no frame shows the old offset at the new height)
        ScrollToIndex(_firstIndex, animate: false);
    }

    // A monitor with a different scale changes what a whole pixel is
    void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Relayout(force: false);

    // A Contrast Theme Turning On Or Off Redraws With The System's Colors (raised off the UI thread)
    void OnContrastChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        BuildWeekdayHeader();
        RenderAll();
    });

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

    void ScrollToIndex(int index, bool animate)
    {
        _pendingIndex = index;
        RetryPendingScroll(animate);
    }

    // Heads for the pending week, or finishes the navigation when the body is already there. Only the
    // navigation's own first scroll animates; catching up after a clamp or a relayout jumps.
    void RetryPendingScroll(bool animate = false)
    {
        if (_disposed || _pendingIndex is not { } pending || (_animating && !animate))
        {
            return;
        }

        var target = pending * RowHeight;
        if (IsAt(target, _scroll.VerticalOffset))
        {
            _pendingIndex = null;
            _firstIndex   = pending;
            Settle();
            return;
        }

        animate    = animate && target <= _scroll.ScrollableHeight;
        _animating = animate;
        ScrollIndicator.Hide(_scroll);
        _scroll.ChangeView(null, target, null, !animate);
    }

    void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            _animating = false;
        }

        // A Box Being Dragged Keeps Its Press Corner On The Day It Started On
        if (_boxDrag is { Started: true } box)
        {
            DrawBox(box);
        }

        // Navigating: wait for the target (the destination was reported when the navigation began)
        if (_pendingIndex is not null)
        {
            if (!e.IsIntermediate)
            {
                RetryPendingScroll();
            }

            return;
        }

        // Scrolling By Hand: report each new top week as it comes into view
        _firstIndex = Math.Clamp((int)Math.Round(_scroll.VerticalOffset / RowHeight), 0, _weeks.Count - 1);
        ReportVisible();
        if (e.IsIntermediate)
        {
            return;
        }

        // Stopped: Snap To A Row
        var target = _firstIndex * RowHeight;
        if (!IsAt(target, _scroll.VerticalOffset))
        {
            _animating = true;
            _scroll.ChangeView(null, target, null, false);
            return;
        }

        Settle();
    }

    static bool IsAt(double target, double offset) => Math.Abs(target - offset) < 0.5;

    // Once the body is still: paint data that arrived while it was moving
    void Settle()
    {
        ReportVisible();
        if (_renderDeferred)
        {
            _renderDeferred = false;
            RenderAll();
        }
    }

    // Tells the view model which weeks show; a new focused month only re-colors the day numbers
    void ReportVisible()
    {
        if (_disposed || _weeks.Count == 0)
        {
            return;
        }

        _firstIndex = Math.Clamp(_firstIndex, 0, _weeks.Count - 1);
        if (_firstIndex == _reportedIndex)
        {
            return;
        }

        _reportedIndex = _firstIndex;
        var first = _weeks[_firstIndex].WeekStart;
        var focus = _weeks[Math.Min(_firstIndex + 2, _weeks.Count - 1)].WeekStart.AddDays(3);
        var month = ViewNavigator.MonthStartOf(focus);

        if (month != FocusMonth)
        {
            FocusMonth = month;
            foreach (var row in _rows)
            {
                row.RenderFocus();
            }
        }

        _vm.OnViewScrolled(first, first.AddDays(42), focus);
    }

    // =========================================================================
    // VIEW MODEL EVENTS
    // =========================================================================

    // While a navigation is scrolling, painting new data waits until it lands, so a month loading
    // mid-scroll can't drop animation frames
    void OnOccurrencesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_pendingIndex is not null)
        {
            _renderDeferred = true;
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

        // The weeks are rebuilt, so a running drag's cells no longer line up
        CancelDrag();
        if (_vm.SwitchingTo is { } switching)
        {
            FocusMonth = ViewNavigator.MonthStartOf(switching);
        }

        BuildWeeks(FocusMonth);
        _firstIndex = WeekIndexOf(FocusMonth);
        BuildWeekdayHeader();
        Relayout(force: true);
        ReportVisible();
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
    // DRAGGING
    // =========================================================================

    // How far the pointer must move before a press becomes a drag (less stays a click)
    const double DragThreshold = 4;

    sealed class ChipDrag(CalendarOccurrence occurrence, Point origin, DateOnly grabbedDay)
    {
        public CalendarOccurrence Occurrence { get; } = occurrence;
        public Point Origin { get; } = origin;
        public DateOnly GrabbedDay { get; } = grabbedDay;
        public bool Started { get; set; }
        public bool Duplicate { get; set; }
        public (int Row, int Column)? Cell { get; set; }
        public DateOnly? Target { get; set; }
    }

    // Shift+Drag Box: the press point (in the drag layer, for the threshold; in the week rows, so it scrolls with
    // them), the cell it started in, and where the pointer is now (in the drag layer)
    sealed class BoxDrag(Point origin, Point corner, (int Row, int Column) cell)
    {
        public Point Origin { get; } = origin;
        public Point Corner { get; } = corner;
        public (int Row, int Column) Cell { get; } = cell;
        public Point Pointer { get; set; }
        public bool Started { get; set; }
    }

    ChipDrag? _drag;
    BoxDrag? _boxDrag;
    readonly Border _box = TimeGridView.SelectionBox();

    /// <summary>True between a press on a chip (or a Shift+press on empty space) and its release.</summary>
    public bool IsDragPending => _drag is not null || _boxDrag is not null;

    void OnBoxPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_dragLayer);
        if (!point.Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType == PointerDeviceType.Touch || !KeyState.IsDown(VirtualKey.Shift))
        {
            return;
        }

        // Not While Picking Times To Share Or Editing, And Only On A Row's Empty Space
        if (_vm.IsSharing || _vm.Editing is not null || _drag is not null || !_rows.Any(r => ReferenceEquals(r, e.OriginalSource)))
        {
            return;
        }

        var corner = e.GetCurrentPoint(_repeater).Position;
        _boxDrag   = new BoxDrag(point.Position, corner, CellAt(corner));
    }

    /// <summary>A chip was pressed: dragging moves the event to another day (keeping its time). Events you can't change don't drag.</summary>
    public void BeginChipDrag(CalendarOccurrence occurrence, PointerRoutedEventArgs e)
    {
        if (!_vm.CanEdit(occurrence))
        {
            return;
        }

        _drag = new ChipDrag(occurrence, e.GetCurrentPoint(this).Position, DateAt(e.GetCurrentPoint(_repeater).Position));
    }

    /// <summary>The day under a point in the week rows' coordinates (a real date, so hidden weekends still count as days).</summary>
    public DateOnly DateAt(Point pointInRepeater)
    {
        var (row, column) = CellAt(pointInRepeater);
        var days          = ColumnDates(_weeks[row].WeekStart);
        return days[Math.Min(column, days.Count - 1)];
    }

    /// <summary>Drops a pending or running drag without changing anything (Esc). Returns true when there was one.</summary>
    public bool CancelDrag()
    {
        if (_drag is null && _boxDrag is null)
        {
            return false;
        }

        _drag             = null;
        _boxDrag          = null;
        _ghost.Visibility = Visibility.Collapsed;
        _box.Visibility   = Visibility.Collapsed;
        ReleasePointerCaptures();
        return true;
    }

    // Week index and visible column under a point in the week rows' coordinates
    (int Row, int Column) CellAt(Point pointInRepeater)
    {
        var columns = ViewNavigator.VisibleColumnCount(Core.Settings.CalendarViewMode.Month, 0, _vm.Settings.ShowWeekends);
        var row     = Math.Clamp((int)Math.Floor(pointInRepeater.Y / RowHeight), 0, _weeks.Count - 1);
        var column  = Math.Clamp((int)Math.Floor(pointInRepeater.X / ColumnWidth), 0, columns - 1);
        return (row, column);
    }

    // The cell under the pointer, kept to the visible rows (a pointer above or below the view counts as the edge row)
    (int Row, int Column) VisibleCellAt(PointerRoutedEventArgs e)
    {
        var inRepeater = e.GetCurrentPoint(_repeater).Position;
        var top        = _scroll.VerticalOffset;
        return CellAt(new Point(inRepeater.X, Math.Clamp(inRepeater.Y, top, top + Math.Max(0, _scroll.ViewportHeight - 1))));
    }

    void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_boxDrag is { } box)
        {
            MoveBox(box, e);
            return;
        }

        if (_drag is not { } drag)
        {
            return;
        }

        // Button Already Up: the release went somewhere else (off the grid, Alt+Tab), so the press is over
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelDrag();
            return;
        }

        var at = point.Position;
        if (!drag.Started)
        {
            if (Math.Abs(at.X - drag.Origin.X) < DragThreshold && Math.Abs(at.Y - drag.Origin.Y) < DragThreshold)
            {
                return;
            }

            if (!CapturePointer(e.Pointer))
            {
                CancelDrag();
                return;
            }

            drag.Started = true;
        }

        // Redraw The Ghost Only When The Cell Or Copy Mode Changes (the pointer is kept to the visible rows)
        var top       = _scroll.VerticalOffset;
        var cell      = VisibleCellAt(e);
        var duplicate = KeyState.IsDown(VirtualKey.Menu);
        e.Handled = true;
        if (cell == drag.Cell && duplicate == drag.Duplicate)
        {
            return;
        }

        var days = ColumnDates(_weeks[cell.Row].WeekStart);
        drag.Cell      = cell;
        drag.Duplicate = duplicate;
        drag.Target    = days[Math.Min(cell.Column, days.Count - 1)];

        // Ghost Over The Target Cell
        _ghost.Width           = ColumnWidth - 2;
        _ghost.Height          = RowHeight - 2;
        _ghost.BorderBrush     = LeafBrushes.Accent(IsDark);
        _ghost.Background      = LeafBrushes.Hover(IsDark);
        _ghostLabel.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_ghost, cell.Column * ColumnWidth + 1);
        Canvas.SetTop(_ghost, cell.Row * RowHeight - top + 1);
        _ghost.Visibility      = Visibility.Visible;
    }

    // The box follows the pointer once it moves past the threshold (ponytail: selection applies on release, not live)
    void MoveBox(BoxDrag box, PointerRoutedEventArgs e)
    {
        // Button Already Up: the release went somewhere else, so the press is over
        var point = e.GetCurrentPoint(_dragLayer);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelDrag();
            return;
        }

        var at = point.Position;
        if (!box.Started)
        {
            if (Math.Abs(at.X - box.Origin.X) < DragThreshold && Math.Abs(at.Y - box.Origin.Y) < DragThreshold)
            {
                return;
            }

            if (!CapturePointer(e.Pointer))
            {
                CancelDrag();
                return;
            }

            // The Accent Is Read Once Per Drag
            box.Started = true;
            TimeGridView.StyleBox(_box, IsDark);
        }

        e.Handled   = true;
        box.Pointer = at;
        DrawBox(box);
    }

    // The press corner stays on the day it was pressed on (it scrolls with the rows); the other follows the pointer
    void DrawBox(BoxDrag box) => TimeGridView.ShowBox(_box, _repeater.TransformToVisual(_dragLayer).TransformPoint(box.Corner), box.Pointer);

    // Every event on the shown days of the covered cells (hidden weekends aren't columns, so they stay out); Ctrl adds
    void ReleaseBox(BoxDrag box, PointerRoutedEventArgs e)
    {
        _boxDrag        = null;
        _box.Visibility = Visibility.Collapsed;
        ReleasePointerCapture(e.Pointer);
        if (!box.Started)
        {
            return;
        }

        e.Handled = true;
        var end                 = VisibleCellAt(e);
        var (firstRow, lastRow) = (Math.Min(box.Cell.Row, end.Row), Math.Max(box.Cell.Row, end.Row));
        var (firstCol, lastCol) = (Math.Min(box.Cell.Column, end.Column), Math.Max(box.Cell.Column, end.Column));
        var days                = Enumerable.Range(firstRow, lastRow - firstRow + 1)
            .SelectMany(r => ColumnDates(_weeks[r].WeekStart).Where((_, column) => column >= firstCol && column <= lastCol));

        _vm.SelectBox(_vm.OnDays(days), add: KeyState.IsDown(VirtualKey.Control));
    }

    void OnDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_boxDrag is { } box)
        {
            ReleaseBox(box, e);
            return;
        }

        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        ReleasePointerCapture(e.Pointer);
        _ghost.Visibility = Visibility.Collapsed;

        // Dropped On The Day It Started: nothing to do (no scope question, no copy on top of the original)
        if (!drag.Started || drag.Target is not { } target || target == drag.GrabbedDay)
        {
            return;
        }

        e.Handled = true;
        var o            = drag.Occurrence;
        var (start, end) = DragMath.ShiftDays(o, target.DayNumber - drag.GrabbedDay.DayNumber, _vm.Zone);

        // Alt+Drag Duplicates (Alt is also read as the pointer moves: it can already be up when the release is handled)
        if (drag.Duplicate || KeyState.IsDown(VirtualKey.Menu))
        {
            _vm.Duplicate(o, start, end, o.IsAllDay);
            return;
        }

        _vm.Fire(() => _vm.MoveAsync(o, start, end, o.IsAllDay), "calendar.move.failed");
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
        readonly ItemPins _pins = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var row = _pool.Count > 0 ? _pool.Pop() : new WeekRow(owner);
            row.Bind(((WeekItem)args.Data).WeekStart);
            _pins.Pin(row, args.Data);
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            var row = (WeekRow)args.Element;
            _pins.Unpin(row);
            _pool.Push(row);
        }
    }
}
