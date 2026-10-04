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

    [Theory]
    [InlineData("C", false, false, CalendarCommand.CreateEvent)]
    [InlineData("E", false, false, CalendarCommand.EditEvent)]
    [InlineData("V", false, false, CalendarCommand.OpenMeetingLink)]
    [InlineData("X", false, false, CalendarCommand.ToggleSelect)]
    [InlineData("Delete", false, false, CalendarCommand.DeleteSelected)]
    [InlineData("Delete", true, true, CalendarCommand.CancelEventQuietly)]
    [InlineData("A", true, false, CalendarCommand.SelectAll)]
    [InlineData("C", true, false, CalendarCommand.Copy)]
    [InlineData("X", true, false, CalendarCommand.Cut)]
    [InlineData("V", true, false, CalendarCommand.Paste)]
    [InlineData("Z", true, false, CalendarCommand.Undo)]
    [InlineData("J", true, false, CalendarCommand.JoinMeeting)]
    public void Resolve_EventChord_MapsCommand(string key, bool ctrl, bool shift, CalendarCommand command)
    {
        Assert.Equal(command, ShortcutMap.Resolve(key, ctrl, shift, alt: false).Command);
    }

    [Fact]
    public void AltLeft_IsBack_AltRight_IsForward()
    {
        Assert.Equal(CalendarCommand.NavigateBack, ShortcutMap.Resolve("Left", false, false, alt: true).Command);
        Assert.Equal(CalendarCommand.NavigateForward, ShortcutMap.Resolve("Right", false, false, alt: true).Command);
    }

    [Theory]
    [InlineData("K", true, false, CalendarCommand.CommandMenu)]
    [InlineData("F", true, false, CalendarCommand.Search)]
    [InlineData("191", false, false, CalendarCommand.Search)]       // "/"
    [InlineData("Divide", false, false, CalendarCommand.Search)]    // numpad "/"
    [InlineData("191", false, true, CalendarCommand.ShortcutSheet)] // "?" is Shift+"/"
    [InlineData("188", true, false, CalendarCommand.OpenSettings)]  // Ctrl+,
    [InlineData("Z", false, false, CalendarCommand.TimeTravel)]
    [InlineData("S", false, false, CalendarCommand.ShareAvailability)]
    [InlineData("P", false, false, CalendarCommand.PeopleOverlay)]
    [InlineData("F", false, false, CalendarCommand.MeetWith)]
    public void Resolve_M5Keys(string key, bool ctrl, bool shift, CalendarCommand expected) =>
        Assert.Equal(expected, ShortcutMap.Resolve(key, ctrl, shift, alt: false).Command);

    [Fact]
    public void Resolve_CtrlZ_StillUndo_ZAloneTimeTravels()
    {
        Assert.Equal(CalendarCommand.Undo, ShortcutMap.Resolve("Z", true, false, false).Command);
        Assert.Equal(CalendarCommand.TimeTravel, ShortcutMap.Resolve("Z", false, false, false).Command);
    }

    // Other Layouts: Punctuation Shortcuts Follow The Character The Key Types (Shift Included), Not The US Key Code
    [Theory]
    [InlineData("191", false, true, '?', CalendarCommand.ShortcutSheet)] // US: Shift+/ types "?"
    [InlineData("191", false, false, '/', CalendarCommand.Search)]       // US: / types "/"
    [InlineData("190", false, false, '.', CalendarCommand.GoToDate)]     // US
    [InlineData("187", true, false, '=', CalendarCommand.ZoomIn)]        // US and French: Ctrl+=
    [InlineData("189", true, false, '-', CalendarCommand.ZoomOut)]       // US and German: Ctrl+-
    [InlineData("188", true, false, ',', CalendarCommand.OpenSettings)]  // US and French: Ctrl+,
    [InlineData("219", false, true, '?', CalendarCommand.ShortcutSheet)] // German: Shift+ß types "?"
    [InlineData("187", true, false, '+', CalendarCommand.ZoomIn)]        // German: Ctrl and the + key
    [InlineData("188", false, true, '?', CalendarCommand.ShortcutSheet)] // French: Shift+, types "?"
    [InlineData("191", false, true, '/', CalendarCommand.Search)]        // French: Shift+: types "/"
    [InlineData("190", false, true, '.', CalendarCommand.GoToDate)]      // French: Shift+; types "."
    public void Resolve_TypedCharacter_MatchesPunctuationOnAnyLayout(string key, bool ctrl, bool shift, char typed, CalendarCommand expected) =>
        Assert.Equal(expected, ShortcutMap.Resolve(key, ctrl, shift, alt: false, typed).Command);

    [Theory]
    [InlineData("191", false, false, '#')]  // German: the US "/" key types "#"
    [InlineData("191", false, true, '\'')]  // German: Shift+# types "'"
    [InlineData("190", false, false, ';')]  // French: the US "." key types ";"
    [InlineData("188", true, false, ';')]   // A layout whose US "," key types ";"
    [InlineData("189", true, false, ')')]   // French: the US "-" key types ")"
    public void Resolve_TypedCharacter_OtherPunctuation_ReturnsNone(string key, bool ctrl, bool shift, char typed) =>
        Assert.Equal(CalendarCommand.None, ShortcutMap.Resolve(key, ctrl, shift, alt: false, typed).Command);

    [Fact]
    public void OtherAltChords_StayUnmapped()
    {
        Assert.Equal(CalendarCommand.None, ShortcutMap.Resolve("T", false, false, alt: true).Command);
        Assert.Equal(CalendarCommand.None, ShortcutMap.Resolve("Left", true, false, alt: true).Command);
    }
}
