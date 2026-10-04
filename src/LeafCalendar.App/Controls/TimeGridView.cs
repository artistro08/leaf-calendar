using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

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

    /// <summary>The all-day row's height with nothing in it: room to double-click, and a multiple of 4 so the grid under it
    /// stays on whole pixels at 125% and 150%.</summary>
    public const double EmptyAllDayHeight = 24;

    /// <summary>Lanes shown before "expand".</summary>
    public const int MaxCollapsedLanes = 3;

    /// <summary>Width of one time-zone column in the gutter.</summary>
    public const double ZoneColumnWidth = 56;

    // ponytail: ~8 years of days; rebuild the strip around the target date if someone scrolls past the ends
    private const int StripDaysEachSide = 1500;

    private readonly CalendarViewModel _vm;
    // Wheel step over the gutter and header (three 16 px lines per notch, like a ScrollViewer)
    private const double WheelStep = 48.0 / 120;

    private readonly ScrollViewer _headerScroll = new() { Background = LeafBrushes.Transparent, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    private readonly ScrollViewer _gutterScroll = new() { Background = LeafBrushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    private readonly ScrollViewer _bodyScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Enabled, ZoomMode = ZoomMode.Disabled };
    // One row of fixed-size cells: positions are exact (index × width), unlike StackLayout's estimates,
    // which drift by weeks when jumping deep into the strip after the column width changes
    private readonly UniformGridLayout _headerLayout = new() { Orientation = Orientation.Vertical, MaximumRowsOrColumns = 1 };
    private readonly UniformGridLayout _bodyLayout = new() { Orientation = Orientation.Vertical, MaximumRowsOrColumns = 1 };
    private readonly ItemsRepeater _headerRepeater = new() { HorizontalCacheLength = 2 };
    private readonly ItemsRepeater _bodyRepeater = new() { HorizontalCacheLength = 2 };
    private readonly Grid _headerContent = new();
    // What the header and gutter scrollers scroll; the header content and the gutter inside them are
    // shifted by the compositor to where the body is (see FollowBody)
    private readonly Grid _headerHost = new();
    private readonly Grid _gutterHost = new();
    private readonly AllDayCanvas _allDay;
    private readonly TimeZoneGutter _gutter;
    private readonly StackPanel _zoneLabels = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _weekNumber = new() { FontSize = 11, Margin = new Thickness(30, 6, 0, 0) };
    // The line under the all-day row, across the corner and the days, so the row always has a bottom edge whatever the
    // grid below is scrolled to
    private readonly Border _allDayRule = new() { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Button _allDayExpand = new() { Padding = new Thickness(4), Background = LeafBrushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly DispatcherQueueTimer _clock;
    private readonly HashSet<DayColumn> _columns = [];
    private readonly HashSet<DayHeaderCell> _headers = [];
    private DayStrip _strip = null!;
    private bool _allDayExpanded;

    // The all-day default last seen in the settings: a change there applies, while your chevron clicks stay otherwise
    private bool _allDayDefault;
    private bool _disposed;
    private bool _initialized;
    private bool _following;
    private bool _renderDeferred;
    private int _firstIndex;
    private int _reportedIndex = -1;
    private double _wheelTarget = double.NaN;

    // Ctrl+wheel deltas waiting to add up to a whole notch
    private readonly WheelNotches _zoomNotches = new();

    // The day a navigation is scrolling to. Until the body lands there, offsets it passes on the way (an
    // animation's frames, or a clamp to a stale extent right after the columns change width) are ignored
    // instead of being taken as the new first day
    private int? _pendingIndex;

    // The XamlRoot this view listens to for scale changes (kept: it's already gone when a closing window unloads the view)
    private XamlRoot? _root;

    // An animated scroll is running. A jump issued now doesn't cancel it: the ScrollViewer adds the rest of
    // the animation on top of the jump (a mode change once landed three years out), so jumps wait for it
    private bool _animating;

    // The first layout's jump to 7:30 AM, kept until the body is tall enough to reach it
    private double? _pendingTop;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public TimeGridView(CalendarViewModel vm)
    {
        _vm = vm;
        _allDay = new AllDayCanvas(this);

        // The All-Day Row Starts As Settings Say (expanded or three lanes)
        _allDayExpanded = _allDayDefault = vm.Settings.AllDayExpanded;
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
        _allDayExpand.Content = new FontIcon { Glyph = "", FontSize = 10, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _allDayTurn };
        AutomationProperties.SetAutomationId(_allDayExpand, "AllDayExpand");
        ShowAllDayChevron(animate: false);
        _allDayExpand.Click += (_, _) =>
        {
            _allDayExpanded = !_allDayExpanded;
            ShowAllDayChevron(animate: true);
            RenderAllDay(slide: true);
        };
        Children.Add(Corner);

        // Time Zones (opens Settings › Time zones)
        var addZone = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Padding = new Thickness(4),
            Background = LeafBrushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 0, 0),
        };
        AutomationProperties.SetAutomationId(addZone, "AddTimeZoneButton");
        AutomationProperties.SetName(addZone, "Time zones");
        ToolTipService.SetToolTip(addZone, "Time zones");
        addZone.Click += (_, _) => _vm.OpenSettings?.Invoke(ViewModels.SettingsSection.TimeZones);
        Corner.Children.Add(addZone);

        // Header (day names + all-day row)
        _headerRepeater.Layout = _headerLayout;
        _bodyRepeater.Layout = _bodyLayout;
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
        SetColumnSpan(_allDayRule, 2);
        Children.Add(_allDayRule);

        // Gutter
        _gutterHost.Children.Add(_gutter);
        _gutterScroll.Content = _gutterHost;
        SetRow(_gutterScroll, 1);
        Children.Add(_gutterScroll);

        // Body
        _bodyRepeater.ItemTemplate = new DayColumnFactory(this);
        _bodyRepeater.ElementPrepared += (_, e) =>
        {
            _columns.Add((DayColumn)e.Element);
            ShowNewEventGhost();
        };
        _bodyRepeater.ElementClearing += (_, e) => _columns.Remove((DayColumn)e.Element);
        _bodyScroll.Content = _bodyRepeater;
        ScrollIndicator.ShowOnHover(_bodyScroll);
        SetRow(_bodyScroll, 1);
        SetColumn(_bodyScroll, 1);
        Children.Add(_bodyScroll);

        // No Scroll Anchoring: this view keeps its own first day. The ScrollViewer's default anchoring
        // "keeps an element in place" when the columns change width, which shifted the offset by
        // hundreds of days after every resize (the view landed years away and loaded that data).
        _bodyScroll.HorizontalAnchorRatio = double.NaN;
        _bodyScroll.VerticalAnchorRatio = double.NaN;
        _headerScroll.HorizontalAnchorRatio = double.NaN;
        _gutterScroll.VerticalAnchorRatio = double.NaN;

        // Scroll Sync And Snapping
        _bodyScroll.ViewChanging += OnBodyViewChanging;
        _bodyScroll.ViewChanged += OnBodyViewChanged;
        _bodyScroll.SizeChanged += (_, _) => Relayout(force: false);
        _bodyRepeater.SizeChanged += (_, _) => RetryPendingScroll();
        _headerRepeater.SizeChanged += (_, _) => SyncSides();

        // Ctrl+Wheel Over The Body Zooms The Hours (on the content, so it runs before the ScrollViewer scrolls)
        _bodyRepeater.PointerWheelChanged += OnBodyWheel;

        // Wheel Over The Gutter And Header Scrolls The Body
        _gutterScroll.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSideWheel), handledEventsToo: true);
        _headerScroll.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSideWheel), handledEventsToo: true);

        // The user took over (touch, wheel, trackpad): drop any unreached navigation target
        _bodyScroll.DirectManipulationStarted += (_, _) => _pendingIndex = null;

        // View Model
        _vm.OccurrencesChanged += OnOccurrencesChanged;
        _vm.LayoutChanged += OnLayoutChanged;
        _vm.NavigateRequested += OnNavigateRequested;
        _vm.ScrollToTimeRequested += OnScrollToTimeRequested;
        _vm.OverlayChanged += OnOverlayChanged;
        _vm.ShareChanged += OnShareChanged;
        _sharingShown = vm.IsSharing;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        ActualThemeChanged += (_, _) => RenderRealized();
        Loaded += (_, _) =>
        {
            (_root = XamlRoot).Changed += OnXamlRootChanged;
            LeafBrushes.ContrastChanged += OnContrastChanged;
            FollowBody();
        };
        Unloaded += (_, _) =>
        {
            _root?.Changed -= OnXamlRootChanged;
            LeafBrushes.ContrastChanged -= OnContrastChanged;
        };

        // Now Line Clock
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromMinutes(1);
        _clock.Tick += (_, _) => RenderToday();
        _clock.Start();

        // Dragging (a press on an event, a chip, or empty time becomes a drag once the pointer moves a few pixels)
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnDragMoved), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnDragReleased), handledEventsToo: true);
        PointerCaptureLost += (_, _) => CancelDrag();
        PointerCanceled += (_, _) => CancelDrag();

        // Selection Box (over the whole view, so its canvas shares this view's coordinates; clicks go through)
        _boxLayer.Children.Add(_box);
        SetRowSpan(_boxLayer, 2);
        SetColumnSpan(_boxLayer, 2);
        Children.Add(_boxLayer);

        // Built On The Period A View Switch Is Heading To (else it would scroll there from the old one), and hidden until
        // its first layout has landed on that day and the morning, so it never shows them snapping into place
        BuildStrip(_vm.SwitchingTo ?? _vm.PeriodStart);
        Opacity = 0;

        // Shown By Now Whatever Happens (a layout that never lands, say at no size, mustn't leave the view invisible)
        _reveal = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _reveal.Interval = RevealFallback;
        _reveal.IsRepeating = false;
        _reveal.Tick += (_, _) => Opacity = 1;
        _reveal.Start();
    }

    // Shows a new view if its first layout hasn't landed in time (held here so it lives until it fires)
    private readonly DispatcherQueueTimer _reveal;

    // How long a new view may stay hidden waiting for its first layout to land
    private static readonly TimeSpan RevealFallback = TimeSpan.FromMilliseconds(250);

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
        if (!CanLead(date))
        {
            BuildStrip(date);
        }

        // Report the destination first (title, mini month, data); data that loads now paints once the scroll lands
        _firstIndex = _strip.IndexOf(date);
        _pendingIndex = _firstIndex;
        ReportVisible();
        ScrollToIndex(_firstIndex, animate);
    }

    /// <summary>Scrolls vertically so <paramref name="instant"/> sits a third of the way down.</summary>
    public void ScrollToTime(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _vm.Zone);
        var y = local.TimeOfDay.TotalHours * HourHeight - _bodyScroll.ViewportHeight / 3;
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
        _reveal.Stop();
        _vm.OccurrencesChanged -= OnOccurrencesChanged;
        _vm.LayoutChanged -= OnLayoutChanged;
        _vm.NavigateRequested -= OnNavigateRequested;
        _vm.ScrollToTimeRequested -= OnScrollToTimeRequested;
        _vm.OverlayChanged -= OnOverlayChanged;
        _vm.ShareChanged -= OnShareChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        Track(null);
    }

    // =========================================================================
    // LAYOUT
    // =========================================================================

    private void BuildStrip(DateOnly around)
    {
        _strip = new DayStrip(around, StripDaysEachSide, StripDaysEachSide, skipWeekends: !_vm.Settings.ShowWeekends);
        var items = Enumerable.Range(0, _strip.Count).Select(i => new DayItem(_strip[i])).ToList();
        _headerRepeater.ItemsSource = items;
        _bodyRepeater.ItemsSource = items;
        _firstIndex = _strip.IndexOf(around);
        _reportedIndex = -1;
    }

    // True when the body can scroll so the date is the first column (the strip's last few days can't
    // be: the body stops a full view before its end)
    private bool CanLead(DateOnly date) => _strip.Contains(date) && _strip.IndexOf(date) <= _strip.Count - _vm.VisibleColumns;

    // Sizes the columns for the space available and keeps the same first day. Resizing the window calls
    // this for every step of the drag, so it does nothing unless the column width or body height really
    // changed (a height-only resize costs nothing), and it only repaints what depends on the width.
    private void Relayout(bool force)
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
        ColumnWidth = width;
        _bodyScroll.Margin = spare;
        _headerScroll.Margin = spare;
        _bodyRepeater.Height = BodyHeight;
        _headerRepeater.Height = DayHeaderHeight;
        _bodyLayout.MinItemWidth = ColumnWidth;
        _bodyLayout.MinItemHeight = BodyHeight;
        _headerLayout.MinItemWidth = ColumnWidth;
        _headerLayout.MinItemHeight = DayHeaderHeight;
        _bodyRepeater.InvalidateMeasure();
        _headerRepeater.InvalidateMeasure();
        RenderColumns();

        // First Layout: the corner and hour labels (they don't depend on the width), and a jump to 7:30 AM
        if (!_initialized)
        {
            _initialized = true;
            _pendingTop = Math.Max(0, 7.5 * HourHeight - 20);
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
    private void FollowBody()
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

    private static void Follow(UIElement element, string expression, Microsoft.UI.Composition.CompositionPropertySet body, Microsoft.UI.Composition.CompositionPropertySet side)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("body", body);
        animation.SetReferenceParameter("side", side);
        visual.StartAnimation("Translation", animation);
    }

    // One scroll to the exact day edge (animated for pagers and "today"; a new animated scroll retargets one
    // that's running)
    private void ScrollToIndex(int index, bool animate)
    {
        _pendingIndex = index;
        RetryPendingScroll(animate);
    }

    // A monitor with a different scale changes what a whole pixel is
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Relayout(force: false);

    // A Contrast Theme Turning On Or Off Redraws With The System's Colors (raised off the UI thread)
    private void OnContrastChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RenderRealized);

    private void OnBodyViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        _headerScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
        _gutterScroll.ChangeView(null, e.NextView.VerticalOffset, null, true);
    }

    private void OnBodyViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        SyncSides();
        if (!e.IsIntermediate)
        {
            _wheelTarget = double.NaN;
            _animating = false;
        }

        // A Box Being Dragged Keeps Its Press Corner On The Time It Started At
        if (_drag is { Kind: DragKind.Box, Started: true } box)
        {
            DrawBox(box);
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

    private static bool IsAt(double target, double offset) => Math.Abs(target - offset) < 0.5;

    // Catches the header and gutter up with the body when ViewChanging's sync was clamped (right after the
    // columns change width, the header's extent is still the old one)
    private void SyncSides()
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
    private void Settle()
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
    private void RetryPendingScroll(bool animate = false)
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

        // Arrived (a new view shows itself now, on its day and the morning)
        var target = pending * ColumnWidth;
        if (top is null && IsAt(target, _bodyScroll.HorizontalOffset))
        {
            _pendingIndex = null;
            _firstIndex = pending;
            Opacity = 1;
            Settle();
            return;
        }

        animate = animate && target <= _bodyScroll.ScrollableWidth;
        _animating = animate;
        ScrollIndicator.Hide(_bodyScroll);
        _bodyScroll.ChangeView(target, top, null, !animate);
    }

    // Tells the view model which days show (title, mini month, data), and redraws what follows the first
    // day. Runs for each new first day while scrolling, so it's skipped when the first day hasn't changed.
    private void ReportVisible()
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
            _gutter.Render(first, onlyIfChanged: true);
        }

        // The Local Zone's Label Follows The First Day Across A DST Change
        if (_initialized && LocalZoneLabel() != _localZoneLabel)
        {
            RenderCorner();
        }

    }

    // Where the body came to rest, for UI tests (the first day, the offsets, and the column width)
    // (and ";box=1" while a selection box shows)
    private void PublishOffset() =>
        AutomationProperties.SetItemStatus(this, string.Create(CultureInfo.InvariantCulture, $"first={_strip[_firstIndex]:yyyy-MM-dd};offset={_bodyScroll.HorizontalOffset:R};column={ColumnWidth:R};top={_bodyScroll.VerticalOffset:R}{(_box.Visibility == Visibility.Visible ? ";box=1" : "")}"));

    // The mouse wheel over the hour gutter or the day headers scrolls the body (vertically; a tilt wheel or
    // Shift+wheel horizontally), building on a scroll that's still animating so fast notches add up
    private void OnSideWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (TryZoom(e))
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Properties;
        var horizontal = point.IsHorizontalMouseWheel || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        var delta = point.MouseWheelDelta * WheelStep;
        e.Handled = true;

        if (horizontal)
        {
            // A tilt right is positive; Shift+wheel down is negative but means later days, as over the body
            _bodyScroll.ChangeView(_bodyScroll.HorizontalOffset + (point.IsHorizontalMouseWheel ? delta : -delta), null, null, false);
            return;
        }

        var from = double.IsNaN(_wheelTarget) ? _bodyScroll.VerticalOffset : _wheelTarget;
        _wheelTarget = Math.Clamp(from - delta, 0, _bodyScroll.ScrollableHeight);
        _bodyScroll.ChangeView(null, _wheelTarget, null, false);
    }

    // Ctrl+Wheel Zooms The Hours (the ScrollViewer never sees it, so the grid doesn't scroll)
    private void OnBodyWheel(object sender, PointerRoutedEventArgs e) => TryZoom(e);

    // Each full notch (smooth wheels send pieces) is one Ctrl+= / Ctrl+- step; the settings keep the height inside
    // its limits. A tilt wheel isn't a zoom: it scrolls sideways as usual
    private bool TryZoom(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Properties;
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) == 0 || point.IsHorizontalMouseWheel)
        {
            return false;
        }

        // A zero delta adds nothing
        e.Handled = true;
        var notches = _zoomNotches.Add(point.MouseWheelDelta);
        if (notches != 0)
        {
            _vm.ZoomBy(notches * 8);
        }

        return true;
    }

    // Everything that depends on the column width: the columns, the headers, and the all-day row
    private void RenderColumns()
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

    private void RenderAllDay(bool slide = false)
    {
        var count = _vm.VisibleColumns;
        var maxLanes = _allDayExpanded ? int.MaxValue : MaxCollapsedLanes;
        _allDay.Render(_strip, _firstIndex - count, count * 3, maxLanes);
        _allDay.Width = _strip.Count * ColumnWidth;
        _allDayExpand.Visibility = _allDay.LaneCount > MaxCollapsedLanes ? Visibility.Visible : Visibility.Collapsed;
        SizeAllDay(slide);
    }

    // The expand chevron points down while the row shows three lanes and turns up (half a turn, 167 ms) while it shows
    // them all. Its rotation is made and held here, never read back from the icon (a Native AOT trap)
    private readonly RotateTransform _allDayTurn = new();
    private static readonly TimeSpan AllDaySlide = TimeSpan.FromMilliseconds(167);

    private void ShowAllDayChevron(bool animate)
    {
        var angle = _allDayExpanded ? 180 : 0;
        var name = _allDayExpanded ? "Show fewer all-day events" : "Show all all-day events";
        AutomationProperties.SetName(_allDayExpand, name);
        ToolTipService.SetToolTip(_allDayExpand, name);
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            _allDayTurn.Angle = angle;
            return;
        }

        var turn = new DoubleAnimation
        {
            To = angle,
            Duration = AllDaySlide,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(turn, _allDayTurn);
        Storyboard.SetTargetProperty(turn, nameof(RotateTransform.Angle));
        new Storyboard { Children = { turn } }.Begin();
    }

    // Hides the all-day ghost, giving back a lane the row grew for it
    private void ClearAllDayGhost()
    {
        var grown = _allDay.GhostLanes > 0;
        _allDay.ClearGhost();
        if (grown)
        {
            SizeAllDay();
        }
    }

    // The row's height slide (the chevron's expand and collapse only), or null
    private Storyboard? _allDaySlide;

    // The all-day row's height: its shown lanes, or more while a ghost needs a lane below them. With slide, the row and
    // the corner grow or shrink to it over 167 ms; the header's scroller clips the chips below the row meanwhile, so
    // they're revealed (or covered) as it slides
    private void SizeAllDay(bool slide = false)
    {
        var maxLanes = _allDayExpanded ? int.MaxValue : MaxCollapsedLanes;
        var lanes = Math.Max(Math.Min(_allDay.LaneCount, maxLanes), _allDay.GhostLanes);

        // Never Shorter Than EmptyAllDayHeight: empty, the row is still there to double-click for a new all-day event
        var from = _allDay.ActualHeight;
        var height = Math.Max(EmptyAllDayHeight, lanes * AllDayLaneHeight + 4);

        // The Final Sizes Are Set First; A Slide Only Shows The Way There (stopping it leaves them)
        _allDaySlide?.Stop();
        _allDaySlide = null;
        _allDay.Height = height;
        Corner.Height = DayHeaderHeight + height;

        // Zone Labels Sit At The Bottom Of The Day-Header Band (the all-day row's corner keeps the expand chevron)
        _zoneLabels.Margin = new Thickness(0, 0, 0, height + 4);

        if (!slide || from <= 0 || from == height || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            return;
        }

        _allDaySlide = new Storyboard();
        foreach (var (target, offset) in new (FrameworkElement, double)[] { (_allDay, 0), (Corner, DayHeaderHeight) })
        {
            var grow = new DoubleAnimation
            {
                From = from + offset,
                To = height + offset,
                Duration = AllDaySlide,
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, target);
            Storyboard.SetTargetProperty(grow, nameof(Height));
            _allDaySlide.Children.Add(grow);
        }

        var running = _allDaySlide;
        running.Completed += (_, _) =>
        {
            running.Stop();
            if (ReferenceEquals(_allDaySlide, running))
            {
                _allDaySlide = null;
            }
        };
        running.Begin();
    }

    // This PC's zone as the first shown day's clock reads (EST in a January week, even in October), and the one drawn
    private string _localZoneLabel = "";

    private string LocalZoneLabel() => ZoneAbbreviation.For(_vm.Zone, DragMath.Instant(_strip[_firstIndex], 12 * 60, _vm.Zone));

    private void RenderCorner()
    {
        var dark = IsDark;
        _zoneLabels.Children.Clear();

        // Extra zones oldest first, then this PC's zone next to the days (the gutter's column order)
        var zones = _vm.Settings.TimeZones.Select(z => (z.Id, Label: TimeZoneCatalog.ShortLabel(z))).ToList();
        _localZoneLabel = LocalZoneLabel();
        zones.Add(("Local", _localZoneLabel));

        foreach (var (id, label) in zones)
        {
            var text = new TextBlock { Text = label, FontSize = 10, Width = ZoneColumnWidth - 8, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = LeafBrushes.SecondaryText(dark), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(text, $"ZoneLabel_{id}");
            _zoneLabels.Children.Add(text);
        }

        Corner.Width = zones.Count * ZoneColumnWidth;
        _weekNumber.Foreground = LeafBrushes.SecondaryText(dark);
        _allDayRule.Background = LeafBrushes.GridLine(dark);
        RenderWeekNumber();
    }

    // Numbered By The Middle Shown Day (ISO weeks start Monday, so a Sunday-start week's first day belongs to the week before)
    private void RenderWeekNumber() =>
        _weekNumber.Text = _vm.Settings.ShowWeekNumbers ? string.Create(CultureInfo.InvariantCulture, $"W{ViewNavigator.WeekNumber(_strip[_firstIndex + (_vm.VisibleColumns - 1) / 2])}") : "";

    private void RenderToday()
    {
        foreach (var column in _columns.Where(c => c.Date == _vm.Today))
        {
            column.RenderEventsAndNow();
        }
    }

    // =========================================================================
    // VIEW MODEL EVENTS
    // =========================================================================

    // Data only changes the columns and the all-day row. While a navigation is scrolling, painting waits
    // until it lands, so a month loading mid-scroll can't drop animation frames.
    private void OnOccurrencesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // A Dropped Resize's Card Now Draws From The Saved Event
        if (_holdResize)
        {
            _holdResize = false;
            _resized = null;
        }

        if (_pendingIndex is not null)
        {
            _renderDeferred = true;
            return;
        }

        RenderColumns();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // The All-Day Default Changed In Settings
        if (_vm.Settings.AllDayExpanded != _allDayDefault)
        {
            _allDayExpanded = _allDayDefault = _vm.Settings.AllDayExpanded;
            ShowAllDayChevron(animate: false);
        }

        if (_strip.SkipsWeekends == _vm.Settings.ShowWeekends)
        {
            BuildStrip(_strip[_firstIndex]);
        }

        // A View Switch: the new layout starts on its new first day, so nothing scrolls across from the old one
        if (_vm.SwitchingTo is { } switching)
        {
            if (!CanLead(switching))
            {
                BuildStrip(switching);
            }

            _firstIndex = _strip.IndexOf(switching);
            _pendingIndex = null;
            _animating = false;
        }

        _reportedIndex = -1;
        Relayout(force: true);
        RenderCorner();
        _gutter.Render(_strip[_firstIndex]);
        ReportVisible();
    }

    private void OnNavigateRequested(object? sender, DateOnly date)
    {
        if (_disposed)
        {
            return;
        }

        ScrollToDate(date, animate: true);
    }

    private void OnScrollToTimeRequested(object? sender, DateTimeOffset instant) => ScrollToTime(instant);

    // Only the overlay layer changes (people, their busy times)
    private void OnOverlayChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var column in _columns)
        {
            column.RenderOverlay();
        }
    }

    // Only the shared-availability slots changed
    private void OnShareChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Starting Or Stopping: every event changes its look (faded with diagonal lines while marking times)
        if (_sharingShown != _vm.IsSharing)
        {
            _sharingShown = _vm.IsSharing;
            RenderColumns();
            return;
        }

        foreach (var column in _columns)
        {
            column.RenderSlots();
        }
    }

    // Whether the events were last drawn for marking times to share (from the start: a grid built while scheduling draws
    // its days for it, so stopping must redraw them)
    private bool _sharingShown;

    // =========================================================================
    // DRAGGING
    // =========================================================================

    // How far the pointer must move before a press becomes a drag (less stays a click)
    private const double DragThreshold = 4;

    private enum DragKind { Move, Resize, Create, CreateAllDay, AllDay, Box, Nudge }

    private sealed class DragSession(DragKind kind, Point origin)
    {
        public DragKind Kind { get; } = kind;
        public Point Origin { get; } = origin;
        public CalendarOccurrence? Occurrence { get; init; }

        // A Read-Only Event's Card Or Chip Transform: pulled a little while dragged, sprung back on release
        public TranslateTransform? Pull { get; init; }
        public DateTimeOffset GrabbedAt { get; init; }
        public DateOnly GrabbedDay { get; init; }
        public DateOnly BoxDay { get; init; }
        public double BoxMinutes { get; init; }
        public Point BoxCorner { get; init; }
        public Point BoxPointer { get; set; }
        public bool Started { get; set; }
        public bool Duplicate { get; set; }

        // An Alt+Drag Of An Event You Can't Change: it only ever copies, even once Alt is let go
        public bool CopyOnly { get; init; }
        public (DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, bool InHeader)? Target { get; set; }
    }

    private DragSession? _drag;

    // Shift+Drag Box (spec 7.3), drawn on its own layer above the grid (the probe gives UI tests something to find)
    private readonly Canvas _boxLayer = new() { IsHitTestVisible = false };
    private readonly Border _box = SelectionBox();

    /// <summary>True between a press on something draggable and its release.</summary>
    public bool IsDragPending => _drag is not null;

    /// <summary>
    /// A timed event was pressed: dragging moves it, or resizes it from the bottom edge. An event you can't change gives
    /// a little (<paramref name="pull"/>, its card's transform) and springs back, with a notice saying why, unless Alt is
    /// down: then the drag makes a copy.
    /// </summary>
    public void BeginEventDrag(CalendarOccurrence occurrence, PointerRoutedEventArgs e, bool resize, TranslateTransform pull)
    {
        // Picking Times To Share: a drag that starts on an event picks times too (busy ones are left out when copying)
        if (_vm.IsSharing)
        {
            BeginCreateDrag(e);
            return;
        }

        // An Event You Can't Change Can Still Be Alt+Dragged To A Copy (in a calendar you can write to)
        var copyOnly = !_vm.CanEdit(occurrence);
        if (copyOnly && !KeyState.IsDown(Windows.System.VirtualKey.Menu))
        {
            _drag = new DragSession(DragKind.Nudge, e.GetCurrentPoint(this).Position) { Occurrence = occurrence, Pull = pull };
            return;
        }

        var (day, minutes) = BodyPosition(e);
        _drag = new DragSession(resize && !copyOnly ? DragKind.Resize : DragKind.Move, e.GetCurrentPoint(this).Position)
        {
            Occurrence = occurrence,
            GrabbedAt = DragMath.Instant(day, minutes, _vm.Zone),
            CopyOnly = copyOnly,
        };
    }

    /// <summary>Empty time was pressed: dragging creates an event over the range.</summary>
    public void BeginCreateDrag(PointerRoutedEventArgs e)
    {
        var (day, minutes) = BodyPosition(e);

        // Shift: Box Select Instead Of Create (spec 7.3). While editing it does nothing (no box, no new event);
        // while picking times to share it picks a time like any drag
        if (KeyState.IsDown(Windows.System.VirtualKey.Shift) && !_vm.IsSharing)
        {
            if (_vm.Editing is null)
            {
                _drag = new DragSession(DragKind.Box, e.GetCurrentPoint(this).Position) { BoxDay = day, BoxMinutes = minutes, BoxCorner = e.GetCurrentPoint(_bodyRepeater).Position };
            }

            return;
        }

        _drag = new DragSession(DragKind.Create, e.GetCurrentPoint(this).Position) { GrabbedAt = DragMath.Instant(day, minutes, _vm.Zone) };
    }

    /// <summary>Empty all-day space was pressed: dragging across days makes a new all-day event over them.</summary>
    public void BeginAllDayCreateDrag(PointerRoutedEventArgs e)
    {
        // Picking times to share is about hours, and a box select needs the grid's times: neither starts here
        if (_vm.IsSharing || KeyState.IsDown(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        _drag = new DragSession(DragKind.CreateAllDay, e.GetCurrentPoint(this).Position) { GrabbedDay = DayAt(e.GetCurrentPoint(_allDay).Position.X) };
    }

    /// <summary>
    /// An all-day chip was pressed: dragging moves it across days, or into the grid to become timed. An event you can't
    /// change gives a little (<paramref name="pull"/>, the chip's transform) and springs back, with a notice saying why,
    /// unless Alt is down: then the drag makes a copy.
    /// </summary>
    public void BeginAllDayDrag(CalendarOccurrence occurrence, PointerRoutedEventArgs e, TranslateTransform pull)
    {
        var copyOnly = !_vm.CanEdit(occurrence);
        if (copyOnly && !KeyState.IsDown(Windows.System.VirtualKey.Menu))
        {
            _drag = new DragSession(DragKind.Nudge, e.GetCurrentPoint(this).Position) { Occurrence = occurrence, Pull = pull };
            return;
        }

        _drag = new DragSession(DragKind.AllDay, e.GetCurrentPoint(this).Position)
        {
            Occurrence = occurrence,
            GrabbedDay = DayAt(e.GetCurrentPoint(_allDay).Position.X),
            CopyOnly = copyOnly,
        };
    }

    /// <summary>Double-click on empty time: a new one-hour event there (nothing while picking times to share; only a drag adds a time).</summary>
    public void CreateAt(DateOnly day, double y)
    {
        if (_vm.IsSharing)
        {
            return;
        }

        var start = DragMath.SnapOnDay(day, y / HourHeight * 60, _vm.Zone);
        _vm.BeginCreate(start, start + DragMath.DefaultLength, isAllDay: false);
    }

    /// <summary>Double-click on empty space in the all-day row: a new all-day event on that day (nothing while picking times to share).</summary>
    public void CreateAllDayAt(double x)
    {
        if (_vm.IsSharing)
        {
            return;
        }

        var start = new DateTimeOffset(DayAt(x).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        _vm.BeginCreate(start, start.AddDays(1), isAllDay: true);
    }

    /// <summary>Drops a pending or running drag without changing anything (Esc). Returns true when there was one.</summary>
    public bool CancelDrag()
    {
        if (_drag is null)
        {
            return false;
        }

        if (_drag is { Kind: DragKind.Nudge, Started: true, Pull: { } pulled })
        {
            ElasticNudge.SnapBack(pulled);
        }

        _drag = null;
        HideBox();
        ShowNewEventGhost();
        ReleasePointerCaptures();
        return true;
    }

    private void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
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

        // A Read-Only Event Gives A Little With The Pointer
        if (drag.Kind == DragKind.Nudge)
        {
            ElasticNudge.Pull(drag.Pull!, at.X - drag.Origin.X, at.Y - drag.Origin.Y);
            e.Handled = true;
            return;
        }

        // Box: drawn from the press to the pointer (ponytail: selection applies on release, not live; live highlighting re-renders every move)
        e.Handled = true;
        if (drag.Kind == DragKind.Box)
        {
            // The Accent Is Read Once Per Drag (so theme and high-contrast changes apply to the next box)
            if (_box.Visibility == Visibility.Collapsed)
            {
                StyleBox(_box, IsDark);
            }

            drag.BoxPointer = at;
            DrawBox(drag);
            return;
        }

        // Redraw The Ghost Only When The Snapped Target Or Copy Mode Changes
        var target = TargetFor(drag, e);
        var duplicate = drag.CopyOnly || (drag.Kind is DragKind.Move or DragKind.AllDay && KeyState.IsDown(Windows.System.VirtualKey.Menu));
        if (target.Equals(drag.Target) && duplicate == drag.Duplicate)
        {
            return;
        }

        drag.Target = target;
        drag.Duplicate = duplicate;

        // Resizing Resizes The Card Itself
        if (drag.Kind == DragKind.Resize && target is { } resize)
        {
            ShowResize(drag.Occurrence!, resize.End);
            return;
        }

        // Creating: the range sits beside the events already there (picking times to share keeps the plain ghost)
        if (drag.Kind == DragKind.Create && !_vm.IsSharing && target is { } create && StandInFor(create.Start, create.End, isAllDay: false) is { } standIn)
        {
            ClearAllDayGhost();
            SetPreviews(standIn, null);
            return;
        }

        ShowGhost(target, duplicate);
    }

    private void OnDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        HideBox();
        ReleasePointerCapture(e.Pointer);

        // A Read-Only Event Springs Back, And The Notice Says Why It Stayed
        if (drag.Kind == DragKind.Nudge)
        {
            if (drag.Started)
            {
                e.Handled = true;
                ElasticNudge.SnapBack(drag.Pull!);
                _vm.ExplainReadOnly(drag.Occurrence!);
            }

            return;
        }

        // A Resize That Changes The End Keeps Its Card At The New Size Until The Saved Change Redraws The Grid
        _holdResize = drag is { Kind: DragKind.Resize, Started: true, Target: { } resized } && resized.End != drag.Occurrence!.End;
        ShowNewEventGhost();
        if (!drag.Started)
        {
            return;
        }

        // Box: the timed events under it on the days showing (a hidden weekend's events stay out); Ctrl adds
        if (drag.Kind == DragKind.Box)
        {
            e.Handled = true;
            var (day, minutes) = BodyPosition(e);
            var days = _strip.Between(drag.BoxDay, day);
            _vm.SelectBox(BoxSelection.InTimeBox(_vm.OnDays(days), days, drag.BoxMinutes, minutes, _vm.Zone), add: KeyState.IsDown(Windows.System.VirtualKey.Control));
            return;
        }

        if (drag.Target is not { } target)
        {
            return;
        }

        e.Handled = true;
        // Sharing Availability: the range is a time to share, not a new event
        if (drag.Kind == DragKind.Create && _vm.IsSharing)
        {
            _vm.AddShareSlot(target.Start, target.End);
            return;
        }

        if (drag.Kind is DragKind.Create or DragKind.CreateAllDay)
        {
            _vm.BeginCreate(target.Start, target.End, isAllDay: drag.Kind == DragKind.CreateAllDay);
            return;
        }

        // Dropped Where It Started: nothing to do (no scope question, no copy on top of the original)
        var o = drag.Occurrence!;
        if (target.Start == o.Start && target.End == o.End && target.IsAllDay == o.IsAllDay)
        {
            return;
        }

        // Alt+Drag Duplicates (a resize always resizes). Alt is also read as the pointer moves: by the time the
        // release is handled, the key state can already show Alt up when it's let go right after the button
        if ((drag.Duplicate || drag.CopyOnly || KeyState.IsDown(Windows.System.VirtualKey.Menu)) && drag.Kind != DragKind.Resize)
        {
            _vm.Duplicate(o, target.Start, target.End, target.IsAllDay);
            return;
        }

        _vm.Fire(() => _vm.MoveAsync(o, target.Start, target.End, target.IsAllDay), "calendar.move.failed");
    }

    private (DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, bool InHeader)? TargetFor(DragSession drag, PointerRoutedEventArgs e)
    {
        var zone = _vm.Zone;
        var (day, minutes) = BodyPosition(e);
        var pointerAt = DragMath.Instant(day, minutes, zone);
        var o = drag.Occurrence;

        switch (drag.Kind)
        {
            case DragKind.Move:
                var (moveStart, moveEnd) = DragMath.MoveTimed(o!, drag.GrabbedAt, pointerAt, zone);
                return (moveStart, moveEnd, false, false);

            case DragKind.Resize:
                return (o!.Start, DragMath.ResizeEnd(o, pointerAt, zone), false, false);

            case DragKind.Create:
                var (createStart, createEnd) = DragMath.CreateRange(drag.GrabbedAt, pointerAt, zone);
                return (createStart, createEnd, false, false);

            case DragKind.CreateAllDay:
                var (allDayStart, allDayEnd) = DragMath.AllDayRange(drag.GrabbedDay, DayAt(e.GetCurrentPoint(_allDay).Position.X));
                return (allDayStart, allDayEnd, true, true);

            default:
                // Over The Grid: a one-hour timed event there
                if (e.GetCurrentPoint(_bodyScroll).Position.Y >= 0)
                {
                    var start = DragMath.Snap(pointerAt, zone);
                    return (start, start + DragMath.DefaultLength, false, false);
                }

                // Over The All-Day Row: the same event, moved by whole days
                var days = DayAt(e.GetCurrentPoint(_allDay).Position.X).DayNumber - drag.GrabbedDay.DayNumber;
                var (shiftStart, shiftEnd) = DragMath.ShiftDays(o!, days, zone);
                return (shiftStart, shiftEnd, o!.IsAllDay, true);
        }
    }

    private void ShowGhost((DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, bool InHeader)? target, bool duplicate)
    {
        var copy = duplicate ? "+ Copy  " : "";

        // The Dragged Event's Color And Title Ride Along (a new event being drawn out has neither yet)
        var dragged = _drag?.Occurrence;
        var accent = dragged is null ? null : EventColors.ResolveAccent(dragged.ColorId, dragged.CalendarColor);
        var title = dragged is null ? "" : dragged.Title + "\n";
        SetPreviews(null, HeldResize);

        // All-Day Row
        if (target is { InHeader: true } header)
        {
            foreach (var column in _columns)
            {
                column.ClearGhost();
            }

            var first = header.IsAllDay ? DateOnly.FromDateTime(header.Start.UtcDateTime) : LocalDate(header.Start);
            var last = header.IsAllDay ? DateOnly.FromDateTime(header.End.UtcDateTime).AddDays(-1) : LocalDate(header.End.AddTicks(-1));
            _allDay.SetGhost(first, last < first ? first : last, duplicate, duplicate ? null : _drag?.Occurrence?.Key, accent);
            SizeAllDay();
            return;
        }

        // Grid Columns: the part of the range that falls on each day
        ClearAllDayGhost();
        foreach (var column in _columns)
        {
            if (target is not { } t)
            {
                column.ClearGhost();
                continue;
            }

            var dayStart = OccurrenceQuery.LocalMidnight(column.Date, _vm.Zone);
            var dayEnd = OccurrenceQuery.LocalMidnight(column.Date.AddDays(1), _vm.Zone);
            var end = t.End > t.Start ? t.End : t.Start + TimeSpan.FromMinutes(DragMath.SnapMinutes);
            if (t.Start >= dayEnd || end <= dayStart)
            {
                column.ClearGhost();
                continue;
            }

            var top = t.Start <= dayStart ? 0 : MinutesIntoDay(t.Start);
            var bottom = end >= dayEnd ? 24 * 60 : MinutesIntoDay(end);
            column.SetGhost(top, Math.Max(bottom, top + DragMath.SnapMinutes), copy + title + (t.Start >= dayStart ? TimeLabels.GridRange(t.Start, t.End, _vm.Zone, _vm.Settings.Use24HourTime) : ""), accent);
        }
    }

    // The press corner stays on the time it was pressed at (it scrolls with the body); the other follows the pointer
    private void DrawBox(DragSession drag)
    {
        var showing = _box.Visibility == Visibility.Visible;
        ShowBox(_box, _bodyRepeater.TransformToVisual(this).TransformPoint(drag.BoxCorner), drag.BoxPointer);
        if (!showing)
        {
            PublishOffset();
        }
    }

    private void HideBox()
    {
        if (_box.Visibility == Visibility.Collapsed)
        {
            return;
        }

        _box.Visibility = Visibility.Collapsed;
        PublishOffset();
    }

    /// <summary>A hidden selection box (the time grid's and the month view's), with an automation probe UI tests can find.</summary>
    internal static Border SelectionBox()
    {
        var probe = new TextBlock();
        AutomationProperties.SetAutomationId(probe, "SelectionBox");
        AutomationProperties.SetName(probe, "Selection box");
        return new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Visibility = Visibility.Collapsed, Child = probe };
    }

    /// <summary>Colors a selection box with the accent, once per drag.</summary>
    internal static void StyleBox(Border box, bool dark)
    {
        var accent = LeafBrushes.Accent(dark);
        box.BorderBrush = accent;
        box.Background = accent;
        box.Opacity = 0.25;
    }

    /// <summary>Shows a selection box between two corners in its canvas.</summary>
    internal static void ShowBox(Border box, Point a, Point b)
    {
        box.Width = Math.Abs(b.X - a.X);
        box.Height = Math.Abs(b.Y - a.Y);
        box.Visibility = Visibility.Visible;
        Canvas.SetLeft(box, Math.Min(a.X, b.X));
        Canvas.SetTop(box, Math.Min(a.Y, b.Y));
    }

    private void ClearGhosts()
    {
        foreach (var column in _columns)
        {
            column.ClearGhost();
        }

        ClearAllDayGhost();
    }

    // =========================================================================
    // NEW EVENT GHOST
    // =========================================================================

    // The editor open on a new event (dragged out, double-clicked, or C), whose times the ghost follows
    private EventEditorViewModel? _newEvent;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.Editing))
        {
            Track(_vm.Editing is { IsNew: true } editor ? editor : null);
        }
    }

    // Follows a new event's editor (or none)
    private void Track(EventEditorViewModel? editor)
    {
        _newEvent?.PropertyChanged -= OnNewEventChanged;
        _newEvent = editor;
        _newEvent?.PropertyChanged += OnNewEventChanged;
        ShowNewEventGhost();
    }

    private void OnNewEventChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EventEditorViewModel.StartDate) or nameof(EventEditorViewModel.StartTime)
            or nameof(EventEditorViewModel.EndDate) or nameof(EventEditorViewModel.EndTime)
            or nameof(EventEditorViewModel.IsAllDay) or nameof(EventEditorViewModel.TimeZoneId))
        {
            ShowNewEventGhost();
        }
    }

    // While the editor is open on a new event, its time range stays drawn on the grid as a ghost (the range you dragged
    // out, following edits to its times) until it's saved or canceled; otherwise no ghost. A drag in progress draws its own
    private void ShowNewEventGhost()
    {
        if (_disposed || _drag is { Started: true })
        {
            return;
        }

        if (_newEvent?.ToDraft() is not { } draft)
        {
            SetPreviews(null, HeldResize);
            ClearGhosts();
            return;
        }

        // A Timed Range Sits Beside The Events Already There, As One More Overlapping Column
        if (StandInFor(draft.Start, draft.End, draft.IsAllDay) is { } standIn)
        {
            ClearAllDayGhost();
            SetPreviews(standIn, HeldResize);
            return;
        }

        ShowGhost((draft.Start, draft.End, draft.IsAllDay, draft.IsAllDay), duplicate: false);
    }

    // =========================================================================
    // STAND-INS
    // =========================================================================

    // Laid out with the day's events by the columns: a new event's range (drawn as the ghost, one more overlapping
    // column) and an event being resized (its own card, drawn at the new end)
    private CalendarOccurrence? _standIn;
    private CalendarOccurrence? _resized;

    // A dropped resize: its card keeps the new end until the saved change redraws the grid
    // (ponytail: a save that fails without a redraw leaves the card at the new size until the next data change)
    private bool _holdResize;

    private CalendarOccurrence? HeldResize => _holdResize ? _resized : null;

    /// <summary>The new event's range as the columns lay it out, or null.</summary>
    internal CalendarOccurrence? StandIn => _standIn;

    /// <summary>A day's events with the stand-ins: the resized event in place of the original, and the new event's range.</summary>
    internal IEnumerable<CalendarOccurrence> WithPreviews(IReadOnlyList<CalendarOccurrence> day)
    {
        IEnumerable<CalendarOccurrence> events = day;
        if (_resized is { } resized)
        {
            events = events.Where(o => o.Key != resized.Key).Append(resized);
        }

        return _standIn is { } standIn ? events.Append(standIn) : events;
    }

    // A timed range the columns can lay out (one of 24 hours or more draws as the plain ghost on every day it covers).
    // Its account sorts after every real one, so at the same start and length it takes the column on the right
    private static CalendarOccurrence? StandInFor(DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        if (isAllDay)
        {
            return null;
        }

        var standIn = new CalendarOccurrence("￿", "", "NewEvent", null, null, start, end > start ? end : start + TimeSpan.FromMinutes(DragMath.SnapMinutes), false, "", default, default, "", null, false, false);
        return SpanLayout.IsSpanning(standIn) ? null : standIn;
    }

    // Redraws the events when a stand-in changes (a ghost left by the old new-event range is cleared)
    private void SetPreviews(CalendarOccurrence? standIn, CalendarOccurrence? resized)
    {
        if (Equals(standIn, _standIn) && Equals(resized, _resized))
        {
            return;
        }

        var hadStandIn = _standIn is not null;
        _standIn = standIn;
        _resized = resized;
        foreach (var column in _columns)
        {
            if (hadStandIn && standIn is null)
            {
                column.ClearGhost();
            }

            column.RenderEventsAndNow();
        }
    }

    // The card being resized is drawn at its new end, and the new time shows under that end
    private void ShowResize(CalendarOccurrence occurrence, DateTimeOffset end)
    {
        var resized = occurrence with { End = end };
        ClearGhosts();
        SetPreviews(null, SpanLayout.IsSpanning(resized) ? null : resized);

        var label = TimeLabels.GridRange(resized.Start, end, _vm.Zone, _vm.Settings.Use24HourTime);
        foreach (var column in _columns)
        {
            var dayStart = OccurrenceQuery.LocalMidnight(column.Date, _vm.Zone);
            var dayEnd = OccurrenceQuery.LocalMidnight(column.Date.AddDays(1), _vm.Zone);
            if (end > dayStart && end <= dayEnd)
            {
                column.SetTimeLabel(end == dayEnd ? 24 * 60 : MinutesIntoDay(end), label);
            }
        }
    }

    // Day and minutes past local midnight under the pointer, in the day columns
    private (DateOnly Day, double Minutes) BodyPosition(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_bodyRepeater).Position;
        return (DayAt(point.X), Math.Clamp(point.Y / HourHeight * 60, 0, 24 * 60));
    }

    // The strip's day at an x position (the repeater and the all-day row both lay the strip out from 0)
    private DateOnly DayAt(double x) => _strip[Math.Clamp((int)Math.Floor(x / ColumnWidth), 0, _strip.Count - 1)];

    /// <summary>Minutes past local midnight (the wall clock of the zone on screen).</summary>
    internal double MinutesIntoDay(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _vm.Zone).TimeOfDay.TotalMinutes;

    private DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _vm.Zone).DateTime);

    // =========================================================================
    // RECYCLING
    // =========================================================================

    /// <summary>An item in the strip (a class, so WinRT can hold it).</summary>
    public sealed class DayItem(DateOnly date)
    {
        /// <summary>The day.</summary>
        public DateOnly Date { get; } = date;
    }

    private sealed partial class DayColumnFactory(TimeGridView owner) : IElementFactory
    {
        private readonly Stack<DayColumn> _pool = new();
        private readonly ItemPins _pins = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var column = _pool.Count > 0 ? _pool.Pop() : new DayColumn(owner);
            column.Bind(((DayItem)args.Data).Date);
            _pins.Pin(column, args.Data);
            return column;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            var column = (DayColumn)args.Element;
            _pins.Unpin(column);
            _pool.Push(column);
        }
    }

    private sealed partial class DayHeaderFactory(TimeGridView owner) : IElementFactory
    {
        private readonly Stack<DayHeaderCell> _pool = new();
        private readonly ItemPins _pins = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var cell = _pool.Count > 0 ? _pool.Pop() : new DayHeaderCell(owner);
            cell.Bind(((DayItem)args.Data).Date);
            _pins.Pin(cell, args.Data);
            return cell;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            var cell = (DayHeaderCell)args.Element;
            _pins.Unpin(cell);
            _pool.Push(cell);
        }
    }
}
