using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TraySettingsTests : IDisposable
{
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    readonly FakeGoogleServer _google = new();
    readonly List<string> _profiles = [];

    public void Dispose()
    {
        foreach (var profile in _profiles)
        {
            LeafApp.DeleteProfile(profile);
        }

        _google.Dispose();
    }

    string Profile(LeafSettings? settings = null)
    {
        var profile = SeededProfile.Create(settings);
        _profiles.Add(profile);
        return profile;
    }

    LeafApp Launch(string profile, string now = "2026-10-01T08:00:00-04:00")
    {
        var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    [Fact]
    public void Notifications_TurnedOff_StayOffAfterReopening()
    {
        using var leaf = Launch(Profile());
        leaf.OpenSettings("Notifications");
        foreach (var id in new[] { "RemindersSwitch", "JoinNowSwitch", "InvitesSwitch", "SoundSwitch" })
        {
            leaf.WaitInSettings(id).AsToggleButton().Toggle();
        }

        leaf.SettingsWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Settings") > 0, TimeSpan.FromSeconds(10)).Success);
        leaf.OpenSettings("Notifications");

        foreach (var id in new[] { "RemindersSwitch", "JoinNowSwitch", "InvitesSwitch", "SoundSwitch" })
        {
            Assert.Equal(ToggleState.Off, leaf.WaitInSettings(id).AsToggleButton().ToggleState);
        }
    }

    [Fact]
    public void RemindersOff_NoReminderShows()
    {
        // 10 seconds before the meeting starts, its reminder (due at 1:50) would show at once if it were on; Join now at 2:00 is the proof the scheduler ran
        var profile = Profile(new LeafSettings { ReminderNotifications = false });
        using var leaf = Launch(profile, "2026-10-01T13:59:50-04:00");

        LeafApp.WaitForNotification(profile, l => l.StartsWith("show	join	", StringComparison.Ordinal), seconds: 30);

        Assert.DoesNotContain(LeafApp.NotificationLines(profile), l => l.StartsWith("show\treminders\t", StringComparison.Ordinal));
    }

    [Fact]
    public void Tray_DaysAllDayAndLookahead_AreSaved()
    {
        using var leaf = Launch(Profile());
        leaf.OpenSettings("Tray");
        leaf.WaitInSettings("FlyoutDaysNumberBox").Patterns.RangeValue.Pattern.SetValue(7);
        leaf.WaitInSettings("FlyoutAllDaySwitch").AsToggleButton().Toggle();
        leaf.WaitInSettings("LookaheadComboBox").AsComboBox().Select("2 hours");

        leaf.SettingsWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Settings") > 0, TimeSpan.FromSeconds(10)).Success);
        leaf.OpenSettings("Tray");

        Assert.Equal(7, leaf.WaitInSettings("FlyoutDaysNumberBox").Patterns.RangeValue.Pattern.Value.Value);
        Assert.Equal(ToggleState.Off, leaf.WaitInSettings("FlyoutAllDaySwitch").AsToggleButton().ToggleState);
        Assert.Equal("2 hours", leaf.WaitInSettings("LookaheadComboBox").AsComboBox().SelectedItem?.Text);
    }

    [Fact]
    public void Shortcuts_PressANewJoinShortcut_ItJoins()
    {
        var profile = Profile(new LeafSettings { JoinShortcut = "Ctrl+Alt+Shift+F9", FlyoutShortcut = "Ctrl+Alt+Shift+F10" });
        using var leaf = Launch(profile, "2026-10-01T13:55:00-04:00");
        leaf.OpenSettings("Shortcuts");

        leaf.WaitInSettings("JoinShortcutButton").AsButton().Invoke();

        // Keys reach the dialog only once it's open (Invoke returns before it shows)
        leaf.WaitForAnywhere("ShortcutPreview");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F7);
        Assert.True(Retry.WhileFalse(() => leaf.WaitForAnywhere("ShortcutPreview").Name == "Ctrl+Alt+Shift+F7", TimeSpan.FromSeconds(5)).Success);
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("JoinShortcutButton").Properties.HelpText.ValueOrDefault == "Ctrl+Alt+Shift+F7", TimeSpan.FromSeconds(5)).Success);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F7);
        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Shortcuts_TakenByAnotherApp_ShowTheWarning()
    {
        // This test's own thread takes Ctrl+Alt+Shift+F8 first, like another app would
        const uint ctrlAltShift = 0x2 | 0x1 | 0x4 | 0x4000;
        Assert.True(NativeMethods.RegisterHotKey(nint.Zero, 1, ctrlAltShift, 0x77), "Ctrl+Alt+Shift+F8 is already taken on this PC.");
        try
        {
            var profile = Profile(new LeafSettings { JoinShortcut = "Ctrl+Alt+Shift+F8", FlyoutShortcut = "Ctrl+Alt+Shift+F10" });
            using var leaf = Launch(profile);
            leaf.OpenSettings("Shortcuts");

            Assert.NotNull(leaf.WaitInSettings("JoinShortcutWarning"));
            Assert.Null(leaf.SettingsWindow.FindFirstDescendant(cf => cf.ByAutomationId("FlyoutShortcutWarning")));
        }
        finally
        {
            NativeMethods.UnregisterHotKey(nint.Zero, 1);
        }
    }

    [Fact]
    public void General_StartWithWindows_IsListed()
    {
        using var leaf = Launch(Profile());

        leaf.OpenSettings("General");

        Assert.NotNull(leaf.WaitInSettings("StartupSwitch"));
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint hwnd, int id);
    }
}
