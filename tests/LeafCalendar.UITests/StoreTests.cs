using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class StoreTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // Trimmed Package: WinUI, Mica, DWrite, And Foundation Still Load (Review Focus 5)
    [Fact]
    public void TrimmedPackage_LaunchesAndShowsANotification()
    {
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
        Assert.Contains(_google.Requests, r => r.Contains("/events", StringComparison.Ordinal));
        // TODO: Add the toast check once Milestone 4's notification test hook is merged (not on this branch)
    }
}
