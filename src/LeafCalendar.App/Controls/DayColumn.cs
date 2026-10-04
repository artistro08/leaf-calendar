using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One day of the time grid: hour lines (no half-hour lines, so the grid stays quiet), the left divider, weekend tint, the event
/// cards (laid out with <see cref="DayLayout"/>), and on today a current-time line. Instances are
/// recycled by the grid, and <see cref="Bind"/> repaints for a new date.
/// </summary>
public sealed partial class DayColumn : Canvas
{
    // The app's icon button look, read once (as SidebarView reads its day styles)
    private static readonly Lazy<Style> IconButtonStyle = new(() => (Style)Application.Current.Resources["LeafIconButtonStyle"]);

    private readonly TimeGridView _owner;
    private readonly Action<CalendarOccurrence> _select;
    private readonly Rectangle[] _hourLines = new Rectangle[24];
    private readonly Rectangle _divider = new() { Width = 1 };
    private readonly Rectangle _nowLine = new() { Height = 2, Fill = LeafBrushes.NowLine };
    private readonly Ellipse _nowDot = new() { Width = 10, Height = 10, Fill = LeafBrushes.NowLine };
    private readonly List<EventBlock> _blocks = [];
    private readonly Border _ghost = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    // The drag ghost's shadow lands on this, under everything (a receiver can't be the ghost's ancestor)
    private readonly Rectangle _floor = new() { IsHitTestVisible = false };
    // (13 tall lines, so a narrow day's wrapped "(No title)" and its time still fit an hour at the default height)
    private readonly TextBlock _ghostLabel = new() { FontSize = 11, LineHeight = 13, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(6, 2, 4, 0), TextWrapping = TextWrapping.WrapWholeWords, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Canvas _offHours = new() { IsHitTestVisible = false };

    // Marking times to share: diagonal lines over the day's empty time (under the events and the picked times)
    private readonly Microsoft.UI.Xaml.Shapes.Path _hatch = Hatch.Create();
    private readonly List<Border> _offHourBlocks = [];
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly List<(Border Block, TextBlock Title)> _overlayBlocks = [];
    private readonly Canvas _slots = new() { IsHitTestVisible = false };
    private readonly List<(Grid Slot, Rectangle Fill, Rectangle Edge)> _slotItems = [];
    private readonly Canvas _slotButtons = new();
    private readonly List<Button> _slotRemoves = [];
    private readonly List<int> _slotRemoveIndexes = [];

    /// <summary>Creates a column owned by <paramref name="owner"/>.</summary>
    public DayColumn(TimeGridView owner)
    {
        _owner = owner;
        _select = o => KeyState.SelectClicked(_owner.ViewModel, o);

        // Off-Hours Shading (behind everything, even the hour lines)
        Children.Add(_offHours);

        for (var h = 0; h < 24; h++)
        {
            Children.Add(_hourLines[h] = new Rectangle { Height = 1, IsHitTestVisible = false });
        }

        // Diagonal Lines While Marking Times To Share
        Children.Add(_hatch);

        // People Overlay, Then Shared-Availability Slots (under the events; clicks and drags go through to the grid)
        Children.Add(_overlay);
        Children.Add(_slots);

        // Slot Remove Buttons (above the events, so they can be clicked)
        Children.Add(_slotButtons);
        SetZIndex(_slotButtons, 15);

        _divider.IsHitTestVisible = false;
        _nowLine.IsHitTestVisible = false;
        _nowDot.IsHitTestVisible = false;
        Children.Add(_divider);
        Children.Add(_nowLine);
        Children.Add(_nowDot);

        // Drag Ghost (its label is what UI tests find, by date), with a drop shadow onto the floor
        _floor.Fill = LeafBrushes.Transparent;
        Children.Insert(0, _floor);
        SetZIndex(_floor, -1);
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(_floor);
        _ghost.Child = _ghostLabel;
        _ghost.Shadow = shadow;
        _ghost.Translation = new System.Numerics.Vector3(0, 0, 24);
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
        Tapped += (_, e) =>
        {
            _owner.ViewModel.CursorTime = DragMath.SnapOnDay(Date, e.GetPosition(this).Y / _owner.HourHeight * 60, _owner.ViewModel.Zone);
        };
    }

    /// <summary>The day shown.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Shows <paramref name="date"/>.</summary>
    public void Bind(DateOnly date)
    {
        Date = date;
        ClearGhost();
        AutomationProperties.SetAutomationId(_ghostLabel, string.Create(CultureInfo.InvariantCulture, $"Ghost_{date:yyyy-MM-dd}"));
        Render();
    }

    /// <summary>Repaints for the current size, theme, data, and selection.</summary>
    public void Render()
    {
        var dark = _owner.IsDark;
        var width = _owner.ColumnWidth;
        var hour = _owner.HourHeight;
        Width = width;
        Height = _owner.BodyHeight;
        Background = ViewNavigator.IsWeekend(Date) ? LeafBrushes.WeekendFill(dark) : LeafBrushes.Transparent;

        // Grid Lines
        for (var h = 0; h < 24; h++)
        {
            _hourLines[h].Width = width;
            _hourLines[h].Fill = LeafBrushes.GridLine(dark);
            SetTop(_hourLines[h], h * hour);
        }

        _divider.Height = Height;
        _divider.Fill = LeafBrushes.GridLine(dark);
        Hatch.Draw(_hatch, _owner.ViewModel.IsSharing, width, Height, LeafBrushes.GridLine(dark));

        RenderOffHours();
        RenderOverlay();
        RenderSlots();
        RenderEventsAndNow();
    }

    // Room right of the cards: 8 between days, and in Day view (one column, so its right edge is the grid's, under the
    // scroll indicator) enough to press and drag a new event there, clear of the indicator
    private double RightInset => _owner.ViewModel.VisibleColumns == 1 ? 28 : 8;

    /// <summary>
    /// Repaints the events (past ones fade) and the now line: what the time grid's minute clock redraws. The shading,
    /// overlay, and slot layers only change with their own data, so the clock leaves them alone.
    /// </summary>
    public void RenderEventsAndNow()
    {
        var vm = _owner.ViewModel;
        var dark = _owner.IsDark;
        var width = _owner.ColumnWidth;
        var hour = _owner.HourHeight;

        // Events (drawn at the same minimum length DayLayout uses for overlap, so short events never collide), with the
        // event being resized laid out in place of the original
        _floor.Width = width;
        _floor.Height = _owner.BodyHeight;
        var blocks = DayLayout.Layout(Date, _owner.WithPreviews(vm.Cache.ForDay(Date)), vm.Zone);
        var shown = 0;
        EnsureBlocks(blocks.Count);

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var usable = width - 2 - RightInset;
            var colW = usable / b.ColumnCount;
            var card = _blocks[shown++];
            var height = Math.Max(b.EndMinute - b.StartMinute, DayLayout.MinVisualMinutes) / 60 * hour - 2;
            // Marking Times To Share: every event looks past (they're taken; the day behind them wears diagonal lines)
            var faded = vm.IsPast(b.Occurrence) || vm.IsSharing;
            var palette = LeafBrushes.CardPalette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark, faded, vm.IsSelected(b.Occurrence));

            card.Width = Math.Max(colW - 2, 10);
            card.Height = height;
            card.Visibility = Visibility.Visible;
            SetLeft(card, 2 + b.Column * colW);
            SetTop(card, b.StartMinute / 60 * hour + 1);
            card.HoldsEnd = b.Occurrence.End <= OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
            // The card shows its times without AM/PM (its place on the grid says which); the tooltip and name keep them
            card.Bind(b.Occurrence, palette, TimeLabels.GridRange(b.Occurrence.Start, b.Occurrence.End, vm.Zone, vm.Settings.Use24HourTime), vm.IsSelected(b.Occurrence), compact: height < 36, _select, faded, StripesFor(b.Occurrence, dark));
        }

