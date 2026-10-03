using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The all-day row: all-day and 24-hour-plus events as bars across day columns, packed into lanes
/// with <see cref="SpanLayout"/>. Only a window of columns around the visible ones is drawn. Chips can be
/// dragged across days, or down into the grid to become timed. Like the time grid's columns, each day keeps a strip of
/// empty space on its right (<see cref="SpareWidth"/>), so there's always room to drag out a new all-day event. The day
/// dividers are drawn here for the day headers too, one line per day from just under the header's top (fading in) to
/// the grid.
/// </summary>
public sealed partial class AllDayCanvas : Canvas
{
    /// <summary>Empty space kept on the right of each day's chips (the time grid's columns keep the same 10 px).</summary>
    public const double SpareWidth = 10;

    readonly TimeGridView _owner;
    readonly List<(Border Chip, TextBlock Text, TranslateTransform Pull)> _chips = [];

    // The drag ghost's shadow lands on this (a receiver can't be the ghost's ancestor)
    readonly Rectangle _floor = new() { IsHitTestVisible = false };
    readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _dividers = [];
    readonly Dictionary<Border, CalendarOccurrence> _shown = [];

    // Every laid-out event's days (strip indexes) and lane, shown or not, so the ghost can find a free lane
    readonly List<(int First, int Last, int Lane, string Key)> _lanes = [];
    readonly Border _ghost = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), Height = TimeGridView.AllDayLaneHeight - 3, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly TextBlock _ghostLabel = new() { FontSize = 11, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Text = "+ Copy" };
    DayStrip? _strip;

    /// <summary>Creates the row owned by <paramref name="owner"/>. Its background is hit-testable, so a click on empty all-day space reaches the page and clears the selection, and a double-click there creates an all-day event.</summary>
    public AllDayCanvas(TimeGridView owner)
    {
        _owner     = owner;
        Background = LeafBrushes.Transparent;
        _ghost.Child = _ghostLabel;
        _floor.Fill = LeafBrushes.Transparent;
        Children.Insert(0, _floor);
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(_floor);
        _ghost.Shadow      = shadow;
        _ghost.Translation = new System.Numerics.Vector3(0, 0, 24);
        Children.Add(_ghost);
        SetZIndex(_ghost, 20);

        // Drag Across Empty Space: a new all-day event over those days (a press on a chip is the chip's own drag)
        PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            {
                _owner.BeginAllDayCreateDrag(e);
            }
        };

        // The Day Dividers Reach The Row's Bottom, However Many Lanes It Shows
        SizeChanged += (_, _) => SizeDividers();

        // Double-Click Empty Space: a new all-day event that day (chips mark their own double-clicks handled)
        DoubleTapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this))
            {
                _owner.CreateAllDayAt(e.GetPosition(this).X);
                e.Handled = true;
            }
        };
    }

    /// <summary>Lanes the showing ghost needs (its lane and every lane above it), or 0 with no ghost.</summary>
    public int GhostLanes { get; private set; }

    /// <summary>Lanes used by the last render.</summary>
    public int LaneCount { get; private set; }

    /// <summary>Draws columns <c>[firstIndex, firstIndex + count)</c> of the strip, showing at most <paramref name="maxLanes"/> lanes.</summary>
    public void Render(DayStrip strip, int firstIndex, int count, int maxLanes)
    {
        _strip = strip;
        var vm      = _owner.ViewModel;
        var dark    = _owner.IsDark;
        var width   = _owner.ColumnWidth;
        var first   = Math.Max(0, firstIndex);
        var columns = Enumerable.Range(first, Math.Min(count, strip.Count - first)).Select(i => strip[i]).ToList();
        var items   = columns.SelectMany(vm.Cache.ForDay).DistinctBy(o => o.Key).ToList();
        var blocks  = SpanLayout.Layout(columns, items, vm.Zone, includeTimed: false);

        RenderDividers(first, columns.Count, dark);

        LaneCount = blocks.Count == 0 ? 0 : blocks.Max(b => b.Lane) + 1;
        _lanes.Clear();
        _lanes.AddRange(blocks.Select(b => (first + b.FirstColumn, first + b.FirstColumn + b.ColumnSpan - 1, b.Lane, b.Occurrence.Key)));
        var shown = blocks.Where(b => b.Lane < maxLanes).ToList();

        while (_chips.Count < shown.Count)
        {
            AddChip();
        }

        _shown.Clear();
        for (var i = 0; i < _chips.Count; i++)
        {
            var (chip, text, pull) = _chips[i];
            if (i >= shown.Count)
            {
                chip.Visibility = Visibility.Collapsed;
                continue;
            }

            pull.X = 0;
            pull.Y = 0;

            var b       = shown[i];
            var faded   = vm.IsPast(b.Occurrence) || vm.IsSharing; // marking times to share fades every event
            var palette = LeafBrushes.CardPalette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark, faded, vm.IsSelected(b.Occurrence));
            var start   = SpanLayout.CoveredDates(b.Occurrence, vm.Zone).First;
            _shown[chip] = b.Occurrence;

            chip.Visibility      = Visibility.Visible;
            chip.Width           = Math.Max(b.ColumnSpan * width - 2 - SpareWidth, 8);
            chip.Height          = TimeGridView.AllDayLaneHeight - 3;
            chip.Background      = LeafBrushes.FromHex(palette.Fill);
            chip.BorderBrush     = LeafBrushes.FromHex(palette.Accent);
            chip.BorderThickness = LeafBrushes.CardBorder(vm.IsSelected(b.Occurrence));
            text.Text            = (b.ContinuesBefore ? "‹ " : "") + b.Occurrence.Title + (b.ContinuesAfter ? " ›" : "");
            text.Foreground      = LeafBrushes.FromHex(palette.Text);

            SetLeft(chip, (first + b.FirstColumn) * width + 2);
            SetTop(chip, b.Lane * TimeGridView.AllDayLaneHeight + 2);
            AutomationProperties.SetAutomationId(chip, string.Create(CultureInfo.InvariantCulture, $"AllDay_{b.Occurrence.EventId}_{start:yyyyMMdd}"));
            AutomationProperties.SetName(chip, vm.CardName(b.Occurrence, "All day"));

            // Past Events Fade (still readable)
            var past = vm.IsPast(b.Occurrence);
            AutomationProperties.SetItemStatus(chip, past ? "Past" : "");
        }
    }

    /// <summary>
    /// Shows where a dragged or new all-day event would land (marked "+ Copy" when <paramref name="copy"/>, for Alt+drag),
    /// in the first lane its days leave free, so it never covers an event. <paramref name="skipKey"/> is the dragged
    /// event's own key (its lane counts as free). <see cref="GhostLanes"/> says how tall the row must be to show it.
    /// </summary>
    public void SetGhost(DateOnly first, DateOnly last, bool copy = false, string? skipKey = null, string? accentHex = null)
    {
        _floor.Width  = ActualWidth;
        _floor.Height = ActualHeight;
        if (_strip is not { } strip)
        {
            return;
        }

        var width = _owner.ColumnWidth;
        var from  = strip.IndexOf(first);
        var to    = Math.Max(from, strip.IndexOf(last));
        _ghost.Width           = Math.Max((to - from + 1) * width - 2 - SpareWidth, 8);
        (_ghost.BorderBrush, _ghost.Background) = accentHex is null
            ? (LeafBrushes.Accent(_owner.IsDark), LeafBrushes.Hover(_owner.IsDark))
            : LeafBrushes.GhostPalette(accentHex, _owner.IsDark);
        _ghostLabel.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;

        // First Free Lane Over The Ghost's Days
        var taken = _lanes.Where(l => l.Key != skipKey && l.First <= to && l.Last >= from).Select(l => l.Lane).ToHashSet();
        var lane  = 0;
        while (taken.Contains(lane))
        {
            lane++;
        }

        GhostLanes = lane + 1;
        SetLeft(_ghost, from * width + 2);
        SetTop(_ghost, lane * TimeGridView.AllDayLaneHeight + 2);
        _ghost.Visibility  = Visibility.Visible;
    }

    /// <summary>Hides the ghost.</summary>
    public void ClearGhost()
    {
        _ghost.Visibility = Visibility.Collapsed;
        GhostLanes        = 0;
    }

    // The day dividers reach up through the day headers from here, so each day has one line from the header to the
    // grid (drawn in one place, they can't land a pixel apart). They start a little below the header's top and fade in
    // over their first few pixels, so the tips trail off instead of ending square
    const double DividerHeaderReach = TimeGridView.DayHeaderHeight - 8;
    const double DividerFade        = 16;

    // The dividers' fading brush and the line color it was made for
    LinearGradientBrush? _dividerBrush;
    Windows.UI.Color _dividerColor;

    // One divider on the left edge of each drawn day, from the day header down through the row
    void RenderDividers(int first, int count, bool dark)
    {
        while (_dividers.Count < count)
        {
            var line = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, IsHitTestVisible = false };
            _dividers.Add(line);
            Children.Insert(0, line);
        }

        // One Brush Per Line Color (this runs on every scroll step; a new brush each time cost an allocation and a repaint per line)
        var width = _owner.ColumnWidth;
        var color = LeafBrushes.GridLine(dark).Color;
        if (_dividerBrush is null || _dividerColor != color)
        {
            _dividerBrush = FadingDivider(color);
            _dividerColor = color;
        }

        for (var i = 0; i < _dividers.Count; i++)
        {
            var line = _dividers[i];
            line.Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            if (line.Fill != _dividerBrush)
            {
                line.Fill = _dividerBrush;
            }

            SetLeft(line, (first + i) * width);
            SetTop(line, -DividerHeaderReach);
        }

        SizeDividers();
    }

    // The line color, clear at the tip and full strength DividerFade below it (in DIPs, whatever the line's length)
    static LinearGradientBrush FadingDivider(Windows.UI.Color color)
    {
        var clear = color with { A = 0 };
        return new LinearGradientBrush
        {
            MappingMode   = BrushMappingMode.Absolute,
            StartPoint    = new Windows.Foundation.Point(0, 0),
            EndPoint      = new Windows.Foundation.Point(0, DividerFade),
            GradientStops = { new GradientStop { Color = clear, Offset = 0 }, new GradientStop { Color = color, Offset = 1 } },
        };
    }

    void SizeDividers()
    {
        var height = DividerHeaderReach + (double.IsNaN(Height) ? ActualHeight : Height);
        foreach (var line in _dividers)
        {
            line.Height = height;
        }
    }

    void AddChip()
    {
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var pull = new TranslateTransform();
        var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0), Child = text, RenderTransform = pull };

        chip.Tapped += (_, e) =>
        {
            if (_shown.TryGetValue(chip, out var o))
            {
                KeyState.SelectClicked(_owner.ViewModel, o);
            }

            e.Handled = true;
        };

        // Right-Click Menu, And The Event Under The Mouse (X toggles it)
        chip.RightTapped += (_, e) =>
        {
            if (_shown.TryGetValue(chip, out var o))
            {
                EventContextMenu.Show(chip, e.GetPosition(chip), _owner.ViewModel, o);
            }

            e.Handled = true;
        };
        // Hover Tooltip: title, time, and location, filled in as the pointer arrives (the location is a lookup)
        var tip = new ToolTip();
        ToolTipService.SetToolTip(chip, tip);
        chip.PointerEntered += (_, _) =>
        {
            var vm = _owner.ViewModel;
            vm.PointerEvent = _shown.GetValueOrDefault(chip);
            if (vm.PointerEvent is { } o)
            {
                tip.Content = vm.HoverText(o, o.IsAllDay ? "All day" : TimeLabels.Range(o.Start, o.End, vm.Zone, vm.Settings.Use24HourTime));
            }
        };
        chip.PointerExited  += (_, _) => _owner.ViewModel.PointerEvent = null;
        chip.DoubleTapped += (_, e) =>
        {
            if (_shown.TryGetValue(chip, out var o))
            {
                _owner.ViewModel.Select(o);
                _owner.ViewModel.BeginEdit();
            }

            e.Handled = true;
        };
        chip.PointerPressed += (_, e) =>
        {
            if (_shown.TryGetValue(chip, out var o) && e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            {
                _owner.BeginAllDayDrag(o, e, pull);
            }
        };

        _chips.Add((chip, text, pull));
        Children.Add(chip);
    }
}
