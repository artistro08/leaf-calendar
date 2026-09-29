using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ShortcutMapTests
{
    [Theory]
    [InlineData("T", false, false, CalendarCommand.Today, 0)]
    [InlineData("Left", false, false, CalendarCommand.Previous, 0)]
    [InlineData("Right", false, false, CalendarCommand.Next, 0)]
    [InlineData("J", false, false, CalendarCommand.Next, 0)]
    [InlineData("K", false, false, CalendarCommand.Previous, 0)]
    [InlineData("D", false, false, CalendarCommand.DayView, 0)]
    [InlineData("Number1", false, false, CalendarCommand.DayView, 0)]
    [InlineData("W", false, false, CalendarCommand.WeekView, 0)]
    [InlineData("Number0", false, false, CalendarCommand.WeekView, 0)]
    [InlineData("M", false, false, CalendarCommand.MonthView, 0)]
    [InlineData("Number4", false, false, CalendarCommand.Days, 4)]
    [InlineData("NumberPad9", false, false, CalendarCommand.Days, 9)]
    [InlineData("190", false, false, CalendarCommand.GoToDate, 0)]
    [InlineData("Decimal", false, false, CalendarCommand.GoToDate, 0)]
    [InlineData("N", false, false, CalendarCommand.NextEvent, 0)]
    [InlineData("B", false, false, CalendarCommand.PreviousEvent, 0)]
    [InlineData("N", false, true, CalendarCommand.PreviousEvent, 0)]
    [InlineData("E", true, true, CalendarCommand.ToggleWeekends, 0)]
    [InlineData("D", true, true, CalendarCommand.ToggleDeclined, 0)]
    [InlineData("L", true, true, CalendarCommand.ToggleTheme, 0)]
    [InlineData("187", true, false, CalendarCommand.ZoomIn, 0)]
    [InlineData("Add", true, false, CalendarCommand.ZoomIn, 0)]
    [InlineData("189", true, false, CalendarCommand.ZoomOut, 0)]
    [InlineData("Number0", true, false, CalendarCommand.ZoomReset, 0)]
    public void Resolve_KnownChord_MapsCommand(string key, bool ctrl, bool shift, CalendarCommand command, int days)
    {
        Assert.Equal(new ShortcutResult(command, days), ShortcutMap.Resolve(key, ctrl, shift, alt: false));
    }

    [Theory]
    [InlineData("T", false, false, true)]
    [InlineData("T", true, false, false)]
    [InlineData("T", false, true, false)]
    [InlineData("Q", false, false, false)]
    [InlineData("E", true, false, false)]
    public void Resolve_Unmapped_ReturnsNone(string key, bool ctrl, bool shift, bool alt)
    {
        Assert.Equal(CalendarCommand.None, ShortcutMap.Resolve(key, ctrl, shift, alt).Command);
    }
}
