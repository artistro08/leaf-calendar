using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One day of the time grid: hour and half-hour lines, the left divider, weekend tint, the event
/// cards (laid out with <see cref="DayLayout"/>), and on today a current-time line. Instances are
/// recycled by the grid, and <see cref="Bind"/> repaints for a new date.
/// </summary>
public sealed partial class DayColumn : Canvas
{
    readonly TimeGridView _owner;
    readonly Action<CalendarOccurrence> _select;
    readonly Rectangle[] _hourLines = new Rectangle[24];
    readonly Rectangle[] _halfLines = new Rectangle[24];
    readonly Rectangle _divider = new() { Width = 1 };
    readonly Rectangle _nowLine = new() { Height = 2, Fill = LeafBrushes.NowLine };
    readonly Ellipse _nowDot = new() { Width = 10, Height = 10, Fill = LeafBrushes.NowLine };
    readonly List<EventBlock> _blocks = [];
    readonly Border _ghost = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly TextBlock _ghostLabel = new() { FontSize = 11, Margin = new Thickness(6, 2, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Creates a column owned by <paramref name="owner"/>.</summary>
    public DayColumn(TimeGridView owner)
    {
        _owner  = owner;
        _select = o => KeyState.SelectClicked(_owner.ViewModel, o);
        for (var h = 0; h < 24; h++)
        {
            Children.Add(_hourLines[h] = new Rectangle { Height = 1, IsHitTestVisible = false });
            Children.Add(_halfLines[h] = new Rectangle { Height = 1, IsHitTestVisible = false });
        }

        _divider.IsHitTestVisible = false;
        _nowLine.IsHitTestVisible = false;
        _nowDot.IsHitTestVisible  = false;
        Children.Add(_divider);
        Children.Add(_nowLine);
        Children.Add(_nowDot);

        // Drag Ghost
        _ghost.Child = _ghostLabel;
        Children.Add(_ghost);
        SetZIndex(_ghost, 20);

        // Empty Time: drag to create, double-click for a one-hour event, click to pick the paste time (a press on a
        // card isn't empty time, so it never creates)
        PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (ReferenceEquals(e.OriginalSource, this) && point.Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            {
                _owner.BeginCreateDrag(e);
            }
        };
        DoubleTapped += (_, e) => _owner.CreateAt(Date, e.GetPosition(this).Y);
        Tapped       += (_, e) =>
        {
            var zone = _owner.ViewModel.Zone;
            _owner.ViewModel.CursorTime = DragMath.Snap(DragMath.Instant(Date, e.GetPosition(this).Y / _owner.HourHeight * 60, zone), zone);
        };
    }

    /// <summary>The day shown.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Shows <paramref name="date"/>.</summary>
    public void Bind(DateOnly date)
    {
        Date = date;
        ClearGhost();
        Render();
    }

    /// <summary>Repaints for the current size, theme, data, and selection.</summary>
    public void Render()
    {
        var vm     = _owner.ViewModel;
        var dark   = _owner.IsDark;
        var width  = _owner.ColumnWidth;
        var hour   = _owner.HourHeight;
        Width      = width;
        Height     = _owner.BodyHeight;
        Background = ViewNavigator.IsWeekend(Date) ? LeafBrushes.WeekendFill(dark) : LeafBrushes.Transparent;

        // Grid Lines
        for (var h = 0; h < 24; h++)
        {
            _hourLines[h].Width = width;
            _hourLines[h].Fill  = LeafBrushes.GridLine(dark);
            SetTop(_hourLines[h], h * hour);
            _halfLines[h].Width = width;
            _halfLines[h].Fill  = LeafBrushes.HalfHourLine(dark);
            SetTop(_halfLines[h], h * hour + hour / 2);
        }

        _divider.Height = Height;
        _divider.Fill   = LeafBrushes.GridLine(dark);

        // Events (drawn at the same minimum length DayLayout uses for overlap, so short events never collide)
        var blocks = DayLayout.Layout(Date, vm.Cache.ForDay(Date), vm.Zone);
        EnsureBlocks(blocks.Count);

        for (var i = 0; i < blocks.Count; i++)
        {
            var b       = blocks[i];
            var card    = _blocks[i];
            var usable  = width - 10;
            var colW    = usable / b.ColumnCount;
            var height  = Math.Max(b.EndMinute - b.StartMinute, DayLayout.MinVisualMinutes) / 60 * hour - 2;
            var palette = EventColors.Palette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark);

            card.Width      = Math.Max(colW - 2, 10);
            card.Height     = height;
            card.Visibility = Visibility.Visible;
            SetLeft(card, 2 + b.Column * colW);
            SetTop(card, b.StartMinute / 60 * hour + 1);
            card.HoldsEnd = b.Occurrence.End <= OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
            card.Bind(b.Occurrence, palette, TimeLabels.Range(b.Occurrence.Start, b.Occurrence.End, vm.Zone, vm.Settings.Use24HourTime), vm.IsSelected(b.Occurrence), compact: height < 36, _select);
        }

        for (var i = blocks.Count; i < _blocks.Count; i++)
        {
            _blocks[i].Visibility = Visibility.Collapsed;
        }

        // Now Line
        var isToday = Date == vm.Today;
        _nowLine.Visibility = _nowDot.Visibility = isToday ? Visibility.Visible : Visibility.Collapsed;
        if (isToday)
        {
            var now = TimeZoneInfo.ConvertTime(vm.Now, vm.Zone);
            var top = now.TimeOfDay.TotalMinutes / 60 * hour;
            _nowLine.Width = width;
            SetTop(_nowLine, top - 1);
            SetLeft(_nowDot, -5);
            SetTop(_nowDot, top - 5);
            SetZIndex(_nowLine, 10);
            SetZIndex(_nowDot, 10);
        }
    }

    /// <summary>Shows where a dragged or new event would land (minutes past local midnight).</summary>
    public void SetGhost(double startMinute, double endMinute, string label)
    {
        var hour  = _owner.HourHeight;
        var dark  = _owner.IsDark;
        _ghost.Width       = Math.Max(_owner.ColumnWidth - 6, 10);
        _ghost.Height      = Math.Max((endMinute - startMinute) / 60 * hour - 2, 10);
        _ghost.BorderBrush = LeafBrushes.Accent(dark);
        _ghost.Background  = LeafBrushes.Hover(dark);
        _ghostLabel.Text   = label;
        SetLeft(_ghost, 2);
        SetTop(_ghost, startMinute / 60 * hour + 1);
        _ghost.Visibility  = Visibility.Visible;
    }

    /// <summary>Hides the ghost.</summary>
    public void ClearGhost() => _ghost.Visibility = Visibility.Collapsed;

    void EnsureBlocks(int count)
    {
        while (_blocks.Count < count)
        {
            var block = new EventBlock(_owner);
            _blocks.Add(block);
            Children.Add(block);
        }
    }
}
