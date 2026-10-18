using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// The Sunsama card in Settings › Accounts against the fake Sunsama: signing in and out from its header without opening
/// it, the reason after Sunsama refuses the saved sign-in, and the Permissions row inside (spec 2026-10-16, section 6.1).
/// </summary>
public sealed class SunsamaSettingsTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private string _profile = SeededProfile.Create(new LeafSettings());

    public SunsamaSettingsTests() => SunsamaGridTests.Seed(_google);

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private void SignedIn()
    {
        LeafApp.DeleteProfile(_profile);
        _profile = SunsamaGridTests.SignedInProfile(new LeafSettings());
    }

    private static bool SettingsShows(LeafApp leaf, string text) =>
        Retry.WhileFalse(() => leaf.SettingsView.FindFirstDescendant(cf => cf.ByName(text)) is not null, TimeSpan.FromSeconds(45)).Success;

    private static ExpandCollapseState Folding(LeafApp leaf) => leaf.WaitInSettings("SunsamaExpander").Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value;

    // Sign in from the card's header: the browser comes straight back, the success bar and the email show, the card
    // stays closed, and the client ID Leaf Calendar registered and the refresh token are saved
    [Fact]
    public void SignIn_FromHeader_DoesNotExpand()
    {
        using var leaf = SunsamaGridTests.Launch(_profile, _google);
        leaf.OpenSettings("Accounts");
        Assert.True(SettingsShows(leaf, "Not signed in"));

        leaf.WaitInSettings("SunsamaSignIn").AsButton().Invoke();

        Assert.NotNull(leaf.WaitInSettings("SunsamaSuccess"));
        Assert.True(SettingsShows(leaf, "uitest@example.com"), "The card didn't show the account's email.");
        Assert.NotNull(leaf.WaitInSettings("SunsamaSignOut"));
        Assert.Equal(ExpandCollapseState.Collapsed, Folding(leaf));
        var store = new ProtectedFileTokenStore(LeafApp.ProfileFolder(_profile));
        Assert.Equal(FakeGoogleServer.SunsamaClientId, store.GetProviderSecret("sunsama", "client_id"));
        Assert.Equal(FakeGoogleServer.SunsamaRefreshToken, store.GetProviderSecret("sunsama", "refresh_token"));
    }

    // Sign out: the card says "Not signed in", the sidebar group and the blocks go, and nothing is left saved
    [Fact]
    public void SignOut_RemovesGroupAndBlocks()
    {
        SignedIn();
        using var leaf = SunsamaGridTests.Launch(_profile, _google);
        var block = SunsamaGridTests.BlockId(SunsamaGridTests.ProjectedId, SunsamaGridTests.ProjectedStart);
        SunsamaGridTests.WaitForBlock(leaf, block);
        Assert.NotNull(leaf.WaitFor("AccountHeader_sunsama"));

        leaf.OpenSettings("Accounts");
        leaf.WaitInSettings("SunsamaSignOut").AsButton().Invoke();
        Assert.True(SettingsShows(leaf, "Not signed in"));
        Assert.NotNull(leaf.WaitInSettings("SunsamaSignIn"));
        Assert.Equal(ExpandCollapseState.Collapsed, Folding(leaf));
        leaf.CloseSettings();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("AccountHeader_sunsama"), TimeSpan.FromSeconds(10)).Success, "The sidebar kept the Sunsama group.");
        Assert.True(Retry.WhileTrue(() => leaf.Exists(block), TimeSpan.FromSeconds(10)).Success, "The calendar kept the Sunsama block.");
        Assert.Null(new ProtectedFileTokenStore(LeafApp.ProfileFolder(_profile)).GetProviderSecret("sunsama", "refresh_token"));
    }

    // Sunsama refusing the saved sign-in: the card opens with the reason, and offers Sign in again
    [Fact]
    public void RefusedRefresh_ShowsSignedOutReason()
    {
        SignedIn();
        _google.SunsamaRefuseToken = true;
        using var leaf = SunsamaGridTests.Launch(_profile, _google);

        leaf.OpenSettings("Accounts");

        Assert.True(SettingsShows(leaf, "Sunsama signed you out. Sign in again to see your task plan."), "The card didn't say why.");
        Assert.NotNull(leaf.WaitInSettings("SunsamaSignIn"));
        Assert.NotNull(leaf.WaitInSettings("SunsamaSignOut"));
    }

    // Sunsama down for 3 passes in a row: the title bar's quiet Sunsama icon says so (nothing else)
    [Fact]
    public void Unreachable_ShowsTheQuietSyncStatus()
    {
        SignedIn();
        _google.SunsamaDown = true;
        using var leaf = SunsamaGridTests.Launch(_profile, _google);

        var icon = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SunsamaUnreachableIndicator")), TimeSpan.FromSeconds(90)).Result;

        Assert.NotNull(icon);
        Assert.Equal("Couldn't reach Sunsama. Leaf Calendar will try again.", icon.Name);
        Assert.False(leaf.Exists("OfflineIndicator"), "Sunsama being down showed Google's offline icon.");
    }

    // Opening the card shows what Leaf Calendar can see
    [Fact]
    public void Expand_ShowsPermissions()
    {
        using var leaf = SunsamaGridTests.Launch(_profile, _google);
        leaf.OpenSettings("Accounts");

        leaf.ExpandInSettings("SunsamaExpander");

        Assert.True(SettingsShows(leaf, "Permissions"));
        Assert.True(SettingsShows(leaf, "Leaf Calendar can only read your Sunsama tasks and calendar. It never changes anything in Sunsama."));
    }
}
