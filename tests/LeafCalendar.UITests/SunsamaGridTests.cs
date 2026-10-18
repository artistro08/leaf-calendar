using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// Sunsama's task times on the calendar against the fake Sunsama under <c>--fake-google</c>: projected and timeboxed
/// blocks with the channel and tracked time, a task matched to a Google event drawing no second block, blocks that
/// can't be dragged or changed, the sidebar's two switches, and the details panel (spec 2026-10-16, section 8.2).
/// </summary>
public sealed class SunsamaGridTests : IDisposable
{
    internal const string ProjectedId = "6ac7a44534029300011d076e";
    internal const string TimeboxId = "aaaaaaaaaaaaaaaaaaaaaaa1";
    internal const string MatchedId = "aaaaaaaaaaaaaaaaaaaaaaa2";
    internal const string Dentist = "Event_evt-single_202610011300";

    // Oct 1 in Chicago (CDT, UTC-5): the projected task 9:30-10:10 AM and 12:30-1:50 PM, the timebox 2-3 PM
    internal static readonly DateTimeOffset ProjectedStart = new(2026, 10, 1, 14, 30, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset TimeboxStart = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SignedInProfile(new LeafSettings());

    public SunsamaGridTests() => Seed(_google);

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    /// <summary>
    /// Seeds Oct 1: "Home Page Redesign" (Illuminate Digital, 2 hours planned, 1 h 24 m tracked over two projected
    /// blocks), "Write copy" timeboxed on Sunsama's calendar, and "Dentist appointment" imported from the seeded Google event.
    /// </summary>
    internal static void Seed(FakeGoogleServer google) => google.SeedSunsamaDay(
        new DateOnly(2026, 10, 1),
        $$$$"""
        {"tasks":[
          {"_id":"{{{{ProjectedId}}}}","title":"Home Page Redesign","channel":"Illuminate Digital","timeEstimate":"2 hours",
           "completed":false,"timeboxEventIds":[],"actualTimeSpent":{"total":"1 hours and 24 minutes"},
           "projectedTimeEntries":[{"startDate":"2026-10-01 9:30 AM","endDate":"2026-10-01 10:10 AM"},
                                   {"startDate":"2026-10-01 12:30 PM","endDate":"2026-10-01 1:50 PM"}]},
          {"_id":"{{{{TimeboxId}}}}","title":"Write copy","channel":"Work","timeEstimate":"1 hour","timeboxEventIds":["ev-box"]},
          {"_id":"{{{{MatchedId}}}}","title":"Dentist appointment","channel":"Personal","timeboxEventIds":[],
           "actualTimeSpent":{"total":"30 minutes"},
           "importedFrom":{"service":"googleCalendar","identifier":{"sunsamaId":"ev-dentist"}}}]}
        """,
        """
        {"events":[
          {"eventId":"ev-box","title":"Write copy","calendarId":"sunsama-cal",
           "start":{"dateTime":"2026-10-01T14:00:00-05:00"},"end":{"dateTime":"2026-10-01T15:00:00-05:00"}},
          {"eventId":"ev-dentist","title":"Dentist appointment","calendarId":"leaf.tester@gmail.com",
           "start":{"dateTime":"2026-10-01T08:00:00-05:00"},"end":{"dateTime":"2026-10-01T09:00:00-05:00"}}]}
        """);

    /// <summary>A profile with these settings, signed in to the fake Google and to the fake Sunsama.</summary>
    internal static string SignedInProfile(LeafSettings settings)
    {
        var profile = SeededProfile.Create(settings);
        var store = new ProtectedFileTokenStore(LeafApp.ProfileFolder(profile));
        store.SetProviderSecret("sunsama", "client_id", FakeGoogleServer.SunsamaClientId);
        store.SetProviderSecret("sunsama", "refresh_token", FakeGoogleServer.SunsamaRefreshToken);
        store.SetProviderSecret("sunsama", "email", "uitest@example.com");
        return profile;
    }

    /// <summary>The automation ID of a Sunsama block (its occurrence's event ID is the task ID and the start's ticks).</summary>
    internal static string BlockId(string taskId, DateTimeOffset start) =>
        string.Create(CultureInfo.InvariantCulture, $"Event_{taskId}@{start.UtcTicks}_{start.UtcDateTime:yyyyMMddHHmm}");

    // Test mode's "now" is 8:00 on the start date, so the grid opens on the morning
    internal static LeafApp Launch(string profile, FakeGoogleServer google) =>
        LeafApp.Launch(profile, $"--fake-google {google.BaseUri} --start-date 2026-10-01");

    // Sunsama is fetched after the first Google sync pass, so its blocks can take a pass or two to arrive
    internal static AutomationElement WaitForBlock(LeafApp leaf, string id) =>
        Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(45)).Result
        ?? throw new InvalidOperationException($"The Sunsama block '{id}' didn't appear.");

