using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public class SetupTests
{
    internal static void EnterCredentials(LeafApp leaf, string clientId, string secret)
    {
        leaf.WaitFor("ClientIdBox").AsTextBox().Enter(clientId);
        leaf.WaitFor("ClientSecretBox").Focus();
        Keyboard.Type(secret);
        leaf.WaitFor("SaveCredentialsButton").AsButton().Invoke();
    }

    [Fact]
    public void Launch_FreshProfile_ShowsTitleBarAndSetupGuide()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);

            Assert.NotNull(leaf.WaitFor("AppTitleBar"));
            Assert.NotNull(leaf.WaitFor("SetupGuideText"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Save_InvalidClientId_ShowsError()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);

            EnterCredentials(leaf, "not-a-client-id", "GOCSPX-uitest");

            Assert.NotNull(leaf.WaitFor("SetupError"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Save_ValidCredentials_OpensAccountsAndPersistsAcrossLaunches()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using (var leaf = LeafApp.Launch(profile))
            {
                EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");

                Assert.NotNull(leaf.WaitFor("AddAccountButton"));
            }

            using (var relaunched = LeafApp.Launch(profile))
            {
                Assert.NotNull(relaunched.WaitFor("AddAccountButton"));
            }
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
