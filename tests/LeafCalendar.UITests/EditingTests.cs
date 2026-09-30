using System.Drawing;
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

    // After Next the week slides in; an event found mid-slide would be clicked where it no longer is
    static void ClickWhenSettled(AutomationElement element)
    {
        LeafApp.WaitUntilStill(element);
        element.Click();
    }

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

    // Whether any element on screen carries this text in its name
    static bool ShowsText(LeafApp leaf, string text) =>
        Retry.WhileFalse(() => leaf.MainWindow.FindAllDescendants().Any(e => e.Name.Contains(text, StringComparison.Ordinal)), TimeSpan.FromSeconds(10)).Success;

    [Fact]
    public void Delete_AfterTheNoticeIsGone_CtrlZ_RecreatesItQuietly()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE");
        Assert.True(Retry.WhileTrue(() => leaf.Exists("NoticeBar"), TimeSpan.FromSeconds(10)).Success, "The notice never hid itself.");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);

        var create = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Contains("sendUpdates=none", create.Query, StringComparison.Ordinal);
        Assert.Contains("\"Dentist appointment\"", create.Body, StringComparison.Ordinal);
        Assert.True(ShowsText(leaf, "Dentist appointment"));
    }

    [Fact]
    public void Delete_RepeatingInstanceSent_CtrlZ_RestoresThatDay()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.WaitFor("NextButton").AsButton().Invoke();
        ClickWhenSettled(leaf.WaitFor("Event_evt-weekly_202610051330"));
        leaf.Press(VirtualKeyShort.DELETE);
        leaf.WaitForAnywhere("ScopeThis").Click();
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-weekly_20261005T133000Z", StringComparison.Ordinal));

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);

        var restore = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-weekly_20261005T133000Z", StringComparison.Ordinal));
        Assert.Contains("sendUpdates=none", restore.Query, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"confirmed\"", restore.Body.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));
    }

    [Fact]
    public void CtrlZ_Twice_UndoesTwoDeletesNewestFirst()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal));

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        var first = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Contains("\"Design review\"", first.Body, StringComparison.Ordinal);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        var second = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Dentist appointment\"", StringComparison.Ordinal));
        Assert.NotNull(second);
        Assert.Equal(2, _google.Writes.Count(w => w.Method == "POST"));
    }

    [Fact]
    public void CtrlZ_WhileTypingInTheEditor_UndoesTextNotADelete()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE");
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Focus();
        Keyboard.Type("abc");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);

        Assert.False(Retry.WhileNull(() => _google.Writes.FirstOrDefault(w => w.Method == "POST"), TimeSpan.FromSeconds(3)).Success, "Ctrl+Z in the editor must not bring back a delete.");
    }

    [Fact]
    public void NoticeBar_IsSolidOverTheGrid()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        var box = leaf.WaitFor("NoticeBar").BoundingRectangle;

        // A strip left of the icon: hour lines behind a see-through bar would vary its lightness
        var strip = new Rectangle(box.X + 3, box.Y + 3, 8, box.Height - 6);
        using var shot = FlaUI.Core.Capturing.Capture.Rectangle(strip);
        var lightness = Enumerable.Range(0, shot.Bitmap.Width).SelectMany(x => Enumerable.Range(0, shot.Bitmap.Height).Select(y => shot.Bitmap.GetPixel(x, y).GetBrightness())).ToList();
        Assert.True(lightness.Max() - lightness.Min() < 0.03f, $"The grid shows through the notice ({lightness.Min():0.00} to {lightness.Max():0.00}).");
    }

    [Fact]
    public void Delete_RepeatingInstance_ThisEvent_CancelsOnlyThatDay()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.WaitFor("NextButton").AsButton().Invoke();
        ClickWhenSettled(leaf.WaitFor("Event_evt-weekly_202610051330"));

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
        ClickWhenSettled(leaf.WaitFor("Event_evt-weekly_202610051330"));
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

    [Fact]
    public void EditIcon_InDetailsTitleBar_ClicksThroughAndHidesWhileEditing()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();

        // In the title bar row, its glyph (8 in on a 32-wide button) over the details content's left edge
        var edit  = leaf.WaitFor("DetailsEditButton");
        var box   = edit.BoundingRectangle;
        var scale = box.Width / 32.0;
        var top   = leaf.WaitFor("CalendarRoot").BoundingRectangle.Top;
        var text  = leaf.WaitFor("DetailsTitle").BoundingRectangle.Left;
        Assert.True(box.Bottom <= top + 48 * scale + 1, $"The edit button ends at {box.Bottom}, below the title bar row ({top + 48 * scale}).");
        Assert.True(Math.Abs(box.Left + 8 * scale - text) <= 1.5, $"The edit glyph starts at {box.Left + 8 * scale}, the details text at {text}.");
        Assert.Equal("Edit event", edit.Name);

        // Delete touches it on the right, in the same row
        var delete = leaf.WaitFor("DeleteEventButton").BoundingRectangle;
        Assert.True(Math.Abs(delete.Left - box.Right) <= 1 && delete.Top == box.Top, $"Delete is at {delete}, the edit button at {box}.");

        // A real click (not an automation invoke) reaches it through the title bar
        ClickWhenSettled(edit);

        Assert.NotNull(leaf.WaitFor("EditorTitle"));
        Assert.True(Retry.WhileTrue(() => leaf.Exists("DetailsEditButton") || leaf.Exists("DeleteEventButton"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void DeleteIcon_InDetailsTitleBar_DeletesWithUndo()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();

        // A real click through the title bar
        ClickWhenSettled(leaf.WaitFor("DeleteEventButton"));

        Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-single_202610011300"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("UndoButton"));
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

    [Fact]
    public void InviteYouCantChange_ShowsDeleteDisabled()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        leaf.WaitFor("DetailsTitle");

        // Delete stays in its place, disabled, and the Delete key does nothing
        var delete = leaf.WaitFor("DeleteEventButton");
        Assert.False(delete.IsEnabled);
        leaf.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DELETE);
        Assert.False(Retry.WhileTrue(() => leaf.Exists("Event_evt-meeting_202610011800"), TimeSpan.FromSeconds(2)).Success);

        // Picking a changeable event enables it again
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DeleteEventButton").IsEnabled, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void EditTwice_CalendarPickerKeepsTheEventsCalendar()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("EditorCalendar").AsComboBox().SelectedItem);
        leaf.WaitFor("EditorCancelButton").AsButton().Invoke();

        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("EditorCalendar").AsComboBox().SelectedItem);
    }
}