        for (var i = shown; i < _blocks.Count; i++)
        {
            _blocks[i].Visibility = Visibility.Collapsed;
        }

        // The New Event's Ghost: on top of the events, so the ones under it keep their own width, and its title (or
        // "(No title)") wraps over its time on the day it starts. Over an event it leaves the left quarter of the day
        // showing, so the event under it still shows its color bar and the start of its title. A new event on another
        // day leaves no ghost here
        if (_owner.StandIn is { } standIn)
        {
            if (DayLayout.Layout(Date, [standIn], vm.Zone) is [var g])
            {
                var title = string.IsNullOrWhiteSpace(standIn.Title) ? EventDetailsParser.NoTitle : standIn.Title;
                var time = standIn.Start >= OccurrenceQuery.LocalMidnight(Date, vm.Zone) ? "\n" + TimeLabels.GridRange(standIn.Start, standIn.End, vm.Zone, vm.Settings.Use24HourTime) : "";
                var end = Math.Max(g.EndMinute, g.StartMinute + DragMath.SnapMinutes);
                var usable = width - 2 - RightInset;
                var inset = blocks.Any(b => b.StartMinute < end && b.EndMinute > g.StartMinute) ? Math.Round(usable / 4) : 0;
                PlaceGhost(2 + inset, Math.Max(usable - inset, 10), g.StartMinute, end, title + time);
            }
            else
            {
                ClearGhost();
            }
        }

