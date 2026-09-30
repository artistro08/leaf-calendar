using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class AccountFlowTests : IDisposable
{
    readonly FakeGoogleServer _google = new();

    public void Dispose() => _google.Dispose();

    LeafApp LaunchAndAddAccount(string profile)
    {
        var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri}");
        SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
        leaf.WaitFor("AccountsButton").AsButton().Invoke();
        leaf.WaitFor("AddAccountButton").AsButton().Invoke();
        leaf.WaitForName("2 calendars · 5 events");
        return leaf;
    }

    [Fact]
    public void AddAccount_FakeGoogle_ShowsAccountWithCounts()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            Assert.NotNull(leaf.WaitForName("leaf.tester@gmail.com"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void SyncNow_AfterAdd_RunsIncrementalSync()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            leaf.WaitFor("SyncNowButton").AsButton().Invoke();

            Assert.True(Retry.WhileFalse(() => _google.Requests.Any(r => r.Contains("syncToken=sync-token-1", StringComparison.Ordinal)), TimeSpan.FromSeconds(15)).Success);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Disconnect_Confirmed_RevokesAndRemovesAccount()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            leaf.WaitForName("Disconnect").AsButton().Invoke();
            leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

            Assert.True(Retry.WhileTrue(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("leaf.tester@gmail.com")) is not null, TimeSpan.FromSeconds(15)).Success);
            Assert.Equal(1, _google.RevokeCount);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Disconnect_BackToCalendar_RemovesAccountEvents()
    {
        var profile = SeededProfile.Create();
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("Event_evt-single_202610011300");

            // Disconnect the Only Account
            leaf.WaitFor("AccountsButton").AsButton().Invoke();
            leaf.WaitForName("Disconnect").AsButton().Invoke();
            leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
            Assert.True(Retry.WhileTrue(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("leaf.tester@gmail.com")) is not null, TimeSpan.FromSeconds(15)).Success);

            // Back to the Calendar
            leaf.WaitFor("PART_BackButton").AsButton().Invoke();
            leaf.WaitFor("CalendarRoot");

            Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-single_202610011300"), TimeSpan.FromSeconds(10)).Success);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
