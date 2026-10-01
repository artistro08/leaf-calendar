using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CalendarShellTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Launch_SignedIn_ShowsCalendarWithToolbarAndTitle()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Assert.NotNull(leaf.WaitFor("TodayButton"));
        Assert.Contains("2026", leaf.WaitFor("PeriodTitle").Name, StringComparison.Ordinal);
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
        leaf.WaitForAnywhere("ViewMonth").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitFor("MonthGrid"));
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name == "Month", TimeSpan.FromSeconds(5)).Success);
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
        leaf.WaitForAnywhere("ViewDay").AsMenuItem().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Day", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);

        Mouse.MoveTo(leaf.WaitFor("ViewHost").GetClickablePoint());
        Mouse.Click(MouseButton.XButton1);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Week", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);
    }

    // The owner thought the title bar icons sat off the caption buttons' line. Every title bar button's ink is
    // measured against the Close glyph's: icons whole, text buttons by their first capital only (T, W: no descender,
    // so the ink box runs from the cap top to the baseline), and the view menu's chevron on its own. The measurements
    // go to the test output, and an 8x zoom of the title bar with a red line at the Close glyph's center is saved to
    // TestOutput/title-bar-ink-8x.png next to the test binaries, for the owner
    [Fact]
    public void TitleBarGlyphs_ShareTheCaptionButtonsInkCenter()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        var scale  = leaf.Scale;
        var client = leaf.ClientBounds;
        var row    = (int)Math.Round(48 * scale);
        var inset  = (int)Math.Ceiling(3 * scale);
        var log    = TestContext.Current.TestOutputHelper;

        // Caption Buttons: the three glyphs in the client area's top-right 3 x 46 (Minimize, Maximize, Close)
        var captionWidth = (int)Math.Round(3 * 46 * scale);
        InkBox close;
        using (var caption = Ink.Capture(new Rectangle(client.Right - captionWidth, client.Top, captionWidth, row)))
        {
            var runs = caption.Runs();
            Assert.True(runs.Count >= 3, $"Expected the three caption glyphs, found {runs.Count} ink runs.");
            var names = new[] { "Minimize", "Maximize", "Close" };
            var boxes = runs.TakeLast(3).Select((r, i) => Required(caption.Measure(fromX: r.From, toX: r.To), names[i])).ToList();
            for (var i = 0; i < boxes.Count; i++)
            {
                log?.WriteLine($"{names[i]}: {boxes[i]}");
            }

            close = boxes[2];
        }

        Assert.True(Math.Abs(close.Width - 10 * scale) <= 1.5 * scale, $"The Close glyph should be about {10 * scale:0.0} px wide, measured {close}.");

        // Title Bar Buttons (Today and the view menu by ID, in case their control types aren't Button)
        var titleBar = leaf.WaitFor("AppTitleBar");
        var buttons  = titleBar.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
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
            var path = Path.Combine(AppContext.BaseDirectory, "TestOutput", "title-bar-ink-8x.png");
            bar.SaveZoomed(path, 8, close.CenterY);
            log?.WriteLine($"Zoomed title bar: {path}");
        }

        // Each Ink Center On The Close Glyph's (half a screen pixel)
        Assert.All(measured, m => Assert.True(
            Math.Abs(m.Box.CenterY - close.CenterY) <= 0.5,
            $"{m.Name} ink {m.Box}; Close {close}."));
    }

    // A text button's first capital: the ink in the first 5 DIP from where its text starts (inside the letter,
    // short of the next one)
    static InkBox FirstLetter(Ink ink, int inset, double scale, string name)
    {
        var start = ink.Runs(inset)[0].From;
        return Required(ink.Measure(inset, start, start + (int)Math.Round(5 * scale)), name);
    }

    static InkBox Required(InkBox? box, string name)
    {
        Assert.True(box.HasValue, $"No ink found for {name}.");
        return box.Value;
    }
}
