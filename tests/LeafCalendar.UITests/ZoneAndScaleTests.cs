using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ZoneAndScaleTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly FakeGoogleServer _google = new();
    string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch(LeafSettings? settings = null)
    {
        if (settings is not null)
        {
            LeafApp.DeleteProfile(_profile);
            _profile = SeededProfile.Create(settings);
        }

        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor(Dentist);
        return leaf;
    }

    // Opens the dentist's details through the command menu (Enter: details only), wherever the grid has scrolled
    static string DentistWhen(LeafApp leaf, bool jump = false)
    {
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        leaf.WaitForAnywhere("CommandSearchBox");
        Keyboard.Type("dentist");
        Assert.True(Retry.WhileFalse(() => leaf.ExistsAnywhere("SearchResult_evt-single"), Wait).Success);
        if (jump)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.RETURN);
        }
        else
        {
            Keyboard.Type(VirtualKeyShort.RETURN);
        }

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Dentist appointment", Wait).Success);
        return leaf.WaitFor("DetailsWhen").Name;
    }

    // Z, "Tokyo", the first suggestion, Go
    static void TravelToTokyo(LeafApp leaf)
    {
        leaf.Press(VirtualKeyShort.KEY_Z);
        var box = leaf.WaitForAnywhere("TimeTravelBox");
        box.Focus();
        Keyboard.Type("Tokyo");
        Thread.Sleep(500);
        Keyboard.Type(VirtualKeyShort.DOWN);
        Thread.Sleep(300);
        var go = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("Go")), Wait).Result;
        Assert.NotNull(go);
        Assert.True(Retry.WhileFalse(() => go.IsEnabled, Wait).Success, "Go stayed disabled after picking a suggestion.");
        go.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.AnyTextContains("Viewing your calendar in Tokyo time"), Wait).Success, "The time travel bar didn't show.");
    }

    [Fact]
    public void Z_TravelToTokyo_ShowsTheBar_AndEventTimesFollow()
    {
        using var leaf = Launch();
        var before = DentistWhen(leaf);

        TravelToTokyo(leaf);

        // 9 AM New York Is 10 PM In Tokyo
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsWhen").Name.Contains("10 PM – 11 PM", StringComparison.Ordinal), Wait).Success,
            $"Details read {leaf.WaitFor("DetailsWhen").Name}.");

        leaf.WaitFor("TimeTravelReturn").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsWhen").Name == before, Wait).Success);
        Assert.True(Retry.WhileTrue(() => leaf.AnyTextContains("Viewing your calendar in"), Wait).Success, "The bar didn't close.");
    }

    [Fact]
    public void WhileTraveling_DragCreate_UsesTheTravelZone()
    {
        using var leaf = Launch();
        TravelToTokyo(leaf);

        // The Dentist (10 PM Oct 1 In Tokyo) In View, Then Drag An Hour At The Same Time On Oct 2
        DentistWhen(leaf, jump: true);
        var dentist = leaf.WaitFor(Dentist);
        LeafApp.WaitUntilStill(dentist);
        var hour   = dentist.BoundingRectangle.Height + 2;
        var column = leaf.WaitFor("DayHeader_2026-10-02").BoundingRectangle;
        var x      = column.X + column.Width / 2;
        var from   = dentist.BoundingRectangle.Y + hour / 10;
        LeafApp.Drag(new Point(x, from), new Point(x, from + hour));

        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Tokyo sync";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("Tokyo sync", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(write.Body);
        var start = body.RootElement.GetProperty("start");
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.FromHours(9)), start.GetProperty("dateTime").GetDateTimeOffset());
        Assert.Equal("Asia/Tokyo", start.GetProperty("timeZone").GetString());
    }

    [Fact]
    public void PrimaryZone_FromSettings_MovesTheGrid()
    {
        using var leaf = Launch();
        var before = DentistWhen(leaf);

        // Pin London (BST, so 9 AM New York is 2 PM)
        leaf.OpenSettings("TimeZones");
        var follow = leaf.WaitInSettings("FollowWindowsZoneSwitch").AsToggleButton();
        Assert.Equal(FlaUI.Core.Definitions.ToggleState.On, follow.ToggleState);
        Assert.False(leaf.WaitInSettings("ZonePromptSwitch").IsEnabled);
        follow.Toggle();
        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("ZonePromptSwitch").IsEnabled, Wait).Success);

        var box = leaf.WaitInSettings("PrimaryZoneBox");
        box.Focus();
        Keyboard.Type("London");
        Thread.Sleep(500);
        Keyboard.Type(VirtualKeyShort.DOWN);
        Keyboard.Type(VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsWhen").Name.Contains("2 PM – 3 PM", StringComparison.Ordinal), Wait).Success,
            $"Details read {leaf.WaitFor("DetailsWhen").Name}.");

        // Following Windows Again Puts It Back
        leaf.WaitInSettings("FollowWindowsZoneSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsWhen").Name == before, Wait).Success);
    }

    [Fact]
    public void Scale125_SeededSetting_PanesAndCalendarGrow()
    {
        using var leaf = Launch(new LeafSettings { InterfaceScale = 1.25 });
        var sidebar = leaf.WaitFor("Sidebar");
        LeafApp.WaitUntilStill(sidebar);

        Assert.True(Math.Abs(sidebar.BoundingRectangle.Width - 264 * 1.25 * leaf.Scale) <= 1 + leaf.Scale,
            $"The sidebar is {sidebar.BoundingRectangle.Width} px wide at display scale {leaf.Scale}.");
        Assert.True(Math.Abs(leaf.WaitFor("MiniDay_2026-10-01").BoundingRectangle.Width - 28 * 1.25 * leaf.Scale) <= 1 + leaf.Scale);
    }

    [Fact]
    public void Scale150_AtMinimumWindow_NothingOverlaps()
    {
        using var leaf = Launch(new LeafSettings { InterfaceScale = 1.5 });

        // Shrink Below The Minimum (it stops there), Then Open The Editor
        leaf.MainWindow.Patterns.Transform.Pattern.Resize(400, 300);
        Thread.Sleep(1000);
        leaf.Press(VirtualKeyShort.KEY_C);
        Assert.NotNull(leaf.WaitFor("EditorSaveButton"));
        Thread.Sleep(500);

        // Title Bar And Island Header: No Two Overlap
        string[] ids = ["PeriodTitle", "SearchButton", "TodayButton", "ViewModeButton", "PreviousButton", "NextButton", "DetailsEditButton"];
        var boxes = ids.Where(leaf.Exists).Select(id => (Id: id, Box: leaf.WaitFor(id))).Where(b => !b.Box.IsOffscreen).Select(b => (b.Id, Rect: b.Box.BoundingRectangle)).ToList();
        foreach (var a in boxes)
        {
            foreach (var b in boxes.Where(b => string.CompareOrdinal(a.Id, b.Id) < 0))
            {
                Assert.False(a.Rect.IntersectsWith(b.Rect), $"{a.Id} {a.Rect} overlaps {b.Id} {b.Rect}.");
            }
        }

        // The Editor's Footer Fits; The Mini Month Stays In The Sidebar
        var client = leaf.ClientBounds;
        Assert.True(client.Contains(leaf.WaitFor("EditorSaveButton").BoundingRectangle), "Save is clipped.");
        Assert.True(client.Contains(leaf.WaitFor("EditorCancelButton").BoundingRectangle), "Cancel is clipped.");

        var sidebar = leaf.WaitFor("Sidebar").BoundingRectangle;
        var days    = leaf.MainWindow.FindAllDescendants().Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("MiniDay_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(days);
        Assert.All(days, d => Assert.True(Rectangle.Inflate(sidebar, 1, 1).Contains(d.BoundingRectangle), $"{d.AutomationId} {d.BoundingRectangle} is outside the sidebar {sidebar}."));
    }

    [Fact]
    public void ScaleFromTheCommandMenu_Applies()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        leaf.WaitForAnywhere("CommandSearchBox");
        Keyboard.Type("scale 110");
        Assert.True(Retry.WhileFalse(() => leaf.ExistsAnywhere("CommandResult_scale-110"), Wait).Success);
        Keyboard.Type(VirtualKeyShort.RETURN);

        // The Sidebar Grows, And Settings Shows The Choice
        var sidebar = leaf.WaitFor("Sidebar");
        Assert.True(Retry.WhileFalse(() => Math.Abs(sidebar.BoundingRectangle.Width - 264 * 1.1 * leaf.Scale) <= 1 + leaf.Scale, Wait).Success,
            $"The sidebar is {sidebar.BoundingRectangle.Width} px wide.");

        leaf.OpenSettings("General");
        var box = leaf.WaitInSettings("InterfaceScaleBox");
        Assert.Contains("110%", box.AsComboBox().SelectedItem?.Text ?? box.Name, StringComparison.Ordinal);
    }
}
