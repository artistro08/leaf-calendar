using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class AccountFlowTests : IDisposable
{
    const string Email = "leaf.tester@gmail.com";

    readonly FakeGoogleServer _google = new();

    public void Dispose() => _google.Dispose();

    // Setup, then Settings › Accounts › Add Google account
    LeafApp LaunchAndAddAccount(string profile)
    {
        var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri}");
        SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
        leaf.OpenSettings("Accounts");
        leaf.WaitInSettings("AddAccountButton").AsButton().Invoke();
        WaitForNameInSettings(leaf, "2 calendars · 6 events");
        return leaf;
    }

    static AutomationElement WaitForNameInSettings(LeafApp leaf, string name) =>
        Retry.WhileNull(() => leaf.SettingsWindow.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"'{name}' didn't appear in Settings.");

    static bool InSettings(LeafApp leaf, string name) => leaf.SettingsWindow.FindFirstDescendant(cf => cf.ByName(name)) is not null;

    // Disconnect the only account and confirm
    static void Disconnect(LeafApp leaf)
    {
        WaitForNameInSettings(leaf, "Disconnect").AsButton().Invoke();
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
    }

    [Fact]
    public void AddAccount_FakeGoogle_ShowsAccountWithCounts()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            Assert.NotNull(WaitForNameInSettings(leaf, Email));

            // The main window's sidebar lists the new account's calendars without a restart
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
