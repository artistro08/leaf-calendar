using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class OfflineConflictTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Dentist = "Event_evt-single_202610011300";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    static void Rename(LeafApp leaf, string title)
    {
        leaf.WaitFor(Dentist).Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle").AsTextBox().Text = title;
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();
    }

    // Offline with Google changed behind Leaf's back, then a local rename, then back online: Leaf's patch gets 412
    string MakeConflict(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        _google.Offline = true;
        _google.EditOnGoogle(Primary, "evt-single", e => e["summary"] = "Changed on Google");
        Rename(leaf, "Mine");
        _google.Offline = false;
        Assert.True(Retry.WhileFalse(() => leaf.Exists("ConflictsButton"), TimeSpan.FromSeconds(45)).Success);
        return (string)_google.EventOnGoogle(Primary, "evt-single")!["etag"]!;
    }

    [Fact]
    public void OfflineEdit_ShowsWaiting_ThenSendsWhenBackOnline()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        _google.Offline = true;

        Rename(leaf, "Offline rename");

        Assert.True(Retry.WhileFalse(() => leaf.Exists("PendingChangesIndicator") && leaf.WaitFor("PendingChangesIndicator").Name == "1 change waiting to sync", TimeSpan.FromSeconds(10)).Success);
        Assert.True(Retry.WhileFalse(() => leaf.Exists("OfflineIndicator"), TimeSpan.FromSeconds(20)).Success);

        // One slot: offline with one waiting, said in words by the waiting button's help text and the offline button's name
        Assert.StartsWith("Can't reach Google. 1 change waiting to sync.", leaf.WaitFor("OfflineIndicator").Name, StringComparison.Ordinal);
        Assert.StartsWith("Can't reach Google.", leaf.WaitFor("PendingChangesIndicator").Properties.HelpText.ValueOrDefault, StringComparison.Ordinal);
        Assert.Equal("Offline rename", leaf.WaitFor("DetailsTitle").Name);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(_google.Writes, w => w.Method == "PATCH");

        _google.Offline = false;

        _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal), seconds: 45);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("PendingChangesIndicator"), TimeSpan.FromSeconds(20)).Success);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("OfflineIndicator"), TimeSpan.FromSeconds(20)).Success);
        Assert.Equal("Offline rename", (string?)_google.EventOnGoogle(Primary, "evt-single")!["summary"]);
    }

    [Fact]
    public void Conflict_KeepMine_ResendsOnGooglesVersion()
    {
        using var leaf = Launch();
        var googleEtag = MakeConflict(leaf);

        // Conflicts take the slot's first place
        Assert.Equal("1 change needs your review", leaf.WaitFor("ConflictsButton").Name);
        Assert.False(leaf.Exists("OfflineIndicator"));

        leaf.WaitFor("ConflictsButton").AsButton().Invoke();
        Assert.Equal("Mine", leaf.WaitForAnywhere("ConflictMine_Title").Name);
        var theirs = leaf.WaitForAnywhere("ConflictGoogle_Title");
        Assert.Equal("Changed on Google", theirs.Name);
        Assert.Equal("differs", theirs.Properties.ItemStatus.ValueOrDefault);
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        _google.WaitForWrite(w => w.Method == "PATCH" && w.IfMatch == googleEtag, seconds: 45);
        Assert.True(Retry.WhileFalse(() => (string?)_google.EventOnGoogle(Primary, "evt-single")!["summary"] == "Mine", TimeSpan.FromSeconds(10)).Success);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ConflictsButton"), TimeSpan.FromSeconds(20)).Success);
    }

    [Fact]
    public void Conflict_KeepGoogles_ShowsGooglesVersion()
    {
        using var leaf = Launch();
        MakeConflict(leaf);

        leaf.WaitFor("ConflictsButton").AsButton().Invoke();
        leaf.WaitForAnywhere("SecondaryButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Changed on Google", TimeSpan.FromSeconds(15)).Success);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ConflictsButton"), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Conflict_DecideLater_KeepsTheBadge()
    {
        using var leaf = Launch();
        MakeConflict(leaf);

        leaf.WaitFor("ConflictsButton").AsButton().Invoke();
        leaf.WaitForAnywhere("CloseButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.ExistsAnywhere("ConflictMine_Title"), TimeSpan.FromSeconds(5)).Success);
        Assert.True(leaf.Exists("ConflictsButton"));
        Assert.Equal("Mine", leaf.WaitFor("DetailsTitle").Name);
    }

    // Accounts live in Settings; the delete waits out its undo window offline, so it hasn't reached Google
    [Fact]
    public void Disconnect_WithUnsentChange_WarnsFirst()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();
        _google.Offline = true;
        leaf.Press(VirtualKeyShort.DELETE);

        leaf.OpenSettings("Accounts");
        leaf.PressDisconnectInSettings();

        Assert.True(Retry.WhileFalse(() => leaf.AnyTextContains("1 change you made here hasn't reached Google yet, and it will be lost."), TimeSpan.FromSeconds(10)).Success);
        leaf.WaitForAnywhere("CloseButton").AsButton().Invoke();
    }

    // Deletes wait out their undo window before they count as waiting, so the slot stays empty right after one
    [Fact]
    public void Delete_Online_DoesNotShowWaiting()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.DELETE);

        Assert.False(Retry.WhileFalse(() => leaf.Exists("PendingChangesIndicator"), TimeSpan.FromSeconds(4)).Success);
        _google.WaitForWrite(w => w.Method == "DELETE" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
    }
}
