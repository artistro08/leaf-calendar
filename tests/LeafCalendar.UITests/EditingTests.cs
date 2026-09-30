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

    [Fact]
    public void Edit_TitleCtrlEnter_SavesAndPatches()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Text = "Dentist (rescheduled)";
        title.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Dentist (rescheduled)", TimeSpan.FromSeconds(10)).Success);
        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Contains("Dentist (rescheduled)", write.Body, StringComparison.Ordinal);
        Assert.Contains("sendUpdates=all", write.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithC_SaveSendsInsert()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Coffee with Sam";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Coffee with Sam", TimeSpan.FromSeconds(10)).Success);
        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Path.EndsWith("/events", StringComparison.Ordinal));
        Assert.Contains("Coffee with Sam", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void EditRepeatingInstance_ThisEvent_PatchesOnlyThatInstance()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.WaitFor("NextButton").AsButton().Invoke();
        leaf.WaitFor("Event_evt-weekly_202610051330").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Standup in room 2";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ScopeThis").Click();
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-weekly_20261005T133000Z", StringComparison.Ordinal));
        Assert.DoesNotContain(_google.Writes, w => w.Path.EndsWith("/events/evt-weekly", StringComparison.Ordinal));
    }

    [Fact]
    public void AddGuest_SaveWithoutEmailing_SendsAttendeesQuietly()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        leaf.WaitFor("EditorGuestInput").AsTextBox().Text = "sam@example.com";
        leaf.WaitFor("EditorAddGuest").AsButton().Invoke();
        leaf.WaitFor("EditorGuestOptional_sam@example.com");
        leaf.WaitFor("EditorSaveQuietButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Contains("sam@example.com", write.Body, StringComparison.Ordinal);
        Assert.Contains("sendUpdates=none", write.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_Escape_DiscardsChanges()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Text = "Never saved";
        title.Focus();

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Assert.Empty(_google.Writes);
    }

    // Regression guard: may already pass before the editor exists (no Edit button at all)
    [Fact]
    public void InviteYouCantEdit_HasNoEditButton()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();

        leaf.WaitFor("DetailsTitle");
        Assert.False(leaf.Exists("DetailsEditButton"));
    }
}
