using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // The app's icon button look, read once (as SidebarView reads its day styles)
    static readonly Lazy<Style> IconButtonStyle = new(() => (Style)Application.Current.Resources["LeafIconButtonStyle"]);

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
    readonly Canvas _offHours = new() { IsHitTestVisible = false };
    readonly List<Border> _offHourBlocks = [];
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly List<(Border Block, TextBlock Title)> _overlayBlocks = [];
    readonly Canvas _slots = new() { IsHitTestVisible = false };
    readonly List<(Grid Slot, Rectangle Fill, Rectangle Edge)> _slotItems = [];
    readonly Canvas _slotButtons = new();
    readonly List<Button> _slotRemoves = [];
    readonly List<int> _slotRemoveIndexes = [];

    /// <summary>Creates a column owned by <paramref name="owner"/>.</summary>
    public DayColumn(TimeGridView owner)
    {
        _owner  = owner;
        _select = o => KeyState.SelectClicked(_owner.ViewModel, o);

        // Off-Hours Shading (behind everything, even the hour lines)
        Children.Add(_offHours);

        for (var h = 0; h < 24; h++)
        {
            Children.Add(_hourLines[h] = new Rectangle { Height = 1, IsHitTestVisible = false });
            Children.Add(_halfLines[h] = new Rectangle { Height = 1, IsHitTestVisible = false });
        }

        // People Overlay, Then Shared-Availability Slots (under the events; clicks and drags go through to the grid)
        Children.Add(_overlay);
        Children.Add(_slots);

        // Slot Remove Buttons (above the events, so they can be clicked)
        Children.Add(_slotButtons);
        SetZIndex(_slotButtons, 15);

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

        RenderOffHours();
        RenderOverlay();
        RenderSlots();
        RenderEventsAndNow();
    }

    /// <summary>
    /// Repaints the events (past ones fade) and the now line: what the time grid's minute clock redraws. The shading,
    /// overlay, and slot layers only change with their own data, so the clock leaves them alone.
    /// </summary>
    public void RenderEventsAndNow()
    {
        var vm    = _owner.ViewModel;
        var dark  = _owner.IsDark;
        var width = _owner.ColumnWidth;
        var hour  = _owner.HourHeight;

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
            var palette = EventColors.Palette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark, vm.IsPast(b.Occurrence));

            card.Width      = Math.Max(colW - 2, 10);
            card.Height     = height;
            card.Visibility = Visibility.Visible;
            SetLeft(card, 2 + b.Column * colW);
            SetTop(card, b.StartMinute / 60 * hour + 1);
            card.HoldsEnd = b.Occurrence.End <= OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
            card.Bind(b.Occurrence, palette, TimeLabels.Range(b.Occurrence.Start, b.Occurrence.End, vm.Zone, vm.Settings.Use24HourTime), vm.IsSelected(b.Occurrence), compact: height < 36, _select, vm.IsPast(b.Occurrence));
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
        _ghost.Background  = LeafBrushes.GhostFill(dark);
        _ghostLabel.Text   = label;
        SetLeft(_ghost, 2);
        SetTop(_ghost, startMinute / 60 * hour + 1);
        _ghost.Visibility  = Visibility.Visible;
    }

    /// <summary>Hides the ghost.</summary>
    public void ClearGhost() => _ghost.Visibility = Visibility.Collapsed;

    // Shades the time outside your working hours. Your hours (in your own zone) are laid onto the clock on screen
    // (ViewModel.Zone), so while traveling to Tokyo your 9 to 5 shows where it falls in Tokyo time
    void RenderOffHours()
    {
        var vm    = _owner.ViewModel;
        var hour  = _owner.HourHeight;
        var fill  = LeafBrushes.OffHours(_owner.IsDark);
        var spans = WorkingHoursMath.OffHours(vm.Settings.WorkingHours, Date, vm.UserZone, vm.Zone);

        for (var i = 0; i < spans.Count; i++)
        {
            if (i == _offHourBlocks.Count)
            {
                var block = new Border();
                _offHourBlocks.Add(block);
                _offHours.Children.Add(block);
            }

            var (start, end) = spans[i];
            var shade        = _offHourBlocks[i];
            shade.Visibility = Visibility.Visible;
            shade.Background = fill;
            shade.Width      = _owner.ColumnWidth;
            shade.Height     = (end - start) / 60.0 * hour;
            SetTop(shade, start / 60.0 * hour);
            AutomationProperties.SetAutomationId(shade, string.Create(CultureInfo.InvariantCulture, $"OffHours_{Date:yyyy-MM-dd}_{i}"));

            // A Border is in the automation tree only with a name
            AutomationProperties.SetName(shade, "Outside working hours");
        }

        for (var i = spans.Count; i < _offHourBlocks.Count; i++)
        {
            _offHourBlocks[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Draws the overlaid people's busy stretches that fall on this day, under the events. Each block's number counts
    /// that person's loaded blocks in time order, so it's the same in every column and after a redraw.
    /// </summary>
    public void RenderOverlay()
    {
        var vm       = _owner.ViewModel;
        var dark     = _owner.IsDark;
        var dayStart = OccurrenceQuery.LocalMidnight(Date, vm.Zone);
        var dayEnd   = OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
        var counts   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shown    = 0;

        foreach (var b in vm.OverlayBlocks(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        {
            var n = counts[b.Email] = counts.GetValueOrDefault(b.Email) + 1;
            if (b.Start >= dayEnd || b.End <= dayStart)
            {
                continue;
            }

            // Reuse A Pooled Block
            if (shown == _overlayBlocks.Count)
            {
                var title = new TextBlock { FontSize = 12, Margin = new Thickness(6, 2, 6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                var block = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(3, 0, 0, 0), Child = title };
                _overlayBlocks.Add((block, title));
                _overlay.Children.Add(block);
            }

            var (border, text) = _overlayBlocks[shown++];
            var top    = b.Start <= dayStart ? 0 : _owner.MinutesIntoDay(b.Start);
            var bottom = b.End >= dayEnd ? 24 * 60 : _owner.MinutesIntoDay(b.End);

            border.Visibility  = Visibility.Visible;
            border.Background  = LeafBrushes.PersonFill(b.ColorIndex, dark);
            border.BorderBrush = LeafBrushes.Person(b.ColorIndex, dark);
            border.Width       = Math.Max(_owner.ColumnWidth - 2, 4);
            border.Height      = Math.Max((bottom - top) / 60 * _owner.HourHeight, 2);
            text.Text          = b.Title ?? "";
            text.Foreground    = LeafBrushes.PrimaryText(dark);
            SetTop(border, top / 60 * _owner.HourHeight);

            var when = $"{TimeZoneInfo.ConvertTime(b.Start, vm.Zone).ToString("h:mm tt", English)}–{TimeZoneInfo.ConvertTime(b.End, vm.Zone).ToString("h:mm tt", English)}";
            AutomationProperties.SetAutomationId(border, $"OverlayBlock_{b.Email}_{n - 1}");
            AutomationProperties.SetName(border, $"{b.Email} busy {when}" + (b.Title is { } t ? $": {t}" : ""));
        }

        for (var i = shown; i < _overlayBlocks.Count; i++)
        {
            _overlayBlocks[i].Block.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Draws the times picked for sharing that fall on this day (accent fill, dashed edge), each with a remove button
    /// at its top right on the day it starts. A slot's number is its place in the view model's list.
    /// </summary>
    public void RenderSlots()
    {
        var vm       = _owner.ViewModel;
        var dark     = _owner.IsDark;
        var accent   = LeafBrushes.Accent(dark);
        var dayStart = OccurrenceQuery.LocalMidnight(Date, vm.Zone);
        var dayEnd   = OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
        var width    = Math.Max(_owner.ColumnWidth - 6, 10);
        var shown    = 0;
        var buttons  = 0;

        for (var n = 0; n < vm.ShareSlots.Count; n++)
        {
            var s = vm.ShareSlots[n];
            if (s.Start >= dayEnd || s.End <= dayStart)
            {
                continue;
            }

            // Reuse A Pooled Slot
            if (shown == _slotItems.Count)
            {
                var fill = new Rectangle { RadiusX = 4, RadiusY = 4, Opacity = 0.15 };
                var edge = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = 1.5, StrokeDashArray = [4, 2] };
                var slot = new Grid();
                slot.Children.Add(fill);
                slot.Children.Add(edge);
                _slotItems.Add((slot, fill, edge));
                _slots.Children.Add(slot);
            }

            var (box, inside, border) = _slotItems[shown++];
            var top    = s.Start <= dayStart ? 0 : _owner.MinutesIntoDay(s.Start);
            var bottom = s.End >= dayEnd ? 24 * 60 : _owner.MinutesIntoDay(s.End);
            var y      = top / 60 * _owner.HourHeight;

            box.Visibility = Visibility.Visible;
            box.Width      = width;
            box.Height     = Math.Max((bottom - top) / 60 * _owner.HourHeight - 2, 4);
            inside.Fill    = accent;
            border.Stroke  = accent;
            SetLeft(box, 2);
            SetTop(box, y + 1);
            AutomationProperties.SetAutomationId(box, $"ShareSlot_{n}");
            AutomationProperties.SetName(box, $"Time to share {TimeLabels.Range(s.Start, s.End, vm.Zone, vm.Settings.Use24HourTime)}");

            // Remove Button, On The Day The Slot Starts
            if (s.Start < dayStart)
            {
                continue;
            }

            if (buttons == _slotRemoves.Count)
            {
                var index  = _slotRemoves.Count;
                var remove = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 10 },
                    Width   = 20,
                    Height  = 20,
                    Padding = new Thickness(0),
                    Style   = IconButtonStyle.Value,
                };
                AutomationProperties.SetName(remove, "Remove this time");
                ToolTipService.SetToolTip(remove, "Remove this time");
                remove.Click += (_, _) => _owner.ViewModel.RemoveShareSlot(_slotRemoveIndexes[index]);
                _slotRemoves.Add(remove);
                _slotRemoveIndexes.Add(n);
                _slotButtons.Children.Add(remove);
            }

            var button = _slotRemoves[buttons];
            _slotRemoveIndexes[buttons++] = n;
            button.Visibility = Visibility.Visible;
            AutomationProperties.SetAutomationId(button, $"ShareSlot_{n}_Remove");
            SetLeft(button, 2 + width - 22);
            SetTop(button, y + 3);
        }

        for (var i = shown; i < _slotItems.Count; i++)
        {
            _slotItems[i].Slot.Visibility = Visibility.Collapsed;
        }

        for (var i = buttons; i < _slotRemoves.Count; i++)
        {
            _slotRemoves[i].Visibility = Visibility.Collapsed;
        }
    }

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
