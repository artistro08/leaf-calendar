using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
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

    // Picks 10 AM-12 PM on Oct 1, titles it and copies it (saving it as a group), then opens the group with a click,
    // types the guest and approves its time
    private static void SaveAndApprove(LeafApp leaf)
    {
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 12);
        leaf.WaitFor("ShareSlot_0");
        leaf.WaitFor("ShareTitleBox").AsTextBox().Text = Title;
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Copy didn't stop sharing.");

        leaf.WaitFor(FirstSaved).Click();
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
        Assert.True(Retry.WhileTrue(() => leaf.Exists(FirstSaved), TimeSpan.FromSeconds(10)).Success, "The group stayed after the event was saved.");
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