        // Now Line
        var isToday = Date == vm.Today;
        _nowLine.Visibility = _nowDot.Visibility = isToday ? Visibility.Visible : Visibility.Collapsed;
        if (isToday)
        {
            _nowLine.Fill = _nowDot.Fill = LeafBrushes.NowLine;
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

    // The same event on several shown calendars: each calendar's bar color, faded or full as the card is
    private IReadOnlyList<string>? StripesFor(CalendarOccurrence occurrence, bool dark)
    {
        var vm = _owner.ViewModel;
        var accents = vm.Cache.StripesOf(occurrence);
        if (accents.Count < 2)
        {
            return null;
        }

        var past = vm.IsPast(occurrence) || vm.IsSharing;
        var selected = vm.IsSelected(occurrence);
        return [.. accents.Select(a => LeafBrushes.CardPalette(a, dark, past, selected).Accent)];
    }

    /// <summary>
    /// Shows where a dragged or new event would land (minutes past local midnight), in the dragged event's color
    /// (<paramref name="accentHex"/>) or the system accent for a new one.
    /// </summary>
    public void SetGhost(double startMinute, double endMinute, string label, string? accentHex = null) =>
        PlaceGhost(2, Math.Max(_owner.ColumnWidth - 2 - RightInset, 10), startMinute, endMinute, label, accentHex);

    /// <summary>
    /// Shows only the time a resized event will end at (<paramref name="label"/>), just under its new end
    /// (<paramref name="endMinute"/> past local midnight), or just above it at the bottom of the day. The card itself
    /// is drawn at the new size, so there's no outline.
    /// </summary>
    public void SetTimeLabel(double endMinute, string label)
    {
        var y = endMinute / 60 * _owner.HourHeight;
        _ghost.Width = double.NaN;
        _ghost.Height = TimeLabelHeight;
        _ghost.BorderBrush = LeafBrushes.Transparent;
        _ghost.Background = LeafBrushes.GhostFill(_owner.IsDark);
        _ghostLabel.Text = label;
        SetLeft(_ghost, 2);
        SetTop(_ghost, y + 2 + TimeLabelHeight <= _owner.BodyHeight ? y + 2 : y - 2 - TimeLabelHeight);
        _ghost.Visibility = Visibility.Visible;
    }

    // Height of the resize time label (one line of the ghost's 11 px text)
    private const double TimeLabelHeight = 20;

    private void PlaceGhost(double left, double width, double startMinute, double endMinute, string label, string? accentHex = null)
    {
        var hour = _owner.HourHeight;
        var dark = _owner.IsDark;
        var (border, fill) = LeafBrushes.GhostPalette(accentHex, dark);
        _ghost.Width = width;
        _ghost.Height = Math.Max((endMinute - startMinute) / 60 * hour - 2, 10);
        _ghost.BorderBrush = border;
        _ghost.Background = fill;
        _ghostLabel.Text = label;
        SetLeft(_ghost, left);
        SetTop(_ghost, startMinute / 60 * hour + 1);
        _ghost.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the ghost.</summary>
    public void ClearGhost() => _ghost.Visibility = Visibility.Collapsed;

    // Shades the time outside your working hours. Your hours (in your own zone) are laid onto the clock on screen
    // (ViewModel.Zone), so while traveling to Tokyo your 9 to 5 shows where it falls in Tokyo time
    private void RenderOffHours()
    {
        var vm = _owner.ViewModel;
        var hour = _owner.HourHeight;
        var fill = LeafBrushes.OffHours(_owner.IsDark);
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
            var shade = _offHourBlocks[i];
            shade.Visibility = Visibility.Visible;
            shade.Background = fill;
            shade.Width = _owner.ColumnWidth;
            shade.Height = (end - start) / 60.0 * hour;
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
        var vm = _owner.ViewModel;
        var dark = _owner.IsDark;
        var dayStart = OccurrenceQuery.LocalMidnight(Date, vm.Zone);
        var dayEnd = OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shown = 0;

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
            var top = b.Start <= dayStart ? 0 : _owner.MinutesIntoDay(b.Start);
            var bottom = b.End >= dayEnd ? 24 * 60 : _owner.MinutesIntoDay(b.End);

            border.Visibility = Visibility.Visible;
            border.Background = LeafBrushes.PersonFill(b.ColorIndex, dark);
            border.BorderBrush = LeafBrushes.Person(b.ColorIndex, dark);
            border.Width = Math.Max(_owner.ColumnWidth - 2, 4);
            border.Height = Math.Max((bottom - top) / 60 * _owner.HourHeight, 2);
            text.Text = b.Title ?? "";
            text.Foreground = LeafBrushes.PrimaryText(dark);
            SetTop(border, top / 60 * _owner.HourHeight);

            var when = TimeLabels.Range(b.Start, b.End, vm.Zone, vm.Settings.Use24HourTime);
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
        var vm = _owner.ViewModel;
        var dark = _owner.IsDark;
        var accent = LeafBrushes.Accent(dark);
        var dayStart = OccurrenceQuery.LocalMidnight(Date, vm.Zone);
        var dayEnd = OccurrenceQuery.LocalMidnight(Date.AddDays(1), vm.Zone);
        var width = Math.Max(_owner.ColumnWidth - 6, 10);
        var shown = 0;
        var buttons = 0;

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
            var top = s.Start <= dayStart ? 0 : _owner.MinutesIntoDay(s.Start);
            var bottom = s.End >= dayEnd ? 24 * 60 : _owner.MinutesIntoDay(s.End);
            var y = top / 60 * _owner.HourHeight;

            box.Visibility = Visibility.Visible;
            box.Width = width;
            box.Height = Math.Max((bottom - top) / 60 * _owner.HourHeight - 2, 4);
            inside.Fill = accent;
            border.Stroke = accent;
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
                var index = _slotRemoves.Count;
                var remove = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 10 },
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Style = IconButtonStyle.Value,
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

    private void EnsureBlocks(int count)
    {
        while (_blocks.Count < count)
        {
            var block = new EventBlock(_owner);
            _blocks.Add(block);
            Children.Add(block);
        }
    }
}
