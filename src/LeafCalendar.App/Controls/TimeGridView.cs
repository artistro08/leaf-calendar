using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

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
/// their own, so they can't fight the body's animations; the mouse wheel over them is passed to the
/// body). The compositor draws them where the body is, frame by frame (see <see cref="FollowBody"/>),
/// and their own scrollers catch up afterward so the right header cells are built. Columns are a whole
/// number of screen pixels wide, so every day edge is a whole-pixel scroll offset and text stays sharp. While you scroll by hand, the view model hears about each new first
/// day as it comes into view (title, mini month, data); when scrolling stops, the view snaps to a day
/// edge. Navigation (pagers, "today", resizing, mode changes) reports the new days right away, then
/// runs one scroll to the exact target and ignores the offsets it passes on the way.
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
    // Wheel step over the gutter and header (three 16 px lines per notch, like a ScrollViewer)
    const double WheelStep = 48.0 / 120;

    readonly ScrollViewer _headerScroll = new() { Background = LeafBrushes.Transparent, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ScrollViewer _gutterScroll = new() { Background = LeafBrushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ScrollViewer _bodyScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Enabled, ZoomMode = ZoomMode.Disabled };
    // One row of fixed-size cells: positions are exact (index × width), unlike StackLayout's estimates,
    // which drift by weeks when jumping deep into the strip after the column width changes
    readonly UniformGridLayout _headerLayout = new() { Orientation = Orientation.Vertical, MaximumRowsOrColumns = 1 };
    readonly UniformGridLayout _bodyLayout = new() { Orientation = Orientation.Vertical, MaximumRowsOrColumns = 1 };
    readonly ItemsRepeater _headerRepeater = new() { HorizontalCacheLength = 2 };
    readonly ItemsRepeater _bodyRepeater = new() { HorizontalCacheLength = 2 };
    readonly Grid _headerContent = new();
    // What the header and gutter scrollers scroll; the header content and the gutter inside them are
    // shifted by the compositor to where the body is (see FollowBody)
    readonly Grid _headerHost = new();
    readonly Grid _gutterHost = new();
    readonly AllDayCanvas _allDay;
    readonly TimeZoneGutter _gutter;
    readonly StackPanel _zoneLabels = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBlock _weekNumber = new() { FontSize = 11, Margin = new Thickness(30, 6, 0, 0) };
    readonly Button _allDayExpand = new() { Padding = new Thickness(4), Background = LeafBrushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    readonly DispatcherQueueTimer _clock;
    readonly HashSet<DayColumn> _columns = [];
    readonly HashSet<DayHeaderCell> _headers = [];
    DayStrip _strip = null!;
    bool _allDayExpanded;
    bool _disposed;
    bool _initialized;
    bool _following;
    bool _renderDeferred;
    int _firstIndex;
    int _reportedIndex = -1;
    double _wheelTarget = double.NaN;

    // The day a navigation is scrolling to. Until the body lands there, offsets it passes on the way (an
    // animation's frames, or a clamp to a stale extent right after the columns change width) are ignored
    // instead of being taken as the new first day
    int? _pendingIndex;

    // The XamlRoot this view listens to for scale changes (kept: it's already gone when a closing window unloads the view)
    XamlRoot? _root;

    // An animated scroll is running. A jump issued now doesn't cancel it: the ScrollViewer adds the rest of
    // the animation on top of the jump (a mode change once landed three years out), so jumps wait for it
    bool _animating;

    // The first layout's jump to 7:30 AM, kept until the body is tall enough to reach it
    double? _pendingTop;

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

        // Add Time Zone
        var addZone = new Button
        {
            Content             = new FontIcon { Glyph = "", FontSize = 10 },
            Padding             = new Thickness(4),
            Background          = LeafBrushes.Transparent,
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(4, 4, 0, 0),
        };
        AutomationProperties.SetAutomationId(addZone, "AddTimeZoneButton");
        AutomationProperties.SetName(addZone, "Time zones");
        ToolTipService.SetToolTip(addZone, "Time zones");
        addZone.Click += (_, _) =>
        {
            var panel = new Views.TimeZonePanel();
            panel.Attach(_vm);
            new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft }.ShowAt(addZone);
        };
        Corner.Children.Add(addZone);

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
        _headerHost.Children.Add(_headerContent);
        _headerScroll.Content = _headerHost;
        SetColumn(_headerScroll, 1);
        Children.Add(_headerScroll);

        // Gutter
        _gutterHost.Children.Add(_gutter);
        _gutterScroll.Content = _gutterHost;
        SetRow(_gutterScroll, 1);
        Children.Add(_gutterScroll);

        // Body
        _bodyRepeater.ItemTemplate = new DayColumnFactory(this);
        _bodyRepeater.ElementPrepared += (_, e) => _columns.Add((DayColumn)e.Element);
        _bodyRepeater.ElementClearing += (_, e) => _columns.Remove((DayColumn)e.Element);
        _bodyScroll.Content = _bodyRepeater;
        ScrollIndicator.ShowOnHover(_bodyScroll);
        SetRow(_bodyScroll, 1);
        SetColumn(_bodyScroll, 1);
        Children.Add(_bodyScroll);

        // No Scroll Anchoring: this view keeps its own first day. The ScrollViewer's default anchoring
        // "keeps an element in place" when the columns change width, which shifted the offset by
        // hundreds of days after every resize (the view landed years away and loaded that data).
        _bodyScroll.HorizontalAnchorRatio   = double.NaN;
        _bodyScroll.VerticalAnchorRatio     = double.NaN;
        _headerScroll.HorizontalAnchorRatio = double.NaN;
        _gutterScroll.VerticalAnchorRatio   = double.NaN;

        // Scroll Sync And Snapping
        _bodyScroll.ViewChanging  += OnBodyViewChanging;
        _bodyScroll.ViewChanged   += OnBodyViewChanged;
        _bodyScroll.SizeChanged   += (_, _) => Relayout(force: false);
        _bodyRepeater.SizeChanged += (_, _) => RetryPendingScroll();
        _headerRepeater.SizeChanged += (_, _) => SyncSides();

        // Wheel Over The Gutter And Header Scrolls The Body
        _gutterScroll.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSideWheel), handledEventsToo: true);
        _headerScroll.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSideWheel), handledEventsToo: true);

        // The user took over (touch, wheel, trackpad): drop any unreached navigation target
        _bodyScroll.DirectManipulationStarted += (_, _) => _pendingIndex = null;

        // View Model
        _vm.OccurrencesChanged    += OnOccurrencesChanged;
        _vm.LayoutChanged         += OnLayoutChanged;
        _vm.NavigateRequested     += OnNavigateRequested;
        _vm.ScrollToTimeRequested += OnScrollToTimeRequested;
        ActualThemeChanged        += (_, _) => RenderRealized();
        Loaded                    += (_, _) =>
        {
            (_root = XamlRoot).Changed += OnXamlRootChanged;
            FollowBody();
        };
        Unloaded                  += (_, _) => _root?.Changed -= OnXamlRootChanged;

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

        // Report the destination first (title, mini month, data); data that loads now paints once the scroll lands
        _firstIndex   = _strip.IndexOf(date);
        _pendingIndex = _firstIndex;
        ReportVisible();
        ScrollToIndex(_firstIndex, animate);
    }

    /// <summary>Scrolls vertically so <paramref name="instant"/> sits a third of the way down.</summary>
    public void ScrollToTime(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _vm.Zone);
        var y     = local.TimeOfDay.TotalHours * HourHeight - _bodyScroll.ViewportHeight / 3;
        ScrollIndicator.Hide(_bodyScroll);
        _bodyScroll.ChangeView(null, Math.Max(0, y), null, false);
    }

    /// <summary>Repaints every built column, header, the all-day row, the corner, and the gutter.</summary>
    public void RenderRealized()
    {
        RenderColumns();
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
        _firstIndex    = _strip.IndexOf(around);
        _reportedIndex = -1;
    }

    // Sizes the columns for the space available and keeps the same first day. Resizing the window calls
    // this for every step of the drag, so it does nothing unless the column width or body height really
    // changed (a height-only resize costs nothing), and it only repaints what depends on the width.
    void Relayout(bool force)
    {
        var available = _bodyScroll.ActualWidth + _bodyScroll.Margin.Right;
        if (_disposed || available <= 0)
        {
            return;
        }

        // Whole-Pixel Columns (the few pixels left over become a margin at the right edge)
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Max(48, Math.Floor(available * scale / _vm.VisibleColumns) / scale);
        if (!force && width == ColumnWidth && _bodyRepeater.Height == BodyHeight)
        {
            return;
        }

        var spare = new Thickness(0, 0, Math.Max(0, available - width * _vm.VisibleColumns), 0);
        ColumnWidth                 = width;
        _bodyScroll.Margin          = spare;
        _headerScroll.Margin        = spare;
        _bodyRepeater.Height        = BodyHeight;
        _headerRepeater.Height      = DayHeaderHeight;
        _bodyLayout.MinItemWidth    = ColumnWidth;
        _bodyLayout.MinItemHeight   = BodyHeight;
        _headerLayout.MinItemWidth  = ColumnWidth;
        _headerLayout.MinItemHeight = DayHeaderHeight;
        _bodyRepeater.InvalidateMeasure();
        _headerRepeater.InvalidateMeasure();
        RenderColumns();

        // First Layout: the corner and hour labels (they don't depend on the width), and a jump to 7:30 AM
        if (!_initialized)
        {
            _initialized = true;
            _pendingTop  = Math.Max(0, 7.5 * HourHeight - 20);
            RenderCorner();
            _gutter.Render(_strip[_firstIndex]);
        }

        // Keep The Same First Day (right away, so no frame shows the old offset at the new width)

        ScrollToIndex(_firstIndex, animate: false);
    }

    // Draws the header content and the gutter where the body is, in the same compositor frame. Syncing
    // their scrollers from the body's ViewChanging lands a frame late, so the day names and all-day chips
    // trailed the columns mid-scroll. Each one gets a translation of (body offset − its own scroller's
    // offset): wherever its scroller has got to, it's drawn at the body's offset. At rest the scrollers
    // agree and the translation is 0, so hit testing and automation bounds are exact.
    void FollowBody()
    {
        if (_following)
        {
            return;
        }

        _following = true;
        var body = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(_bodyScroll);
        Follow(_headerContent, "Vector3(body.Translation.X - side.Translation.X, 0, 0)", body, ElementCompositionPreview.GetScrollViewerManipulationPropertySet(_headerScroll));
        Follow(_gutter, "Vector3(0, body.Translation.Y - side.Translation.Y, 0)", body, ElementCompositionPreview.GetScrollViewerManipulationPropertySet(_gutterScroll));
    }

    static void Follow(UIElement element, string expression, Microsoft.UI.Composition.CompositionPropertySet body, Microsoft.UI.Composition.CompositionPropertySet side)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual    = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("body", body);
        animation.SetReferenceParameter("side", side);
        visual.StartAnimation("Translation", animation);
    }

    // One scroll to the exact day edge (animated for pagers and "today"; a new animated scroll retargets one
    // that's running)
    void ScrollToIndex(int index, bool animate)
    {
        _pendingIndex = index;
        RetryPendingScroll(animate);
    }

    // A monitor with a different scale changes what a whole pixel is
    void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Relayout(force: false);

    void OnBodyViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        _headerScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
        _gutterScroll.ChangeView(null, e.NextView.VerticalOffset, null, true);
    }

    void OnBodyViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        SyncSides();
        if (!e.IsIntermediate)
        {
            _wheelTarget = double.NaN;
            _animating   = false;
        }

        // Navigating: wait for the target, then take it (the destination was reported when the navigation began)
        if (_pendingIndex is not null)
        {
            if (e.IsIntermediate)
            {
                return;
            }

            RetryPendingScroll();
            return;
        }

        // Scrolling By Hand: report each new first day as it comes into view
        var index = Math.Clamp((int)Math.Round(_bodyScroll.HorizontalOffset / ColumnWidth), 0, _strip.Count - 1);
        _firstIndex = index;
        ReportVisible();
        if (e.IsIntermediate)
        {
            return;
        }

        // Stopped: Snap To A Day Edge
        var target = index * ColumnWidth;
        if (!IsAt(target, _bodyScroll.HorizontalOffset))
        {
            _animating = true;
            _bodyScroll.ChangeView(target, null, null, false);
            return;
        }

        Settle();
    }

    static bool IsAt(double target, double offset) => Math.Abs(target - offset) < 0.5;

    // Catches the header and gutter up with the body when ViewChanging's sync was clamped (right after the
    // columns change width, the header's extent is still the old one)
    void SyncSides()
    {
        if (!IsAt(_bodyScroll.HorizontalOffset, _headerScroll.HorizontalOffset))
        {
            _headerScroll.ChangeView(_bodyScroll.HorizontalOffset, null, null, true);
        }

        if (!IsAt(_bodyScroll.VerticalOffset, _gutterScroll.VerticalOffset))
        {
            _gutterScroll.ChangeView(null, _bodyScroll.VerticalOffset, null, true);
        }
    }

    // Once the body is still: paint data that arrived while it was moving
    void Settle()
    {
        ReportVisible();
        PublishOffset();
        if (_renderDeferred)
        {
            _renderDeferred = false;
            RenderColumns();
        }
    }

    // Heads for the pending day (and the first layout's 7:30 AM), or finishes the navigation when the body
    // is already there (no scroll means no ViewChanged to finish it). Only the navigation's own first
    // scroll animates, and only inside the measured strip; catching up after a clamp or a relayout jumps.
    void RetryPendingScroll(bool animate = false)
    {
        if (_disposed || _pendingIndex is not { } pending || (_animating && !animate))
        {
            return;
        }

        // The 7:30 AM jump (as far as the body can go, once it has been measured at full height)
        var top = _pendingTop;
        if (top is { } y && _bodyScroll.ExtentHeight >= BodyHeight - 0.5)
        {
            top = Math.Min(y, _bodyScroll.ScrollableHeight);
            if (IsAt(top.Value, _bodyScroll.VerticalOffset))
            {
                _pendingTop = top = null;
            }
        }

        // Arrived
        var target = pending * ColumnWidth;
        if (top is null && IsAt(target, _bodyScroll.HorizontalOffset))
        {
            _pendingIndex = null;
            _firstIndex   = pending;
            Settle();
            return;
        }

        animate    = animate && target <= _bodyScroll.ScrollableWidth;
        _animating = animate;
        ScrollIndicator.Hide(_bodyScroll);
        _bodyScroll.ChangeView(target, top, null, !animate);
    }

    // Tells the view model which days show (title, mini month, data), and redraws what follows the first
    // day. Runs for each new first day while scrolling, so it's skipped when the first day hasn't changed.
    void ReportVisible()
    {
        if (_disposed || _firstIndex == _reportedIndex)
        {
            return;
        }

        _reportedIndex = _firstIndex;
        var first = _strip[_firstIndex];
        var after = _firstIndex + _vm.VisibleColumns >= _strip.Count ? _strip.Last.AddDays(1) : _strip[_firstIndex + _vm.VisibleColumns];

        _vm.OnViewScrolled(first, after);
        RenderAllDay();
        RenderWeekNumber();

        // Extra time zones label each local hour of the first day (their offsets can differ across a DST change)
        if (_vm.Settings.TimeZones.Count > 0)
        {
            _gutter.Render(first);
        }

    }

    // Where the body came to rest, for UI tests (the first day, the offsets, and the column width)
    void PublishOffset() =>
        AutomationProperties.SetItemStatus(this, string.Create(CultureInfo.InvariantCulture, $"first={_strip[_firstIndex]:yyyy-MM-dd};offset={_bodyScroll.HorizontalOffset:R};column={ColumnWidth:R};top={_bodyScroll.VerticalOffset:R}"));

    // The mouse wheel over the hour gutter or the day headers scrolls the body (vertically; a tilt wheel or
    // Shift+wheel horizontally), building on a scroll that's still animating so fast notches add up
    void OnSideWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point      = e.GetCurrentPoint(this).Properties;
        var horizontal = point.IsHorizontalMouseWheel || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        var delta      = point.MouseWheelDelta * WheelStep;
        e.Handled = true;

        if (horizontal)
        {
            _bodyScroll.ChangeView(_bodyScroll.HorizontalOffset + delta, null, null, false);
            return;
        }

        var from = double.IsNaN(_wheelTarget) ? _bodyScroll.VerticalOffset : _wheelTarget;
        _wheelTarget = Math.Clamp(from - delta, 0, _bodyScroll.ScrollableHeight);
        _bodyScroll.ChangeView(null, _wheelTarget, null, false);
    }

    // Everything that depends on the column width: the columns, the headers, and the all-day row
    void RenderColumns()
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

        // Extra zones oldest first, then this PC's zone next to the days (the gutter's column order)
        var zones = _vm.Settings.TimeZones.Select(z => (z.Id, Label: TimeZoneCatalog.ShortLabel(z))).ToList();
        zones.Add(("Local", TimeZoneCatalog.OffsetLabel(_vm.Zone.GetUtcOffset(_vm.Now))));

        foreach (var (id, label) in zones)
        {
            var text = new TextBlock { Text = label, FontSize = 10, Width = ZoneColumnWidth - 8, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = LeafBrushes.SecondaryText(dark), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(text, $"ZoneLabel_{id}");
            _zoneLabels.Children.Add(text);
        }

        Corner.Width           = zones.Count * ZoneColumnWidth;
        _weekNumber.Foreground = LeafBrushes.SecondaryText(dark);
        RenderWeekNumber();
    }

    void RenderWeekNumber() =>
        _weekNumber.Text = _vm.Settings.ShowWeekNumbers ? string.Create(CultureInfo.InvariantCulture, $"W{ViewNavigator.WeekNumber(_strip[_firstIndex])}") : "";

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

    // Data only changes the columns and the all-day row. While a navigation is scrolling, painting waits
    // until it lands, so a month loading mid-scroll can't drop animation frames.
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

        RenderColumns();
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

        _reportedIndex = -1;
        Relayout(force: true);
        RenderCorner();
        _gutter.Render(_strip[_firstIndex]);
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
