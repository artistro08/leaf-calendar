using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TrayTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void CloseMainWindow_KeepsLeafInTheTray()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        Assert.NotEqual(0, leaf.TrayWindow());

        leaf.MainWindow.Close();

        Assert.True(Retry.WhileTrue(() => leaf.MainWindowCount() > 0, TimeSpan.FromSeconds(10)).Success);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.False(leaf.App.HasExited);
        Assert.NotEqual(0, leaf.TrayWindow());
    }

    // Closing hides the window to the tray and keeps it built: a dialog open in it (here Settings › Accounts' "Disconnect
    // this account?") must close with it, not come back over the calendar, where Disconnect would still remove the account
    [Fact]
    public void CloseWithDialogOpen_ReopensWithoutTheDialog()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        leaf.OpenSettings("Accounts");
        leaf.PressDisconnectInSettings();
        leaf.WaitForAnywhere("PrimaryButton");

        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.MainWindowCount() > 0, TimeSpan.FromSeconds(10)).Success);

        using var second = Launch();
        Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);
        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Thread.Sleep(TimeSpan.FromSeconds(1));
        Assert.False(leaf.ExistsAnywhere("PrimaryButton"), "The Disconnect dialog came back with the window.");
    }

    [Fact]
    public void LaunchAgainAfterClosing_OpensTheMainWindow()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.MainWindowCount() > 0, TimeSpan.FromSeconds(10)).Success);

        using var second = Launch();
        Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Assert.True(Retry.WhileFalse(() => leaf.IsInFront, TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void TrayIconClick_OpensTheFlyoutAndAgainClosesIt()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");

        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.NotNull(leaf.WaitForPopup("FlyoutRoot"));

        Thread.Sleep(400);
        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }
}
