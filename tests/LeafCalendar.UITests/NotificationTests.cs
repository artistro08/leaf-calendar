using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class NotificationTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // The fixtures' Design review is Oct 1, 2-3 PM New York, with a Meet link; the primary calendar reminds 10 minutes before
    LeafApp Launch(string now) => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");

    [Fact]
    public void Reminder_AtGooglesDefaultTime_IsShownWithJoinAndSnooze()
    {
        using var leaf = Launch("2026-10-01T13:49:50-04:00");

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\treminders\t", StringComparison.Ordinal) && l.Contains("Design review", StringComparison.Ordinal));

        Assert.Contains("content=\"Join\"", line, StringComparison.Ordinal);
        Assert.Contains("arguments=\"snooze\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("scenario=", line, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinNow_AtStart_UsesTheReminderScenario()
    {
        using var leaf = Launch("2026-10-01T13:59:50-04:00");

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tjoin\t", StringComparison.Ordinal));

        Assert.Contains("scenario=\"reminder\"", line, StringComparison.Ordinal);
        Assert.Contains("Design review", line, StringComparison.Ordinal);
        Assert.Contains("activationType=\"background\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("snooze", line, StringComparison.Ordinal);
    }

    [Fact]
    public void StartedAfterTheMeeting_ShowsNothingForIt()
    {
        using var leaf = Launch("2026-10-01T15:30:00-04:00");
        leaf.WaitFor("CalendarRoot");

        Thread.Sleep(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(LeafApp.NotificationLines(_profile), l => l.Contains("Design review", StringComparison.Ordinal));
    }

    [Fact]
    public void JoinNow_AfterTheMeetingEnds_IsWithdrawn()
    {
        // Starts 30 seconds before the meeting ends, so Join now shows at once (the look-back) and goes when it ends
        using var leaf = Launch("2026-10-01T14:59:30-04:00");

        var shown = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tjoin\t", StringComparison.Ordinal));
        var tag   = shown.Split('\t')[2];

        LeafApp.WaitForNotification(_profile, l => l == $"remove\tjoin\t{tag}\t");
    }
}
