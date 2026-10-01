using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class PeopleOverlayTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";
    const string Meeting = "Event_evt-meeting_202610011800";
    const string Dana    = "dana@example.com";
    const string Sam     = "sam@example.com";
    const string Nobody  = "nobody@example.org";

    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // An Eastern wall-clock time
    static DateTimeOffset Et(int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, month, day, hour, minute, 0);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    // The dentist runs 9-10 AM Eastern: its card (an hour less 2 px, 1 px below the 9:00 line) measures the grid
    static int HourPixels(AutomationElement dentist) => dentist.BoundingRectangle.Height + 2;

    static double LineY(AutomationElement dentist, int hour) => dentist.BoundingRectangle.Top - 1 + (hour - 9) * HourPixels(dentist);

    // The text box inside the picker's AutoSuggestBox
    static TextBox PickerEdit(LeafApp leaf) =>
        Retry.WhileNull(() => leaf.WaitForAnywhere("PeoplePickerBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), TimeSpan.FromSeconds(10)).Result?.AsTextBox()
        ?? throw new InvalidOperationException("The people picker has no text box inside.");

    // Opens the picker with a key (P or F), adds each address with Enter, and presses the primary button
    static void Pick(LeafApp leaf, VirtualKeyShort key, params string[] addresses)
    {
        leaf.WaitFor(Dentist);
        leaf.Press(key);
        foreach (var address in addresses)
        {
            var edit = PickerEdit(leaf);
            edit.Focus();
            Keyboard.Type(address);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Assert.NotNull(leaf.WaitForAnywhere($"PickedPerson_{address}"));
        }

        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
    }

    static string ReadLog(string profile)
    {
        var log = Path.Combine(LeafApp.ProfileFolder(profile), "Logs", "leaf.log");
        Assert.True(File.Exists(log), "The app wrote no log.");
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The block sits in the column, its top on the line, an hour tall (± 2 px)
    // A day's column on screen: it starts where the day's header does, and is as wide as the grid says (the header's
    // own box is only as wide as its text)
    static Rectangle Column(LeafApp leaf, string date)
    {
        var status = leaf.WaitFor("TimeGrid").Properties.ItemStatus.ValueOrDefault ?? "";
        var column = status.Split(';').Select(p => p.Split('=')).Where(p => p is ["column", _]).Select(p => double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)).Single();
        var header = leaf.WaitFor($"DayHeader_{date}").BoundingRectangle;
        return new Rectangle(header.Left, header.Top, (int)Math.Round(column * leaf.Scale), header.Height);
    }

    static void AssertBlockAt(AutomationElement block, Rectangle column, double top, int height)
    {
        var box = block.BoundingRectangle;
        Assert.True(box.Left >= column.Left - 1 && box.Right <= column.Right + 1, $"Block {box} isn't inside the column {column}.");
        Assert.True(Math.Abs(box.Top - top) <= 2, $"Block top {box.Top}, expected {top}.");
        Assert.True(Math.Abs(box.Height - height) <= 2, $"Block height {box.Height}, expected {height}.");
    }

    [Fact]
    public void P_PickATeammate_ShowsTheirBusyBlocks()
    {
        _google.Busy[Dana] = [(Et(10, 1, 11), Et(10, 1, 12))];
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);

        Pick(leaf, VirtualKeyShort.KEY_P, Dana);

        Assert.NotNull(leaf.WaitFor($"OverlayChip_{Dana}"));
        var block = leaf.WaitFor($"OverlayBlock_{Dana}_0");
        AssertBlockAt(block, Column(leaf, "2026-10-01"), LineY(dentist, 11), HourPixels(dentist));
        // Named in the zone on screen (Windows' zone), whatever this machine's zone is
        Assert.StartsWith($"{Dana} busy {TimeZoneInfo.ConvertTime(Et(10, 1, 11), TimeZoneInfo.Local).ToString("h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"))}", block.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPerson_SaysNoInfo()
    {
        using var leaf = Launch();

        Pick(leaf, VirtualKeyShort.KEY_P, Nobody);

        var chip = leaf.WaitFor($"OverlayChip_{Nobody}");
        Assert.True(Retry.WhileFalse(() => chip.Name.Contains("No free/busy info", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).Success, $"The chip reads \"{chip.Name}\".");
        Assert.False(leaf.Exists($"OverlayBlock_{Nobody}_0"));
    }

    // Offline, a busy person is never drawn as free: the chip says there's no info, and no block shows
    [Fact]
    public void Offline_BusyPersonIsNeverShownFree()
    {
        _google.Busy[Dana] = [(Et(10, 1, 11), Et(10, 1, 12))];
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        _google.Offline = true;

        Pick(leaf, VirtualKeyShort.KEY_P, Dana);

        var chip = leaf.WaitFor($"OverlayChip_{Dana}");
        Assert.True(Retry.WhileFalse(() => chip.Name.Contains("No free/busy info", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).Success, $"The chip reads \"{chip.Name}\".");
        Assert.False(leaf.Exists($"OverlayBlock_{Dana}_0"));
        Assert.True(leaf.AnyTextContains("Couldn't get busy times. Check your connection."), "The notice didn't say why.");
    }

    // Enter adds the typed person and keeps the picker open (it has no default button)
    [Fact]
    public void Picker_EnterAddsAndNeverCloses()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        leaf.Press(VirtualKeyShort.KEY_P);

        foreach (var address in new[] { Dana, Sam })
        {
            var edit = PickerEdit(leaf);
            edit.Focus();
            Keyboard.Type(address);
            Keyboard.Press(VirtualKeyShort.RETURN);
            Assert.NotNull(leaf.WaitForAnywhere($"PickedPerson_{address}"));
        }

        Keyboard.Press(VirtualKeyShort.RETURN);
        Thread.Sleep(500);
        Assert.True(leaf.ExistsAnywhere("PeoplePickerBox"), "Enter closed the picker.");
        Assert.False(leaf.Exists($"OverlayChip_{Dana}"));
    }

    [Fact]
    public void SharedWithDetails_ShowsTheirTitles()
    {
        // Free/busy marks the time busy; the shared calendar names it
        _google.Busy[Dana]           = [(Et(10, 1, 13), Et(10, 1, 13, 30))];
        _google.TeammateEvents[Dana] = new JsonArray(new JsonObject
        {
            ["id"]      = "dana-1on1",
            ["status"]  = "confirmed",
            ["summary"] = "1:1 with Sam",
            ["start"]   = new JsonObject { ["dateTime"] = "2026-10-01T13:00:00-04:00" },
            ["end"]     = new JsonObject { ["dateTime"] = "2026-10-01T13:30:00-04:00" },
        });
        using var leaf = Launch();

        Pick(leaf, VirtualKeyShort.KEY_P, Dana);

        var block = leaf.WaitFor($"OverlayBlock_{Dana}_0");
        Assert.Contains("1:1 with Sam", block.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void MeetWith_DragCreatesAnEventWithThemAsGuests()
    {
        _google.Busy[Dana] = [(Et(10, 1, 11), Et(10, 1, 12))];
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);

        Pick(leaf, VirtualKeyShort.KEY_F, Dana);
        Assert.NotNull(leaf.WaitFor($"OverlayChip_{Dana}"));

        // Drag 3-4 PM on Oct 1 (a tenth of an hour in, so snapping is clear)
        var hour   = HourPixels(dentist);
        var column = leaf.WaitFor("DayHeader_2026-10-01").BoundingRectangle;
        var x      = column.X + column.Width / 2;
        var from   = (int)LineY(dentist, 15) + hour / 10;
        LeafApp.Drag(new Point(x, from), new Point(x, from + hour));

        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Sync";
        leaf.WaitFor("EditorTitle").AsTextBox().Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Sync\"", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(write.Body);
        var guests = body.RootElement.GetProperty("attendees").EnumerateArray().Select(a => a.GetProperty("email").GetString()).ToList();
        Assert.Contains(Dana, guests);
    }

    [Fact]
    public void EThenF_OverlaysTheSelectedEventsGuests()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.Press(VirtualKeyShort.KEY_F);

        Assert.NotNull(leaf.WaitFor("OverlayChip_boss@example.com"));
        Assert.NotNull(leaf.WaitFor($"OverlayChip_{Sam}"));
        Assert.False(leaf.Exists($"OverlayChip_{SeededProfile.Email}"));
    }

    [Fact]
    public void Overlay_FollowsPaging()
    {
        _google.Busy[Dana] = [(Et(10, 1, 11), Et(10, 1, 12)), (Et(10, 8, 11), Et(10, 8, 12))];
        using var leaf = Launch();

        Pick(leaf, VirtualKeyShort.KEY_P, Dana);
        var first = leaf.WaitFor($"OverlayBlock_{Dana}_0");
        var oct1  = Column(leaf, "2026-10-01");
        Assert.True(first.BoundingRectangle.Left >= oct1.Left - 1 && first.BoundingRectangle.Right <= oct1.Right + 1);
        var queries = _google.FreeBusyQueries.Count;

        leaf.Press(VirtualKeyShort.RIGHT);

        // A block shows in the Oct 8 column
        // (the column is measured each try: paging slides the days in)
        Assert.True(Retry.WhileFalse(() => leaf.FindAllAnywhere($"OverlayBlock_{Dana}_1").Concat(leaf.FindAllAnywhere($"OverlayBlock_{Dana}_0"))
            .Any(b => Column(leaf, "2026-10-08") is var oct8 && b.BoundingRectangle.Left >= oct8.Left - 1 && b.BoundingRectangle.Right <= oct8.Right + 1), TimeSpan.FromSeconds(10)).Success,
            "No overlay block shows in the Oct 8 column.");

        // Paging asked Google again, for a range covering Oct 8
        Assert.True(Retry.WhileFalse(() => _google.FreeBusyQueries.Count > queries, TimeSpan.FromSeconds(10)).Success, "Paging sent no new free/busy query.");
        using var query = JsonDocument.Parse(_google.FreeBusyQueries.Last());
        Assert.True(query.RootElement.GetProperty("timeMin").GetDateTimeOffset() <= Et(10, 8, 0));
        Assert.True(query.RootElement.GetProperty("timeMax").GetDateTimeOffset() >= Et(10, 9, 0));
    }

    [Fact]
    public void ClearAndRemove()
    {
        _google.Busy[Dana] = [(Et(10, 1, 11), Et(10, 1, 12))];
        _google.Busy[Sam]  = [(Et(10, 1, 15), Et(10, 1, 16))];
        using var leaf = Launch();

        Pick(leaf, VirtualKeyShort.KEY_P, Dana, Sam);
        Assert.NotNull(leaf.WaitFor($"OverlayBlock_{Sam}_0"));

        leaf.WaitFor($"OverlayRemove_{Sam}").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists($"OverlayChip_{Sam}") || leaf.Exists($"OverlayBlock_{Sam}_0"), TimeSpan.FromSeconds(5)).Success, "Sam's chip or block is still shown.");
        Assert.True(leaf.Exists($"OverlayChip_{Dana}"));

        leaf.WaitFor("OverlayClear").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("OverlayBar") && !leaf.WaitFor("OverlayBar").IsOffscreen, TimeSpan.FromSeconds(5)).Success, "The overlay bar still shows.");
        Assert.False(leaf.Exists($"OverlayBlock_{Dana}_0"));
    }

    [Fact]
    public void Overlay_WritesNoAddressesToTheLog()
    {
        _google.Busy[Dana] = [(Et(10, 1, 11), Et(10, 1, 12))];
        using (var leaf = Launch())
        {
            Pick(leaf, VirtualKeyShort.KEY_P, Dana);
            leaf.WaitFor($"OverlayBlock_{Dana}_0");
        }

        // The lookup really ran (so the checks below mean something)
        Assert.NotEmpty(_google.FreeBusyQueries);

        var text = ReadLog(_profile);
        Assert.Contains("freebusy.lookup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[email]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dana", text, StringComparison.OrdinalIgnoreCase);
    }
}
