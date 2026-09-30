using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The all-day row: all-day and 24-hour-plus events as bars across day columns, packed into lanes
/// with <see cref="SpanLayout"/>. Only a window of columns around the visible ones is drawn.
/// </summary>
public sealed partial class AllDayCanvas : Canvas
{
    readonly TimeGridView _owner;
    readonly List<Border> _chips = [];

    /// <summary>Creates the row owned by <paramref name="owner"/>. Its background is hit-testable, so a click on empty all-day space reaches the page and clears the selection.</summary>
    public AllDayCanvas(TimeGridView owner)
    {
        _owner     = owner;
        Background = LeafBrushes.Transparent;
    }

    /// <summary>Lanes used by the last render.</summary>
    public int LaneCount { get; private set; }

    /// <summary>Draws columns <c>[firstIndex, firstIndex + count)</c> of the strip, showing at most <paramref name="maxLanes"/> lanes.</summary>
    public void Render(DayStrip strip, int firstIndex, int count, int maxLanes)
    {
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
            var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0) };
            chip.Child = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            chip.Tapped += (s, e) =>
            {
                if (((Border)s).Tag is CalendarOccurrence o)
                {
                    vm.Select(o);
                }

                e.Handled = true;
            };
            _chips.Add(chip);
            Children.Add(chip);
        }

        for (var i = 0; i < _chips.Count; i++)
        {
            var chip = _chips[i];
            if (i >= shown.Count)
            {
                chip.Visibility = Visibility.Collapsed;
                continue;
            }

            var b       = shown[i];
            var palette = EventColors.Palette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark);
            var text    = (TextBlock)chip.Child;
            var start   = SpanLayout.CoveredDates(b.Occurrence, vm.Zone).First;

            chip.Visibility      = Visibility.Visible;
            chip.Tag             = b.Occurrence;
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
}
