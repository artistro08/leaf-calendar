using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class DetailsPanelTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    static readonly string[] EditorDividers = ["Calendar", "Guests", "Reminder", "Description"];

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void ClickEvent_ShowsDetails_EscapeReturnsToUpcoming()
    {
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-single_202610011300").Click();

        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);
        Assert.Contains("Thursday, October 1", leaf.WaitFor("DetailsWhen").Name, StringComparison.Ordinal);

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
    }

    [Fact]
    public void Guests_NamedGuestShowsNameThenAddress()
    {
        _google.EditOnGoogle(SeededProfile.Email, "evt-meeting", e => e["attendees"]![0]!["displayName"] = "Pat Boss");
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        var list  = leaf.WaitFor("DetailsGuestList");
        var texts = Retry.WhileNull(() => list.FindAllDescendants().FirstOrDefault(t => t.Name == "Pat Boss") is { } name ? list.FindAllDescendants() : null, TimeSpan.FromSeconds(10)).Result
            ?? throw new InvalidOperationException("The named guest's name didn't show.");
        var name    = texts.First(t => t.Name == "Pat Boss").BoundingRectangle;
        var address = texts.First(t => t.Name == "boss@example.com").BoundingRectangle;

        // Name on line 1, the address under it; an unnamed guest shows its address alone
        Assert.True(address.Top >= name.Bottom, $"The address ({address}) isn't under the name ({name}).");
        Assert.Single(texts, t => t.Name == "sam@example.com");
    }

    [Fact]
    public void Details_ShowBusyAndVisibility()
    {
        _google.EditOnGoogle(SeededProfile.Email, "evt-single", e =>
        {
            e["transparency"] = "transparent";
            e["visibility"]   = "private";
        });
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-single_202610011300").Click();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsStatus").Name == "Free · Private", TimeSpan.FromSeconds(5)).Success, $"Status says \"{leaf.WaitFor("DetailsStatus").Name}\".");

        // Another Event Keeps Google's Defaults
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsStatus").Name == "Busy · Default visibility", TimeSpan.FromSeconds(5)).Success, $"Status says \"{leaf.WaitFor("DetailsStatus").Name}\".");
    }

    [Fact]
    public void Details_DividersSeparateTheGroupsThatShow_NeverAtTheEnds()
    {
        using var leaf = Launch();

        // The dentist has no call, guests, or description: one group, no dividers
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsStatus");
        Assert.False(leaf.Exists("DetailsDivider_Join") || leaf.Exists("DetailsDivider_People") || leaf.Exists("DetailsDivider_Description"));

        // The design review has all four groups: three dividers, in order, each between two groups
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        var status = leaf.WaitFor("DetailsStatus").BoundingRectangle;
        var join   = leaf.WaitFor("DetailsDivider_Join").BoundingRectangle;
        var people = leaf.WaitFor("DetailsDivider_People").BoundingRectangle;
        var text   = leaf.WaitFor("DetailsDivider_Description").BoundingRectangle;
        var last   = leaf.WaitFor("DetailsDescription").BoundingRectangle;
        Assert.True(status.Bottom <= join.Top && join.Bottom <= leaf.WaitFor("DetailsJoinButton").BoundingRectangle.Top, "The first divider isn't between the title block and Join.");
        Assert.True(join.Bottom < people.Top && people.Bottom < text.Top && text.Bottom <= last.Top, "The dividers are out of order or touching.");
    }

    [Fact]
    public void Editor_DividersSeparateTheGroups()
    {
        using var leaf = Launch();

        // Tall enough for the first two groups (show as and visibility take a row each)
        leaf.Resize((int)(1366 * leaf.Scale), (int)(900 * leaf.Scale));
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsStatus");
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle");

        // Title and dates | calendar, color, repeat | location, call, guests | reminders | description
        var edges = EditorDividers
            .Select(name => leaf.WaitFor($"EditorDivider_{name}").BoundingRectangle.Top)
            .ToList();
        // Dividers scrolled out of view report no position, so only the ones on screen are ordered
        var shown = edges.Where(top => top > 0).ToList();
        Assert.True(shown.Count >= 2, "The first two dividers should be on screen.");
        Assert.Equal(shown.Order().ToList(), shown);
        Assert.True(leaf.WaitFor("EditorDivider_Calendar").BoundingRectangle.Top > leaf.WaitFor("EditorEndTime").BoundingRectangle.Top, "A divider sits above the dates.");
    }

    // Review Focus 1: A Link Whose Text Doesn't Show Where It Goes Is Followed By Its Real Host
    [Fact]
    public void Description_LinkTextHidesTheTarget_ShowsTheRealHost()
    {
        _google.AddEvent(SeededProfile.Email, new JsonObject
        {
            ["id"]          = "evt-lunch",
            ["summary"]     = "Team lunch",
            ["description"] = "<a href=\"https://evil.example/login\">Log in at bank.example</a> or <a href=\"https://example.com/\">example.com</a>",
            ["start"]       = new JsonObject { ["dateTime"] = "2026-10-01T16:00:00Z" },
            ["end"]         = new JsonObject { ["dateTime"] = "2026-10-01T17:00:00Z" },
        });
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-lunch_202610011600").Click();
        var text = "";
        Assert.True(Retry.WhileFalse(() =>
        {
            text = leaf.WaitFor("DetailsDescription").Patterns.Text.Pattern.DocumentRange.GetText(-1);
            return text.Contains("Log in at bank.example (evil.example)", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5)).Success, $"The description reads \"{text}\".");
        Assert.DoesNotContain("example.com (", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClickEmptyGridSpace_ClearsSelection()
    {
        using var leaf = Launch();

        var dentist = leaf.WaitFor("Event_evt-single_202610011300");
        dentist.Click();
        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);

        // Same Day Column, Well Below The Event
        var box = dentist.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(box.X + box.Width / 2, box.Bottom + 150));

        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
        Assert.True(Retry.WhileTrue(() => leaf.Exists("DetailsTitle"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Wheel_OverDetailsPanel_ScrollsIt()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        // The family calendar's school play has a long description; N steps through events until it's selected
        Assert.True(Retry.WhileFalse(() =>
        {
            leaf.Press(VirtualKeyShort.KEY_N);
            Thread.Sleep(300);
            return leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "School play";
        }, TimeSpan.FromSeconds(10)).Success);

        // At the smallest window height the details overflow
        leaf.Resize(1300, 200);
        var scroll = leaf.WaitFor("DetailsScroll").Patterns.Scroll.Pattern;
        Assert.True(Retry.WhileFalse(() => scroll.VerticallyScrollable.ValueOrDefault, TimeSpan.FromSeconds(5)).Success);

        LeafApp.WheelOver(leaf.WaitFor("DetailsWhen"), -3);

        Assert.True(Retry.WhileFalse(() => scroll.VerticalScrollPercent.ValueOrDefault > 0, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Upcoming_OnStartDateMorning_ListsDentist()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitForName("Dentist appointment"));
        Assert.NotNull(leaf.WaitFor("UpcomingList"));
    }

    [Fact]
    public void DetailsToggle_HidesAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
            Assert.True(Retry.WhileTrue(() => leaf.Exists("UpcomingHeader"), TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        relaunched.WaitFor("CalendarRoot");
        Assert.False(relaunched.Exists("UpcomingHeader"));
    }
}
