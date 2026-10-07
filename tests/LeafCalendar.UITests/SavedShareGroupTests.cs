using System.Drawing;
using System.Globalization;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
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

    // Picks 10 AM-12 PM on Oct 1, titles it and copies it, which saves it as a group and stops sharing
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

    // Saves the group, opens it with a click, types the guest and approves its time
    internal static void SaveAndApprove(LeafApp leaf)
    {
        Save(leaf);

        OpenFirst(leaf);
        leaf.WaitFor("ShareGuestBox").AsTextBox().Text = Guest;
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

    // Copy is off while Google answers, and Cancel during that wait throws the picks away: nothing is copied or saved
    [Fact]
    public void CancelWhileCopying_CopiesAndSavesNothing()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        _google.FreeBusyDelay = TimeSpan.FromSeconds(4);
        Clipboard.Clear();

        var copy = leaf.WaitFor("ShareCopyButton").AsButton();
        copy.Invoke();
        Assert.True(Retry.WhileTrue(() => copy.IsEnabled, TimeSpan.FromSeconds(2)).Success, "Copy stayed on while copying.");
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(2)).Success, "Cancel left the panel up.");

        Assert.True(Retry.WhileTrue(() => _google.FreeBusyQueries.IsEmpty, TimeSpan.FromSeconds(5)).Success, "Copy sent no free/busy query.");
        Thread.Sleep(TimeSpan.FromSeconds(6));
        Assert.False(leaf.Exists(FirstSaved), "The canceled share was saved.");
        Assert.Null(Clipboard.Text());
    }

    // A click on a saved time opens its group in the panel with its title
    [Fact]
    public void Click_OpensTheGroup()
    {
        using var leaf = Launch();
        Save(leaf);

        OpenFirst(leaf);

        Assert.Equal(Title, leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
        Assert.NotNull(leaf.WaitFor("ShareSlot_0"));
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

    // Delete in an open group deletes it and stops sharing
    [Fact]
    public void Delete_RemovesTheGroup()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);

        leaf.WaitFor("ShareDeleteButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists(FirstSaved) || leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Delete left the group.");
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

    // Esc with focus in an open group's Title box, after typing a new title, keeps that title
    [Fact]
    public void Escape_InAnOpenGroupsTitle_KeepsTheNewTitle()
    {
        using var leaf = Launch();
        Save(leaf);
        OpenFirst(leaf);
        leaf.WaitFor("ShareTitleBox").Focus();
        Thread.Sleep(300);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Lunch");

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Esc left the share panel up.");
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor(FirstSaved).Name.StartsWith("Lunch, ", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success, "The saved time doesn't show the new title.");
        OpenFirst(leaf);
        Assert.Equal("Lunch", leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
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
