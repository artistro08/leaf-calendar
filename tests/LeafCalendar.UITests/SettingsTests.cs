using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.UITests.Support;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.UITests;

public sealed class SettingsTests : IDisposable
{
    private const string FamilyId = "family123@group.calendar.google.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        return leaf;
    }

    [Fact]
    public void SettingsButton_ShowsSettingsInTheMainWindow_AndBackReturnsToTheCalendar()
    {
        using var leaf = Launch();

        leaf.OpenSettings();
        Assert.NotNull(leaf.WaitInSettings("ThemeComboBox"));

        // In Place Of The Calendar, Not A Window Of Its Own
        Assert.Equal(0, leaf.WindowCount("Settings"));
        Assert.True(Retry.WhileFalse(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton")) is null or { IsOffscreen: true }, TimeSpan.FromSeconds(5)).Success, "The calendar still shows.");

        // The Title Bar's Back Button Brings The Calendar Back
        leaf.CloseSettings();
        Assert.NotNull(leaf.WaitFor("SettingsButton"));
        Assert.True(Retry.WhileFalse(() => leaf.BackButton() is null, TimeSpan.FromSeconds(5)).Success, "Back stayed on the calendar.");
    }

    [Fact]
    public void TimeZonesButton_OpensTheTimeZonesPage()
    {
        using var leaf = Launch();

        leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitInSettings("TimeZoneSearch"));
    }

    [Fact]
    public void ThemeDark_AppliesAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.OpenSettings();
            leaf.WaitInSettings("ThemeComboBox").AsComboBox().Select(2);
            Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("ThemeComboBox").AsComboBox().SelectedItem?.Name == "Dark", TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        relaunched.OpenSettings();
        Assert.True(Retry.WhileFalse(() => relaunched.WaitInSettings("ThemeComboBox").AsComboBox().SelectedItem?.Name == "Dark", TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void ShowWeekendsOff_HidesWeekendColumnsRightAway()
    {
        using var leaf = Launch();
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-03"));

        leaf.OpenSettings();
        leaf.WaitInSettings("WeekendsSwitch").AsToggleButton().Toggle();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("DayHeader_2026-10-03"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-02"));
    }

    [Fact]
    public void Shortcut_WhileSettingsOpen_UpdatesTheSwitch()
    {
        using var leaf = Launch();
        leaf.OpenSettings();
        Assert.Equal(ToggleState.On, leaf.WaitInSettings("WeekendsSwitch").AsToggleButton().ToggleState);

        // Back in the main window (a click, so it really has the keyboard), then the shortcut
        leaf.WaitFor("MiniMonthTitle").Click();
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_E);

        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("WeekendsSwitch").AsToggleButton().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void GeneralPage_HasNoDefaultView()
    {
        using var leaf = Launch();
        var settings = leaf.OpenSettings("General");

        // The page is loaded (a neighboring row is there), and the removed row never shows up
        leaf.WaitInSettings("CustomDaysNumberBox");
        Assert.False(Retry.WhileFalse(() => settings.FindFirstDescendant(cf => cf.ByAutomationId("DefaultViewComboBox")) is not null, TimeSpan.FromSeconds(2)).Success);
    }

    [Fact]
    public void AccountsPage_DefaultCalendar_NewEventsGoThere()
    {
        using var leaf = Launch();
        var settings = leaf.OpenSettings("Accounts");
        leaf.WaitInSettings("DefaultCalendarComboBox").AsComboBox().Select("Family");
        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("DefaultCalendarComboBox").AsComboBox().SelectedItem?.Name == "Family", TimeSpan.FromSeconds(5)).Success);
        leaf.CloseSettings();

        leaf.MainWindow.Focus();
        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Picnic";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Contains(Uri.EscapeDataString(FamilyId), write.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void CalendarsPage_HideCalendar_UnchecksTheSidebar()
    {
        using var leaf = Launch();

        leaf.OpenSettings("Calendars");
        leaf.WaitInSettings($"CalendarVisible_{FamilyId}").AsToggleButton().Toggle();

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void CalendarsPage_PickSwatch_ThenReset()
    {
        using var leaf = Launch();
        var settings = leaf.OpenSettings("Calendars");

        leaf.WaitInSettings($"CalendarColor_{FamilyId}").AsButton().Invoke();
        leaf.WaitForAnywhere("ColorSwatch_16A765").AsButton().Invoke();
        Assert.True(Retry.WhileNull(() => settings.FindFirstDescendant(cf => cf.ByName("Custom color")), TimeSpan.FromSeconds(5)).Success);

        leaf.WaitInSettings($"CalendarColor_{FamilyId}").AsButton().Invoke();
        leaf.WaitForAnywhere("ColorReset").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => settings.FindFirstDescendant(cf => cf.ByName("Custom color")) is not null, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void ClosingMainWindow_InSettings_KeepsLeafRunning_AndReopensOnTheCalendar()
    {
        using var leaf = Launch();
        leaf.OpenSettings("About");
        Assert.NotNull(leaf.WaitInSettings("AboutVersion"));

        leaf.MainWindow.Close();

        Assert.True(Retry.WhileTrue(() => leaf.MainWindowCount() > 0, TimeSpan.FromSeconds(10)).Success);
        Assert.False(leaf.App.HasExited);

        // Opened Again From The Tray: The Calendar, Not Settings
        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuOpen").AsMenuItem().Invoke();
        Assert.NotNull(leaf.WaitFor("SettingsButton"));
        Assert.False(leaf.IsSettingsOpen);
    }

    [Fact]
    public void ChangeOAuthClient_Cancel_ReturnsToAccounts()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Accounts");

        leaf.WaitInSettings("ChangeClientButton").AsButton().Invoke();
        Assert.Equal("123-uitest.apps.googleusercontent.com", leaf.WaitInSettings("SettingsClientIdBox").AsTextBox().Text);
        leaf.WaitInSettings("ClientCancelButton").AsButton().Invoke();
        Assert.NotNull(leaf.WaitInSettings("AddAccountButton"));

        // Accounts in the pane (still selected) also comes back from the form
        leaf.WaitInSettings("ChangeClientButton").AsButton().Invoke();
        leaf.WaitInSettings("SettingsClientIdBox");
        leaf.WaitInSettings("SettingsNav_Accounts").Click();
        Assert.NotNull(leaf.WaitInSettings("AddAccountButton"));
    }

    [Fact]
    public void ChangeOAuthClient_TitleBarBack_ReturnsToAccounts_ThenToTheCalendar()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Accounts");

        // The Back Button Sits Left Of The Hamburger
        leaf.WaitInSettings("AddAccountButton");
        var back = Retry.WhileNull(leaf.BackButton, TimeSpan.FromSeconds(5)).Result
            ?? throw new InvalidOperationException("The title bar has no back button in Settings.");
        var toggle = leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("AppTitleBar"))?.FindFirstDescendant(cf => cf.ByAutomationId("PART_PaneToggleButton"))
            ?? throw new InvalidOperationException("The title bar has no hamburger in Settings.");
        Assert.True(back.BoundingRectangle.Right <= toggle.BoundingRectangle.Left, "The back button isn't left of the hamburger.");

        // From The Client Form, Back Goes To Accounts
        leaf.WaitInSettings("ChangeClientButton").AsButton().Invoke();
        leaf.WaitInSettings("SettingsClientIdBox");
        back.AsButton().Invoke();
        Assert.NotNull(leaf.WaitInSettings("AddAccountButton"));
        Assert.True(leaf.IsSettingsOpen);

        // From Accounts, Back Goes To The Calendar
        leaf.CloseSettings();
        Assert.NotNull(leaf.WaitFor("AddTimeZoneButton"));
    }

    [Fact]
    public void Disconnect_WithUnsentChanges_WarnsTheyWillBeLost()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        // Offline: the delete stays in the outbox
        _google.Offline = true;
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DeleteEventButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-single_202610011300"), TimeSpan.FromSeconds(5)).Success);

        // Wait for the outbox entry (it may only be written once the 6 s undo window ends)
        Assert.True(Retry.WhileFalse(() => UnsentChanges() > 0, TimeSpan.FromSeconds(15)).Success);

        leaf.OpenSettings("Accounts");
        leaf.PressDisconnectInSettings();

        Assert.True(Retry.WhileFalse(() => leaf.AnyTextContains("hasn't reached Google yet, and it will be lost."), TimeSpan.FromSeconds(10)).Success);
        leaf.WaitForAnywhere("CloseButton").AsButton().Invoke();
        Assert.Equal(0, _google.RevokeCount);
    }

    [Fact]
    public void ChangeOAuthClient_EnterInClientId_MovesToTheSecret()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Accounts");
        leaf.WaitInSettings("ChangeClientButton").AsButton().Invoke();

        leaf.WaitInSettings("SettingsClientIdBox").Focus();
        Keyboard.Type(VirtualKeyShort.ENTER);

        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("SettingsClientSecretBox").Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(5)).Success);
    }

    // Edits of the seeded account waiting in the outbox, read straight from the profile's database
    private int UnsentChanges()
    {
        var database = new LeafDatabase(Path.Combine(LeafApp.ProfileFolder(_profile), "leaf.db"));
        try
        {
            return new ConflictResolver(database, TimeProvider.System).UnsentFor(SeededProfile.AccountId);
        }
        finally
        {
            // Pooled connections would keep leaf.db open and block deleting the profile
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void About_GitHubLink_OpensTheRepository()
    {
        using var leaf = Launch();
        leaf.OpenSettings("About");

        leaf.WaitInSettings("GitHubLink").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains("https://github.com/artistro08/leaf-calendar"), TimeSpan.FromSeconds(5)).Success);
    }
}
