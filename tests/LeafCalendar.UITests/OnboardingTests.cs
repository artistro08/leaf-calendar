using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Auth;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>The first-run onboarding window: step order and indicator, Back, leaving early, and the full sign-in with the fake Google.</summary>
public sealed class OnboardingTests : IDisposable
{
    readonly FakeGoogleServer _google = new();

    public void Dispose() => _google.Dispose();

    /// <summary>
    /// Runs onboarding end to end against the fake Google (client, sign-in, first sync) and presses "Open Leaf
    /// Calendar". The main window is showing when it returns.
    /// </summary>
    internal static void CompleteOnboarding(LeafApp leaf)
    {
        SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
        leaf.WaitInOnboarding("OnboardingSignIn");
        leaf.OnboardingPrimary();

        // Sign-in and the first sync run on their own; Done shows when the sync finished
        leaf.WaitInOnboarding("OnboardingDone");
        leaf.OnboardingPrimary();
        leaf.WaitFor("CalendarRoot");
    }

    LeafApp Launch(string profile) => LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Steps_MoveInOrder_AndTheIndicatorFollows()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);

            leaf.WaitInOnboarding("OnboardingWelcome");
            Assert.Equal("Step 1 of 5", leaf.OnboardingStepName);
            Assert.Equal("Get started", leaf.WaitInOnboarding("OnboardingPrimaryButton").Name);

            SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
            leaf.WaitInOnboarding("OnboardingSignIn");
            Assert.Equal("Step 3 of 5", leaf.OnboardingStepName);
            Assert.Equal("Sign in with Google", leaf.WaitInOnboarding("OnboardingPrimaryButton").Name);

            leaf.OnboardingPrimary();
            leaf.WaitInOnboarding("OnboardingDone");
            Assert.Equal("Step 5 of 5", leaf.OnboardingStepName);
            Assert.Equal("Open Leaf Calendar", leaf.WaitInOnboarding("OnboardingPrimaryButton").Name);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Back_ReturnsAStep_AndHidesOnWelcome()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");
            Assert.False(leaf.InOnboarding("OnboardingBackButton"));

            leaf.OnboardingPrimary();
            leaf.WaitInOnboarding("OnboardingClient");
            Assert.Equal("Step 2 of 5", leaf.OnboardingStepName);

            leaf.WaitInOnboarding("OnboardingBackButton").AsButton().Invoke();

            leaf.WaitInOnboarding("OnboardingWelcome");
            Assert.True(Retry.WhileTrue(() => leaf.InOnboarding("OnboardingClient"), TimeSpan.FromSeconds(5)).Success);
            Assert.Equal("Step 1 of 5", leaf.OnboardingStepName);
            Assert.False(leaf.InOnboarding("OnboardingBackButton"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void ConsoleLink_OpensGoogleCloudConsole()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");
            leaf.OnboardingPrimary();

            leaf.WaitInOnboarding("ConsoleLink").Patterns.Invoke.Pattern.Invoke();

            Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(profile).Contains("https://console.cloud.google.com/"), TimeSpan.FromSeconds(5)).Success);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Close_BeforeFinishing_AsksAndKeepSettingUpStays()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");
            leaf.OnboardingPrimary();
            leaf.WaitInOnboarding("OnboardingClient");

            leaf.OnboardingWindow.Close();

            Assert.True(Retry.WhileFalse(() => leaf.AnyTextContains("Leaf needs a Google account to show your calendar."), TimeSpan.FromSeconds(10)).Success);
            leaf.WaitForAnywhere("CloseButton").AsButton().Invoke();

            Assert.True(Retry.WhileTrue(() => leaf.ExistsAnywhere("CloseButton"), TimeSpan.FromSeconds(5)).Success);
            Assert.False(leaf.App.HasExited);
            Assert.NotNull(leaf.WaitInOnboarding("OnboardingClient"));
            Assert.Equal("Step 2 of 5", leaf.OnboardingStepName);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Close_BeforeFinishing_LeaveSetup_ExitsWithoutTheMainWindow()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");

            leaf.OnboardingWindow.Close();
            leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

            // It exits, and the main window never shows on the way out
            var sawMainWindow = false;
            var exited        = Retry.WhileFalse(
                () =>
                {
                    sawMainWindow |= MainWindowShowing(leaf);
                    return leaf.App.HasExited;
                },
                TimeSpan.FromSeconds(15)).Success;

            Assert.True(exited);
            Assert.False(sawMainWindow);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    // True when the main window is open (false once the app is gone)
    static bool MainWindowShowing(LeafApp leaf)
    {
        try
        {
            return !leaf.App.HasExited && leaf.WindowCount("Leaf Calendar") > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    // Launches a fresh profile and stops on the sign-in step, with the fake Google unreachable so sign-in waits
    LeafApp LaunchToStalledSignIn(string profile)
    {
        var leaf = Launch(profile);
        SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
        leaf.WaitInOnboarding("OnboardingSignIn");

        _google.Offline = true;
        leaf.OnboardingPrimary();
        leaf.WaitInOnboarding("OnboardingCancelButton");
        return leaf;
    }

    [Fact]
    public void Back_FromSignIn_ReturnsToTheClientStep()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
            leaf.WaitInOnboarding("OnboardingSignIn");

            leaf.WaitInOnboarding("OnboardingBackButton").AsButton().Invoke();

            Assert.NotNull(leaf.WaitInOnboarding("OnboardingClient"));
            Assert.Equal("Step 2 of 5", leaf.OnboardingStepName);
            Assert.Equal("123-uitest.apps.googleusercontent.com", leaf.WaitInOnboarding("ClientIdBox").AsTextBox().Text);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void SignIn_Cancel_ReturnsToTheReadySignInStep_ThenSignsIn()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchToStalledSignIn(profile);

            // While sign-in waits, Cancel replaces Back and the primary action is off
            Assert.False(leaf.InOnboarding("OnboardingBackButton"));
            Assert.False(leaf.WaitInOnboarding("OnboardingPrimaryButton").IsEnabled);

            leaf.WaitInOnboarding("OnboardingCancelButton").AsButton().Invoke();

            Assert.True(Retry.WhileTrue(() => leaf.InOnboarding("OnboardingCancelButton"), TimeSpan.FromSeconds(10)).Success);
            Assert.True(Retry.WhileFalse(() => leaf.WaitInOnboarding("OnboardingPrimaryButton").IsEnabled, TimeSpan.FromSeconds(10)).Success);
            Assert.Equal("Step 3 of 5", leaf.OnboardingStepName);
            Assert.False(leaf.InOnboarding("OnboardingSignInError"));

            // Google is back: signing in again works
            _google.Offline = false;
            leaf.OnboardingPrimary();
            Assert.NotNull(leaf.WaitInOnboarding("OnboardingDone"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Close_DuringSignIn_Leave_CancelsAndExitsWithNothingLeftRunning()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchToStalledSignIn(profile);

            leaf.OnboardingWindow.Close();
            leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

            Assert.True(Retry.WhileFalse(() => leaf.App.HasExited, TimeSpan.FromSeconds(15)).Success);
            Assert.Empty(LeafApp.ProcessIds(profile));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Close_OnDone_OpensTheMainWindowWithoutAsking()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
            leaf.WaitInOnboarding("OnboardingSignIn");
            leaf.OnboardingPrimary();
            leaf.WaitInOnboarding("OnboardingDone");

            leaf.OnboardingWindow.Close();

            Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
            Assert.False(leaf.ExistsAnywhere("PrimaryButton"));
            Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Set up Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);
            Assert.False(leaf.App.HasExited);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void LeaveSetup_WithAnAccountAlready_OpensTheMainWindow()
    {
        // An account is saved but its OAuth client is gone, so onboarding shows
        var profile = SeededProfile.Create();
        var store   = new CredentialLockerTokenStore(profile);
        store.DeleteAll();
        store.SetRefreshToken(SeededProfile.AccountId, "1//test-refresh-token");
        try
        {
            using var leaf = Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");

            leaf.OnboardingWindow.Close();
            leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

            Assert.NotNull(leaf.WaitFor("CalendarRoot"));
            Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Set up Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);
            Assert.False(leaf.App.HasExited);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void HappyPath_FakeGoogle_EndsOnTheMainWindowWithEvents()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = Launch(profile);
            SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
            leaf.WaitInOnboarding("OnboardingSignIn");
            leaf.OnboardingPrimary();

            // Done: what the first sync found, and Open Leaf Calendar enabled
            leaf.WaitInOnboarding("OnboardingDone");
            Assert.Equal("leaf.tester@gmail.com", leaf.WaitInOnboarding("OnboardingEmail").Name);
            Assert.Equal("2 calendars · 6 events", leaf.WaitInOnboarding("OnboardingSyncSummary").Name);
            Assert.True(leaf.WaitInOnboarding("OnboardingPrimaryButton").IsEnabled);
            Assert.False(leaf.InOnboarding("OnboardingBackButton"));
            Assert.Equal(0, leaf.WindowCount("Leaf Calendar"));

            leaf.OnboardingPrimary();

            // The main window shows the synced events, and onboarding is gone
            Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
            Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Set up Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

            // Next launch skips onboarding
            leaf.MainWindow.Close();
            Assert.True(Retry.WhileFalse(() => leaf.App.HasExited, TimeSpan.FromSeconds(15)).Success);
            using var relaunched = Launch(profile);
            Assert.NotNull(relaunched.WaitFor("CalendarRoot"));
            Assert.Equal(0, relaunched.WindowCount("Set up Leaf Calendar"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