    private static string NameOf(LeafApp leaf, string id) => leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id))?.Name ?? "";

    // =========================================================================
    // GRID
    // =========================================================================

    // A projected block names its title, the channel after the time, and its tracked time; the timebox draws too
    [Fact]
    public void ProjectedBlock_ShowsWithChannelAndTracked()
    {
        using var leaf = Launch(_profile, _google);

        var name = WaitForBlock(leaf, BlockId(ProjectedId, ProjectedStart)).Name;

        Assert.Contains("Home Page Redesign", name, StringComparison.Ordinal);
        Assert.Contains("Illuminate Digital", name, StringComparison.Ordinal);
        Assert.Contains("1 h 24 m tracked", name, StringComparison.Ordinal);
        Assert.Contains("Write copy", WaitForBlock(leaf, BlockId(TimeboxId, TimeboxStart)).Name, StringComparison.Ordinal);
    }

    // The task imported from the dentist draws no block of its own; the dentist says how long was tracked
    [Fact]
    public void MatchedTask_DrawsNoSecondBlock()
    {
        using var leaf = Launch(_profile, _google);
        WaitForBlock(leaf, BlockId(ProjectedId, ProjectedStart));

        Assert.True(Retry.WhileFalse(() => NameOf(leaf, Dentist).Contains("30 m tracked", StringComparison.Ordinal), TimeSpan.FromSeconds(15)).Success, $"The dentist reads \"{NameOf(leaf, Dentist)}\".");
        Assert.False(leaf.Exists(BlockId(MatchedId, new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero))), "The matched task drew a block of its own.");
    }

    // Dragging a block an hour down leaves it where it was, opens nothing and writes nothing to Google
    [Fact]
    public void Drag_SunsamaBlock_DoesNothing()
    {
        using var leaf = Launch(_profile, _google);
        var id = BlockId(ProjectedId, ProjectedStart);
        var block = WaitForBlock(leaf, id);
        var before = block.BoundingRectangle;

        LeafApp.DragBy(block, 0, 120);
        Thread.Sleep(2000);

        Assert.Equal(before.Top, WaitForBlock(leaf, id).BoundingRectangle.Top);
        Assert.False(leaf.Exists("EditorTitle"), "A drag opened the editor.");
        Assert.Empty(_google.Writes);
    }

    // The right-click menu has only Open in Sunsama, and it opens the task's sunsama:// link
    [Fact]
    public void RightClick_SunsamaBlock_OnlyOpenInSunsama()
    {
        using var leaf = Launch(_profile, _google);
        WaitForBlock(leaf, BlockId(ProjectedId, ProjectedStart)).RightClick();

        var open = leaf.WaitForAnywhere("MenuOpenInSunsama");
        Assert.False(leaf.ExistsAnywhere("MenuCopy"), "The Sunsama menu offered Copy.");
        Assert.False(leaf.ExistsAnywhere("MenuDelete"), "The Sunsama menu offered Delete.");
        open.AsMenuItem().Invoke();

        Assert.True(
            Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains($"sunsama://action/details/{ProjectedId}"), TimeSpan.FromSeconds(10)).Success,
            $"Launched: [{string.Join(" | ", LeafApp.LaunchedLinks(_profile))}]");
    }

    // =========================================================================
    // SIDEBAR
    // =========================================================================

    // Projected tasks off hides the blocks and on shows them again; Tracked time off takes "tracked" off the block
    [Fact]
    public void Sidebar_TogglesHideBlocksAndTracked()
    {
        using var leaf = Launch(_profile, _google);
        var id = BlockId(ProjectedId, ProjectedStart);
        WaitForBlock(leaf, id);
        Assert.NotNull(leaf.WaitFor("AccountHeader_sunsama"));

        leaf.WaitFor("CalendarToggle_projected").AsCheckBox().Toggle();
        Assert.True(Retry.WhileTrue(() => leaf.Exists(id), TimeSpan.FromSeconds(10)).Success, "Projected tasks off left the block.");

        leaf.WaitFor("CalendarToggle_projected").AsCheckBox().Toggle();
        WaitForBlock(leaf, id);

        leaf.WaitFor("CalendarToggle_tracked").AsCheckBox().Toggle();
        Assert.True(Retry.WhileFalse(() => NameOf(leaf, id) is { Length: > 0 } n && !n.Contains("tracked", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).Success, $"Tracked time off left \"{NameOf(leaf, id)}\".");
        Assert.Equal(ToggleState.Off, leaf.WaitFor("CalendarToggle_tracked").AsCheckBox().ToggleState);
    }

    // =========================================================================
    // DETAILS PANEL
    // =========================================================================

    // A block's details: its title, channel, planned and tracked time, and Open in Sunsama
    [Fact]
    public void Details_SunsamaBlock_ShowsTheTask()
    {
        using var leaf = Launch(_profile, _google);
        WaitForBlock(leaf, BlockId(ProjectedId, ProjectedStart)).Click();

        Assert.Equal("Home Page Redesign", leaf.WaitFor("DetailsSunsamaTitle").Name);
        Assert.Equal("Illuminate Digital", leaf.WaitFor("DetailsSunsamaChannel").Name);
        Assert.Equal("2 h planned", leaf.WaitFor("DetailsSunsamaPlanned").Name);
        Assert.Equal("1 h 24 m tracked", leaf.WaitFor("DetailsSunsamaTracked").Name);
        Assert.NotNull(leaf.WaitFor("DetailsOpenInSunsama"));
        Assert.False(leaf.Exists("DetailsTitle"), "The event details showed for a Sunsama block.");
    }

    // The dentist, matched to a task, shows the task's tracked total
    [Fact]
    public void Details_MatchedGoogleEvent_ShowsTracked()
    {
        using var leaf = Launch(_profile, _google);
        WaitForBlock(leaf, BlockId(ProjectedId, ProjectedStart));
        Assert.True(Retry.WhileFalse(() => NameOf(leaf, Dentist).Contains("tracked", StringComparison.Ordinal), TimeSpan.FromSeconds(15)).Success);

        leaf.WaitFor(Dentist).Click();

        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);
        Assert.Equal("30 m tracked", leaf.WaitFor("DetailsEventTracked").Name);
        Assert.NotNull(leaf.WaitFor("DetailsTrackedOpenInSunsama"));
    }
}
