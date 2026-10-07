using System.Drawing;
using System.Globalization;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SavedShareGroupTests : IDisposable
{
    private const string Guest = "dana@example.com";
    private const string Title = "Interview";

    // The first group saved in a fresh profile
    private const string FirstSaved = "SavedSlot_1_0";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create(new LeafSettings { PrimaryTimeZone = "America/New_York" });

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // Picks 10 AM-12 PM on Oct 1, titles it and copies it (Copy & Save), which saves it as a group and stops sharing
    internal static void Save(LeafApp leaf)
    {
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        leaf.WaitFor("ShareTitleBox").AsTextBox().Text = Title;
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Copy didn't stop sharing.");
        Assert.NotNull(leaf.WaitFor(FirstSaved));
    }

    // Clicks the first saved time and waits for its group in the share panel
    internal static void OpenFirst(LeafApp leaf)
    {
        leaf.WaitFor(FirstSaved).Click();
        Assert.True(Retry.WhileFalse(() => leaf.Exists("ShareSlotsPanel") && leaf.WaitFor("SharePanelTitle").Name == "Saved times", TimeSpan.FromSeconds(5)).Success, "The click didn't open the group.");
    }

    // The message box's text with its line breaks dropped (the box reports them as \r)
    private static string Flat(string text) => text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    // The text box inside the guest AutoSuggestBox
    private static TextBox GuestEdit(LeafApp leaf) =>
        Retry.WhileNull(() => leaf.WaitFor("ShareGuestBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), TimeSpan.FromSeconds(10)).Result?.AsTextBox()
        ?? throw new InvalidOperationException("The guest box has no text box inside.");

    // Types a new title into the open panel's Title box, like a person does
    private static void TypeTitle(LeafApp leaf, string title)
    {
        leaf.WaitFor("ShareTitleBox").Focus();
        Thread.Sleep(300);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(title);
    }

    // The saved time's name starts with the title (it reads "{title}, {time range}")
    private static bool SavedTitleIs(LeafApp leaf, string title) =>
        Retry.WhileFalse(() => leaf.Exists(FirstSaved) && leaf.WaitFor(FirstSaved).Name.StartsWith($"{title}, ", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success;

    // Types an address in the guest box and presses Enter, which adds it to the list under the box as guest index
    private static void AddGuest(LeafApp leaf, string email, int index)
    {
        var edit = GuestEdit(leaf);
        edit.Focus();
        Thread.Sleep(300);
        Keyboard.Type(email);
        Keyboard.Type(VirtualKeyShort.ENTER);
        Assert.True(Retry.WhileFalse(() => GuestIs(leaf, index, email), TimeSpan.FromSeconds(5)).Success, $"{email} wasn't added as guest {index}.");
        Assert.True(Retry.WhileFalse(() => GuestEdit(leaf).Text.Length == 0, TimeSpan.FromSeconds(5)).Success, "Adding the guest didn't empty the box.");
    }

    // The guest row at index shows text (its name, else its address)
    private static bool GuestIs(LeafApp leaf, int index, string text) =>
        leaf.Exists($"ShareGuest_{index}") && leaf.WaitFor($"ShareGuest_{index}").Name == text;

    // Saves the group, opens it with a click, types the guest (not added: a typed address counts) and approves its time
    internal static void SaveAndApprove(LeafApp leaf)
    {
        Save(leaf);

        OpenFirst(leaf);
        GuestEdit(leaf).Text = Guest;
        var approve = leaf.WaitFor("SharePanelApprove_0").AsButton();
        Assert.True(Retry.WhileFalse(() => approve.IsEnabled, TimeSpan.FromSeconds(5)).Success, "Approve… stayed disabled with a valid address.");
        approve.Invoke();

        Assert.NotNull(leaf.WaitFor("EventEditor"));
    }

    // Approve opens the editor with the group's title and the guest while the group's times stay drawn; Save books the
    // event with the guest and the group goes
    [Fact]
    public void Approve_OpensTheEditorFilledIn_AndSaveRemovesTheGroup()
    {
        using var leaf = Launch();
        SaveAndApprove(leaf);

        Assert.Equal(Title, leaf.WaitFor("EditorTitle").AsTextBox().Text);
        Assert.NotNull(leaf.WaitFor($"EditorGuest_{Guest}"));
        Assert.False(leaf.Exists("ShareSlotsPanel"));
        Assert.True(leaf.Exists(FirstSaved), "The group's time went before the event was saved.");

        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains(Guest, StringComparison.Ordinal));
        Assert.Contains(Title, write.Body, StringComparison.Ordinal);
        var body = JsonNode.Parse(write.Body)!;
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), DateTimeOffset.Parse((string)body["start"]!["dateTime"]!, CultureInfo.InvariantCulture));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero), DateTimeOffset.Parse((string)body["end"]!["dateTime"]!, CultureInfo.InvariantCulture));
        Assert.True(Retry.WhileTrue(() => leaf.Exists(FirstSaved), TimeSpan.FromSeconds(10)).Success, "The group stayed after the event was saved.");
    }

    // Copy saves the group with its title, and it's still on the calendar the next time Leaf starts
    [Fact]
    public void Copy_SavesTheGroup_AndItIsThereAfterARestart()
    {
        using (var leaf = Launch())
        {
            Save(leaf);
            Assert.StartsWith($"{Title}, ", leaf.WaitFor(FirstSaved).Name, StringComparison.Ordinal);
        }

        using var again = Launch();
        Assert.StartsWith($"{Title}, ", again.WaitFor(FirstSaved).Name, StringComparison.Ordinal);
    }

    // Copy & Save asks nothing of Google: no free/busy query is sent
    [Fact]
    public void CopyAndSave_SendsNoFreeBusyQuery()
    {
        using var leaf = Launch();
        Save(leaf);

        Thread.Sleep(1000);
        Assert.Empty(_google.FreeBusyQueries);
    }

    // New picks with no times can't be copied and saved, and Cancel on new picks saves nothing
    [Fact]
    public void NewPicks_CopyAndSaveNeedsATime_AndCancelSavesNothing()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        Assert.False(leaf.WaitFor("ShareCopyButton").AsButton().IsEnabled, "Copy & Save was on with no times.");

        ShareAvailabilityTests.DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Cancel left the panel up.");
        Thread.Sleep(1000);
        Assert.False(leaf.Exists(FirstSaved), "Cancel saved the picks.");
    }

    // A click on a saved time opens its group in the panel with its title, and Save (the accent), Copy & Save and Close
    [Fact]
    public void Click_OpensTheGroup()
    {
        using var leaf = Launch();
        Save(leaf);

        OpenFirst(leaf);

        Assert.Equal(Title, leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
        Assert.NotNull(leaf.WaitFor("ShareSlot_0"));
        var save = leaf.WaitFor("ShareSaveButton").BoundingRectangle;
        var copy = leaf.WaitFor("ShareCopyButton");
        var close = leaf.WaitFor("ShareCancelButton");
        Assert.Equal(("Save", "Copy & Save", "Close"), (leaf.WaitFor("ShareSaveButton").Name, copy.Name, close.Name));
        Assert.True(save.Right < copy.BoundingRectangle.Left && copy.BoundingRectangle.Right < close.BoundingRectangle.Left, "Save, Copy & Save and Close aren't in that order.");
    }

    // A click just above a saved time (in its edge strip, but outside the time) is on empty time and opens nothing
    [Fact]
    public void ClickJustOutside_DoesNotOpenTheGroup()
    {
        using var leaf = Launch();
        Save(leaf);
        var slot = leaf.WaitFor(FirstSaved).BoundingRectangle;

        Mouse.Click(new Point(slot.Left + slot.Width / 2, slot.Top - 3));

        Thread.Sleep(1000);
        Assert.False(leaf.Exists("ShareSlotsPanel"), "A click outside the saved time opened its group.");
    }

    // Two guests added (a repeat in another case isn't added again), saved, and both shown when the group reopens; one
    // removed and saved again leaves the other
    [Fact]
    public void TwoGuests_SavedWithTheGroup_ShowWhenReopened_AndOneCanBeRemoved()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        AddGuest(leaf, "pat@example.com", 0);
        AddGuest(leaf, "sam@example.com", 1);
        GuestEdit(leaf).Focus();
        Keyboard.Type("PAT@example.com");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Assert.True(Retry.WhileFalse(() => GuestEdit(leaf).Text.Length == 0, TimeSpan.FromSeconds(5)).Success, "Enter on a repeat didn't empty the box.");
        Assert.False(leaf.Exists("ShareGuest_2"), "A repeat address was added again.");

        leaf.WaitFor("ShareSaveButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Save didn't close the panel.");

        // Reopened, Both Guests Are There, In Order
        OpenFirst(leaf);
        Assert.True(Retry.WhileFalse(() => GuestIs(leaf, 0, "pat@example.com") && GuestIs(leaf, 1, "sam@example.com"), TimeSpan.FromSeconds(5)).Success, "The saved guests didn't show.");

        // One Removed, Then Saved
        var remove = leaf.WaitFor("ShareGuestRemove_0");
        Assert.Equal("Remove guest", remove.Name);
        remove.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => GuestIs(leaf, 0, "sam@example.com") && !leaf.Exists("ShareGuest_1"), TimeSpan.FromSeconds(5)).Success, "Remove didn't take the guest off the list.");
        leaf.WaitFor("ShareSaveButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Save didn't close the panel.");

        OpenFirst(leaf);
        Assert.True(Retry.WhileFalse(() => GuestIs(leaf, 0, "sam@example.com"), TimeSpan.FromSeconds(5)).Success, "The guest left didn't show.");
        Assert.False(leaf.Exists("ShareGuest_1"), "The removed guest came back.");
    }

    // New picks have the guest box too: a guest added there is saved by Copy & Save
    [Fact]
    public void NewPicks_GuestIsSavedByCopyAndSave()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        AddGuest(leaf, "pat@example.com", 0);

        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Copy & Save didn't close the panel.");

        OpenFirst(leaf);
        Assert.True(Retry.WhileFalse(() => GuestIs(leaf, 0, "pat@example.com"), TimeSpan.FromSeconds(5)).Success, "The guest wasn't saved with the group.");
    }

    // Approve opens the editor with every guest: two added and one typed but not added; the event Google gets has all three
    [Fact]
    public void Approve_AddsEveryGuestToTheEvent()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        AddGuest(leaf, "pat@example.com", 0);
        AddGuest(leaf, "sam@example.com", 1);
        GuestEdit(leaf).Text = Guest;

        leaf.WaitFor("SharePanelApprove_0").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("EventEditor"));
        Assert.NotNull(leaf.WaitFor("EditorGuest_pat@example.com"));
        Assert.NotNull(leaf.WaitFor("EditorGuest_sam@example.com"));
        Assert.NotNull(leaf.WaitFor($"EditorGuest_{Guest}"));
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains(Guest, StringComparison.Ordinal));
        var attendees = JsonNode.Parse(write.Body)!["attendees"]!.AsArray().Select(a => (string)a!["email"]!).ToList();
        Assert.Contains("pat@example.com", attendees);
        Assert.Contains("sam@example.com", attendees);
        Assert.Contains(Guest, attendees);
    }

    // Copy & Save over an event copies the dragged time whole and saves it whole: the dentist (9-10 AM) cuts nothing
    [Fact]
    public void CopyAndSave_OverAnEvent_SavesTheDraggedTimeWhole()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 9, 11);
        leaf.WaitFor("ShareSlot_0");
        Clipboard.Clear();

        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();

        var text = Retry.WhileNull(Clipboard.Text, TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(text);
        Assert.EndsWith("Thu Oct 1: 9–11 AM ET", text, StringComparison.Ordinal);
        Assert.True(Retry.WhileFalse(() => leaf.Exists(FirstSaved) && leaf.WaitFor(FirstSaved).Name.EndsWith("9 AM – 11 AM", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success, "The saved time isn't 9-11 AM.");
    }

    // Delete saved times shows in the title bar row only while a saved group is open (not for new picks, never with the
    // event's Edit and Delete); it deletes the group and stops sharing, and goes with the panel
    [Fact]
    public void Delete_InTheTitleBar_RemovesTheGroup()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        Assert.False(leaf.Exists("DeleteSavedTimesButton"), "Delete saved times showed for new picks.");
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Close left the panel up.");

        Save(leaf);
        OpenFirst(leaf);

        var delete = leaf.WaitFor("DeleteSavedTimesButton");
        Assert.Equal("Delete saved times", delete.Name);
        Assert.False(leaf.Exists("DetailsEditButton"), "The event's Edit showed with Delete saved times.");
        Assert.False(leaf.Exists("DeleteEventButton"), "The event's Delete showed with Delete saved times.");
        delete.AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists(FirstSaved) || leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Delete left the group.");
        Assert.True(Retry.WhileTrue(() => leaf.Exists("DeleteSavedTimesButton"), TimeSpan.FromSeconds(5)).Success, "Delete saved times stayed after the panel closed.");
    }

    // Edits to an open group wait for Save: a new title and a removed time, then Close, leave the group as it was
    [Fact]
    public void EditOpenGroup_ThenClose_LeavesItUnchanged()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        TypeTitle(leaf, "Lunch");
        leaf.WaitFor("SharePanelRemove_0").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("SharePanelSlot_0"), TimeSpan.FromSeconds(5)).Success, "The time didn't go from the panel.");
        Assert.False(leaf.WaitFor("ShareSaveButton").AsButton().IsEnabled, "Save was on with no times.");

        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Close left the panel up.");
        Assert.True(SavedTitleIs(leaf, Title), "Close changed the saved group.");
        OpenFirst(leaf);
        Assert.Equal(Title, leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
    }

    // Edits to an open group, then Save: the group takes the new title
    [Fact]
    public void EditOpenGroup_ThenSave_ChangesIt()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        TypeTitle(leaf, "Lunch");

        leaf.WaitFor("ShareSaveButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Save left the panel up.");
        Assert.True(SavedTitleIs(leaf, "Lunch"), "The saved time doesn't show the new title.");
        OpenFirst(leaf);
        Assert.Equal("Lunch", leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
    }

    // The guest box suggests Google contacts as you type; picking one adds them to the list (their name, then their
    // address) and empties the box, and Approve… turns on
    [Fact]
    public void GuestBox_SuggestsAContact_AndPickingAddsThem()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        var edit = GuestEdit(leaf);
        edit.Focus();
        Keyboard.Type("ali");

        var alice = Retry.WhileNull(
            () => leaf.FindAllAnywhere("SuggestionsList").SelectMany(list => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
                .FirstOrDefault(item => item.Properties.Name.ValueOrDefault == "Alice Example <alice@example.com>"),
            TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(alice);
        alice.Click();

        Assert.True(Retry.WhileFalse(() => GuestIs(leaf, 0, "Alice Example"), TimeSpan.FromSeconds(5)).Success, "Picking Alice didn't add her to the guests.");
        Assert.True(Retry.WhileFalse(() => GuestEdit(leaf).Text.Length == 0, TimeSpan.FromSeconds(5)).Success, $"The guest box reads \"{GuestEdit(leaf).Text}\".");
        var approve = leaf.WaitFor("SharePanelApprove_0").AsButton();
        Assert.True(Retry.WhileFalse(() => approve.IsEnabled, TimeSpan.FromSeconds(5)).Success, "Approve… stayed off after picking a contact.");
    }

    // A message typed in the panel goes with that share and its saved group; the next new share starts from the default
    [Fact]
    public void PanelMessageEdit_IsForThatShareOnly()
    {
        const string Message = "Just this once {times}";
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 11);
        leaf.WaitFor("ShareSlot_0");
        leaf.WaitFor("ShareMessageBox").AsTextBox().Text = Message;
        Clipboard.Clear();
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        var text = Retry.WhileNull(Clipboard.Text, TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(text);
        Assert.StartsWith("Just this once ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{times}", text, StringComparison.Ordinal);

        OpenFirst(leaf);
        Assert.Equal(Message, leaf.WaitFor("ShareMessageBox").AsTextBox().Text);
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Close left the panel up.");

        ShareAvailabilityTests.StartSharing(leaf);
        Assert.Equal(Flat(AvailabilityText.DefaultMessage), Flat(leaf.WaitFor("ShareMessageBox").AsTextBox().Text));
    }

    // Esc with focus in an open group's Title box, after typing a new title, closes like Close: the title isn't saved
    [Fact]
    public void Escape_InAnOpenGroupsTitle_ThrowsAwayTheUnsavedTitle()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        TypeTitle(leaf, "Lunch");

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Esc left the share panel up.");
        Thread.Sleep(1000);
        Assert.True(SavedTitleIs(leaf, Title), "Esc saved the new title.");
        OpenFirst(leaf);
        Assert.Equal(Title, leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
    }

    // While the approve editor is open, the group's time still resizes by its bottom edge
    [Fact]
    public void EdgeDrag_WhileApproving_ResizesTheSavedTime()
    {
        using var leaf = Launch();
        SaveAndApprove(leaf);
        var slot = leaf.WaitFor(FirstSaved).BoundingRectangle;
        var hour = slot.Height / 2;
        var x = slot.Left + slot.Width / 2;

        // Just below the bottom edge: inside the edge strip, clear of the new event's card
        LeafApp.Drag(new Point(x, slot.Bottom + 2), new Point(x, slot.Bottom + 2 + hour));

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor(FirstSaved).BoundingRectangle.Height > hour * 2.5, TimeSpan.FromSeconds(5)).Success, "The saved time didn't grow.");
        Assert.True(leaf.Exists("EventEditor"), "The drag closed the editor.");
    }

    // Cancel in the editor books nothing and keeps the group
    [Fact]
    public void Approve_ThenCancel_KeepsTheGroup()
    {
        using var leaf = Launch();
        SaveAndApprove(leaf);

        leaf.WaitFor("EditorCancelButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("EventEditor"), TimeSpan.FromSeconds(5)).Success, "Cancel left the editor open.");
        Thread.Sleep(1000);
        Assert.True(leaf.Exists(FirstSaved), "Cancel deleted the group.");
        Assert.DoesNotContain(_google.Writes, w => w.Method == "POST" && w.Body.Contains(Guest, StringComparison.Ordinal));
    }
}
