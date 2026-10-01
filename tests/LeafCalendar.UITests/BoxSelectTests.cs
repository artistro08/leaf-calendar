using System.Drawing;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class BoxSelectTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";
    const string Monday  = "Event_evt-weekly_202610051330";
    const string Friday  = "Event_evt-weekly_202610091330";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // Week Of Oct 5, Corners 12 px Above Monday's 1:30 PM Card And Below Friday's (empty time)
    static (Point From, Point To) MondayToFriday(LeafApp leaf)
    {
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var monday = leaf.WaitFor(Monday);
        var friday = leaf.WaitFor(Friday);
        LeafApp.WaitUntilStill(monday);

        var from = new Point(monday.BoundingRectangle.Left + 4, monday.BoundingRectangle.Top - 12);
        var to   = new Point(friday.BoundingRectangle.Right - 4, friday.BoundingRectangle.Bottom + 12);
        return (from, to);
    }

    static void ShiftDrag(Point from, Point to, bool ctrl = false)
    {
        Keyboard.Press(VirtualKeyShort.SHIFT);
        if (ctrl)
        {
            Keyboard.Press(VirtualKeyShort.CONTROL);
        }

        try
        {
            LeafApp.Drag(from, to);
        }
        finally
        {
            if (ctrl)
            {
                Keyboard.Release(VirtualKeyShort.CONTROL);
            }

            Keyboard.Release(VirtualKeyShort.SHIFT);
        }
    }

    // A Box From Above Monday's 1:30 PM Card To Below Friday's Selects Both
    [Fact]
    public void ShiftDrag_OverTwoEvents_SelectsBoth()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        var (from, to) = MondayToFriday(leaf);

        ShiftDrag(from, to);

        var summary = leaf.WaitFor("SelectionSummary").Name;
        Assert.Matches(@"^\d+ events selected$", summary);
        Assert.True(int.Parse(summary.Split(' ')[0], CultureInfo.InvariantCulture) >= 2, summary);
        Assert.True(Retry.WhileFalse(() => IsSelected(leaf, Monday) && IsSelected(leaf, Friday), TimeSpan.FromSeconds(5)).Success, "Monday's and Friday's cards aren't both selected.");
        Assert.Empty(_google.Writes);
    }

    // Cards publish "Selected" in their automation ItemStatus
    static bool IsSelected(LeafApp leaf, string id) =>
        (leaf.WaitFor(id).Properties.ItemStatus.ValueOrDefault ?? "").Split(';').Contains("Selected");

    static bool BoxShowing(LeafApp leaf) =>
        (leaf.WaitFor("TimeGrid").Properties.ItemStatus.ValueOrDefault ?? "").Split(';').Contains("box=1");

    // The same path with no Shift still creates an event
    [Fact]
    public void PlainDrag_SamePath_StillCreatesAnEvent()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        var (from, to) = MondayToFriday(leaf);

        LeafApp.Drag(from, to);

        Assert.NotNull(leaf.WaitFor("EventEditor"));
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("EditorHeader").Name == "New event", TimeSpan.FromSeconds(5)).Success, $"The header reads '{leaf.WaitFor("EditorHeader").Name}'.");
        Assert.False(leaf.Exists("SelectionSummary"));
    }

    // Ctrl held too adds to the selection instead of replacing it
    [Fact]
    public void ShiftCtrlDrag_AddsToSelection()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var monday = leaf.WaitFor(Monday);
        LeafApp.WaitUntilStill(monday);

        var box = monday.BoundingRectangle;
        ShiftDrag(new Point(box.Left + 4, box.Top - 12), new Point(box.Right - 4, box.Bottom + 12), ctrl: true);

        Assert.Equal("2 events selected", leaf.WaitFor("SelectionSummary").Name);
    }

    // Esc before the release drops the box and selects nothing
    [Fact]
    public void ShiftDrag_EscBeforeRelease_SelectsNothing()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        var (from, to) = MondayToFriday(leaf);

        Keyboard.Press(VirtualKeyShort.SHIFT);
        LeafApp.MoveMouse(from);
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                LeafApp.MoveMouse(new Point(from.X + (to.X - from.X) * i / 12, from.Y + (to.Y - from.Y) * i / 12));
                Thread.Sleep(30);
            }

            // The Box Really Shows Before Esc
            Assert.True(Retry.WhileFalse(() => BoxShowing(leaf), TimeSpan.FromSeconds(5)).Success, "No box showed while dragging.");
            Assert.True(leaf.Exists("SelectionBox"));

            Keyboard.Release(VirtualKeyShort.SHIFT);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        finally
        {
            Keyboard.Release(VirtualKeyShort.SHIFT);
            Mouse.Up(MouseButton.Left);
        }

        Thread.Sleep(500);
        Assert.False(leaf.Exists("SelectionSummary"));
        Assert.False(leaf.Exists("SelectionBox"));
        Assert.False(BoxShowing(leaf));
        Assert.False(leaf.Exists("EventEditor"));
    }

    // Month view: a box from the Oct 1 cell's empty lower area to the Oct 2 cell's selects the events on both days
    [Fact]
    public void MonthView_ShiftDragAcrossDays_SelectsTheirEvents()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        leaf.Press(VirtualKeyShort.KEY_M);
        LeafApp.WaitUntilStill(leaf.WaitFor("Chip_evt-single_20261001"));

        // Cells: the day number sits 4 px in and 3 px down, so a cell ends 4 px before the next one's number
        var oct1 = leaf.WaitFor("MonthDay_2026-10-01").BoundingRectangle;
        var oct2 = leaf.WaitFor("MonthDay_2026-10-02").BoundingRectangle;
        var oct8 = leaf.WaitFor("MonthDay_2026-10-08").BoundingRectangle;
        var step = oct2.X - oct1.X;
        var from = new Point(oct2.X - 4 - 6, oct8.Y - 3 - 6);

        ShiftDrag(from, new Point(from.X + step, from.Y));

        var summary = leaf.WaitFor("SelectionSummary").Name;
        Assert.Matches(@"^\d+ events selected$", summary);
        Assert.True(int.Parse(summary.Split(' ')[0], CultureInfo.InvariantCulture) >= 2, summary);
        Assert.Empty(_google.Writes);
    }
}
