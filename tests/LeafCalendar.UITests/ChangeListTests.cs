using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// The owner's running change list: sign-in's Cancel and success alert, Add account first, foldable accounts, the command
/// menu opening without a scroll, the details panel's keyboard button and shortcut hints, and the share hint toast.
/// </summary>
public sealed class ChangeListTests : IDisposable
{
    const string FamilyId = "family123@group.calendar.google.com";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        return leaf;
    }

    static bool HasName(Window window, string name) => window.FindFirstDescendant(cf => cf.ByName(name)) is not null;

    // =========================================================================
    // SETTINGS › ACCOUNTS
    // =========================================================================

    [Fact]
    public void AddAccount_Cancel_SitsBesideTheRingAndStopsTheSignIn()
    {
        using var leaf = Launch();
        _google.AbandonSignIn = true;
        leaf.OpenSettings("Accounts");

        leaf.WaitInSettings("AddAccountButton").AsButton().Invoke();
        var cancel = leaf.WaitInSettings("CancelSignInButton");
        Assert.True(Retry.WhileFalse(() => !cancel.IsOffscreen, Wait).Success, "Cancel never showed.");

        // Beside the progress ring, on the same row
        var ring = leaf.WaitInSettings("AddAccountProgress").BoundingRectangle;
        Assert.True(cancel.BoundingRectangle.Left >= ring.Right, "Cancel isn't to the right of the ring.");
        Assert.True(Math.Abs(cancel.BoundingRectangle.Top + cancel.BoundingRectangle.Height / 2 - (ring.Top + ring.Height / 2)) <= 4, "Cancel isn't beside the ring.");

        cancel.AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("AddAccountButton").IsEnabled, Wait).Success, "Add stayed off after Cancel.");
        Assert.True(Retry.WhileFalse(() => leaf.SettingsWindow.FindFirstDescendant(cf => cf.ByAutomationId("CancelSignInButton")) is null or { IsOffscreen: true }, Wait).Success, "Cancel stayed up.");
        Assert.False(HasName(leaf.SettingsWindow, "Finish signing in with Google in your browser."));
    }

    [Fact]
    public void AddAccount_Finished_ShowsASuccessAlert()
    {
        using var leaf = Launch();
        _google.SignInAsOtherUser = true;
        leaf.OpenSettings("Accounts");

        leaf.WaitInSettings("AddAccountButton").AsButton().Invoke();

        var success = $"Signed in as {FakeGoogleServer.OtherUserEmail}. Its calendars are in Leaf now.";
        Assert.True(Retry.WhileFalse(() => HasName(leaf.SettingsWindow, success), TimeSpan.FromSeconds(30)).Success, "No success alert after signing in.");
    }

    [Fact]
    public void AddAccountRow_IsAboveTheAccounts()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Accounts");

        var add     = leaf.WaitInSettings("AddAccountButton").BoundingRectangle;
        var account = leaf.WaitInSettings($"AccountExpander_{SeededProfile.AccountId}").BoundingRectangle;

        Assert.True(add.Bottom < account.Top, "Add a Google account isn't the first row.");
    }

    // =========================================================================
    // FOLDING ACCOUNTS
    // =========================================================================

    static bool Shows(LeafApp leaf, string automationId) =>
        leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { IsOffscreen: false };

    [Fact]
    public void SidebarAccount_FoldsAndUnfolds_AndStaysFoldedNextLaunch()
    {
        using (var leaf = Launch())
        {
            var header = leaf.WaitFor($"AccountHeader_{SeededProfile.AccountId}");
            Assert.Equal(SeededProfile.Email, header.Name);

            header.AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => !Shows(leaf, $"CalendarToggle_{FamilyId}"), Wait).Success, "The calendars didn't fold away.");

            header.AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => Shows(leaf, $"CalendarToggle_{FamilyId}"), Wait).Success, "The calendars didn't come back.");

            header.AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => !Shows(leaf, $"CalendarToggle_{FamilyId}"), Wait).Success);
        }

        using var relaunched = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        relaunched.WaitFor($"AccountHeader_{SeededProfile.AccountId}");
        Assert.False(Shows(relaunched, $"CalendarToggle_{FamilyId}"), "The fold wasn't remembered.");
    }

    [Fact]
    public void FoldingInSettings_FoldsTheSidebarToo()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Calendars");

        leaf.WaitInSettings($"AccountHeader_{SeededProfile.AccountId}").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.SettingsWindow.FindFirstDescendant(cf => cf.ByAutomationId($"CalendarVisible_{FamilyId}")) is null or { IsOffscreen: true }, Wait).Success, "Settings didn't fold the account.");
        Assert.True(Retry.WhileFalse(() => !Shows(leaf, $"CalendarToggle_{FamilyId}"), Wait).Success, "The sidebar didn't follow.");
    }

    // =========================================================================
    // COMMAND MENU
    // =========================================================================

    [Fact]
    public void CommandMenu_OpensWithEveryDefaultActionShowing()
    {
        using var leaf = Launch();
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        leaf.WaitForAnywhere("CommandSearchBox");

        // The last default action is on screen without scrolling
        foreach (var id in new[] { "CommandResult_create-event", "CommandResult_shortcuts" })
        {
            var row = leaf.WaitForAnywhere(id);
            Assert.True(Retry.WhileFalse(() => !row.IsOffscreen, Wait).Success, $"{id} needs a scroll to be seen.");
        }
    }

    // =========================================================================
    // KEYBOARD SHORTCUTS BUTTON AND DETAILS PANEL
    // =========================================================================

    [Fact]
    public void KeyboardButton_IsInTheSidebarsBottomRight_AndOpensTheCheatSheet()
    {
        using var leaf = Launch();

        var button  = leaf.WaitFor("SidebarShortcutsButton");
        var sidebar = leaf.WaitFor("Sidebar").BoundingRectangle;
        var box     = button.BoundingRectangle;
        Assert.Equal("Keyboard shortcuts", button.Name);
        Assert.True(sidebar.Right - box.Right < box.Width, "The keyboard button isn't at the sidebar's right edge.");
        Assert.True(sidebar.Bottom - box.Bottom < box.Height, "The keyboard button isn't at the sidebar's bottom.");
        Assert.False(Shows(leaf, "CornerShortcutsButton"), "The corner button shows with the sidebar open.");

        button.AsButton().Invoke();
        Assert.NotNull(leaf.WaitForAnywhere("ShortcutSheet"));
    }

    [Fact]
    public void KeyboardButton_SidebarClosed_StandsInTheWindowsBottomLeftCorner()
    {
        using var leaf = Launch();
        leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByAutomationId("PART_PaneToggleButton"))!.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => Shows(leaf, "CornerShortcutsButton"), Wait).Success, "No corner keyboard button with the sidebar closed.");

        var window = leaf.MainWindow.BoundingRectangle;
        var corner = leaf.WaitFor("CornerShortcutsButton").BoundingRectangle;
        Assert.True(corner.Left - window.Left < 3 * corner.Width && window.Bottom - corner.Bottom < 3 * corner.Height, "The corner button isn't at the bottom left.");
    }

    [Fact]
    public void SelectedEvent_ListsItsShortcuts()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();

        var hints = leaf.WaitFor("DetailsShortcuts");
        Assert.True(Retry.WhileFalse(() => hints.FindFirstDescendant(cf => cf.ByName("Select / deselect: X")) is not null, Wait).Success, "No shortcut hints under the event.");
    }

    [Fact]
    public void NothingComingUp_ShowsDoneForToday()
    {
        // Late in the day after the seeded events, with nothing in the next 8 hours
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T23:30:00-04:00");
        leaf.WaitFor($"CalendarToggle_{FamilyId}");

        Assert.True(Retry.WhileFalse(() => Shows(leaf, "UpcomingEmpty"), Wait).Success, "No empty state with nothing coming up.");
        Assert.Equal("Done for today", leaf.WaitFor("UpcomingEmptyTitle").Name);
    }

    // =========================================================================
    // SHARING AVAILABILITY
    // =========================================================================

    [Fact]
    public void ShareAvailability_ShowsTheHintToastUntilItStops()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.KEY_S);
        var hint = leaf.WaitFor("SharingHint");
        Assert.True(Retry.WhileFalse(() => !hint.IsOffscreen && HasName(leaf.MainWindow, "Mark times that are available on your calendar"), Wait).Success, "No hint while marking times.");

        leaf.Press(VirtualKeyShort.KEY_S);
        Assert.True(Retry.WhileFalse(() => !Shows(leaf, "SharingHint"), Wait).Success, "The hint stayed after sharing stopped.");
    }

    // =========================================================================
    // SYNC NOW
    // =========================================================================

    [Fact]
    public void SyncNow_FromTheCommandMenu_ShowsTheSyncingRing()
    {
        using var leaf = Launch();
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        var box = leaf.WaitForAnywhere("CommandSearchBox");
        box.AsTextBox().Text = "sync now";
        // The Only Match Is The First Row, Picked As The Menu Opens It (rows are list items' content, not selectable themselves)
        leaf.WaitForAnywhere("CommandResult_sync");
        FlaUI.Core.Input.Keyboard.Type(VirtualKeyShort.RETURN);

        // The ring shows for at least its minimum, then the slot empties
        Assert.True(Retry.WhileFalse(() => Shows(leaf, "SyncingIndicator"), Wait).Success, "No ring while syncing.");
        Assert.True(Retry.WhileFalse(() => !Shows(leaf, "SyncingIndicator"), TimeSpan.FromSeconds(30)).Success, "The ring never went away.");
    }
}
