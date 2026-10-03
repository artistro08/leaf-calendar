using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One week of the month view.
/// </summary>
/// <remarks>
/// Each day cell shows its date, and events are chips packed into lanes with <see cref="SpanLayout"/>.
/// All-day and multi-day events are filled bars; timed events are a dot, time, and title. When a day
/// has more events than fit, the last lane shows "+N more", which opens that day's full list.
/// Rows render in place (as <see cref="DayColumn"/> does): the seven cells are built once, and chips
/// and "+N more" links are pooled, so a repaint only sets properties instead of building new controls.
/// </remarks>
public sealed partial class WeekRow : Canvas
{
    readonly MonthGridView _owner;
    readonly List<(Rectangle Left, Rectangle Top, Rectangle Tint, Button Day, TextBlock Number)> _cells = [];
    readonly List<MonthChip> _chips = [];
    readonly List<HyperlinkButton> _mores = [];
    readonly List<DateOnly> _moreDates = [];
    IReadOnlyList<DateOnly> _dates = [];

    /// <summary>Creates a row owned by <paramref name="owner"/>.</summary>
    public WeekRow(MonthGridView owner)
    {
        _owner     = owner;
        Background = LeafBrushes.Transparent;

        // Cells (all seven, shown or not, so later chips always draw above them)
        for (var c = 0; c < 7; c++)
        {
            var column = c;
            var number = new TextBlock { FontSize = 12 };
            var day    = new Button
            {
                Content         = number,
                Padding         = new Thickness(6, 1, 6, 1),
                MinWidth        = 24,
                Height          = 22,
                CornerRadius    = new CornerRadius(11),
                BorderThickness = new Thickness(0),
            };
            day.Click += (_, _) =>
            {
                _owner.ViewModel.SetMode(CalendarViewMode.Day);
                _owner.ViewModel.NavigateTo(_dates[column]);
            };

            // Click-Through Lines And Tint, So A Press On A Weekend Is A Press On The Row (Shift+drag box)
            var cell = (
                Left:   new Rectangle { Width = 1, IsHitTestVisible = false },
                Top:    new Rectangle { Height = 1, IsHitTestVisible = false },
                Tint:   new Rectangle { IsHitTestVisible = false },
                Day:    day,
                Number: number);
            _cells.Add(cell);
            Children.Add(cell.Left);
            Children.Add(cell.Top);
            Children.Add(cell.Tint);
            Children.Add(cell.Day);
            SetTop(day, 3);
        }

        // Double-Click An Empty Cell: a new all-day event that day (chips mark their own taps handled; the day number strip is skipped)
        DoubleTapped += (_, e) =>
        {
            var at = e.GetPosition(this);
            if (at.Y < MonthGridView.DayNumberHeight)
            {
                return;
            }

            var dates = _owner.ColumnDates(WeekStart);
            var date  = dates[Math.Clamp((int)Math.Floor(at.X / _owner.ColumnWidth), 0, dates.Count - 1)];
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            _owner.ViewModel.BeginCreate(start, start.AddDays(1), isAllDay: true);
            e.Handled = true;
        };
    }

    /// <summary>First day of the week shown.</summary>
    public DateOnly WeekStart { get; private set; }

    /// <summary>Shows the week starting <paramref name="weekStart"/>.</summary>
    public void Bind(DateOnly weekStart)
    {
        WeekStart = weekStart;
        Render();
    }

