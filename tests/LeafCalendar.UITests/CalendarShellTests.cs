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

    // The owner thought the title bar icons sat off the caption buttons' line. Text buttons (Today, the view menu)
    // are left out: their ink is letters with descenders, not a glyph
    [Fact]
    public void TitleBarGlyphs_ShareTheCaptionButtonsInkCenter()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        var titleBar = leaf.WaitFor("AppTitleBar");
        var scale    = leaf.WaitFor("DetailsToggleButton").BoundingRectangle.Width / 32.0;

        // The Close Caption Button: the title bar's top-right 46 x 48
        var bar     = titleBar.BoundingRectangle;
        var close   = new Rectangle(bar.Right - (int)Math.Round(46 * scale), bar.Top, (int)Math.Round(46 * scale), (int)Math.Round(48 * scale));
        var caption = LeafApp.InkCenterY(close);
        Assert.False(double.IsNaN(caption), "No ink in the Close caption button.");

        var glyphs = titleBar.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.IsOffscreen && b.AutomationId is not ("TodayButton" or "ViewModeButton"))
            .ToList();
        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, b =>
        {
            var center = LeafApp.InkCenterY(b.BoundingRectangle);
            Assert.True(Math.Abs(center - caption) <= 1, $"{b.AutomationId} ink center {center}, the caption buttons' {caption}.");
        });
    }
}
