using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
/// </remarks>
public sealed partial class WeekRow : Canvas
{
    readonly MonthGridView _owner;
    readonly List<(Button Button, DateOnly Date)> _dayNumbers = [];

    /// <summary>Creates a row owned by <paramref name="owner"/>.</summary>
    public WeekRow(MonthGridView owner)
    {
        _owner = owner;

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

    /// <summary>Repaints (rebuilds its few children; ponytail: pool chips if month scrolling ever stutters).</summary>
    public void Render()
    {
        var vm     = _owner.ViewModel;
        var dark   = _owner.IsDark;
        var dates  = _owner.ColumnDates(WeekStart);
        var colW   = _owner.ColumnWidth;
        var height = _owner.RowHeight;

        Children.Clear();
        _dayNumbers.Clear();
        Width      = colW * dates.Count;
        Height     = height;
        Background = LeafBrushes.Transparent;

        // Cells
        for (var c = 0; c < dates.Count; c++)
        {
            var date = dates[c];
            AddLine(c * colW, 0, 1, height, dark);
            AddLine(c * colW, 0, colW, 1, dark);
            if (ViewNavigator.IsWeekend(date))
            {
                var tint = new Rectangle { Width = colW, Height = height, Fill = LeafBrushes.WeekendFill(dark) };
                SetLeft(tint, c * colW);
                Children.Add(tint);
            }

            Children.Add(DayNumber(date, c * colW, dark));
        }

        // Chips
        var items    = dates.SelectMany(vm.Cache.ForDay).DistinctBy(o => o.Key).ToList();
        var blocks   = SpanLayout.Layout(dates, items, vm.Zone, includeTimed: true);
        var maxLanes = Math.Max(1, (int)((height - MonthGridView.DayNumberHeight - 4) / MonthGridView.ChipHeight));
        var overflow = new int[dates.Count];

        foreach (var b in blocks)
        {
            var overflowing = Enumerable.Range(b.FirstColumn, b.ColumnSpan).Any(c => blocks.Any(x => x.Lane >= maxLanes && c >= x.FirstColumn && c < x.FirstColumn + x.ColumnSpan));
            var laneLimit   = overflowing ? maxLanes - 1 : maxLanes;
            if (b.Lane < laneLimit)
            {
                Children.Add(Chip(b, colW, dark));
                continue;
            }

            for (var c = b.FirstColumn; c < b.FirstColumn + b.ColumnSpan; c++)
            {
                overflow[c]++;
            }
        }

        // "+N More"
        for (var c = 0; c < dates.Count; c++)
        {
            if (overflow[c] > 0)
            {
                Children.Add(More(dates[c], overflow[c], c * colW, MonthGridView.DayNumberHeight + (maxLanes - 1) * MonthGridView.ChipHeight));
            }
        }
    }

    /// <summary>Re-colors the day numbers for a new focused month (other months' days are dimmed), without rebuilding the row.</summary>
    public void RenderFocus()
    {
        var dark = _owner.IsDark;
        foreach (var (button, date) in _dayNumbers)
        {
            if (date != _owner.ViewModel.Today)
            {
                button.Foreground = InFocusMonth(date) ? LeafBrushes.PrimaryText(dark) : LeafBrushes.DimText(dark);
            }
        }
    }

    bool InFocusMonth(DateOnly date) => date.Month == _owner.FocusMonth.Month && date.Year == _owner.FocusMonth.Year;

    void AddLine(double x, double y, double w, double h, bool dark)
    {
        var line = new Rectangle { Width = w, Height = h, Fill = LeafBrushes.GridLine(dark) };
        SetLeft(line, x);
        SetTop(line, y);
        Children.Add(line);
    }

    Button DayNumber(DateOnly date, double x, bool dark)
    {
        var vm      = _owner.ViewModel;
        var isToday = date == vm.Today;
        var inMonth = InFocusMonth(date);
        var text    = date.Day == 1 ? date.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")) : date.Day.ToString(CultureInfo.InvariantCulture);

        var button = new Button
        {
            Content         = new TextBlock { Text = text, FontSize = 12, FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal },
            Padding         = new Thickness(6, 1, 6, 1),
            MinWidth        = 24,
            Height          = 22,
            CornerRadius    = new CornerRadius(11),
            BorderThickness = new Thickness(0),
            Background      = isToday ? LeafBrushes.Accent(dark) : LeafBrushes.Transparent,
            Foreground      = isToday ? LeafBrushes.OnAccent(dark) : inMonth ? LeafBrushes.PrimaryText(dark) : LeafBrushes.DimText(dark),
        };
        AutomationProperties.SetAutomationId(button, $"MonthDay_{date:yyyy-MM-dd}");
        AutomationProperties.SetName(button, TimeLabels.LongDate(date));
        button.Click += (_, _) =>
        {
            vm.SetMode(CalendarViewMode.Day);
            vm.NavigateTo(date);
        };

        SetLeft(button, x + 4);
        SetTop(button, 3);
        _dayNumbers.Add((button, date));
        return button;
    }

    Border Chip(SpanBlock b, double colW, bool dark)
    {
        var vm       = _owner.ViewModel;
        var o        = b.Occurrence;
        var palette  = EventColors.Palette(EventColors.ResolveAccent(o.ColorId, o.CalendarColor), dark);
        var spanning = SpanLayout.IsSpanning(o);
        var selected = vm.IsSelected(o);
        var first    = SpanLayout.CoveredDates(o, vm.Zone).First;

        var text = new TextBlock
        {
            FontSize          = 12,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Text              = spanning ? o.Title : $"{TimeLabels.Compact(o.Start, vm.Zone, vm.Settings.Use24HourTime)} {o.Title}",
            Foreground        = spanning ? LeafBrushes.FromHex(palette.Text) : LeafBrushes.PrimaryText(dark),
            FontWeight        = spanning ? FontWeights.SemiBold : FontWeights.Normal,
            TextDecorations   = o.SelfResponse == ResponseStatus.Declined ? TextDecorations.Strikethrough : TextDecorations.None,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!spanning)
        {
            content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = LeafBrushes.FromHex(palette.Accent), VerticalAlignment = VerticalAlignment.Center });
        }

