using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SingleInstanceTests : IDisposable
{
    readonly FakeGoogleServer _google = new();

    public void Dispose() => _google.Dispose();

    [Fact]
    public void Launch_SameProfileAgain_BringsTheRunningLeafForward()
    {
        var profile = SeededProfile.Create();
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("CalendarRoot");
            leaf.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);

            // Second Launch: hands its activation to the running Leaf and exits
            using var second = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);

            Assert.Equal([leaf.App.ProcessId], LeafApp.ProcessIds(profile));
            Assert.True(Retry.WhileFalse(() => leaf.IsInFront, TimeSpan.FromSeconds(10)).Success);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Launch_OtherProfile_StartsItsOwnLeaf()
    {
        var first  = SeededProfile.Create();
        var second = SeededProfile.Create();
        try
        {
            using var leaf  = LeafApp.Launch(first, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            using var other = LeafApp.Launch(second, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("CalendarRoot");
            other.WaitFor("CalendarRoot");

            Assert.NotEqual(leaf.App.ProcessId, other.App.ProcessId);
            Assert.Equal([leaf.App.ProcessId], LeafApp.ProcessIds(first));
            Assert.Equal([other.App.ProcessId], LeafApp.ProcessIds(second));
        }
        finally
        {
            LeafApp.DeleteProfile(first);
            LeafApp.DeleteProfile(second);
        }
    }
}
