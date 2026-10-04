using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class StoreTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // Trimmed Package: WinUI, Mica, DWrite, And Foundation Still Load (Review Focus 5)
    [Fact]
    public void TrimmedPackage_LaunchesAndShowsANotification()
    {
        // Ten Seconds Before Design Review's Reminder (Oct 1, 2 PM New York, reminded 10 minutes before)
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T13:49:50-04:00");

        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
        Assert.Contains(_google.Requests, r => r.Contains("/events", StringComparison.Ordinal));

        // The Alert Pipeline Runs In The Trimmed Package (fake-Google mode records the toast instead of calling Windows, so the
        // AppNotifications component itself is covered by PackageManifestTests and the owner's real reminders)
        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\treminders\t", StringComparison.Ordinal) && l.Contains("Design review", StringComparison.Ordinal));
        Assert.Contains("content=\"Join\"", line, StringComparison.Ordinal);
    }
}
