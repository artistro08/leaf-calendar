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

    // The dragged range stays drawn as a ghost while the new event's editor is open, then goes with the editor
    [Fact]
    public void DragEmptyTime_KeepsTheRangeDrawnWhileTheEditorIsOpen()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var hour    = HourPixels(dentist);
        var column  = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;
        var x       = column.X + column.Width / 2;
        var from    = dentist.BoundingRectangle.Y + 4 * hour + hour / 10;

        LeafApp.Drag(new Point(x, from), new Point(x, from + 2 * hour));
        leaf.WaitFor("EditorTitle");

        var ghost = leaf.WaitFor("Ghost_2026-10-02").BoundingRectangle;
        Assert.True(Math.Abs(ghost.Top - (dentist.BoundingRectangle.Y + 4 * hour)) <= hour / 4 && ghost.Height >= hour, $"The ghost ({ghost}) isn't over the dragged range.");

        leaf.WaitFor("EditorTitle").Focus();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("Ghost_2026-10-02"), TimeSpan.FromSeconds(5)).Success, "The ghost stayed after the editor closed.");
    }

    // Double-clicking empty space in the all-day row opens the editor on a new all-day event that day
    [Fact]
    public void DoubleClickEmptyAllDayRow_OpensAllDayEditor()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        var header = leaf.WaitFor("DayHeader_2026-09-29").BoundingRectangle;

        Mouse.DoubleClick(new Point(header.X + header.Width / 2, header.Bottom + (int)(11 * leaf.Scale)));

        Assert.True(leaf.WaitFor("EditorAllDay").AsCheckBox().IsChecked);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }

    // A drag on empty time while an edit hides behind the closed panel starts a new event; only C and E bring the hidden edit back
    [Fact]
    public void DragEmptyTime_WithAHiddenEdit_StartsANewEvent()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var hour    = HourPixels(dentist);
        dentist.Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Text += " moved";
        leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);

        var column = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;
        var x      = column.X + column.Width / 2;
        var from   = dentist.BoundingRectangle.Y + 4 * hour + hour / 10;
        LeafApp.Drag(new Point(x, from), new Point(x, from + 2 * hour));

        Assert.True(Retry.WhileFalse(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.Equal("", leaf.WaitFor("EditorTitle").AsTextBox().Text);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Focus block";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();
        var write = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 17, 0, 0, TimeSpan.Zero), Start(write));
        Assert.DoesNotContain(_google.Writes, w => w.Method == "PATCH");
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

    // The resize ghost starts where the card does; it must hide the card's text under it, or the two labels overlap
    [Fact]
    public void ResizeGhost_CoversTheCardsTextUnderIt()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        LeafApp.WaitUntilStill(dentist);
        var box   = dentist.BoundingRectangle;
        var start = new Point(box.X + box.Width / 2, box.Bottom - 2);

        LeafApp.MoveMouse(start);
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                LeafApp.MoveMouse(new Point(start.X, start.Y + HourPixels(dentist) * i / 12));
                Thread.Sleep(30);
            }

            Thread.Sleep(300);

            // The card's second line (its time) sits under the ghost's empty middle: it should read as one flat fill
            var region = new Rectangle(box.X + box.Width / 20, box.Y + box.Height * 45 / 100, box.Width * 3 / 10, box.Height / 6);
            using var shot = FlaUI.Core.Capturing.Capture.Rectangle(region);
            var lightness = new List<float>();
            for (var x = 0; x < shot.Bitmap.Width; x++)
            {
                for (var y = 0; y < shot.Bitmap.Height; y++)
                {
                    lightness.Add(shot.Bitmap.GetPixel(x, y).GetBrightness());
                }
            }

            Assert.True(lightness.Max() - lightness.Min() < 0.1f, $"Text shows through the ghost (lightness {lightness.Min():0.00} to {lightness.Max():0.00}).");
        }
        finally
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Mouse.Up(MouseButton.Left);
        }
    }

    [Fact]
    public void WiggleRepeatingEventWithinItsSlot_AsksNothingAndSendsNothing()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var hour    = HourPixels(dentist);
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var weekly = leaf.WaitFor("Event_evt-weekly_202610051330");
        LeafApp.WaitUntilStill(weekly);

        // Past the drag threshold, but less than half a snap step, so it lands where it started
        LeafApp.DragBy(weekly, 0, Math.Max(5, hour / 12));

        Thread.Sleep(2000);
        Assert.False(leaf.ExistsAnywhere("ScopeThis"));
        Assert.Empty(_google.Writes);
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));
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
