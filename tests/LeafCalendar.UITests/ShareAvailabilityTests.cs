using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ShareAvailabilityTests : IDisposable
{
    private const string Dentist = "Event_evt-single_202610011300";

    private readonly FakeGoogleServer _google = new();
    // Leaf shows Eastern time (the copied text names ET), whatever this PC's time zone is
    private const string Eastern = "America/New_York";

    private string _profile = SeededProfile.Create(new LeafSettings { PrimaryTimeZone = Eastern });

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // The dentist runs 9-10 AM Eastern: its card (an hour less 2 px, 1 px below the 9:00 line) measures the grid
    private static int HourPixels(AutomationElement dentist) => dentist.BoundingRectangle.Height + 2;

    // Drags from one Eastern hour to another on Oct 1, near the column's right edge (beside the event cards), a tenth
    // of an hour in so snapping is clear
    internal static void DragHours(LeafApp leaf, int fromHour, int toHour)
    {
        var dentist = leaf.WaitFor(Dentist);
        var hour = HourPixels(dentist);
        var column = leaf.WaitFor("DayHeader_2026-10-01").BoundingRectangle;
        var x = column.Right - 4;
        var top = dentist.BoundingRectangle.Top - 1 + hour / 10;
        LeafApp.Drag(new Point(x, top + (fromHour - 9) * hour), new Point(x, top + (toHour - 9) * hour));
    }

    // S, then wait for the share panel
    internal static void StartSharing(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.Press(VirtualKeyShort.KEY_S);
        Assert.NotNull(leaf.WaitFor("ShareSlotsPanel"));
    }

    // Copy, then the clipboard's text once it holds the shared times
    private static string Copy(LeafApp leaf)
    {
        Clipboard.Clear();
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        var text = Retry.WhileNull(Clipboard.Text, TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(text);

        // Wrapped In The Default Message: the times follow its greeting
        var intro = AvailabilityText.Compose(AvailabilityText.DefaultMessage, "");
        Assert.StartsWith(intro, text, StringComparison.Ordinal);
        return text[intro.Length..];
    }

    private static bool NoticeSays(LeafApp leaf, string text) =>
        Retry.WhileFalse(() => leaf.Exists("NoticeBar") && leaf.WaitFor("NoticeBar") is var notice
            && notice.FindAllDescendants().Prepend(notice).Any(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(text, StringComparison.Ordinal)), TimeSpan.FromSeconds(10)).Success;

    private static string ReadLog(string profile)
    {
        var log = Path.Combine(LeafApp.ProfileFolder(profile), "Logs", "leaf.log");
        Assert.True(File.Exists(log), "The app wrote no log.");
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void S_DragSlots_CopyGivesExactlyTheDraggedTimes()
    {
        using var leaf = Launch();
        StartSharing(leaf);

        DragHours(leaf, 9, 12);

        Assert.NotNull(leaf.WaitFor("ShareSlot_0"));
        Assert.False(leaf.Exists("EventEditor"));

        // The dentist (9-10) doesn't cut the time: what's dragged is what's copied
        Assert.Equal("Thu Oct 1: 9 AM–12 PM ET", Copy(leaf));
    }

    [Fact]
    public void CopyInTokyoTime_SplitsAtMidnight()
    {
        LeafApp.DeleteProfile(_profile);
        _profile = SeededProfile.Create(new LeafSettings { PrimaryTimeZone = Eastern, TimeZones = [new ExtraTimeZone("Asia/Tokyo", null)] });
        using var leaf = Launch();
        StartSharing(leaf);

        DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");

        // Typed Like A Person (a typed city submitted with Enter picks its zone)
        var zone = LeafApp.TextIn(leaf.WaitFor("ShareZoneBox"));
        zone.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Tokyo");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Assert.True(Retry.WhileFalse(() => zone.Text.Contains("Tokyo", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success, "The zone box didn't take Tokyo.");

        Assert.Equal("Thu Oct 1: 11 PM–12 AM Tokyo time\r\nFri Oct 2: 12–1 AM Tokyo time", Copy(leaf));
    }

    // A picked time's bottom edge dragged down an hour: 10-11 AM grows to 10 AM-12 PM
    [Fact]
    public void EdgeDrag_ResizesAPickedTime()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 10, 11);
        var slot = leaf.WaitFor("ShareSlot_0").BoundingRectangle;
        var hour = slot.Height + 2;
        var x = slot.Left + slot.Width / 2;

        LeafApp.Drag(new Point(x, slot.Bottom - 1), new Point(x, slot.Bottom - 1 + hour));

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ShareSlot_0").BoundingRectangle.Height > hour * 1.5, TimeSpan.FromSeconds(5)).Success, "The time didn't grow.");
        Assert.False(leaf.Exists("ShareSlot_1"));
        Assert.Equal("Thu Oct 1: 10 AM–12 PM ET", Copy(leaf));
    }

    [Fact]
    public void RemoveASlot_AndCancel()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 15, 16);
        leaf.WaitFor("ShareSlot_0");
        DragHours(leaf, 17, 18);
        leaf.WaitFor("ShareSlot_1");

        leaf.WaitFor("ShareSlot_0_Remove").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlot_1"), TimeSpan.FromSeconds(5)).Success, "Both slots still show.");
        Assert.True(leaf.Exists("ShareSlot_0"));

        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel") || leaf.Exists("ShareSlot_0"), TimeSpan.FromSeconds(5)).Success, "The share panel or a slot still shows.");

        // Dragging makes events again
        DragHours(leaf, 15, 16);
        Assert.NotNull(leaf.WaitFor("EventEditor"));
    }

    // Esc stops sharing right after S, and after a drag on the grid picked a time
    [Fact]
    public void Escape_RightAfterS_StopsSharing()
    {
        using var leaf = Launch();
        StartSharing(leaf);

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Esc left the share panel up.");
    }

    [Fact]
    public void Escape_OnTheGrid_StopsSharing()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 14, 15);
        leaf.WaitFor("ShareSlot_0");

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Esc left the share panel up.");
    }

    // Wherever focus is in the share panel (pressed straight to the focused control, not through the window). The zone
    // box opens its list when it takes focus, so its first Esc closes only the list (Escape_InTheOpenZoneList_ClosesOnlyTheList)
    [Theory]
    [InlineData("ShareMessageBox")]
    [InlineData("SharePanelStart_0")]
    [InlineData("SharePanelEnd_0")]
    [InlineData("SharePanelRemove_0")]
    [InlineData("ShareCopyButton")]
    public void Escape_WithFocusInThePanel_StopsSharing(string focusId)
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 14, 15);
        leaf.WaitFor(focusId).Focus();
        Thread.Sleep(300);

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, $"Esc with focus on {focusId} left the share panel up.");
    }

    // Esc in an open time picker or zone list only closes it; the next Esc stops sharing
    [Fact]
    public void Escape_InAnOpenPicker_ClosesOnlyThePicker()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 14, 15);

        leaf.WaitFor("SharePanelEnd_0").Click();
        leaf.WaitForPopup("MinuteLoopingSelector");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("MinuteLoopingSelector"), TimeSpan.FromSeconds(5)).Success, "Esc left the time picker open.");
        Thread.Sleep(500);
        Assert.True(leaf.Exists("ShareSlotsPanel"), "Esc in the time picker stopped sharing.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "The second Esc left the share panel up.");
    }

    [Fact]
    public void Escape_InTheOpenZoneList_ClosesOnlyTheList()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        LeafApp.TextIn(leaf.WaitFor("ShareZoneBox")).Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Tokyo");
        Assert.True(Retry.WhileFalse(() => leaf.SuggestionNames().Count > 0, TimeSpan.FromSeconds(5)).Success, "Typing didn't open the zone list.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileFalse(() => leaf.SuggestionNames().Count == 0, TimeSpan.FromSeconds(5)).Success, "Esc left the zone list open.");
        Thread.Sleep(500);
        Assert.True(leaf.Exists("ShareSlotsPanel"), "Esc in the zone list stopped sharing.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "The second Esc left the share panel up.");
    }

    [Fact]
    public void SidebarButton_AndCommandMenu_StartIt()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        leaf.WaitFor("SidebarShareAvailability").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("ShareSlotsPanel"));
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Cancel left the share panel up.");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        Thread.Sleep(500);
        Keyboard.Type("share");
        Thread.Sleep(500);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Assert.NotNull(leaf.WaitFor("ShareSlotsPanel"));
    }

    // Copy copies, ends sharing (the panel and the slots go), and says so in the notice like a delete does; the panel has
    // no booking pages link
    [Fact]
    public void Copy_StopsSharing_AndSaysSo()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        Assert.False(leaf.Exists("BookingPagesLink"));
        DragHours(leaf, 9, 12);
        leaf.WaitFor("ShareSlot_0");

        Assert.Equal("Thu Oct 1: 9 AM–12 PM ET", Copy(leaf));

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel") || leaf.Exists("ShareSlot_0"), TimeSpan.FromSeconds(5)).Success, "Sharing didn't stop.");
        Assert.True(NoticeSays(leaf, "Availability copied"), "The notice didn't say the availability was copied.");
    }

    // Everything sits in the right panel: the zone and message, a line, the Guests & proposed times heading over the guest
    // box, then (new picks) only Copy & Save and Cancel splitting the full width at the bottom; the panel keeps its width
    // as times are picked, and there's no calendars dropdown
    [Fact]
    public void Panel_HoldsTheZoneGuestsTimesAndButtons_AtAFixedWidth()
    {
        using var leaf = Launch();
        StartSharing(leaf);

        var panel = leaf.WaitFor("ShareSlotsPanel").BoundingRectangle;
        var zone = leaf.WaitFor("ShareZoneBox").BoundingRectangle;
        var copy = leaf.WaitFor("ShareCopyButton").BoundingRectangle;
        var stop = leaf.WaitFor("ShareCancelButton").BoundingRectangle;
        var edge = 16 * leaf.Scale + 1;
        Assert.False(leaf.Exists("ShareSaveButton"), "Save showed for new picks.");
        Assert.Equal("Copy & Save", leaf.WaitFor("ShareCopyButton").Name);
        Assert.Equal("Cancel", leaf.WaitFor("ShareCancelButton").Name);
        Assert.False(leaf.Exists("ShareCalendarsButton"), "The calendars dropdown still shows.");
        Assert.True(zone.Left - panel.Left <= edge && panel.Right - zone.Right <= edge, $"The zone box ({zone}) doesn't span the panel ({panel}).");
        Assert.True(copy.Left - panel.Left <= edge && panel.Right - stop.Right <= edge && copy.Top == stop.Top, $"Copy & Save ({copy}) and Cancel ({stop}) don't span the panel ({panel}).");
        Assert.True(copy.Right < stop.Left && Math.Abs(copy.Width - stop.Width) <= 1, $"Copy & Save ({copy}) and Cancel ({stop}) aren't two equal buttons in that order.");
        Assert.True(panel.Bottom - copy.Bottom <= edge, $"The buttons ({copy}) aren't at the panel's ({panel}) bottom.");

        // The Default-Message Link Sits Inside The Message Box, Along Its Bottom
        var reset = leaf.WaitFor("ShareMessageReset").BoundingRectangle;
        var message = leaf.WaitFor("ShareMessageBox").BoundingRectangle;
        Assert.True(reset.Left >= message.Left && reset.Right <= message.Right && reset.Bottom <= message.Bottom && reset.Top > message.Top + message.Height / 2, $"The message link ({reset}) isn't inside the bottom of the message box ({message}).");

        // The Line, Then The Heading, Then The Guest Box, All Under The Message
        var line = leaf.WaitFor("ShareGuestsLine").BoundingRectangle;
        var heading = leaf.WaitFor("ShareGuestsHeading");
        var guest = leaf.WaitFor("ShareGuestBox").BoundingRectangle;
        Assert.Equal("Guests & proposed times", heading.Name);
        Assert.True(reset.Bottom <= line.Top && line.Bottom <= heading.BoundingRectangle.Top && heading.BoundingRectangle.Bottom <= guest.Top, $"The message link ({reset}), line ({line}), heading ({heading.BoundingRectangle}) and guest box ({guest}) aren't in that order.");

        DragHours(leaf, 10, 11);
        leaf.WaitFor("SharePanelSlot_0");
        Assert.Equal(panel.Width, leaf.WaitFor("ShareSlotsPanel").BoundingRectangle.Width);
    }

    // While sharing, the right panel lists the picked times; a later end time there grows the grid's slot and changes the copy
    [Fact]
    public void RightPanel_EditsASlot()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 10, 11);
        Assert.NotNull(leaf.WaitFor("SharePanelSlot_0"));
        var slot = leaf.WaitFor("ShareSlot_0").BoundingRectangle;

        // End: 11:00 to 11:05 AM (the picker's minute field, one step on, then accept)
        leaf.WaitFor("SharePanelEnd_0").Click();
        var minutes = leaf.WaitForPopup("MinuteLoopingSelector");
        minutes.Focus();
        Keyboard.Press(VirtualKeyShort.DOWN);
        Thread.Sleep(300);
        leaf.WaitForPopup("AcceptButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ShareSlot_0").BoundingRectangle.Height > slot.Height, TimeSpan.FromSeconds(5)).Success, "The grid's slot didn't grow.");
        Assert.Equal("Thu Oct 1: 10–11:05 AM ET", Copy(leaf));
    }

    // S starts sharing while a new, untouched event's editor is open, when focus isn't in a text box
    [Fact]
    public void S_WithAnUntouchedNewEditor_StartsSharing()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var column = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;
        Mouse.DoubleClick(new Point(column.X + column.Width / 2, dentist.BoundingRectangle.Y + 3 * HourPixels(dentist)));
        leaf.WaitFor("EditorAllDay").Focus();

        Keyboard.Press(VirtualKeyShort.KEY_S);

        Assert.NotNull(leaf.WaitFor("ShareSlotsPanel"));
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success, "The empty editor stayed open.");
    }

    // Once the new event has anything in it, S does nothing (the edit is never thrown away)
    [Fact]
    public void S_WithATouchedNewEditor_DoesNothing()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var column = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;
        Mouse.DoubleClick(new Point(column.X + column.Width / 2, dentist.BoundingRectangle.Y + 3 * HourPixels(dentist)));
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Lunch";
        leaf.WaitFor("EditorAllDay").Focus();

        Keyboard.Press(VirtualKeyShort.KEY_S);
        Thread.Sleep(1500);

        Assert.False(leaf.Exists("ShareSlotsPanel"));
        Assert.True(leaf.Exists("EventEditor"));
    }

    // An end before the start of a daytime time makes no sense: the slot stays as it was and the picker goes back
    [Fact]
    public void RightPanel_EndBeforeStart_PutsThePickerBack()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 10, 11);
        leaf.WaitFor("SharePanelSlot_0");

        // End: 11 AM to 10 AM (the hour field, one step back)
        leaf.WaitFor("SharePanelEnd_0").Click();
        var hours = leaf.WaitForPopup("HourLoopingSelector");
        hours.Focus();
        Keyboard.Press(VirtualKeyShort.UP);
        Thread.Sleep(300);
        leaf.WaitForPopup("AcceptButton").AsButton().Invoke();
        Thread.Sleep(800);

        Assert.Equal("Thu Oct 1: 10–11 AM ET", Copy(leaf));
    }

    // Copy asks nothing of Google, so it works offline
    [Fact]
    public void Offline_CopyStillWorks()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        _google.Offline = true;

        Assert.Equal("Thu Oct 1: 10 AM–12 PM ET", Copy(leaf));

        Assert.True(NoticeSays(leaf, "Availability copied"), "The notice didn't say the availability was copied.");
    }

    [Fact]
    public void Copy_WritesNoAvailabilityToTheLog()
    {
        using (var leaf = Launch())
        {
            StartSharing(leaf);
            DragHours(leaf, 9, 12);
            leaf.WaitFor("ShareSlot_0");
            Assert.Equal("Thu Oct 1: 9 AM–12 PM ET", Copy(leaf));
        }

        var text = ReadLog(_profile);
        Assert.Contains("share.copy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("9 AM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Thu Oct 1", text, StringComparison.Ordinal);
    }
}
