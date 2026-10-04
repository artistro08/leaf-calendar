using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class EventShortcutTests : IDisposable
{
    private const string Dentist = "Event_evt-single_202610011300";
    private const string Meeting = "Event_evt-meeting_202610011800";
    private const string Weekly = "Event_evt-weekly_202610051330";
    private const string MeetJoin = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    private bool Launched(string link) =>
        Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(link), TimeSpan.FromSeconds(10)).Success;

    [Fact]
    public void EThenY_RepliesGoing()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.Press(VirtualKeyShort.KEY_Y);

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal));
        Assert.Contains("\"accepted\"", write.Body, StringComparison.Ordinal);
        Assert.False(leaf.Exists("EventEditor"));
    }

    [Fact]
    public void EAlone_OpensTheEditorAtOnce()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.KEY_E);

        Assert.True(Retry.WhileFalse(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(0.5)).Success);

        // The E itself never lands in the title, and the editor stays once the sequence times out
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.Equal("Dentist appointment", leaf.WaitFor("EditorTitle").AsTextBox().Text);
    }

    [Fact]
    public void EThenU_FocusesTheEndTime()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        Keyboard.Type(VirtualKeyShort.KEY_U);

        var end = leaf.WaitFor("EditorEndTime");
        Assert.True(Retry.WhileFalse(() => end.FindAllDescendants().Prepend(end).Any(e => e.Properties.HasKeyboardFocus.ValueOrDefault), TimeSpan.FromSeconds(5)).Success);
        Assert.Equal("Dentist appointment", leaf.WaitFor("EditorTitle").AsTextBox().Text);
    }

    [Fact]
    public void EThenT_TypesIntoTheTitle()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        // On the next week, so a T taken as "today" would move the view back
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var weekly = leaf.WaitFor(Weekly);
        LeafApp.WaitUntilStill(weekly);
        weekly.Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        Keyboard.Type(VirtualKeyShort.KEY_T);

        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        Assert.True(Retry.WhileFalse(() => title.Text.EndsWith('t'), TimeSpan.FromSeconds(5)).Success, $"The title is \"{title.Text}\".");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        Assert.True(leaf.Exists(Weekly) && !leaf.WaitFor(Weekly).IsOffscreen);
    }

    [Fact]
    public void EThenY_OnYourOwnEvent_ClosesTheEditor()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        Keyboard.Type(VirtualKeyShort.KEY_Y);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);
    }

    [Fact]
    public void V_OpensTheMeetingLinkAsIs()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.Press(VirtualKeyShort.KEY_V);

        Assert.True(Launched("https://meet.google.com/abc-defg-hij"));
    }

    [Fact]
    public void CtrlJ_WithNothingSelected_JoinsTheNextMeeting()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_J);

        Assert.True(Launched(MeetJoin));
    }

    [Fact]
    public void CtrlShiftDelete_DeletesWithoutEmailingGuests()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.DELETE);

        var write = _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Contains("sendUpdates=none", write.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void EThenE_EmailsTheGuests()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.Press(VirtualKeyShort.KEY_E);

        Assert.True(Launched("mailto:boss@example.com?to=sam%40example.com&subject=Design%20review"));
    }

    [Fact]
    public void V_WithTwoSelected_AsksForOneEvent()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();
        Keyboard.Press(VirtualKeyShort.CONTROL);
        leaf.WaitFor(Meeting).Click();
        Keyboard.Release(VirtualKeyShort.CONTROL);

        leaf.Press(VirtualKeyShort.KEY_V);

        var notice = leaf.WaitFor("NoticeBar");
        Assert.True(Retry.WhileFalse(() => notice.FindAllDescendants().Prepend(notice).Any(e => e.Properties.Name.ValueOrDefault == "Select one event"), TimeSpan.FromSeconds(5)).Success);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }
}
