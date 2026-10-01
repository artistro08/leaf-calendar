using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ShortcutTests : IDisposable
{
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    // Combinations nothing else uses, so a Leaf you have running doesn't hold them first (F12 is Windows' debugger key)
    static readonly LeafSettings Shortcuts = new() { JoinShortcut = "Ctrl+Alt+Shift+F9", FlyoutShortcut = "Ctrl+Alt+Shift+F10" };

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create(Shortcuts);

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch(string now)
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    static void Press(VirtualKeyShort key) => Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, key);

    [Fact]
    public void JoinShortcut_MeetingSoon_OpensMeetWithItsAccount()
    {
        using var leaf = Launch("2026-10-01T13:55:00-04:00");

        Press(VirtualKeyShort.F9);

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void JoinShortcut_NothingSoon_SaysSo()
    {
        using var leaf = Launch("2026-10-01T11:00:00-04:00");

        Press(VirtualKeyShort.F9);

        LeafApp.WaitForNotification(_profile, l => l.Contains("No meeting to join", StringComparison.Ordinal), seconds: 10);
    }

    [Fact]
    public void FlyoutShortcut_ShowsAndHidesTheFlyout()
    {
        using var leaf = Launch("2026-10-01T13:50:00-04:00");

        Press(VirtualKeyShort.F10);
        Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNextTitle").Name);

        Thread.Sleep(400);
        Press(VirtualKeyShort.F10);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }
}
