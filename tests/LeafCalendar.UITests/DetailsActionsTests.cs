using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class DetailsActionsTests : IDisposable
{
    private const string Meeting = "Event_evt-meeting_202610011800";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    private bool Launched(string link) =>
        Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(link), TimeSpan.FromSeconds(10)).Success;

    [Fact]
    public void Join_OpensMeetWithTheAccountsAuthUser()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.WaitFor("DetailsJoinButton").AsButton().Invoke();

        Assert.True(Launched("https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com"));
    }

    [Fact]
    public void OpenInGoogleMaps_IsAButtonAndOpensTheMapsSearch()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var maps = leaf.WaitFor("DetailsMapsLink");
        Assert.Equal(ControlType.Button, maps.ControlType);
        Assert.Equal("Open in Google Maps", maps.Name);

        // Full width (the join button's), with a 12 DIP gap under the location text
        var button = maps.BoundingRectangle;
        var join = leaf.WaitFor("DetailsJoinButton").BoundingRectangle;
        var location = leaf.WaitFor("DetailsLocation").BoundingRectangle;
        Assert.Equal(join.Left, button.Left);
        Assert.True(Math.Abs(leaf.WaitFor("DetailsJoinMenuButton").BoundingRectangle.Right - button.Right) <= 1, $"The maps button ({button}) isn't full width.");
        Assert.True(button.Top - location.Bottom >= (int)(12 * leaf.Scale), $"The maps button sits {button.Top - location.Bottom} px under the location.");
        maps.AsButton().Invoke();

        Assert.True(Launched("https://www.google.com/maps/search/?api=1&query=Room%204"));
    }

    [Fact]
    public void Join_NamesTheServiceAndCopiesTheLink()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var join = leaf.WaitFor("DetailsJoinButton");
        Assert.True(Retry.WhileFalse(() => join.Name == "Join Google Meet", TimeSpan.FromSeconds(5)).Success, $"The Join button says \"{join.Name}\".");

        // The arrow opens the menu; Copy link puts the address the tooltip shows on the clipboard
        Clipboard.Clear();
        leaf.WaitFor("DetailsJoinMenuButton").AsButton().Invoke();
        leaf.WaitForAnywhere("CopyMeetingLinkItem").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => Clipboard.Text() == "https://meet.google.com/abc-defg-hij", TimeSpan.FromSeconds(5)).Success, $"The clipboard holds \"{Clipboard.Text()}\".");
        var notice = leaf.WaitFor("NoticeBar");
        Assert.True(Retry.WhileFalse(() => notice.FindAllDescendants().Prepend(notice).Any(e => e.Properties.Name.ValueOrDefault == "Link copied."), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void JoinMenu_CopyLink_LinesUpWithTheButtonsRightEdge()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        // The group is the Join button plus the arrow; the menu's right edge sits on the arrow's
        var arrow = leaf.WaitFor("DetailsJoinMenuButton");
        arrow.AsButton().Invoke();
        var item = leaf.WaitForAnywhere("CopyMeetingLinkItem");

        var menu = item.Parent;
        Assert.True(
            Retry.WhileFalse(() => Math.Abs(menu.BoundingRectangle.Right - arrow.BoundingRectangle.Right) <= 2, TimeSpan.FromSeconds(5)).Success,
            $"Menu {menu.BoundingRectangle} vs arrow {arrow.BoundingRectangle}.");
    }

    [Fact]
    public void JoinMenu_DownReachesCopyLink_EscClosesAndFocusReturnsToTheArrow()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var arrow = leaf.WaitFor("DetailsJoinMenuButton");
        arrow.AsButton().Invoke();
        var item = leaf.WaitForAnywhere("CopyMeetingLinkItem");

        // Down arrow lands on Copy link (the only item), Esc closes the menu
        Keyboard.Press(VirtualKeyShort.DOWN);
        Assert.True(Retry.WhileFalse(() => item.Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)).Success, "Down didn't reach Copy link.");
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.ExistsAnywhere("CopyMeetingLinkItem"), TimeSpan.FromSeconds(5)).Success, "Esc didn't close the menu.");
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsJoinMenuButton").Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)).Success, "Focus didn't return to the arrow.");
    }

    [Fact]
    public void DisabledEditIcon_Hover_ShowsWhy()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        // The invite can't be edited: hovering the disabled icon says why
        var edit = leaf.WaitFor("DetailsEditButton");
        Assert.False(edit.IsEnabled);
        Assert.Equal("You can't edit this event", edit.Properties.HelpText.ValueOrDefault);
        FlaUI.Core.Input.Mouse.MoveTo(edit.GetClickablePoint());
        Assert.True(Retry.WhileFalse(() => leaf.ToolTipShows("You can't edit this event"), TimeSpan.FromSeconds(8)).Success, "No tooltip with the reason.");
    }

    [Fact]
    public void UpcomingJoin_OpensMeet()
    {
        using var leaf = Launch();

        leaf.WaitFor("UpcomingJoin_evt-meeting").AsButton().Invoke();

        Assert.True(Launched("https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com"));
    }

    [Fact]
    public void DescriptionLinks_HttpsOpens_JavascriptIsPlainText()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var agenda = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("agenda").And(cf.ByControlType(ControlType.Hyperlink))), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(agenda);
        agenda.Patterns.Invoke.Pattern.Invoke();

        Assert.True(Launched("https://example.com/agenda"));
        Assert.Null(leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("run").And(cf.ByControlType(ControlType.Hyperlink))));
    }

    [Fact]
    public void Rsvp_MaybeWithNote_SendsAttendeesPatch()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();
        Assert.False(leaf.WaitFor("DeleteEventButton").IsEnabled);

        leaf.WaitFor("DetailsRsvpNote").AsTextBox().Text = "Running late";
        leaf.WaitFor("DetailsResponse").AsButton().Invoke();
        leaf.WaitForAnywhere("DetailsRsvpMaybe").AsMenuItem().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal));
        Assert.Contains("\"tentative\"", write.Body, StringComparison.Ordinal);
        Assert.Contains("Running late", write.Body, StringComparison.Ordinal);
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsResponse").Name == "Your response: Maybe", TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Rsvp_Dropdown_ShowsTheReplyAsABadgeWithEveryChoiceInItsMenu()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        // One full-width dropdown, read as the whole line; the badge carries the reply
        var response = leaf.WaitFor("DetailsResponse");
        Assert.StartsWith("Your response: ", response.Name, StringComparison.Ordinal);
        Assert.Equal(response.Name["Your response: ".Length..], leaf.WaitFor("DetailsResponseLabel").Name);

        response.AsButton().Invoke();
        Assert.NotNull(leaf.WaitForAnywhere("DetailsRsvpYes"));
        Assert.NotNull(leaf.WaitForAnywhere("DetailsRsvpMaybe"));
        Assert.NotNull(leaf.WaitForAnywhere("DetailsRsvpNo"));
    }

    [Fact]
    public void Guests_ListResponsesAndEmailThem()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        Assert.NotNull(leaf.WaitForName("Maybe · Optional · “Might be late”"));
        leaf.WaitFor("DetailsEmailGuests").AsButton().Invoke();

        Assert.True(Launched("mailto:boss@example.com?to=sam%40example.com&subject=Design%20review"));
    }

    [Fact]
    public void FocusedDescriptionLink_WithTooltip_StillTakesShortcuts()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var agenda = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("agenda").And(cf.ByControlType(ControlType.Hyperlink))), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(agenda);
        // Tab to the link from the reply box above it (UIA focus doesn't reach a text element), then let its tooltip open
        leaf.WaitFor("DetailsRsvpEmail").Focus();
        for (var i = 0; i < 10 && !agenda.Properties.HasKeyboardFocus.ValueOrDefault; i++)
        {
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(100);
        }

        Assert.True(agenda.Properties.HasKeyboardFocus.ValueOrDefault);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        FlaUI.Core.Input.Keyboard.Type("m");

        Assert.NotNull(leaf.WaitFor("MonthGrid"));
        Assert.False(leaf.App.HasExited);
    }
}
