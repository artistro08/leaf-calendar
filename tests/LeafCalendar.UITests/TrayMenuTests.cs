using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TrayMenuTests : IDisposable
{
    private const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch(string now = "2026-10-01T08:00:00-04:00")
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    [Fact]
    public void RightClick_ShowsTheSpecItems()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();

        foreach (var id in new[] { "TrayMenuOpen", "TrayMenuNewEvent", "TrayMenuJoin", "TrayMenuSync", "TrayMenuSettings", "TrayMenuQuit" })
        {
            Assert.NotNull(leaf.WaitForPopup(id));
        }

        Assert.Equal("Settings…", leaf.WaitForPopup("TrayMenuSettings").Name);
    }

    [Fact]
    public void RightClick_WithTheFlyoutOpen_SwapsItForTheMenu()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutNewEvent");

        leaf.RightClickTrayIcon();

        // The menu stays up, the flyout goes, and the host stays shown under the menu
        leaf.WaitForPopup("TrayMenuOpen");
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutNewEvent"), TimeSpan.FromSeconds(5)).Success, "The flyout stayed open.");
        Thread.Sleep(1000);
        Assert.True(leaf.PopupExists("TrayMenuOpen"), "The menu closed with the flyout.");
        Assert.True(leaf.IsTrayHostShown());
    }

    // The owner's report: a second right-click on the icon closed the menu and opened it again in one go (a stutter)
    [Fact]
    public void RightClickAgain_ClosesTheMenuAndKeepsItClosed()
    {
        using var leaf = Launch();
        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuOpen");

        leaf.RightClickTrayIcon();

        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("TrayMenuOpen"), TimeSpan.FromSeconds(5)).Success, "The menu stayed open after a second right-click.");
        Thread.Sleep(800);
        Assert.False(leaf.PopupExists("TrayMenuOpen"), "The menu opened again after the second right-click.");
    }

    [Fact]
    public void ClickOutside_ClosesTheMenu()
    {
        using var leaf = Launch();
        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuOpen");

        // A Left Click On The Main Window's Top Left, Away From The Menu By The Taskbar
        var window = leaf.MainWindow.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(window.Left + 200, window.Top + 200));

        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("TrayMenuOpen"), TimeSpan.FromSeconds(5)).Success, "The menu stayed open after a click outside it.");
    }

    [Fact]
    public void Escape_ClosesTheMenuAndHidesItsHost()
    {
        using var leaf = Launch();
        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuOpen");
        Assert.True(leaf.IsTrayHostShown());

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("TrayMenuOpen"), TimeSpan.FromSeconds(5)).Success);
        Assert.True(Retry.WhileTrue(leaf.IsTrayHostShown, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Quit_EndsLeaf()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuQuit").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.App.HasExited, TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public void Settings_ShowsSettingsInTheMainWindow()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuSettings").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitInSettings("ThemeComboBox"));
    }

    [Fact]
    public void Open_WhileInTheTray_ShowsTheMainWindow()
    {
        using var leaf = Launch();
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuOpen").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
    }

    [Fact]
    public void NewEvent_OpensTheEditor()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuNewEvent").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitFor("EditorTitle"));
    }

    [Fact]
    public void JoinNext_MeetingSoon_OpensMeetWithItsAccount()
    {
        using var leaf = Launch("2026-10-01T13:55:00-04:00");

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuJoin").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void JoinNext_NothingSoon_SaysSo()
    {
        using var leaf = Launch("2026-10-01T11:00:00-04:00");

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuJoin").AsMenuItem().Invoke();

        LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tnotices\t", StringComparison.Ordinal) && l.Contains("No meeting to join", StringComparison.Ordinal), seconds: 10);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }

    [Fact]
    public void SyncNow_RefreshesTheCalendarList()
    {
        using var leaf = Launch();
        var before = _google.Requests.Count(r => r.Contains("calendarList", StringComparison.Ordinal));

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuSync").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => _google.Requests.Count(r => r.Contains("calendarList", StringComparison.Ordinal)) > before, TimeSpan.FromSeconds(15)).Success);
    }
}
