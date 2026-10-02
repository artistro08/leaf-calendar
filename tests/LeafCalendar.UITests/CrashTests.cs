using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// Regression tests for the crashes the owner reported in polish round 3: switching between events quickly, and
/// resizing Settings on General. Each one drives the app hard and then checks that it's still running.
/// </summary>
public sealed class CrashTests : IDisposable
{
    const string FamilyId = "family123@group.calendar.google.com";

    // Week Of 2026-10-01: a plain event, a long description full of links, a guest meeting, a call with a place, a family event, and an all-day event
    static readonly string[] Events =
    [
        "Event_evt-single_202610011300",
        "Event_evt-rich_202610011400",
        "Event_evt-meeting_202610011800",
        "Event_evt-crash-call_202610021400",
        "Event_evt-family-play_202610022200",
        "AllDay_evt-crash-allday_20261001",
    ];

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    // A Long Description Full Of Links, A Call With A Place, And An All-Day Event
    public CrashTests()
    {
        var links = string.Concat(Enumerable.Range(0, 120).Select(i => $"<p><b>Line {i}</b> <a href=\"https://example.com/doc/{i}\">Doc {i}</a> https://example.org/{i}</p>"));
        var rich  = new JsonObject
        {
            ["id"]          = "evt-rich",
            ["status"]      = "confirmed",
            ["summary"]     = "Planning",
            ["description"] = links,
            ["start"]       = new JsonObject { ["dateTime"] = "2026-10-01T10:00:00-04:00" },
            ["end"]         = new JsonObject { ["dateTime"] = "2026-10-01T11:00:00-04:00" },
        };
        _google.AddEvent(SeededProfile.Email, rich);
        _google.AddEvent(SeededProfile.Email, JsonNode.Parse("""
            {
              "id": "evt-crash-call", "status": "confirmed", "summary": "Call", "location": "1 Main St, Springfield",
              "hangoutLink": "https://meet.google.com/abc-defg-hij",
              "start": { "dateTime": "2026-10-02T10:00:00-04:00" }, "end": { "dateTime": "2026-10-02T11:00:00-04:00" }
            }
            """)!.AsObject());
        _google.AddEvent(SeededProfile.Email, JsonNode.Parse("""
            {
              "id": "evt-crash-allday", "status": "confirmed", "summary": "Offsite",
              "start": { "date": "2026-10-01" }, "end": { "date": "2026-10-02" }
            }
            """)!.AsObject());
    }

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --gc-stress");
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        return leaf;
    }

    [Fact]
    public void DetailedLogging_TurnedOn_WritesBreadcrumbs()
    {
        using var leaf = Launch();
        leaf.OpenSettings("About");
        var toggle = leaf.WaitInSettings("DetailedLoggingSwitch").AsToggleButton();
        Assert.Equal(FlaUI.Core.Definitions.ToggleState.Off, toggle.ToggleState);
        toggle.Toggle();

        // A Command And A Selection Leave Their Trail (names only)
        leaf.MainWindow.Focus();
        leaf.WaitFor(Events[0]).Click();
        leaf.Press(VirtualKeyShort.KEY_T);

        var log = Path.Combine(LeafApp.ProfileFolder(_profile), "Logs", "leaf.log");
        Assert.True(
            FlaUI.Core.Tools.Retry.WhileFalse(() => ReadShared(log).Contains("TRACE command Today", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).Success,
            "No command breadcrumb in the log.");
        Assert.Contains("TRACE vm.changed SelectedInfo", ReadShared(log), StringComparison.Ordinal);
        Assert.DoesNotContain("Dentist", ReadShared(log), StringComparison.Ordinal);
    }

    // The log while Leaf may be writing it
    static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void SwitchingEventsQuickly_WithAndWithoutTheEditor_DoesNotCrash()
    {
        using var leaf = Launch();
        var cards = Events.Select(leaf.WaitFor).ToList();
        LeafApp.WaitUntilStill(cards[0]);

        // Fast Clicks Between Events
        for (var round = 0; round < 6; round++)
        {
            foreach (var card in cards)
            {
                card.Click();
            }
        }

        // Editor Open, Then Another Event, Again And Again
        for (var round = 0; round < 4; round++)
        {
            foreach (var card in cards.Take(4))
            {
                card.Click();
                leaf.Press(VirtualKeyShort.KEY_E);
                Wait.UntilInputIsProcessed();
            }
        }

        leaf.Press(VirtualKeyShort.ESCAPE);
        Assert.False(leaf.App.HasExited, "Leaf ended while switching between events.");
        Assert.NotNull(leaf.WaitFor(Events[0]));
    }

    // Polish round 4: WinUI destroyed a released time picker after .NET had collected it (the share panel's rows come and go)
    [Fact]
    public void SharePanelTimePickers_AddedAndRemovedOverAndOver_DoNotCrash()
    {
        using var leaf = Launch();

        // Pick A Time, Remove It, Pick Two, Cancel (every row's pickers are released each round)
        for (var round = 0; round < 5; round++)
        {
            ShareAvailabilityTests.StartSharing(leaf);
            ShareAvailabilityTests.DragHours(leaf, 10, 12);
            leaf.WaitFor("SharePanelRemove_0").AsButton().Invoke();
            Assert.True(FlaUI.Core.Tools.Retry.WhileTrue(() => leaf.Exists("SharePanelStart_0"), TimeSpan.FromSeconds(5)).Success, "The removed time's row stayed.");

            ShareAvailabilityTests.DragHours(leaf, 10, 11);
            ShareAvailabilityTests.DragHours(leaf, 14, 16);
            leaf.WaitFor("SharePanelStart_1");
            leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
            Assert.True(FlaUI.Core.Tools.Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Cancel left the share panel up.");
        }

        // Editor Open And Closed (its pickers too)
        for (var round = 0; round < 5; round++)
        {
            leaf.WaitFor(Events[0]).Click();
            leaf.Press(VirtualKeyShort.KEY_E);
            leaf.WaitFor("EditorEndTime");
            leaf.Press(VirtualKeyShort.ESCAPE);
            Wait.UntilInputIsProcessed();
        }

        Thread.Sleep(2000);
        Assert.False(leaf.App.HasExited, "Leaf ended after time pickers were released.");
        Assert.NotNull(leaf.WaitFor(Events[0]));
    }

    [Fact]
    public void ResizingSettingsOnGeneral_AcrossEveryWidth_DoesNotCrash()
    {
        using var leaf = Launch();
        var settings  = leaf.OpenSettings("General");
        var transform = settings.Patterns.Transform.Pattern;
        leaf.WaitInSettings("ThemeComboBox");
        transform.Move(0, 0);

        // Narrowest To Widest And Back, Across The Navigation Pane's Breakpoints
        for (var width = 400; width <= 2400; width += 37)
        {
            transform.Resize(width, 600);
        }

        for (var width = 2400; width >= 400; width -= 23)
        {
            transform.Resize(width, 400 + (width % 300));
        }

        // Dragging The Right Edge (the window's own sizing loop), Narrowest And Widest, Back And Forth
        transform.Resize(1000, 700);
        Wait.UntilInputIsProcessed();
        var bounds = settings.BoundingRectangle;
        var edge   = new System.Drawing.Point(bounds.Right - 3, bounds.Top + bounds.Height / 2);
        Mouse.MoveTo(edge);
        Mouse.Down(MouseButton.Left);
        for (var pass = 0; pass < 3; pass++)
        {
            for (var x = edge.X; x >= bounds.Left + 300; x -= 15)
            {
                Mouse.MoveTo(x, edge.Y);
            }

            for (var x = bounds.Left + 300; x <= bounds.Left + 1800; x += 15)
            {
                Mouse.MoveTo(x, edge.Y);
            }
        }

        Mouse.Up(MouseButton.Left);

        // Maximized And Back
        var window = settings.Patterns.Window.PatternOrDefault;
        window?.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Wait.UntilInputIsProcessed();
        window?.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
        Wait.UntilInputIsProcessed();

        Assert.False(leaf.App.HasExited, "Leaf ended while Settings was resized.");
        Assert.NotNull(leaf.WaitInSettings("ThemeComboBox"));
    }
}
