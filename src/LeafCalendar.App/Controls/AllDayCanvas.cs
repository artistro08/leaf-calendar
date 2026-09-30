using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The all-day row: all-day and 24-hour-plus events as bars across day columns, packed into lanes
/// with <see cref="SpanLayout"/>. Only a window of columns around the visible ones is drawn. Chips can be
/// dragged across days, or down into the grid to become timed.
/// </summary>
public sealed partial class AllDayCanvas : Canvas
{
    readonly TimeGridView _owner;
    readonly List<(Border Chip, TextBlock Text)> _chips = [];
    readonly Dictionary<Border, CalendarOccurrence> _shown = [];
    readonly Border _ghost = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), Height = TimeGridView.AllDayLaneHeight - 3, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    DayStrip? _strip;

    /// <summary>Creates the row owned by <paramref name="owner"/>. Its background is hit-testable, so a click on empty all-day space reaches the page and clears the selection.</summary>
    public AllDayCanvas(TimeGridView owner)
    {
        _owner     = owner;
        Background = LeafBrushes.Transparent;
        Children.Add(_ghost);
        SetZIndex(_ghost, 20);
    }

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

        LaneCount = blocks.Count == 0 ? 0 : blocks.Max(b => b.Lane) + 1;
        var shown = blocks.Where(b => b.Lane < maxLanes).ToList();

        while (_chips.Count < shown.Count)
        {
            AddChip();
        }

        _shown.Clear();
        for (var i = 0; i < _chips.Count; i++)
        {
            var (chip, text) = _chips[i];
            if (i >= shown.Count)
            {
                chip.Visibility = Visibility.Collapsed;
                continue;
            }

            var b       = shown[i];
            var palette = EventColors.Palette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark);
            var start   = SpanLayout.CoveredDates(b.Occurrence, vm.Zone).First;
            _shown[chip] = b.Occurrence;

            chip.Visibility      = Visibility.Visible;
            chip.Width           = Math.Max(b.ColumnSpan * width - 4, 8);
            chip.Height          = TimeGridView.AllDayLaneHeight - 3;
            chip.Background      = LeafBrushes.FromHex(palette.Fill);
            chip.BorderBrush     = LeafBrushes.FromHex(palette.Accent);
            chip.BorderThickness = new Thickness(vm.IsSelected(b.Occurrence) ? 2 : 0);
            text.Text            = (b.ContinuesBefore ? "‹ " : "") + b.Occurrence.Title + (b.ContinuesAfter ? " ›" : "");
            text.Foreground      = LeafBrushes.FromHex(palette.Text);

            SetLeft(chip, (first + b.FirstColumn) * width + 2);
            SetTop(chip, b.Lane * TimeGridView.AllDayLaneHeight + 2);
            AutomationProperties.SetAutomationId(chip, string.Create(CultureInfo.InvariantCulture, $"AllDay_{b.Occurrence.EventId}_{start:yyyyMMdd}"));
            AutomationProperties.SetName(chip, b.Occurrence.Title);
        }
    }

    /// <summary>Shows where a dragged all-day event would land.</summary>
    public void SetGhost(DateOnly first, DateOnly last)
    {
        if (_strip is not { } strip)
        {
            return;
        }

        var width = _owner.ColumnWidth;
        var from  = strip.IndexOf(first);
        var to    = Math.Max(from, strip.IndexOf(last));
        _ghost.Width       = Math.Max((to - from + 1) * width - 4, 8);
        _ghost.BorderBrush = LeafBrushes.Accent(_owner.IsDark);
        _ghost.Background  = LeafBrushes.Hover(_owner.IsDark);
        SetLeft(_ghost, from * width + 2);
        SetTop(_ghost, 2);
        _ghost.Visibility  = Visibility.Visible;
    }

    /// <summary>Hides the ghost.</summary>
    public void ClearGhost() => _ghost.Visibility = Visibility.Collapsed;

    void AddChip()
    {
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0), Child = text };

        chip.Tapped += (_, e) =>
        {
            if (_shown.TryGetValue(chip, out var o))
            {
                _owner.ViewModel.Select(o);
            }

            e.Handled = true;
        };
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
                _owner.BeginAllDayDrag(o, e);
            }
        };

        _chips.Add((chip, text));
        Children.Add(chip);
    }
}
