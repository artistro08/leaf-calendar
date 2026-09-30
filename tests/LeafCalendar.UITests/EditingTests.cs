using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class EditingTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Delete_SelectedEvent_RemovesItAndSendsDeleteWithEtag()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();

        leaf.Press(VirtualKeyShort.DELETE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-single_202610011300"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("UndoButton"));
        var write = _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Equal("\"3181161784712000\"", write.IfMatch);
        Assert.Contains("sendUpdates=all", write.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Undo_RestoresEventAndSendsNothing()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);

        leaf.WaitFor("UndoButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
        Thread.Sleep(TimeSpan.FromSeconds(9));
        Assert.DoesNotContain(_google.Writes, w => w.Method == "DELETE");
    }

    [Fact]
    public void Delete_RepeatingInstance_ThisEvent_CancelsOnlyThatDay()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.WaitFor("NextButton").AsButton().Invoke();
        leaf.WaitFor("Event_evt-weekly_202610051330").Click();

        leaf.Press(VirtualKeyShort.DELETE);
        leaf.WaitForAnywhere("ScopeThis").Click();
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-weekly_202610051330"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610091330"));
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-weekly_20261005T133000Z", StringComparison.Ordinal));
    }
}
