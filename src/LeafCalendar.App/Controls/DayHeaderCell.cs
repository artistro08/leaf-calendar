using System.Globalization;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>A day header: weekday name over the date number (today on an accent circle). Tap opens Day view.</summary>
public sealed partial class DayHeaderCell : Grid
{
    readonly TimeGridView _owner;
    readonly TextBlock _weekday = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _number = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _circle = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Center };
    readonly Rectangle _divider = new() { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Height = 14 };

    /// <summary>Creates a header owned by <paramref name="owner"/>.</summary>
    public DayHeaderCell(TimeGridView owner)
    {
        _owner = owner;
        Height = TimeGridView.DayHeaderHeight;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        _circle.Child = _number;
        stack.Children.Add(_weekday);
        stack.Children.Add(_circle);

        Children.Add(stack);
        Children.Add(_divider);

        Tapped += (_, _) =>
        {
            _owner.ViewModel.SetMode(CalendarViewMode.Day);
            _owner.ViewModel.NavigateTo(Date);
        };
    }

    /// <summary>The day shown.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Shows <paramref name="date"/>.</summary>
    public void Bind(DateOnly date)
    {
        Date  = date;
        Width = _owner.ColumnWidth;

        var dark    = _owner.IsDark;
        var isToday = date == _owner.ViewModel.Today;

        _weekday.Text       = TimeLabels.WeekdayShort(date);
        _weekday.Foreground = isToday ? LeafBrushes.Accent : LeafBrushes.SecondaryText(dark);
        _number.Text        = date.Day.ToString(CultureInfo.InvariantCulture);
        _number.Foreground  = isToday ? LeafBrushes.OnAccent : LeafBrushes.PrimaryText(dark);
        _circle.Background  = isToday ? LeafBrushes.Accent : LeafBrushes.Transparent;
        _divider.Fill       = LeafBrushes.GridLine(dark);

        AutomationProperties.SetAutomationId(this, string.Create(CultureInfo.InvariantCulture, $"DayHeader_{date:yyyy-MM-dd}"));
        AutomationProperties.SetName(this, TimeLabels.LongDate(date));
    }
}