        content.Children.Add(text);

        var chip = new Border
        {
            Width           = Math.Max(b.ColumnSpan * colW - 6, 8),
            Height          = MonthGridView.ChipHeight - 2,
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6, 0, 6, 0),
            Background      = spanning ? LeafBrushes.FromHex(palette.Fill) : LeafBrushes.Transparent,
            BorderBrush     = LeafBrushes.FromHex(palette.Accent),
            BorderThickness = new Thickness(selected ? 2 : 0),
            Child           = content,
        };
        chip.Tapped += (_, e) =>
        {
            vm.Select(o);
            e.Handled = true;
        };
        chip.DoubleTapped += (_, e) =>
        {
            vm.Select(o);
            vm.BeginEdit();
            e.Handled = true;
        };
        chip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            {
                _owner.BeginChipDrag(o, e);
            }
        };
        AutomationProperties.SetAutomationId(chip, string.Create(CultureInfo.InvariantCulture, $"Chip_{o.EventId}_{first:yyyyMMdd}"));
        AutomationProperties.SetName(chip, o.Title);

        SetLeft(chip, b.FirstColumn * colW + 3);
        SetTop(chip, MonthGridView.DayNumberHeight + b.Lane * MonthGridView.ChipHeight);
        return chip;
    }

    HyperlinkButton More(DateOnly date, int count, double x, double y)
    {
        var vm   = _owner.ViewModel;
        var link = new HyperlinkButton { Content = string.Create(CultureInfo.InvariantCulture, $"+{count} more"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0), Height = MonthGridView.ChipHeight - 2 };
        AutomationProperties.SetAutomationId(link, $"More_{date:yyyy-MM-dd}");
        link.Click += (_, _) =>
        {
            var list = new StackPanel { Spacing = 2, MinWidth = 220 };
            list.Children.Add(new TextBlock { Text = TimeLabels.LongDate(date), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

            var flyout = new Flyout();
            foreach (var o in vm.Cache.ForDay(date).OrderBy(o => !o.IsAllDay).ThenBy(o => o.Start))
            {
                var item = new Button
                {
                    Content                    = o.IsAllDay ? o.Title : $"{TimeLabels.Compact(o.Start, vm.Zone, vm.Settings.Use24HourTime)}  {o.Title}",
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background                 = LeafBrushes.Transparent,
                    BorderThickness            = new Thickness(0),
                };
                item.Click += (_, _) =>
                {
                    flyout.Hide();
                    vm.Select(o);
                };
                list.Children.Add(item);
            }

            flyout.Content = list;
            flyout.ShowAt(link);
        };

        SetLeft(link, x + 2);
        SetTop(link, y);
        return link;
    }
}
