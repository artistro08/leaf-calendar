using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SettingsPagesTests : IDisposable
{
    const string FamilyId = "family123@group.calendar.google.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch(string extra = "")
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 {extra}");
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        return leaf;
    }

    static string Selected(LeafApp leaf, string id) => leaf.WaitInSettings(id).AsComboBox().SelectedItem?.Name ?? "";

    static bool IsOn(LeafApp leaf, string id) => leaf.WaitInSettings(id).AsToggleButton().ToggleState == ToggleState.On;

    // The picker's shown time ("9:00 AM"): its button reads the hour, minute, and period
    static string TimeOf(LeafApp leaf, string id) =>
        string.Join(" ", leaf.WaitInSettings(id).FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name).Where(n => n.Length > 0));

    // Opens a time picker, picks an hour, minute, and period in its flyout, and accepts
    static void PickTime(LeafApp leaf, string id, string hour, string minute, string period)
    {
        leaf.WaitInSettings(id).Click();
        foreach (var (selector, value) in new[] { ("HourLoopingSelector", hour), ("MinuteLoopingSelector", minute), ("PeriodLoopingSelector", period) })
        {
            var item = Retry.WhileNull(() => leaf.WaitForAnywhere(selector).FindFirstDescendant(cf => cf.ByName(value)), TimeSpan.FromSeconds(10)).Result
                ?? throw new InvalidOperationException($"'{value}' isn't in {selector}.");
            item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            item.Patterns.SelectionItem.Pattern.Select();
        }

        leaf.WaitForAnywhere("AcceptButton").AsButton().Invoke();
    }

    [Fact]
    public void General_EveryNewSetting_SavesAndSurvivesARelaunch()
    {
        using (var leaf = Launch())
        {
            leaf.OpenSettings();
            leaf.WaitInSettings("InterfaceScaleBox").AsComboBox().Select("125%");
            leaf.WaitInSettings("AllDayExpandedSwitch").AsToggleButton().Toggle();
            leaf.WaitInSettings("UpcomingHoursBox").AsComboBox().Select("Next 4 hours");
            leaf.WaitInSettings("MapProviderBox").AsComboBox().Select("Bing Maps");
            PickTime(leaf, "WorkingStartPicker", "8", "00", "AM");
            PickTime(leaf, "WorkingEndPicker", "4", "00", "PM");
            leaf.WaitInSettings("WorkingDay_Friday").AsToggleButton().Toggle();
            Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("WorkingDay_Friday").AsToggleButton().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        relaunched.OpenSettings();
        Assert.True(Retry.WhileFalse(() => Selected(relaunched, "InterfaceScaleBox") == "125%", TimeSpan.FromSeconds(10)).Success, $"Scale: {Selected(relaunched, "InterfaceScaleBox")}");
        Assert.True(IsOn(relaunched, "AllDayExpandedSwitch"));
        Assert.Equal("Next 4 hours", Selected(relaunched, "UpcomingHoursBox"));
        Assert.Equal("Bing Maps", Selected(relaunched, "MapProviderBox"));
        Assert.Contains("8", TimeOf(relaunched, "WorkingStartPicker"), StringComparison.Ordinal);
        Assert.Contains("4", TimeOf(relaunched, "WorkingEndPicker"), StringComparison.Ordinal);
        Assert.Equal(ToggleState.Off, relaunched.WaitInSettings("WorkingDay_Friday").AsToggleButton().ToggleState);
        Assert.Equal(ToggleState.On, relaunched.WaitInSettings("WorkingDay_Monday").AsToggleButton().ToggleState);
    }

    [Fact]
    public void WorkingHours_EndBeforeStart_IsRefused()
    {
        using (var leaf = Launch())
        {
            leaf.OpenSettings();
            PickTime(leaf, "WorkingEndPicker", "7", "00", "AM");

            var error = leaf.WaitInSettings("WorkingHoursError");
            Assert.Equal("Pick an end time after the start time.", error.Name);
            Assert.Contains("5", TimeOf(leaf, "WorkingEndPicker"), StringComparison.Ordinal);
        }

        using var relaunched = Launch();
        relaunched.OpenSettings();
        var end = TimeOf(relaunched, "WorkingEndPicker");
        Assert.True(end.Contains('5', StringComparison.Ordinal) && end.Contains("PM", StringComparison.Ordinal), $"The end reads \"{end}\".");
    }

    [Fact]
    public void WorkingHours_SwitchOff_DisablesTheRows()
    {
        using var leaf = Launch();
        leaf.OpenSettings();

        leaf.WaitInSettings("WorkingHoursSwitch").AsToggleButton().Toggle();

        Assert.True(Retry.WhileTrue(() => leaf.WaitInSettings("WorkingStartPicker").IsEnabled, TimeSpan.FromSeconds(5)).Success, "The start picker stayed on.");
        Assert.False(leaf.WaitInSettings("WorkingEndPicker").IsEnabled);
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            Assert.False(leaf.WaitInSettings($"WorkingDay_{day}").IsEnabled, $"{day} stayed on.");
        }

        // No shading in the grid (Task 9 draws OffHours_* while it's on)
        Assert.True(
            Retry.WhileTrue(() => leaf.MainWindow.FindAllDescendants().Any(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("OffHours_", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)).Success,
            "The grid still shades outside working hours.");
    }

    [Fact]
    public void UpcomingHours_TwoHours_ShortensTheList()
    {
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");
        var list = leaf.WaitFor("UpcomingList");
        Assert.True(Retry.WhileFalse(() => list.FindFirstDescendant(cf => cf.ByName("Design review")) is not null, TimeSpan.FromSeconds(20)).Success, "The design review isn't listed in the next 8 hours.");
        Assert.NotNull(list.FindFirstDescendant(cf => cf.ByName("Dentist appointment")));

        leaf.OpenSettings();
        leaf.WaitInSettings("UpcomingHoursBox").AsComboBox().Select("Next 2 hours");

        Assert.True(Retry.WhileTrue(() => list.FindFirstDescendant(cf => cf.ByName("Design review")) is not null, TimeSpan.FromSeconds(10)).Success, "The design review is still listed.");
        Assert.NotNull(list.FindFirstDescendant(cf => cf.ByName("Dentist appointment")));
    }

    [Fact]
    public void MainAccount_IsListedFirst()
    {
        using var leaf = Launch();

        // A Second Account (the fake signs in as the other user)
        _google.SignInAsOtherUser = true;
        leaf.OpenSettings("Accounts");
        leaf.WaitInSettings("AddAccountButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName(FakeGoogleServer.OtherUserEmail)) is not null, TimeSpan.FromSeconds(30)).Success, "The second account never showed.");

        Retry.WhileFalse(() => leaf.WaitInSettings("MainAccountBox").IsEnabled, TimeSpan.FromSeconds(10));
        leaf.WaitInSettings("MainAccountBox").AsComboBox().Select(FakeGoogleServer.OtherUserEmail);

        // The other account's header is now the first account header in the sidebar
        Assert.True(
            Retry.WhileFalse(() => Top(leaf, FakeGoogleServer.OtherUserEmail) < Top(leaf, SeededProfile.Email), TimeSpan.FromSeconds(10)).Success,
            "The main account isn't listed first.");
    }

    // The highest element in the main window with this name
    static int Top(LeafApp leaf, string name) =>
        leaf.MainWindow.FindAllDescendants(cf => cf.ByName(name)).Select(e => e.BoundingRectangle.Top).DefaultIfEmpty(int.MaxValue).Min();

    [Fact]
    public void MeetByDefault_PerAccountSwitch()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Accounts");

        var meet = leaf.WaitInSettings($"MeetByDefault_{SeededProfile.AccountId}").AsToggleButton();
        Assert.Equal(ToggleState.Off, meet.ToggleState);
        meet.Toggle();
        Assert.True(Retry.WhileFalse(() => meet.ToggleState == ToggleState.On, TimeSpan.FromSeconds(5)).Success);

        // A New Event In The Main Window
        leaf.MainWindow.Focus();
        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Meet default";
        leaf.WaitFor("EditorTitle").AsTextBox().Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);

        var body = JsonNode.Parse(_google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Meet default\"", StringComparison.Ordinal)).Body)!;
        Assert.NotNull(body["conferenceData"]?["createRequest"]);
    }
}