    /// <summary>Repaints for the current size, theme, data, and selection, reusing the row's controls.</summary>
    public void Render()
    {
        var vm     = _owner.ViewModel;
        var dark   = _owner.IsDark;
        var dates  = _owner.ColumnDates(WeekStart);
        var colW   = _owner.ColumnWidth;
        var height = _owner.RowHeight;

        _dates = dates;
        Width  = colW * dates.Count;
        Height = height;

        // Cells
        for (var c = 0; c < _cells.Count; c++)
        {
            RenderCell(c, colW, height, dark);
        }

        // Chips (a day with more than fits gives its last lane to "+N more")
        var items    = dates.SelectMany(vm.Cache.ForDay).DistinctBy(o => o.Key).ToList();
        var blocks   = SpanLayout.Layout(dates, items, vm.Zone, includeTimed: true);
        var maxLanes = Math.Max(1, (int)((height - MonthGridView.DayNumberHeight - 4) / MonthGridView.ChipHeight));
        var full     = new bool[dates.Count];
        var overflow = new int[dates.Count];
        var shown    = 0;

        foreach (var b in blocks.Where(b => b.Lane >= maxLanes))
        {
            for (var c = b.FirstColumn; c < b.FirstColumn + b.ColumnSpan; c++)
            {
                full[c] = true;
            }
        }

        foreach (var b in blocks)
        {
            var overflowing = Enumerable.Range(b.FirstColumn, b.ColumnSpan).Any(c => full[c]);
            var laneLimit   = overflowing ? maxLanes - 1 : maxLanes;
            if (b.Lane < laneLimit)
            {
                // Reuse A Pooled Chip
                if (shown == _chips.Count)
                {
                    var chip = new MonthChip(_owner);
                    _chips.Add(chip);
                    Children.Add(chip);
                }

                _chips[shown++].Bind(b, colW, dark);
                continue;
            }

            for (var c = b.FirstColumn; c < b.FirstColumn + b.ColumnSpan; c++)
            {
                overflow[c]++;
            }
        }

        for (var i = shown; i < _chips.Count; i++)
        {
            _chips[i].Visibility = Visibility.Collapsed;
        }

        // "+N More"
        var mores = 0;
        for (var c = 0; c < dates.Count; c++)
        {
            if (overflow[c] > 0)
            {
                More(mores++, dates[c], overflow[c], c * colW, MonthGridView.DayNumberHeight + (maxLanes - 1) * MonthGridView.ChipHeight);
            }
        }

        for (var i = mores; i < _mores.Count; i++)
        {
            _mores[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Re-colors the day numbers for a new focused month (other months' days are dimmed), without rebuilding the row.</summary>
    public void RenderFocus()
    {
        var dark = _owner.IsDark;
        for (var c = 0; c < _dates.Count; c++)
        {
            if (_dates[c] != _owner.ViewModel.Today)
            {
                _cells[c].Day.Foreground = InFocusMonth(_dates[c]) ? LeafBrushes.PrimaryText(dark) : LeafBrushes.DimText(dark);
            }
        }
    }

    bool InFocusMonth(DateOnly date) => date.Month == _owner.FocusMonth.Month && date.Year == _owner.FocusMonth.Year;

    // One day cell: its left and top lines, the weekend tint, and the day number (columns past the shown days hide). The
    // first column has no left line: the grid's edge against the window is edge enough
    void RenderCell(int c, double colW, double height, bool dark)
    {
        var (left, top, tint, day, number) = _cells[c];
        var shown = c < _dates.Count;
        top.Visibility = day.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        left.Visibility = shown && c > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            tint.Visibility = Visibility.Collapsed;
            return;
        }

        // Lines And Weekend Tint
        var date = _dates[c];
        var x    = c * colW;
        left.Height     = height;
        left.Fill       = LeafBrushes.GridLine(dark);
        top.Width       = colW;
        top.Fill        = LeafBrushes.GridLine(dark);
        tint.Width      = colW;
        tint.Height     = height;
        tint.Fill       = LeafBrushes.WeekendFill(dark);
        tint.Visibility = ViewNavigator.IsWeekend(date) ? Visibility.Visible : Visibility.Collapsed;
        SetLeft(left, x);
        SetLeft(top, x);
        SetLeft(tint, x);

        // Day Number
        var isToday = date == _owner.ViewModel.Today;
        number.Text       = date.Day == 1 ? date.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")) : date.Day.ToString(CultureInfo.InvariantCulture);
        number.FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal;
        day.Background    = isToday ? LeafBrushes.Accent(dark) : LeafBrushes.Transparent;
        day.Foreground    = isToday ? LeafBrushes.OnAccent(dark) : InFocusMonth(date) ? LeafBrushes.PrimaryText(dark) : LeafBrushes.DimText(dark);
        AutomationProperties.SetAutomationId(day, $"MonthDay_{date:yyyy-MM-dd}");
        AutomationProperties.SetName(day, TimeLabels.LongDate(date));
        SetLeft(day, x + 4);
    }

    // Shows the pooled "+N more" link at <paramref name="index"/> for <paramref name="date"/>
    void More(int index, DateOnly date, int count, double x, double y)
    {
        // Reuse A Pooled Link
        if (index == _mores.Count)
        {
            var created = new HyperlinkButton { FontSize = 12, Padding = new Thickness(6, 0, 6, 0), Height = MonthGridView.ChipHeight - 2 };
            created.Click += (_, _) => ShowDay(created, _moreDates[index]);
            _mores.Add(created);
            _moreDates.Add(date);
            Children.Add(created);
        }

        var link = _mores[index];
        _moreDates[index] = date;
        link.Content      = string.Create(CultureInfo.InvariantCulture, $"+{count} more");
        link.Visibility   = Visibility.Visible;
        AutomationProperties.SetAutomationId(link, $"More_{date:yyyy-MM-dd}");
        SetLeft(link, x + 2);
        SetTop(link, y);
    }

    // The day's full list in a flyout under the "+N more" link
    void ShowDay(HyperlinkButton link, DateOnly date)
    {
        var vm   = _owner.ViewModel;
        var list = new StackPanel { Spacing = 2, MinWidth = 220 };
        list.Children.Add(new TextBlock { Text = TimeLabels.LongDate(date), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

        var flyout = new Flyout();
        var dark   = ActualTheme == ElementTheme.Dark;
        foreach (var o in vm.Cache.ForDay(date).OrderBy(o => !o.IsAllDay).ThenBy(o => o.Start))
        {
            // The Chip's Dot In The Event's Color, The Time And Title, And The Calendar On A Small Second Line
            var past    = vm.IsPast(o);
            var palette = LeafBrushes.CardPalette(EventColors.ResolveAccent(o.ColorId, o.CalendarColor), dark, past, selected: false);
            var row     = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
            var dot     = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Fill = LeafBrushes.FromHex(palette.Accent) };
            var lines   = new StackPanel();
            lines.Children.Add(new TextBlock
            {
                Text            = o.IsAllDay ? o.Title : $"{TimeLabels.Compact(o.Start, vm.Zone, vm.Settings.Use24HourTime)}  {o.Title}",
                TextTrimming    = TextTrimming.CharacterEllipsis,
                TextDecorations = o.SelfResponse == ResponseStatus.Declined ? TextDecorations.Strikethrough : TextDecorations.None,
            });
            lines.Children.Add(new TextBlock
            {
                Text         = vm.Calendars.FirstOrDefault(c => c.AccountId == o.AccountId && c.Id == o.CalendarId)?.Summary ?? "",
                FontSize     = 11,
                Foreground   = LeafBrushes.SecondaryText(dark),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(lines, 1);
            row.Children.Add(dot);
            row.Children.Add(lines);

            var item = new Button
            {
                Content                    = row,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background                 = LeafBrushes.Transparent,
                BorderThickness            = new Thickness(0),
                Padding                    = new Thickness(8, 4, 8, 4),
            };
            AutomationProperties.SetName(item, vm.CardName(o, o.IsAllDay ? "All day" : TimeLabels.Range(o.Start, o.End, vm.Zone, vm.Settings.Use24HourTime)));
            item.Click += (_, _) =>
            {
                flyout.Hide();
                vm.Select(o);
            };
            list.Children.Add(item);
        }

        flyout.Content = list;
        flyout.ShowAt(link);
    }

    // =========================================================================
    // CHIPS
    // =========================================================================

    /// <summary>
    /// One event chip, pooled by its row. Its handlers are wired once and act on whichever event
    /// <see cref="Bind"/> last gave it.
    /// </summary>
    sealed partial class MonthChip : Grid
    {
        readonly MonthGridView _owner;
        readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _text = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        // The chip's give while a read-only event is dragged (ElasticNudge); created here and never read back
        readonly TranslateTransform _pull = new();
        readonly ToolTip _tip = new();
        CalendarOccurrence? _occurrence;
        string _time = "";

        public MonthChip(MonthGridView owner)
        {
            _owner       = owner;
            ToolTipService.SetToolTip(this, _tip);
            Height          = MonthGridView.ChipHeight - 2;
            CornerRadius    = new CornerRadius(4);
            RenderTransform = _pull;
            Padding      = new Thickness(6, 0, 6, 0);

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(_dot);
            content.Children.Add(_text);
            Children.Add(content);

            Tapped += (_, e) =>
            {
                if (_occurrence is { } o)
                {
                    KeyState.SelectClicked(_owner.ViewModel, o);
                }

                e.Handled = true;
            };

            // Right-Click Menu, And The Event Under The Mouse (X toggles it)
            RightTapped += (_, e) =>
            {
                if (_occurrence is { } o)
                {
                    EventContextMenu.Show(this, e.GetPosition(this), _owner.ViewModel, o);
                }

                e.Handled = true;
            };
            // Hover Tooltip: title, time, and location, filled in as the pointer arrives (the location is a lookup)
            PointerEntered += (_, _) =>
            {
                _owner.ViewModel.PointerEvent = _occurrence;
                _tip.Content                  = _occurrence is { } o ? _owner.ViewModel.HoverText(o, _time) : null;
            };
            PointerExited  += (_, _) => _owner.ViewModel.PointerEvent = null;
            DoubleTapped   += (_, e) =>
            {
                if (_occurrence is { } o)
                {
                    _owner.ViewModel.Select(o);
                    _owner.ViewModel.BeginEdit();
                }

                e.Handled = true;
            };
            PointerPressed += (_, e) =>
            {
                if (_occurrence is { } o && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
                {
                    _owner.BeginChipDrag(o, e, _pull);
                }
            };
        }

        /// <summary>Shows block <paramref name="b"/>'s event in its lane and columns.</summary>
        public void Bind(SpanBlock b, double colW, bool dark)
        {
            var vm       = _owner.ViewModel;
            var o        = b.Occurrence;
            var selected = vm.IsSelected(o);
            var past     = vm.IsPast(o);
            var palette  = LeafBrushes.CardPalette(EventColors.ResolveAccent(o.ColorId, o.CalendarColor), dark, past, selected);
            var spanning = SpanLayout.IsSpanning(o);
            var filled   = spanning || selected; // a selected chip is filled with its color at full strength (the highlight pair in a contrast theme)
            var first    = SpanLayout.CoveredDates(o, vm.Zone).First;

            _occurrence = o;
            _time       = o.IsAllDay ? "All day" : TimeLabels.Range(o.Start, o.End, vm.Zone, vm.Settings.Use24HourTime);

            // Text And Dot
            _text.Text            = spanning ? o.Title : $"{TimeLabels.Compact(o.Start, vm.Zone, vm.Settings.Use24HourTime)} {o.Title}";
            _text.Foreground      = filled ? LeafBrushes.FromHex(palette.Text) : LeafBrushes.PrimaryText(dark);
            _text.FontWeight      = spanning ? FontWeights.SemiBold : FontWeights.Normal;
            _text.TextDecorations = o.SelfResponse == ResponseStatus.Declined ? TextDecorations.Strikethrough : TextDecorations.None;
            _dot.Fill             = LeafBrushes.FromHex(palette.Accent);
            _dot.Visibility       = spanning ? Visibility.Collapsed : Visibility.Visible;

            // Chip
            Width           = Math.Max(b.ColumnSpan * colW - 6, 8);
            Background      = filled ? LeafBrushes.FromHex(palette.Fill) : LeafBrushes.Transparent;
            BorderBrush     = LeafBrushes.FromHex(palette.Accent);
            BorderThickness = filled ? LeafBrushes.CardBorder(selected) : new Thickness(0);
            Visibility      = Visibility.Visible;
            AutomationProperties.SetAutomationId(this, string.Create(CultureInfo.InvariantCulture, $"Chip_{o.EventId}_{first:yyyyMMdd}"));
            AutomationProperties.SetName(this, vm.CardName(o, _time));

            // Past Events Fade (still readable)
            AutomationProperties.SetItemStatus(this, past ? "Past" : "");

            SetLeft(this, b.FirstColumn * colW + 3);
            SetTop(this, MonthGridView.DayNumberHeight + b.Lane * MonthGridView.ChipHeight);
        }
    }
}
