using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>The onboarding window's Google Cloud OAuth client step (first run, no fake Google needed).</summary>
public class SetupTests
{
    /// <summary>From Welcome: Get started, then type the client and press Next.</summary>
    internal static void EnterCredentials(LeafApp leaf, string clientId, string secret)
    {
        leaf.WaitInOnboarding("OnboardingWelcome");
        leaf.OnboardingPrimary();

        leaf.WaitInOnboarding("ClientIdBox").AsTextBox().Enter(clientId);
        leaf.WaitInOnboarding("ClientSecretBox").Focus();
        Keyboard.Type(secret);
        leaf.OnboardingPrimary();
    }

    [Fact]
    public void Launch_FreshProfile_ShowsOnboardingInsteadOfTheMainWindow()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);

            Assert.NotNull(leaf.WaitInOnboarding("OnboardingTitleBar"));
            Assert.NotNull(leaf.WaitInOnboarding("OnboardingWelcome"));
            Assert.Equal(0, leaf.WindowCount("Leaf Calendar"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void ClientStep_ShowsTheSetupGuide()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");
            leaf.OnboardingPrimary();

            Assert.NotNull(leaf.WaitInOnboarding("SetupGuideText"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Next_InvalidClientId_ShowsErrorAndStays()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);

            EnterCredentials(leaf, "not-a-client-id", "GOCSPX-uitest");

            Assert.NotNull(leaf.WaitInOnboarding("SetupError"));
            Assert.Equal("Step 2 of 5", leaf.OnboardingStepName);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Next_ValidCredentials_MovesToSignInAndPersistsAcrossLaunches()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using (var leaf = LeafApp.Launch(profile))
            {
                EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");

                Assert.NotNull(leaf.WaitInOnboarding("OnboardingSignIn"));
                Assert.Equal("Step 3 of 5", leaf.OnboardingStepName);
            }

            // Still no account, so onboarding shows again, with the saved client ID filled in
            using (var relaunched = LeafApp.Launch(profile))
            {
                relaunched.WaitInOnboarding("OnboardingWelcome");
                relaunched.OnboardingPrimary();

                Assert.Equal("123-uitest.apps.googleusercontent.com", relaunched.WaitInOnboarding("ClientIdBox").AsTextBox().Text);

                // The saved client moves on without typing the secret again
                relaunched.OnboardingPrimary();
                Assert.NotNull(relaunched.WaitInOnboarding("OnboardingSignIn"));
            }
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void EnterInClientId_MovesToTheSecret_EnterInSecret_RunsNext()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);
            leaf.WaitInOnboarding("OnboardingWelcome");
            leaf.OnboardingPrimary();

            var id = leaf.WaitInOnboarding("ClientIdBox").AsTextBox();
            id.Focus();
            Keyboard.Type("123-uitest.apps.googleusercontent.com");
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);

            Assert.True(Retry.WhileFalse(() => leaf.WaitInOnboarding("ClientSecretBox").Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(5)).Success);

            Keyboard.Type("GOCSPX-uitest");
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);

            Assert.NotNull(leaf.WaitInOnboarding("OnboardingSignIn"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
