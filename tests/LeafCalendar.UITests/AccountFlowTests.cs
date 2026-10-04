using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class AccountFlowTests : IDisposable
{
    private const string Email = "leaf.tester@gmail.com";

    private readonly FakeGoogleServer _google = new();

    public void Dispose() => _google.Dispose();

    // Onboarding signs the account in (and syncs it), then Settings › Accounts shows it
    private LeafApp LaunchAndAddAccount(string profile)
    {
        var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri}");
        OnboardingTests.CompleteOnboarding(leaf);
        leaf.OpenSettings("Accounts");
        WaitForNameInSettings(leaf, "2 calendars · 8 events");
        return leaf;
    }

    private static AutomationElement WaitForNameInSettings(LeafApp leaf, string name) =>
        Retry.WhileNull(() => leaf.SettingsView.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"'{name}' didn't appear in Settings.");

    private static bool InSettings(LeafApp leaf, string name) => leaf.SettingsView.FindFirstDescendant(cf => cf.ByName(name)) is not null;

    // Disconnect the only account and confirm
    private static void Disconnect(LeafApp leaf)
    {
        leaf.PressDisconnectInSettings();
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
    }

    [Fact]
    public void AddAccount_FakeGoogle_ShowsAccountWithCounts()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            // The fake has one user, so disconnect the one onboarding added: the counts below come from the Settings add
            Disconnect(leaf);
            Assert.True(Retry.WhileTrue(() => InSettings(leaf, Email), TimeSpan.FromSeconds(15)).Success);

            leaf.WaitInSettings("AddAccountButton").AsButton().Invoke();

            Assert.NotNull(WaitForNameInSettings(leaf, Email));
            Assert.NotNull(WaitForNameInSettings(leaf, "2 calendars · 8 events"));

            // The main window's sidebar lists the account's calendars
            Assert.NotNull(leaf.WaitForName(Email));
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

            leaf.WaitInSettings("SyncNowButton").AsButton().Invoke();

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

            Disconnect(leaf);

            Assert.True(Retry.WhileTrue(() => InSettings(leaf, Email), TimeSpan.FromSeconds(15)).Success);
            Assert.True(Retry.WhileTrue(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName(Email)) is not null, TimeSpan.FromSeconds(15)).Success);
            Assert.Equal(1, _google.RevokeCount);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Disconnect_FromSettings_RemovesAccountEventsFromCalendar()
    {
        var profile = SeededProfile.Create();
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("Event_evt-single_202610011300");

            leaf.OpenSettings("Accounts");
            Disconnect(leaf);
            Assert.True(Retry.WhileTrue(() => InSettings(leaf, Email), TimeSpan.FromSeconds(15)).Success);

            // The calendar behind Settings drops the account's events right away
            Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-single_202610011300"), TimeSpan.FromSeconds(10)).Success);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
