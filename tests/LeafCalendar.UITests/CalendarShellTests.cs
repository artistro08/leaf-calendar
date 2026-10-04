using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CalendarShellTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Launch_SignedIn_ShowsCalendarWithToolbarAndTitle()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Assert.NotNull(leaf.WaitFor("TodayButton"));
        Assert.Contains("2026", leaf.WaitFor("PeriodTitle").Name, StringComparison.Ordinal);
    }

    // Keyboard focus rests on the calendar itself (no ring), never the mini month's first chevron, where Windows' fallback
    // put it: at startup, and after a command menu row runs
    [Fact]
    public void Focus_RestsOnTheCalendar_AtStartupAndAfterACommand()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        var chevron = leaf.WaitFor("MiniMonthPrevious");
        var month = leaf.WaitFor("MiniMonthTitle");
        string FocusedId() => leaf.Focused()?.Properties.AutomationId.ValueOrDefault ?? "";
        Assert.True(Retry.WhileFalse(() => FocusedId() == "CalendarPage", TimeSpan.FromSeconds(5)).Success, $"Focus starts on '{FocusedId()}'.");

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        leaf.WaitForAnywhere("CommandSearchBox");
        Keyboard.Type("2026-11-02");
        leaf.WaitForAnywhere("CommandResult_date");
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("PeriodTitle").Name == "November 2026", TimeSpan.FromSeconds(5)).Success);
        Assert.True(Retry.WhileFalse(() => FocusedId() == "CalendarPage", TimeSpan.FromSeconds(5)).Success, $"After the command, focus is on '{FocusedId()}'.");

        // Space Presses Nothing There (on the chevron it paged the mini month)
        var shown = month.Name;
        Keyboard.Type(VirtualKeyShort.SPACE);
        Thread.Sleep(500);
        Assert.False(chevron.Properties.HasKeyboardFocus.ValueOrDefault);
        Assert.Equal(shown, month.Name);
    }

    // The window title (what the taskbar and Alt+Tab show) is the days on screen and follows the calendar; the title bar
    // inside the window stays as it was, and Settings doesn't change the window title
    [Fact]
    public void WindowTitle_IsTheDaysOnScreen()
    {
        var profile = SeededProfile.Create(new LeafSettings { ViewMode = CalendarViewMode.Day });
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            var window = leaf.MainWindow;
            Assert.True(Retry.WhileFalse(() => window.Name == "Thursday, October 1, 2026", TimeSpan.FromSeconds(10)).Success, $"The window title is \"{window.Name}\".");

            leaf.Press(VirtualKeyShort.KEY_J);
            Assert.True(Retry.WhileFalse(() => window.Name == "Friday, October 2, 2026", TimeSpan.FromSeconds(10)).Success, $"After Next the window title is \"{window.Name}\".");

            leaf.OpenSettings();
            Assert.Equal("Friday, October 2, 2026", window.Name);
            leaf.CloseSettings();
            Assert.Equal("Friday, October 2, 2026", window.Name);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    // First run: 1277 × 814 DIPs (the owner's own size), no bigger than the work area. Closed at another size, the
    // window opens at that size the next time (in DIPs: it opens on the monitor under the pointer, whatever its scale)
    [Fact]
    public void WindowSize_DefaultThenRemembered()
    {
        double width, height;
        using (var first = Launch())
        {
            first.WaitFor("CalendarRoot");
            var box = first.MainWindow.BoundingRectangle;
            var work = System.Windows.Forms.Screen.FromHandle(first.MainWindow.Properties.NativeWindowHandle.Value).WorkingArea;
            Assert.True(Math.Abs(box.Width - Math.Min(1277 * first.Scale, work.Width)) <= 2, $"The first window is {box.Width} wide.");
            Assert.True(Math.Abs(box.Height - Math.Min(814 * first.Scale, work.Height)) <= 2, $"The first window is {box.Height} tall.");

            first.Resize(1500, 900);
            width = 1500 / first.Scale;
            height = 900 / first.Scale;
            first.MainWindow.Close();
            Assert.True(Retry.WhileTrue(() => first.MainWindowCount() > 0, TimeSpan.FromSeconds(10)).Success);
        }

        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        var size = leaf.MainWindow.BoundingRectangle;
        var area = System.Windows.Forms.Screen.FromHandle(leaf.MainWindow.Properties.NativeWindowHandle.Value).WorkingArea;
        var (w, h) = (Math.Min(width * leaf.Scale, area.Width), Math.Min(height * leaf.Scale, area.Height));
        Assert.True(Math.Abs(size.Width - w) <= 3 && Math.Abs(size.Height - h) <= 3, $"The window reopened at {size.Width} × {size.Height}, not {w:0} × {h:0}.");
    }

    [Fact]
    public void ViewMenu_HasOnlyViewChoices()
    {
        using var leaf = Launch();

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitForAnywhere("ViewMonth"));
        Assert.NotNull(leaf.WaitForAnywhere("ViewDay"));
        foreach (var setting in new[] { "ToggleWeekends", "ToggleDeclined", "ToggleWeekNumbers", "Toggle24Hour", "ThemeDark", "WeekStartMonday" })
        {
            Assert.False(leaf.ExistsAnywhere(setting), $"{setting} is still in the view menu.");
        }
    }

    [Fact]
    public void ViewMenu_Month_SwitchesAndLabelsTheButton()
    {
        using var leaf = Launch();

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        Assert.Equal(ToggleState.On, leaf.WaitForAnywhere("ViewWeek").Patterns.Toggle.Pattern.ToggleState.Value);
        leaf.WaitForAnywhere("ViewMonth").Click(); // a radio item: no Invoke pattern

        Assert.NotNull(leaf.WaitFor("MonthGrid"));
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name == "Month", TimeSpan.FromSeconds(5)).Success);

        // The Menu Marks The New View
        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.WaitForAnywhere("ViewMonth").Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On, TimeSpan.FromSeconds(5)).Success);
        Assert.Equal(ToggleState.Off, leaf.WaitForAnywhere("ViewWeek").Patterns.Toggle.Pattern.ToggleState.Value);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    [Fact]
    public void AltLeftAndAltRight_WalkBackAndForward()
    {
        using var leaf = Launch();
        var title = leaf.WaitFor("PeriodTitle");
        var first = title.Name;
        leaf.WaitFor("NextButton").Click();
        Assert.True(Retry.WhileFalse(() => title.Name != first, TimeSpan.FromSeconds(5)).Success);
        var second = title.Name;

        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.LEFT);
        Assert.True(Retry.WhileFalse(() => title.Name == first, TimeSpan.FromSeconds(5)).Success);
        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.RIGHT);
        Assert.True(Retry.WhileFalse(() => title.Name == second, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void MouseBackButton_ReturnsToTheViewBeforeASwitch()
    {
        using var leaf = Launch();
        leaf.WaitFor("ViewModeButton").Click();
        leaf.WaitForAnywhere("ViewDay").Click(); // a radio item: no Invoke pattern
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Day", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);

        Mouse.MoveTo(leaf.WaitFor("ViewHost").GetClickablePoint());
        Mouse.Click(MouseButton.XButton1);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Week", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);
    }

    // The owner thought the title bar icons sat off the caption buttons' line. Every title bar button's ink is
    // measured against the Close glyph's: icons whole, text buttons by their first capital only (T, W: no descender,
    // so the ink box runs from the cap top to the baseline), and the view menu's chevron on its own. It's measured in
    // both themes with the sidebar open and closed (at whatever display scale the PC runs). The measurements go to the
    // test output, and an 8x zoom of the title bar with a red line at the Close glyph's center is saved per state to
    // TestOutput/title-bar-ink-8x-<state>.png next to the test binaries, for the owner
    [Fact]
    public void TitleBarGlyphs_ShareTheCaptionButtonsInkCenter()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        var start = leaf.ClientBounds;
        Mouse.MoveTo(new Point(start.Left + start.Width / 2, start.Bottom - 20));

        var off = new List<string>();
        foreach (var theme in new[] { "first theme", "other theme" })
        {
            foreach (var pane in new[] { "sidebar open", "sidebar closed" })
            {
                off.AddRange(MeasureTitleBar(leaf, $"{theme}, {pane}"));
                ToggleSidebar(leaf);
            }

            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_L);
            Thread.Sleep(1000);
        }

        // Each Ink Center On The Close Glyph's (half a screen pixel), in every state
        Assert.Empty(off);
    }

    private static void ToggleSidebar(LeafApp leaf)
    {
        leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByAutomationId("PART_PaneToggleButton"))!.AsButton().Invoke();
        Thread.Sleep(1000);
    }

    // Measures one state and returns what's more than half a pixel off the Close glyph's center
    private static List<string> MeasureTitleBar(LeafApp leaf, string state)
    {
        var scale = leaf.Scale;
        var row = (int)Math.Round(48 * scale);
        var inset = (int)Math.Ceiling(3 * scale);
        var log = TestContext.Current.TestOutputHelper;

        // Caption Buttons (the pointer is off the title bar: a hovered Close button fills red): the three glyphs in the
        // client area's top-right 3 x 46 (Minimize, Maximize, Close). Measured again until the Close glyph is whole: while
        // the window still opens (it zooms in, and is placed where it was last), a capture finds faint or partial glyphs
        var captionWidth = (int)Math.Round(3 * 46 * scale);
        var names = new[] { "Minimize", "Maximize", "Close" };
        var client = leaf.ClientBounds;
        var dark = false;
        List<InkBox> boxes = [];
        Retry.WhileFalse(() =>
        {
            client = leaf.ClientBounds;
            using var caption = Ink.Capture(new Rectangle(client.Right - captionWidth, client.Top, captionWidth, row));
            dark = caption.Background < 0.5f;

            // Glyph-wide runs only, inside the edges: the window's rounded corner can leave a 1 px speck at the right edge,
            // and its 1 px border runs along the top
            var runs = caption.Runs(inset).Where(r => r.To - r.From >= 5 * scale).ToList();
            boxes = [.. runs.TakeLast(3).Select(r => caption.Measure(inset, r.From, r.To)).OfType<InkBox>()];
            return boxes.Count == 3 && Math.Abs(boxes[2].Width - 10 * scale) <= 1.5 * scale && Math.Abs(boxes[2].Bottom - boxes[2].Top - 10 * scale) <= 1.5 * scale;
        }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));

        state = $"{(dark ? "dark" : "light")}, {state.Split(", ")[1]}";
        Assert.True(boxes.Count == 3, $"{state}: expected the three caption glyphs, found {boxes.Count}.");
        var close = boxes[2];
        log?.WriteLine($"== {state} (scale {scale:0.00}): Close glyph {close}, its center {close.CenterY - client.Top:0.00} px below the client top");
        Assert.True(Math.Abs(close.Width - 10 * scale) <= 1.5 * scale, $"{state}: the Close glyph should be about {10 * scale:0.0} px wide, measured {close}.");

        // Title Bar Buttons (Today and the view menu by ID, in case their control types aren't Button)
        var titleBar = leaf.WaitFor("AppTitleBar");
        var buttons = titleBar.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.IsOffscreen && b.AutomationId is not ("TodayButton" or "ViewModeButton"))
            .ToList();
        Assert.NotEmpty(buttons);

        var measured = new List<(string Name, InkBox Box)>();
        foreach (var button in buttons)
        {
            using var ink = Ink.Capture(button.BoundingRectangle);
            measured.Add((button.AutomationId, Required(ink.Measure(inset), button.AutomationId)));
        }

        using (var today = Ink.Capture(leaf.WaitFor("TodayButton").BoundingRectangle))
        {
            measured.Add(("TodayButton (T)", FirstLetter(today, inset, scale, "TodayButton")));
        }

        using (var menu = Ink.Capture(leaf.WaitFor("ViewModeButton").BoundingRectangle))
        {
            var chevron = menu.Runs(inset)[^1];
            measured.Add(("ViewModeButton (W)", FirstLetter(menu, inset, scale, "ViewModeButton")));
            measured.Add(("ViewModeButton (chevron)", Required(menu.Measure(inset, chevron.From, chevron.To), "ViewModeButton chevron")));
        }

        // Record For The Owner
        foreach (var (name, box) in measured)
        {
            log?.WriteLine($"{name}: {box}, {box.CenterY - close.CenterY:+0.00;-0.00;0} px from Close");
        }

        using (var bar = Ink.Capture(new Rectangle(client.Left, client.Top, client.Width, row)))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestOutput", $"title-bar-ink-8x-{state.Replace(", ", "-", StringComparison.Ordinal).Replace(' ', '-')}.png");
            bar.SaveZoomed(path, 8, close.CenterY);
            log?.WriteLine($"Zoomed title bar: {path}");
        }

        return [.. measured.Where(m => Math.Abs(m.Box.CenterY - close.CenterY) > 0.5).Select(m => $"{state}: {m.Name} ink {m.Box}; Close {close}.")];
    }

    // A text button's first capital: the ink in the first 5 DIP from where its text starts (inside the letter,
    // short of the next one)
    private static InkBox FirstLetter(Ink ink, int inset, double scale, string name)
    {
        var start = ink.Runs(inset)[0].From;
        return Required(ink.Measure(inset, start, start + (int)Math.Round(5 * scale)), name);
    }

    private static InkBox Required(InkBox? box, string name)
    {
        Assert.True(box.HasValue, $"No ink found for {name}.");
        return box.Value;
    }
}
