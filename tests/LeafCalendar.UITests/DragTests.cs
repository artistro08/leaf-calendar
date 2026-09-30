using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class DragTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // The dentist is one hour long, so its card (an hour less 2 px) measures an hour on screen
    static int HourPixels(AutomationElement dentist) => dentist.BoundingRectangle.Height + 2;

    static DateTimeOffset Start(FakeWrite write)
    {
        using var body = JsonDocument.Parse(write.Body);
        return body.RootElement.GetProperty("start").GetProperty("dateTime").GetDateTimeOffset().ToUniversalTime();
    }

    static DateTimeOffset End(FakeWrite write)
    {
        using var body = JsonDocument.Parse(write.Body);
        return body.RootElement.GetProperty("end").GetProperty("dateTime").GetDateTimeOffset().ToUniversalTime();
    }

    [Fact]
    public void DragEventDownAnHour_MovesAndPatches()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);

        LeafApp.DragBy(dentist, 0, HourPixels(dentist));

        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011400"));
        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), Start(write));
    }

    [Fact]
    public void DragBottomEdge_ResizesAnHourLonger()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var box     = dentist.BoundingRectangle;

        LeafApp.Drag(new Point(box.X + box.Width / 2, box.Bottom - 2), new Point(box.X + box.Width / 2, box.Bottom - 2 + HourPixels(dentist)));

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), Start(write));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), End(write));
    }

    [Fact]
    public void DragEmptyTime_CreatesThatRange()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var hour    = HourPixels(dentist);
        var column  = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;

        // Oct 2, four to six hours after the dentist's start (a tenth of an hour in, so snapping is clear)
        var x    = column.X + column.Width / 2;
        var from = dentist.BoundingRectangle.Y + 4 * hour + hour / 10;
        LeafApp.Drag(new Point(x, from), new Point(x, from + 2 * hour));

        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Focus block";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero), Start(write));
        Assert.Equal(TimeSpan.FromHours(2), End(write) - Start(write));
    }

    [Fact]
    public void AltDrag_DuplicatesAndKeepsTheOriginal()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);

        LeafApp.DragBy(dentist, 0, HourPixels(dentist), alt: true);

        var write = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), Start(write));
        Assert.NotNull(leaf.WaitFor(Dentist));
    }

    [Fact]
    public void DragAllDayChip_ToNextDay_MovesTheDate()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        leaf.WaitFor("NextButton").AsButton().Invoke();
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var chip = leaf.WaitFor("AllDay_evt-allday_20261012");
        LeafApp.WaitUntilStill(chip);
        var step = leaf.WaitFor("DayHeader_2026-10-13").BoundingRectangle.X - leaf.WaitFor("DayHeader_2026-10-12").BoundingRectangle.X;

        LeafApp.DragBy(chip, step, 0, fromTop: 0.5);

        Assert.NotNull(leaf.WaitFor("AllDay_evt-allday_20261013"));
        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-allday", StringComparison.Ordinal));
        Assert.Contains("\"2026-10-13\"", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleClickEmptyTime_OpensEditor_EscapeSendsNothing()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var column  = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;

        Mouse.DoubleClick(new Point(column.X + column.Width / 2, dentist.BoundingRectangle.Y + 3 * HourPixels(dentist)));

        leaf.WaitFor("EditorTitle").Focus();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.Empty(_google.Writes);
    }
}
