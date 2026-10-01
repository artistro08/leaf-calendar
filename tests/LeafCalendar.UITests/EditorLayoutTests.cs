using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.UITests;

public sealed class EditorLayoutTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // Opens the editor on the dentist appointment (an event you own)
    static void EditDentist(LeafApp leaf)
    {
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle");
    }

    static bool Overlap(Rectangle a, Rectangle b) => a.Top < b.Bottom && b.Top < a.Bottom;

    // Hue distance in degrees, the short way around the wheel
    static float HueDistance(Color a, Color b)
    {
        var d = Math.Abs(a.GetHue() - b.GetHue());
        return Math.Min(d, 360 - d);
    }

    static Color CenterPixel(Rectangle box)
    {
        var center = new Rectangle(box.X + box.Width / 2 - 1, box.Y + box.Height / 2 - 1, 3, 3);
        using var shot = FlaUI.Core.Capturing.Capture.Rectangle(center);
        return shot.Bitmap.GetPixel(1, 1);
    }

    [Fact]
    public void Footer_ButtonsFitAndStayPinnedWhileScrolling()
    {
        using var leaf = Launch();
        leaf.Resize(1086, 540);
        EditDentist(leaf);
        ConferencingAndContactsTests.GuestEdit(leaf).Text = "sam@example.com";
        leaf.WaitFor("EditorAddGuest").AsButton().Invoke();
        leaf.WaitFor("EditorSaveQuietButton");

        // Every footer button is whole inside the panel
        var panel = leaf.WaitFor("DetailsPanel").BoundingRectangle;
        foreach (var id in new[] { "EditorSaveButton", "EditorSaveQuietButton", "EditorCancelButton" })
        {
            var box = leaf.WaitFor(id).BoundingRectangle;
            Assert.True(panel.Contains(box), $"{id} at {box} isn't inside the panel {panel}.");
        }

        // Scrolling the body leaves the footer where it is (followed by its Save button: the footer's Border isn't in the
        // automation tree), while the body under it really scrolls
        var footer = leaf.WaitFor("EditorSaveButton").BoundingRectangle;
        var editor = leaf.WaitFor("EventEditor");
        var body   = editor.FindAllDescendants().First(e => e.Patterns.Scroll.IsSupported).Patterns.Scroll.Pattern;
        LeafApp.WheelOver(editor, -10);
        Thread.Sleep(500);
        Assert.Equal(footer.Y, leaf.WaitFor("EditorSaveButton").BoundingRectangle.Y);
        Assert.True(body.VerticalScrollPercent.ValueOrDefault > 0, "The editor body didn't scroll.");
        Assert.True(panel.Contains(leaf.WaitFor("EditorCancelButton").BoundingRectangle), "Cancel left the panel after scrolling.");
    }

    // Saves the clock setting into the profile before launch
    void UseClock(bool use24Hour)
    {
        var database = new LeafDatabase(Path.Combine(LeafApp.ProfileFolder(_profile), "leaf.db"));
        using (var conn = database.Open())
        {
            SettingsStore.Save(conn, SettingsStore.Load(conn) with { Use24HourTime = use24Hour });
        }

        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartDateAndTime_ShareOneRow_AndFit(bool use24Hour)
    {
        UseClock(use24Hour);
        using var leaf = Launch();
        EditDentist(leaf);
        var panel = leaf.WaitFor("DetailsPanel").BoundingRectangle;

        foreach (var (dateId, timeId) in new[] { ("EditorStartDate", "EditorStartTime"), ("EditorEndDate", "EditorEndTime") })
        {
            var date = leaf.WaitFor(dateId).BoundingRectangle;
            var time = leaf.WaitFor(timeId).BoundingRectangle;
            Assert.True(Overlap(date, time), $"{dateId} at {date} and {timeId} at {time} aren't on one row.");
            Assert.True(date.Right <= panel.Right && time.Right <= panel.Right, $"{dateId} ({date}) or {timeId} ({time}) runs past the panel ({panel}).");

            // Every part of the time shows: "8 00 AM" is five separate glyphs, "8 00" three. Its box alone can't tell, since
            // UI Automation clips it to what's visible; a picker drawn wider than its column loses the minutes or AM/PM
            using var ink = Ink.Capture(time);
            var glyphs = ink.Runs((int)Math.Ceiling(4 * leaf.Scale)).Count;
            Assert.True(glyphs >= (use24Hour ? 3 : 5), $"{timeId} shows {glyphs} glyphs; part of the time is cut off.");
        }
    }

    [Fact]
    public void Header_FollowsTheTitleForANewEvent()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.Press(VirtualKeyShort.KEY_C);
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Focus();
        Keyboard.Type("Lunch");

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("EditorHeader").Name == "Lunch", TimeSpan.FromSeconds(5)).Success, $"The header reads '{leaf.WaitFor("EditorHeader").Name}'.");

        title.Text = "";
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("EditorHeader").Name == "New event", TimeSpan.FromSeconds(5)).Success, $"The header reads '{leaf.WaitFor("EditorHeader").Name}'.");
    }

    [Fact]
    public void ColorSwatch_KeepsItsHueOnHover()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        // The swatches sit low in the editor; scroll them into view, with the pointer off them
        LeafApp.WheelOver(leaf.WaitFor("EventEditor"), -10);
        var swatch = leaf.WaitFor("EditorColor_11");
        LeafApp.WaitUntilStill(swatch);
        var box = swatch.BoundingRectangle;
        Mouse.MoveTo(new Point(box.X - 40, box.Y - 40));
        Thread.Sleep(300);
        var before = CenterPixel(box);

        Mouse.MoveTo(new Point(box.X + box.Width / 2, box.Y + box.Height / 2));
        Thread.Sleep(400);
        var after = CenterPixel(box);

        Assert.True(HueDistance(before, after) <= 20, $"The hue moved from {before.GetHue():0} to {after.GetHue():0}.");
        Assert.True(after.GetSaturation() > 0.3f, $"The hovered swatch lost its color (saturation {after.GetSaturation():0.00}).");
    }

    [Fact]
    public void Reminders_DropdownSendsTheChosenMinutes()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        // Turning off the default gives one row; pick 30 min there, then add a second and pick 1 hr
        leaf.WaitFor("EditorDefaultReminders").AsCheckBox().IsChecked = false;
        leaf.WaitFor("EditorReminder_0").AsComboBox().Select("30 min");
        leaf.WaitFor("EditorAddReminder").AsButton().Invoke();
        leaf.WaitFor("EditorReminder_1").AsComboBox().Select("1 hr");

        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        var body  = write.Body.Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"useDefault\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"overrides\":[{\"method\":\"popup\",\"minutes\":30},{\"method\":\"popup\",\"minutes\":60}]", body, StringComparison.Ordinal);
    }
}
