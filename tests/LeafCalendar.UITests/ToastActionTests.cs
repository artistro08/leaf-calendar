using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Alerts;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ToastActionTests : IDisposable
{
    private const string Primary = "leaf.tester@gmail.com";
    private const string Dentist = "Event_evt-single_202610011300";
    private const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";
    private static readonly DateTimeOffset MeetingStart = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // 8 AM in New York on Oct 1, so the fixtures' Design review (2 PM) is still ahead whenever the tests run
    private LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    // A notification click as UI tests make one: a second launch hands the click's arguments to the running Leaf
    private void Click(ToastArgs args)
    {
        using var second = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --toast-action {args.Encode()}");
        Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);
    }

    private ToastArgs Meeting(ToastAction action, string? profile = null) =>
        new(action, profile ?? _profile, SeededProfile.AccountId, Primary, "evt-meeting", MeetingStart);

    // Offline with Google changed behind Leaf's back, then a local rename, then back online: Leaf's patch gets 412
    private void MakeConflict(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        _google.Offline = true;
        _google.EditOnGoogle(Primary, "evt-single", e => e["summary"] = "Changed on Google");
        leaf.WaitFor(Dentist).Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Mine";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();
        _google.Offline = false;
        Assert.True(Retry.WhileFalse(() => leaf.Exists("ConflictsButton"), TimeSpan.FromSeconds(45)).Success);
    }

    [Fact]
    public void Join_OpensMeetWithItsAccount()
    {
        using var leaf = Launch();

        Click(Meeting(ToastAction.Join));

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Accept_SendsYesToGoogle()
    {
        using var leaf = Launch();

        Click(Meeting(ToastAction.Accept));

        _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal) && w.Body.Contains("\"accepted\"", StringComparison.Ordinal), seconds: 30);
    }

    [Fact]
    public void Open_WhileInTheTray_ShowsTheEvent()
    {
        using var leaf = Launch();
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        Click(Meeting(ToastAction.Open));

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Design review", TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public void AnotherProfilesNotification_IsIgnored()
    {
        using var leaf = Launch();

        Click(Meeting(ToastAction.Join, profile: "uitest-someone-else"));
        Thread.Sleep(TimeSpan.FromSeconds(3));

        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }

    [Fact]
    public void Conflict_ShowsTheReviewNotification_AndItsClickOpensTheDialog()
    {
        using var leaf = Launch();
        MakeConflict(leaf);

        LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tconflicts\t", StringComparison.Ordinal) && l.Contains("1 change needs your review", StringComparison.Ordinal), seconds: 30);

        Click(new ToastArgs(ToastAction.ReviewConflicts, _profile));
        Assert.Equal("Mine", leaf.WaitForAnywhere("ConflictMine_Title").Name);
    }

    [Fact]
    public void SignInStopsWorking_ShowsSignInAgain()
    {
        _google.RejectRefresh = true;
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tsignin\t", StringComparison.Ordinal), seconds: 30);

        Assert.Contains("Sign in again", line, StringComparison.Ordinal);
        Assert.Contains("leaf.tester@gmail.com", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OrganizerChangesAnInvite_ShowsTheUpdateWithYesNoMaybe()
    {
        using var leaf = Launch();

        // The first sync recorded the pending invite quietly; the organizer now moves it to another room
        _google.EditOnGoogle(Primary, "evt-meeting", e =>
        {
            e["sequence"] = 1;
            e["location"] = "Room 5";
        });

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tinvites\t", StringComparison.Ordinal), seconds: 45);
        Assert.Contains("Updated invitation from boss@example.com", line, StringComparison.Ordinal);
        Assert.Contains("content=\"Yes\"", line, StringComparison.Ordinal);
        Assert.Contains("content=\"Maybe\"", line, StringComparison.Ordinal);
    }
}
