using System.Drawing;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class EditingTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // After Next the week slides in; an event found mid-slide would be clicked where it no longer is
    private static void ClickWhenSettled(AutomationElement element)
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

    // Whether any element on screen carries this text in its name (some elements have no name at all)
    private static bool ShowsText(LeafApp leaf, string text) =>
        Retry.WhileFalse(() => leaf.MainWindow.FindAllDescendants().Any(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(text, StringComparison.Ordinal)), TimeSpan.FromSeconds(10)).Success;

    // A second event of your own on Oct 1 at 16:00 UTC (the seeded "Design review" is someone else's invite, which you
    // can neither edit nor delete)
    private void AddLunch() => _google.AddEvent(SeededProfile.Email, new JsonObject
    {
        ["id"] = "evt-lunch",
        ["summary"] = "Team lunch",
        ["start"] = new JsonObject { ["dateTime"] = "2026-10-01T16:00:00Z" },
        ["end"] = new JsonObject { ["dateTime"] = "2026-10-01T17:00:00Z" },
    });

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

        // Google keeps the restored day as its own instance (the canceled exception, confirmed again), so it shows under that ID
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_20261005T133000Z_202610051330"));
    }

    [Fact]
    public void CtrlZ_Twice_UndoesTwoDeletesNewestFirst()
    {
        AddLunch();
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        leaf.WaitFor("Event_evt-lunch_202610011600").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-lunch", StringComparison.Ordinal));

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        var first = _google.WaitForWrite(w => w.Method == "POST");
        Assert.Contains("\"Team lunch\"", first.Body, StringComparison.Ordinal);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        var second = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Dentist appointment\"", StringComparison.Ordinal));
        Assert.NotNull(second);
        Assert.Equal(2, _google.Writes.Count(w => w.Method == "POST"));
    }

    [Fact]
    public void CtrlZ_WhileTypingInTheEditor_UndoesTextNotADelete()
    {
        AddLunch();
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.DELETE);
        _google.WaitForWrite(w => w.Method == "DELETE");
        leaf.WaitFor("Event_evt-lunch_202610011600").Click();
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

        // In This Theme, Then The Other (Ctrl+Shift+L)
        var first = Lightness(box);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_L);
        Thread.Sleep(1000);
        var other = Lightness(leaf.WaitFor("NoticeBar").BoundingRectangle);

        Assert.True(Math.Abs(first.Average() - other.Average()) > 0.3f, "The theme didn't change.");
        foreach (var lightness in new[] { first, other })
        {
            Assert.True(lightness.Max() - lightness.Min() < 0.03f, $"The grid shows through the notice ({lightness.Min():0.00} to {lightness.Max():0.00}).");
        }
    }

    // A strip left of the notice's icon, clear of its rounded corners (their edge stroke reads darker in light theme):
    // hour lines behind a see-through bar would vary its lightness
    private static List<float> Lightness(Rectangle box)
    {
        var strip = new Rectangle(box.X + 6, box.Y + box.Height / 4, 8, box.Height / 2);
        using var shot = FlaUI.Core.Capturing.Capture.Rectangle(strip);
        return [.. Enumerable.Range(0, shot.Bitmap.Width).SelectMany(x => Enumerable.Range(0, shot.Bitmap.Height).Select(y => shot.Bitmap.GetPixel(x, y).GetBrightness()))];
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

    // Ruling: A Description Leaf Had To Cut Short Is Read-Only And Never Sent (sending it would delete the rest on Google)
    [Fact]
    public void Edit_DescriptionTooLong_IsReadOnlyAndNotSent()
    {
        _google.AddEvent(SeededProfile.Email, new JsonObject
        {
            ["id"] = "evt-lunch",
            ["summary"] = "Team lunch",
            ["description"] = new string('x', 20_000),
            ["start"] = new JsonObject { ["dateTime"] = "2026-10-01T16:00:00Z" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-10-01T17:00:00Z" },
        });
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-lunch_202610011600").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        Assert.Equal("This description is too long to edit in Leaf.", leaf.WaitFor("EditorDescriptionTooLong").Name);

        // Typing Changes Nothing (the description is a RichEditBox, which has no Value pattern to ask; its text is checked instead)
        var description = leaf.WaitFor("EditorDescription");
        var before = description.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        description.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type("yz");
        Thread.Sleep(300);
        Assert.Equal(before, description.Patterns.Text.Pattern.DocumentRange.GetText(-1));

        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Long lunch";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-lunch", StringComparison.Ordinal));
        Assert.Contains("Long lunch", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("description", write.Body, StringComparison.Ordinal);
    }

    // A Read-Only Description With A Link Survives A List Shortcut And A Click Inside The Link (both used to crash)
    [Fact]
    public void Edit_DescriptionTooLongWithLink_ShortcutAndClickChangeNothing()
    {
        _google.AddEvent(SeededProfile.Email, new JsonObject
        {
            ["id"] = "evt-lunch",
            ["summary"] = "Team lunch",
            ["description"] = "<a href=\"https://example.com/menu\">menu</a> " + new string('x', 20_000),
            ["start"] = new JsonObject { ["dateTime"] = "2026-10-01T16:00:00Z" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-10-01T17:00:00Z" },
        });
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-lunch_202610011600").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        var description = leaf.WaitFor("EditorDescription");
        var before = description.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        description.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_L);
        var box = description.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(box.Left + 12, box.Top + 12));
        Thread.Sleep(300);

        Assert.Equal(before, leaf.WaitFor("EditorDescription").Patterns.Text.Pattern.DocumentRange.GetText(-1));
    }

    [Fact]
    public void AddGuest_SaveWithoutEmailing_SendsAttendeesQuietly()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        ConferencingAndContactsTests.GuestEdit(leaf).Text = "sam@example.com";
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
        var edit = leaf.WaitFor("DetailsEditButton");
        var box = edit.BoundingRectangle;
        var scale = box.Width / 32.0;
        var top = leaf.WaitFor("CalendarRoot").BoundingRectangle.Top;
        var text = leaf.WaitFor("DetailsTitle").BoundingRectangle.Left;
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

    [Fact]
    public void InviteYouCantEdit_ShowsEditDisabled()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        leaf.WaitFor("DetailsTitle");

        // Edit stays in its place, disabled; a changeable event enables it again
        Assert.False(leaf.WaitFor("DetailsEditButton").IsEnabled);
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsEditButton").IsEnabled, TimeSpan.FromSeconds(5)).Success);
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
    public void ClosingTheDetailsPanel_WhileEditing_KeepsTheEdit()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Text += " moved";

        var toggle = leaf.WaitFor("DetailsToggleButton").AsToggleButton();
        toggle.Toggle();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        toggle.Toggle();

        Assert.True(Retry.WhileFalse(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.EndsWith(" moved", leaf.WaitFor("EditorTitle").AsTextBox().Text, StringComparison.Ordinal);
        Assert.Empty(_google.Writes);
    }

    [Fact]
    public void ClosingTheDetailsPanel_WhileEditing_CBringsTheEditBack()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Text += " moved";

        leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        leaf.Press(VirtualKeyShort.KEY_C);

        Assert.True(Retry.WhileFalse(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.Equal("Dentist appointment moved", leaf.WaitFor("EditorTitle").AsTextBox().Text);
    }

    [Fact]
    public void SelectingAnotherEvent_WhileEditing_ShowsItsDetails()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Never saved";

        leaf.WaitFor("Event_evt-meeting_202610011800").Click();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.Equal("Design review", leaf.WaitFor("DetailsTitle").Name);
        Assert.Empty(_google.Writes);
    }

    [Fact]
    public void SelectingAnotherEvent_RightAfterE_StaysOnItsDetails()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.WaitFor("EventEditor");

        // Within the 1.5 s "E then ..." window, then past it: the timer mustn't open an editor again
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        Thread.Sleep(TimeSpan.FromSeconds(2));

        Assert.False(leaf.Exists("EventEditor"));
        Assert.Equal("Design review", leaf.WaitFor("DetailsTitle").Name);
    }

    [Fact]
    public void ClickingEmptyTime_WhileEditing_EndsTheEdit()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor("Event_evt-single_202610011300");
        dentist.Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.WaitFor("EditorTitle");

        // Same Day Column, Well Below The Event
        var box = dentist.BoundingRectangle;
        Mouse.Click(new Point(box.X + box.Width / 2, box.Bottom + 150));

        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor") || leaf.Exists("DetailsTitle"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
    }

    [Fact]
    public void ClickingEmptyTime_WhileCreating_EndsTheEdit()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor("Event_evt-single_202610011300");
        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Never saved";

        // Same Day Column, Well Below The Event (read after the editor opened the panel and the grid moved)
        var box = dentist.BoundingRectangle;
        Mouse.Click(new Point(box.X + box.Width / 2, box.Bottom + 150));

        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
        Assert.Empty(_google.Writes);
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
