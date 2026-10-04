using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ShortcutSheetTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create(new LeafSettings { JoinShortcut = "Ctrl+Alt+Shift+J" });

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        return leaf;
    }

    // The sheet's in-app rows showing now
    private static List<AutomationElement> Rows(AutomationElement sheet) =>
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

    // A panel at the right of the calendar view, over it (not a centered dialog), like PowerToys' shortcut guide; Esc
    // closes it even with focus in its filter box, and so does ? (typed into the filter, it's not a search)
    [Fact]
    public void Sheet_IsAPanelAtTheViewsRight_EscAndQuestionMarkClose()
    {
        using var leaf = Launch();

        foreach (var key in new[] { VirtualKeyShort.ESCAPE, VirtualKeyShort.OEM_2 })
        {
            leaf.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2);
            var sheet = leaf.WaitFor("ShortcutSheet");
            var view = leaf.WaitFor("ViewHost").BoundingRectangle;
            var box = sheet.BoundingRectangle;
            Assert.True(box.Right <= view.Right && view.Right - box.Right <= 24 * leaf.Scale, $"The sheet ({box}) isn't at the view's ({view}) right edge.");
            Assert.True(box.Top >= view.Top && box.Left > view.Left, $"The sheet ({box}) isn't a panel over the view ({view}).");
            var filter = leaf.WaitFor("ShortcutFilterBox");
            Assert.True(Retry.WhileFalse(() => filter.Properties.HasKeyboardFocus.ValueOrDefault, Wait).Success, "The filter box didn't get focus.");

            if (key == VirtualKeyShort.ESCAPE)
            {
                Keyboard.Press(key);
            }
            else
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, key);
            }

            Assert.True(Retry.WhileTrue(() => leaf.Exists("ShortcutSheet"), Wait).Success, $"{key} didn't close the sheet.");
        }
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
