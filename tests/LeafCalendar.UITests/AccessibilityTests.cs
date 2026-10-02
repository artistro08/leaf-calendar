using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class AccessibilityTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";
    const string Family  = "family123@group.calendar.google.com";
    const string TrayNow = "--now 2026-10-01T13:50:00-04:00";

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    /// <summary>Every screen the audit opens (Milestones 3 to 6). Settings pages are one row each. The screenshot tour walks the same list.</summary>
    public static readonly string[] ScreenNames =
    [
        "Main", "Details", "Editor", "SettingsGeneral", "SettingsCalendars", "SettingsTimeZones", "SettingsAccounts", "SettingsAbout",

        // Milestone 4
        "TrayFlyout", "TrayMenu", "SettingsNotifications", "SettingsTray", "SettingsShortcuts",

        // Milestone 5
        "CommandMenu", "CheatSheet", "SharePanel", "PeoplePicker", "OverlayBar", "TimeTravelBar", "RenameDialog", "RemindersDialog", "RoomInput",

        // Milestone 6
        "BoxSelect", "RichEditor", "PastCards",
    ];

    /// <summary>The screens as theory rows.</summary>
    public static TheoryData<string> Screens() => [.. ScreenNames];

    /// <summary>
    /// Each screen: extra launch arguments, how to open it, and the AutomationIds Tab must reach there. An empty
    /// MustReach skips the Tab check (menus move with the arrow keys, not Tab).
    /// </summary>
    static (string Extra, Action<LeafApp> Open, string[] MustReach) Screen(string name, FakeGoogleServer google) => name switch
    {
        // Milestone 3
        "Main"              => ("", leaf => leaf.WaitFor(Dentist), ["TodayButton", "PreviousButton", "NextButton", "ViewModeButton", "SettingsButton"]),
        "Details"           => ("", leaf => leaf.WaitFor(Dentist).Click(), ["DetailsEditButton", "DeleteEventButton"]),
        "Editor"            => ("", OpenEditor, ["EditorTitle", "EditorTimeZoneBox", "DescriptionBold", "DescriptionNumbers", "EditorDescription", "EditorSaveButton"]),
        "SettingsGeneral"   => ("", leaf => OpenSettings(leaf, "General"), ["ThemeComboBox", "HourHeightSlider", "WeekendsSwitch", "WorkingHoursSwitch", "StartupSwitch"]),
        "SettingsCalendars" => ("", leaf => OpenSettings(leaf, "Calendars"), [$"CalendarMore_{Family}"]),
        "SettingsTimeZones" => ("", leaf => OpenSettings(leaf, "TimeZones"), ["TimeZoneSearch", "PrimaryZoneExpander"]),
        "SettingsAccounts"  => ("", leaf => OpenSettings(leaf, "Accounts"), ["AddAccountButton", "DefaultCalendarComboBox", "SyncNowButton", "ChangeClientButton"]),
        "SettingsAbout"     => ("", leaf => OpenSettings(leaf, "About"), ["GitHubLink", "OpenLogsButton"]),

        // Milestone 4 (the tray icon is driven with the shell's own messages, as TrayTests does)
        "TrayFlyout"            => (TrayNow, OpenFlyout, ["FlyoutJoinButton", "FlyoutNewEvent"]),
        "TrayMenu"              => (TrayNow, OpenTrayMenu, []),
        "SettingsNotifications" => ("", leaf => OpenSettings(leaf, "Notifications"), ["RemindersSwitch", "JoinNowSwitch", "InvitesSwitch", "SoundSwitch"]),
        "SettingsTray"          => ("", leaf => OpenSettings(leaf, "Tray"), ["FlyoutDaysNumberBox", "FlyoutAllDaySwitch", "LookaheadComboBox"]),
        "SettingsShortcuts"     => ("", leaf => OpenSettings(leaf, "Shortcuts"), ["JoinShortcutButton", "FlyoutShortcutButton", "ShowCheatSheetButton"]),

        // Milestone 5
        "CommandMenu"     => ("", leaf => { leaf.WaitFor(Dentist); leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K); leaf.WaitForAnywhere("CommandSearchBox"); }, []),
        "CheatSheet"      => ("", leaf => { leaf.WaitFor(Dentist); leaf.Press(VirtualKeyShort.SHIFT, VirtualKeyShort.OEM_2); leaf.WaitForAnywhere("ShortcutSheet"); }, ["ShortcutFilterBox"]),
        "SharePanel"      => ("", leaf => { leaf.WaitFor(Dentist); leaf.Press(VirtualKeyShort.KEY_S); leaf.WaitFor("ShareSlotsPanel"); }, ["ShareZoneBox", "ShareCancelButton"]),
        "PeoplePicker"    => ("", leaf => { leaf.WaitFor(Dentist); leaf.Press(VirtualKeyShort.KEY_P); leaf.WaitForAnywhere("PeoplePickerBox"); }, []),
        "OverlayBar"      => ("", OpenOverlay, ["OverlayClear"]),
        "TimeTravelBar"   => ("", OpenTimeTravel, ["TimeTravelReturn"]),
        "RenameDialog"    => ("", leaf => SidebarMenu(leaf, "CalendarMenu_Rename", "RenameCalendarBox"), []),
        "RemindersDialog" => ("", OpenReminders, []),
        "RoomInput"       => ("", leaf => OpenRoomInput(leaf, google), ["EditorGuestInput", "EditorRoomInput"]),

        // Milestone 6
        "BoxSelect"  => ("", OpenBoxSelect, ["SelectionDeleteButton"]),
        "PastCards"  => ("--start-date 2026-10-02", leaf => leaf.WaitFor(Dentist), ["TodayButton", "NextButton"]),
        "RichEditor" => ("", OpenRichEditor, ["DescriptionBold", "DescriptionItalic", "DescriptionUnderline", "DescriptionBullets", "DescriptionNumbers", "EditorDescription"]),

        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // Settings Opens From The Sidebar Button, On One Page
    static void OpenSettings(LeafApp leaf, string page)
    {
        leaf.WaitFor(Dentist);
        leaf.OpenSettings(page);
        Thread.Sleep(500);
    }

    // The Edit Button (not E: a key typed right after E goes to the title)
    static void OpenEditor(LeafApp leaf)
    {
        leaf.WaitFor(Dentist).Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle");
    }

    static void OpenFlyout(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutJoinButton");
    }

    static void OpenTrayMenu(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuQuit");
    }

    // P, One Person, Show: The Overlay Bar Lists Them
    static void OpenOverlay(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.Press(VirtualKeyShort.KEY_P);
        var edit = Retry.WhileNull(() => leaf.WaitForAnywhere("PeoplePickerBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), Wait).Result
            ?? throw new InvalidOperationException("The people picker has no text box inside.");
        edit.Focus();
        Keyboard.Type("dana@example.com");
        Keyboard.Press(VirtualKeyShort.RETURN);
        leaf.WaitForAnywhere("PickedPerson_dana@example.com");
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
        leaf.WaitFor("OverlayBar");
    }

    // Z, Tokyo, Go: The Time Travel Bar Shows
    static void OpenTimeTravel(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.Press(VirtualKeyShort.KEY_Z);
        leaf.WaitForAnywhere("TimeTravelBox").Focus();
        Keyboard.Type("Tokyo");
        Thread.Sleep(500);
        Keyboard.Type(VirtualKeyShort.DOWN);
        Thread.Sleep(300);
        var go = Retry.WhileNull(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("Go")), Wait).Result
            ?? throw new InvalidOperationException("Go didn't show.");
        Retry.WhileFalse(() => go.IsEnabled, Wait);
        go.AsButton().Invoke();
        leaf.WaitFor("TimeTravelReturn");
    }

    // Focus The Family Calendar, Open Its Menu From The Keyboard (Shift+F10), And Pick An Item; The Dialog Opens. The
    // keyboard path is the one under test here, and another window on top can't swallow it the way it can a right-click.
    static void SidebarMenu(LeafApp leaf, string item, string inDialog)
    {
        leaf.WaitFor(Dentist);
        leaf.WaitFor($"CalendarToggle_{Family}").Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
        leaf.WaitForAnywhere(item).AsMenuItem().Invoke();
        leaf.WaitForAnywhere(inDialog);
    }

    // Settings › Calendars, The Family Calendar's "…" Menu, Default Reminders
    static void OpenReminders(LeafApp leaf)
    {
        OpenSettings(leaf, "Calendars");
        leaf.WaitInSettings($"CalendarMore_{Family}").AsButton().Invoke();
        leaf.WaitForAnywhere("CalendarMenu_Reminders").AsMenuItem().Invoke();
        leaf.WaitForAnywhere("PrimaryButton");
    }

    // A Workspace Account Shows The Room Box (Open sets the domain before launch; it arrives with the account's user info)
    static void OpenRoomInput(LeafApp leaf, FakeGoogleServer google)
    {
        leaf.WaitFor(Dentist);
        Retry.WhileFalse(() => google.Requests.Any(r => r.StartsWith("GET /userinfo", StringComparison.Ordinal)), TimeSpan.FromSeconds(20));
        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorRoomInput");
    }

    // Shift+Drag From Above Monday's 1:30 PM Card To Below Friday's (next week)
    static void OpenBoxSelect(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var monday = leaf.WaitFor("Event_evt-weekly_202610051330");
        var friday = leaf.WaitFor("Event_evt-weekly_202610091330");
        LeafApp.WaitUntilStill(monday);

        Keyboard.Press(VirtualKeyShort.SHIFT);
        try
        {
            LeafApp.Drag(
                new Point(monday.BoundingRectangle.Left + 4, monday.BoundingRectangle.Top - 12),
                new Point(friday.BoundingRectangle.Right - 4, friday.BoundingRectangle.Bottom + 12));
        }
        finally
        {
            Keyboard.Release(VirtualKeyShort.SHIFT);
        }

        leaf.WaitFor("SelectionSummary");
    }

    // The Rich Event's Editor, With A Numbered List Typed Under Its Description (the toolbar and list drawing in view)
    static void OpenRichEditor(LeafApp leaf)
    {
        leaf.WaitFor("Event_evt-rich_202610011400").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        var title = leaf.WaitFor("EditorTitle");
        Retry.WhileFalse(() => title.Properties.HasKeyboardFocus.ValueOrDefault, Wait);

        var box = leaf.WaitFor("EditorDescription");
        box.Focus();
        Retry.WhileFalse(() => box.Properties.HasKeyboardFocus.ValueOrDefault, Wait);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type(VirtualKeyShort.ENTER);

        // The Toolbar Is Laid Out A Beat After The Box Takes Focus (until then it has no clickable point)
        var numbers = leaf.WaitFor("DescriptionNumbers");
        Retry.WhileTrue(() => numbers.Properties.IsOffscreen.ValueOrDefault, Wait);
        numbers.Click();
        Keyboard.Type("First");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Keyboard.Type("Second");
    }

    /// <summary>
    /// Launches Leaf on <paramref name="profile"/> and opens a screen. <paramref name="sized"/> runs between launch and
    /// opening (the tour sizes the window there). The room row needs the workspace domain before launch.
    /// </summary>
    public static LeafApp Open(string name, FakeGoogleServer google, string profile, Action<LeafApp>? sized = null)
    {
        var (extra, open, _) = Screen(name, google);
        if (name == "RoomInput")
        {
            google.HostedDomain = "example.com";
        }

        // The Rich Event (10-11 AM New York, Oct 1) Is Seeded Once Per Fake Google
        if (name == "RichEditor" && google.EventOnGoogle(SeededProfile.Email, "evt-rich") is null)
        {
            google.AddEvent(SeededProfile.Email, System.Text.Json.Nodes.JsonNode.Parse("""
                {
                  "id": "evt-rich", "status": "confirmed", "summary": "Planning",
                  "description": "<b>Bold</b> <i>italic</i> <u>underlined</u><ul><li>Bullet one</li><li>Bullet two</li></ul><a href=\"https://example.com/doc\">Doc</a>",
                  "start": { "dateTime": "2026-10-01T10:00:00-04:00" }, "end": { "dateTime": "2026-10-01T11:00:00-04:00" }
                }
                """)!.AsObject());
        }

        var leaf = LeafApp.Launch(profile, $"--fake-google {google.BaseUri} --start-date 2026-10-01 {extra}".Trim());
        sized?.Invoke(leaf);
        open(leaf);
        return leaf;
    }

    LeafApp Open(string name) => Open(name, _google, _profile);

    // Narrator: Every Control On Every Window Has A Real Name
    [Theory, MemberData(nameof(Screens))]
    public void Screen_EveryControlHasAName(string name)
    {
        using var leaf = Open(name);

        var unnamed = leaf.AllWindows().SelectMany(A11yAudit.Unnamed).ToList();
        Assert.True(unnamed.Count == 0, $"{name}: " + string.Join("; ", unnamed));
    }

    // Keyboard: Tab Reaches Every Control The Screen Needs
    [Theory, MemberData(nameof(Screens))]
    public void Screen_TabReachesEveryControl(string name)
    {
        var mustReach = Screen(name, _google).MustReach;
        if (mustReach.Length == 0)
        {
            Assert.Skip($"{name} moves with the arrow keys or closes on Tab; its names are audited above.");
        }

        using var leaf = Open(name);

        var window  = leaf.AllWindows().FirstOrDefault(w => w.FindFirstDescendant(cf => cf.ByAutomationId(mustReach[0])) is not null)
            ?? throw new InvalidOperationException($"No window holds '{mustReach[0]}'.");
        var reached = A11yAudit.TabStops(leaf, window);
        Assert.True(!mustReach.Except(reached).Any(), $"{name}: Tab never reached {string.Join(", ", mustReach.Except(reached))}. Reached: {string.Join(", ", reached)}");
    }

    // Narrator: An Event Card Says Its Title, Time, And Calendar (never color alone)
    [Fact]
    public void EventCard_NameSaysTitleTimeAndCalendar()
    {
        using var leaf = Launch();
        var card = leaf.WaitFor(Dentist);

        Assert.Matches(@"^Dentist appointment, .*\d.*, .+$", card.Name);
    }

    // Contrast Theme (only when the owner turned one on): cards use system colors
    [Fact]
    public void HighContrast_EventCardUsesSystemColors()
    {
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            Assert.Skip("Turn on a contrast theme (Settings › Accessibility › Contrast themes) to run this.");
        }

        using var leaf = Launch();
        var card    = leaf.WaitFor(Dentist);
        var ui      = new Windows.UI.ViewManagement.UISettings();
        var fill    = Ink.PixelAt(card.BoundingRectangle.Right - 3, card.BoundingRectangle.Bottom - 3);
        var allowed = new[] { Windows.UI.ViewManagement.UIElementType.Window, Windows.UI.ViewManagement.UIElementType.Highlight, Windows.UI.ViewManagement.UIElementType.ButtonFace }
            .Select(t => ui.UIElementColor(t)).Select(c => Color.FromArgb(c.R, c.G, c.B));

        Assert.Contains(allowed, c => c.ToArgb() == fill.ToArgb());
    }

    // Text Size (only when the owner set it to 150% or more): chrome text isn't clipped
    [Fact]
    public void LargeText_ChromeTextIsNotClipped()
    {
        if (new Windows.UI.ViewManagement.UISettings().TextScaleFactor < 1.5)
        {
            Assert.Skip("Set Settings › Accessibility › Text size to 150% or more to run this.");
        }

        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        // ponytail: the text-clip check compares each text's box to its parent's; it misses clipping inside custom-drawn controls (screenshots cover those).
        var clipped = leaf.AllWindows().SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            .Where(t => !t.Properties.IsOffscreen.ValueOrDefault && t.Parent is { } p && p.Properties.ControlType.ValueOrDefault != ControlType.Pane)
            .Where(t => !t.Parent.BoundingRectangle.Contains(Rectangle.Inflate(t.BoundingRectangle, -1, -1)))
            .Select(t => $"'{t.Name}' in {t.Parent.Properties.AutomationId.ValueOrDefault}")
            .ToList();

        Assert.True(clipped.Count == 0, string.Join("; ", clipped));
    }
}
