using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class DetailsActionsTests : IDisposable
{
    const string Meeting = "Event_evt-meeting_202610011800";

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
    public void Join_OpensMeetWithTheAccountsAuthUser()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.WaitFor("DetailsJoinButton").AsButton().Invoke();

        Assert.True(Launched("https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com"));
    }

    [Fact]
    public void UpcomingJoin_OpensMeet()
    {
        using var leaf = Launch();

        leaf.WaitFor("UpcomingJoin_evt-meeting").AsButton().Invoke();

        Assert.True(Launched("https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com"));
    }

    [Fact]
    public void DescriptionLinks_HttpsOpens_JavascriptIsPlainText()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var agenda = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("agenda").And(cf.ByControlType(ControlType.Hyperlink))), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(agenda);
        agenda.Patterns.Invoke.Pattern.Invoke();

        Assert.True(Launched("https://example.com/agenda"));
        Assert.Null(leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("run").And(cf.ByControlType(ControlType.Hyperlink))));
    }

    [Fact]
    public void Rsvp_MaybeWithNote_SendsAttendeesPatch()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();
        Assert.False(leaf.Exists("DetailsDeleteButton"));

        leaf.WaitFor("DetailsRsvpNote").AsTextBox().Text = "Running late";
        leaf.WaitFor("DetailsRsvpMaybe").Click();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal));
        Assert.Contains("\"tentative\"", write.Body, StringComparison.Ordinal);
        Assert.Contains("Running late", write.Body, StringComparison.Ordinal);
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsResponse").Name == "Your response: Maybe", TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Guests_ListResponsesAndEmailThem()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        Assert.NotNull(leaf.WaitForName("Maybe · Optional · “Might be late”"));
        leaf.WaitFor("DetailsEmailGuests").AsButton().Invoke();

        Assert.True(Launched("mailto:boss@example.com?to=sam%40example.com&subject=Design%20review"));
    }

    [Fact]
    public void FocusedDescriptionLink_WithTooltip_StillTakesShortcuts()
    {
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var agenda = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("agenda").And(cf.ByControlType(ControlType.Hyperlink))), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(agenda);
        // Tab to the link from the reply box above it (UIA focus doesn't reach a text element), then let its tooltip open
        leaf.WaitFor("DetailsRsvpEmail").Focus();
        for (var i = 0; i < 10 && !agenda.Properties.HasKeyboardFocus.ValueOrDefault; i++)
        {
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Thread.Sleep(100);
        }

        Assert.True(agenda.Properties.HasKeyboardFocus.ValueOrDefault);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        FlaUI.Core.Input.Keyboard.Type("m");

        Assert.NotNull(leaf.WaitFor("MonthGrid"));
        Assert.False(leaf.App.HasExited);
    }
}
