using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TrayTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void CloseMainWindow_KeepsLeafInTheTray()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        Assert.NotEqual(0, leaf.TrayWindow());

        leaf.MainWindow.Close();

        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.False(leaf.App.HasExited);
        Assert.NotEqual(0, leaf.TrayWindow());
    }

    [Fact]
    public void LaunchAgainAfterClosing_OpensTheMainWindow()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

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
