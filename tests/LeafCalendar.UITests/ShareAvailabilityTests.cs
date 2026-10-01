using System.Drawing;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ShareAvailabilityTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";
    const string Family  = "family123@group.calendar.google.com";

    readonly FakeGoogleServer _google = new();
    string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // The dentist runs 9-10 AM Eastern: its card (an hour less 2 px, 1 px below the 9:00 line) measures the grid
    static int HourPixels(AutomationElement dentist) => dentist.BoundingRectangle.Height + 2;

    // Drags from one Eastern hour to another on Oct 1, near the column's right edge (beside the event cards), a tenth
    // of an hour in so snapping is clear
    static void DragHours(LeafApp leaf, int fromHour, int toHour)
    {
        var dentist = leaf.WaitFor(Dentist);
        var hour    = HourPixels(dentist);
        var column  = leaf.WaitFor("DayHeader_2026-10-01").BoundingRectangle;
        var x       = column.Right - 4;
        var top     = dentist.BoundingRectangle.Top - 1 + hour / 10;
        LeafApp.Drag(new Point(x, top + (fromHour - 9) * hour), new Point(x, top + (toHour - 9) * hour));
    }

    // S, then wait for the bar
    static void StartSharing(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.Press(VirtualKeyShort.KEY_S);
        Assert.NotNull(leaf.WaitFor("ShareBar"));
    }

    // Copy, then the clipboard's text once it holds the shared times
    static string Copy(LeafApp leaf)
    {
        Clipboard.Clear();
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        var text = Retry.WhileNull(Clipboard.Text, TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(text);
        return text;
    }

    static bool NoticeSays(LeafApp leaf, string text) =>
        Retry.WhileFalse(() => leaf.Exists("NoticeBar") && leaf.WaitFor("NoticeBar") is var notice
            && notice.FindAllDescendants().Prepend(notice).Any(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(text, StringComparison.Ordinal)), TimeSpan.FromSeconds(10)).Success;

    static string ReadLog(string profile)
    {
        var log = Path.Combine(LeafApp.ProfileFolder(profile), "Logs", "leaf.log");
        Assert.True(File.Exists(log), "The app wrote no log.");
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void S_DragSlots_CopyGivesFreeTimesOnly()
    {
        using var leaf = Launch();
        StartSharing(leaf);

        DragHours(leaf, 9, 12);

        Assert.NotNull(leaf.WaitFor("ShareSlot_0"));
        Assert.False(leaf.Exists("EventEditor"));

        // The dentist (9-10) is busy
        Assert.Equal("Thu Oct 1: 10 AM–12 PM ET", Copy(leaf));
    }

    [Fact]
    public void CopyInTokyoTime_SplitsAtMidnight()
    {
        LeafApp.DeleteProfile(_profile);
        _profile = SeededProfile.Create(new LeafSettings { TimeZones = [new ExtraTimeZone("Asia/Tokyo", null)] });
        using var leaf = Launch();
        StartSharing(leaf);

        DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        leaf.WaitFor("ShareZoneBox").AsComboBox().Select("Tokyo (UTC+9)");

        Assert.Equal("Thu Oct 1: 11 PM–12 AM Tokyo time\r\nFri Oct 2: 12–1 AM Tokyo time", Copy(leaf));
    }

    [Fact]
    public void LeavingACalendarOut_ItsEventsDontBlock()
    {
        _google.AddEvent(Family, new JsonObject
        {
            ["id"]      = "evt-family-practice",
            ["summary"] = "Practice",
            ["start"]   = new JsonObject { ["dateTime"] = "2026-10-01T10:00:00-04:00" },
            ["end"]     = new JsonObject { ["dateTime"] = "2026-10-01T11:00:00-04:00" },
        });
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");

        Assert.Equal("Thu Oct 1: 11 AM–12 PM ET", Copy(leaf));

        leaf.WaitFor("ShareCalendarsButton").Click();
        leaf.WaitForAnywhere($"ShareCalendar_{Family}").Click();

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
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareBar") || leaf.Exists("ShareSlot_0"), TimeSpan.FromSeconds(5)).Success, "The share bar or a slot still shows.");

        // Dragging makes events again
        DragHours(leaf, 15, 16);
        Assert.NotNull(leaf.WaitFor("EventEditor"));
    }

    [Fact]
    public void SidebarButton_AndCommandMenu_StartIt()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        leaf.WaitFor("SidebarShareAvailability").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("ShareBar"));
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareBar"), TimeSpan.FromSeconds(5)).Success, "Cancel left the share bar up.");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        Thread.Sleep(500);
        Keyboard.Type("share");
        Thread.Sleep(500);
        Keyboard.Press(VirtualKeyShort.RETURN);
        Assert.NotNull(leaf.WaitFor("ShareBar"));
    }

    [Fact]
    public void BookingPagesLink_OpensGoogle()
    {
        using var leaf = Launch();
        StartSharing(leaf);

        leaf.WaitFor("BookingPagesLink").Click();

        const string link = "https://calendar.google.com/calendar/appointments?authuser=leaf.tester%40gmail.com";
        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(link), TimeSpan.FromSeconds(10)).Success, $"Launched: {string.Join(" | ", LeafApp.LaunchedLinks(_profile))}");
    }

    [Fact]
    public void Offline_CopySaysWhy_AndKeepsTheSlots()
    {
        using var leaf = Launch();
        StartSharing(leaf);
        DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        _google.Offline = true;

        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();

        Assert.True(NoticeSays(leaf, "Couldn't check your calendars. Check your connection."), "The notice didn't say why.");
        Assert.True(leaf.Exists("ShareSlot_0"));
    }

    [Fact]
    public void Copy_WritesNoAvailabilityToTheLog()
    {
        using (var leaf = Launch())
        {
            StartSharing(leaf);
            DragHours(leaf, 9, 12);
            leaf.WaitFor("ShareSlot_0");
            Assert.Equal("Thu Oct 1: 10 AM–12 PM ET", Copy(leaf));
        }

        var text = ReadLog(_profile);
        Assert.Contains("share.copy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10 AM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Thu Oct 1", text, StringComparison.Ordinal);
    }
}
