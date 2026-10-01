using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ShortcutSheetTests : IDisposable
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create(new LeafSettings { JoinShortcut = "Ctrl+Alt+Shift+J" });

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        return leaf;
    }

    // The sheet's in-app rows showing now
    static List<AutomationElement> Rows(AutomationElement sheet) =>
        [.. sheet.FindAllDescendants().Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("ShortcutRow_", StringComparison.Ordinal))];

    [Fact]
    public void QuestionMark_OpensTheSheet_FilterNarrowsIt()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);
        var sheet = leaf.WaitForAnywhere("ShortcutSheet");
        Assert.True(Rows(sheet).Count > 10);

        leaf.WaitForAnywhere("ShortcutFilterBox").Click();
        Keyboard.Type("rsvp");

        Assert.True(Retry.WhileFalse(() => Rows(sheet) is [var only] && only.Name.Contains("RSVP yes / no / maybe", StringComparison.Ordinal), Wait).Success,
            $"Expected one RSVP row; found {string.Join(", ", Rows(sheet).Select(r => r.Name))}.");
    }

    [Fact]
    public void Sheet_ShowsTheGlobalShortcutsAsSet()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);
        var sheet = leaf.WaitForAnywhere("ShortcutSheet");

        Assert.NotNull(sheet.FindFirstDescendant(cf => cf.ByName("Join meeting: Ctrl+Alt+Shift+J")));
    }

    [Fact]
    public void SettingsShortcutsLink_OpensTheSheet()
    {
        using var leaf = Launch();

        leaf.OpenSettings("Shortcuts");
        leaf.WaitInSettings("ShowCheatSheetButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitInSettings("ShortcutSheet"));
    }

    [Fact]
    public void About_OpenLogFolder_LaunchesTheLogsFolder()
    {
        using var leaf = Launch();

        leaf.OpenSettings("About");
        leaf.WaitInSettings("OpenLogsButton").AsButton().Invoke();

        var expected = "folder:" + Path.Combine(LeafApp.ProfileFolder(_profile), "Logs");
        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Any(l => string.Equals(l, expected, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(10)).Success,
            $"launched.txt doesn't have {expected}.");
    }
}
