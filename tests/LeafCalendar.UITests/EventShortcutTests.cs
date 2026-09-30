using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class EventShortcutTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";
    const string Meeting = "Event_evt-meeting_202610011800";
    const string MeetJoin = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    bool Launched(string link) =>
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
    public void EAlone_OpensTheEditorAfterTheTimeout()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.KEY_E);

        Assert.NotNull(leaf.WaitFor("EditorTitle"));
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
}
