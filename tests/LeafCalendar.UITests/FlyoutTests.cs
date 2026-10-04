using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class FlyoutTests : IDisposable
{
    private const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // 1:50 PM in New York on Oct 1: Design review (2 PM, Meet) is next, 10 minutes out
    private LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T13:50:00-04:00");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    // The owner's report: after a click to open and a click to close, a third click didn't open it again. Another app
    // takes the foreground between clicks, the way the taskbar does when the icon is clicked
    [Fact]
    public void TrayClick_OpenCloseOpen_OpensEveryTime()
    {
        using var leaf = Launch();

        for (var round = 0; round < 3; round++)
        {
            leaf.PostTrayMessage(LeafApp.TraySelect);
            Assert.NotNull(leaf.WaitForPopup("FlyoutNewEvent"));
            Thread.Sleep(500);
            Assert.True(leaf.PopupExists("FlyoutNewEvent"), $"Round {round + 1}: the flyout closed as soon as it opened.");

            leaf.PostTrayMessage(LeafApp.TraySelect);
            Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutNewEvent"), TimeSpan.FromSeconds(5)).Success, $"Round {round + 1}: the flyout didn't close.");

            // Something Else In Front, Then Past The Reopen Guard
            leaf.MainWindow.Focus();
            Thread.Sleep(500);
        }
    }

    [Fact]
    public void Footer_NewEventLeft_OpenCalendarRight_OpensTheWindow()
    {
        using var leaf = Launch();
        leaf.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
        leaf.PostTrayMessage(LeafApp.TraySelect);

        var newEvent = leaf.WaitForPopup("FlyoutNewEvent").BoundingRectangle;
        var open = leaf.WaitForPopup("FlyoutOpenCalendar");
        Assert.True(newEvent.Right < open.BoundingRectangle.Left, "New event isn't left of Open calendar.");

        open.AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.IsInFront, TimeSpan.FromSeconds(10)).Success, "Open calendar didn't bring the window up.");
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutNewEvent"), TimeSpan.FromSeconds(5)).Success, "The flyout stayed open.");
    }

    [Fact]
    public void ClickOutside_ClosesTheFlyout()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutNewEvent");

        // A Left Click On The Main Window's Top Left, Away From The Flyout By The Taskbar
        var window = leaf.MainWindow.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(window.Left + 200, window.Top + 200));

        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutNewEvent"), TimeSpan.FromSeconds(5)).Success, "The flyout stayed open after a click outside it.");
    }

    [Fact]
    public void TrayRightClick_Repeatedly_OpensTheMenuEveryTime()
    {
        using var leaf = Launch();

        for (var round = 0; round < 3; round++)
        {
            leaf.RightClickTrayIcon();
            Assert.NotNull(leaf.WaitForPopup("TrayMenuOpen"));
            Thread.Sleep(500);
            Assert.True(leaf.PopupExists("TrayMenuOpen"), $"Round {round + 1}: the menu closed as soon as it opened.");

            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Assert.True(Retry.WhileTrue(() => leaf.PopupExists("TrayMenuOpen"), TimeSpan.FromSeconds(5)).Success, $"Round {round + 1}: the menu didn't close.");
            leaf.MainWindow.Focus();
            Thread.Sleep(500);
        }
    }

    [Fact]
    public void TrayClick_ShowsTheNextMeetingAndTheAgenda()
    {
        using var leaf = Launch();

        leaf.PostTrayMessage(LeafApp.TraySelect);

        // (the times follow this PC's time zone, so only the countdown is checked)
        Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNextTitle").Name);
        Assert.Matches(@" · in (9|10) min$", leaf.WaitForPopup("FlyoutNextWhen").Name);
        Assert.NotNull(leaf.WaitForPopup("FlyoutJoinButton"));
        Assert.NotNull(leaf.WaitForPopup("FlyoutEvent_evt-meeting_202610011800"));
        Assert.NotNull(leaf.WaitForPopup("FlyoutJoin_evt-meeting_202610011800"));
    }

    [Fact]
    public void NextHeader_SharesTheTitlesLeftEdge()
    {
        using var leaf = Launch();

        leaf.PostTrayMessage(LeafApp.TraySelect);

        var header = leaf.WaitForPopup("FlyoutNextHeader").BoundingRectangle;
        var title = leaf.WaitForPopup("FlyoutNextTitle").BoundingRectangle;
        Assert.True(Math.Abs(header.Left - title.Left) <= 1, $"\"Next\" starts at {header.Left}, the title at {title.Left}.");
    }

    [Fact]
    public void JoinButton_OpensMeetWithItsAccount()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);

        leaf.WaitForPopup("FlyoutJoinButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void EventRow_WhileInTheTray_OpensTheMainWindowOnIt()
    {
        using var leaf = Launch();
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.MainWindowCount() > 0, TimeSpan.FromSeconds(10)).Success);

        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutEvent_evt-meeting_202610011800").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Design review", TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public void Escape_ClosesTheFlyout()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutRoot");

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }

    // New event sits at the footer's left end (Open calendar has the right)
    [Fact]
    public void NewEvent_OpensTheEditor()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);

        var button = leaf.WaitForPopup("FlyoutNewEvent");
        var root = leaf.WaitForPopup("FlyoutRoot").BoundingRectangle;
        Assert.True(button.BoundingRectangle.Right - root.Left < root.Width / 2, $"New event ({button.BoundingRectangle}) isn't at the flyout's ({root}) left.");
        button.AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("EditorTitle"));
    }

    [Fact]
    public void HostileTitle_ShowsAsPlainClippedText()
    {
        // Markup, quotes, an ampersand, line breaks, and 5,000 more characters
        var hostile = "</TextBlock><Button Content=\"x\"/> & \"quoted\"\r\nsecond line\n" + new string('A', 5000);
        _google.EditOnGoogle("leaf.tester@gmail.com", "evt-meeting", e => e["summary"] = hostile);
        using var leaf = Launch();

        leaf.PostTrayMessage(LeafApp.TraySelect);

        foreach (var id in new[] { "FlyoutNextTitle", "FlyoutEvent_evt-meeting_202610011800" })
        {
            var name = leaf.WaitForPopup(id).Name;
            Assert.StartsWith("</TextBlock><Button Content=\"x\"/> & \"quoted\" second line", name, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', name);
            Assert.DoesNotContain('\r', name);
            Assert.True(name.Length <= 201, $"Title wasn't clipped: {name.Length} characters");
        }
    }

    [Fact]
    public void OpenAndCloseThreeTimes_KeepsWorking()
    {
        using var leaf = Launch();

        for (var i = 0; i < 3; i++)
        {
            leaf.PostTrayMessage(LeafApp.TraySelect);
            Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNextTitle").Name);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
            Thread.Sleep(400);
        }

        Assert.False(leaf.App.HasExited);
    }
}
