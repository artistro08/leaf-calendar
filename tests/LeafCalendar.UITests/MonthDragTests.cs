using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class MonthDragTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp LaunchInMonth()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.Press(VirtualKeyShort.KEY_M);
        LeafApp.WaitUntilStill(leaf.WaitFor("Chip_evt-single_20261001"));
        return leaf;
    }

    [Fact]
    public void DragChip_ToNextDay_MovesKeepingItsTime()
    {
        using var leaf = LaunchInMonth();
        var step = leaf.WaitFor("MonthDay_2026-10-02").BoundingRectangle.X - leaf.WaitFor("MonthDay_2026-10-01").BoundingRectangle.X;

        LeafApp.DragBy(leaf.WaitFor("Chip_evt-single_20261001"), step, 0, fromTop: 0.5);

        Assert.NotNull(leaf.WaitFor("Chip_evt-single_20261002"));
        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Contains("\"2026-10-02T09:00:00-04:00\"", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleClickEmptyCell_OpensAllDayEditor()
    {
        using var leaf = LaunchInMonth();
        var day = leaf.WaitFor("MonthDay_2026-10-20").BoundingRectangle;

        Mouse.DoubleClick(new Point(day.X + 40, day.Y + 60));

        Assert.True(leaf.WaitFor("EditorAllDay").AsCheckBox().IsChecked);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }
}
