using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class WorkingHoursTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";

    readonly FakeGoogleServer _google = new();
    string? _profile;

    public void Dispose()
    {
        if (_profile is not null)
        {
            LeafApp.DeleteProfile(_profile);
        }

        _google.Dispose();
    }

    // A tall window, so the grid (opened at 7:30 AM) shows both the morning and the 5 PM line
    LeafApp Launch(LeafSettings? settings = null)
    {
        _profile = SeededProfile.Create(settings);
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor(Dentist);
        leaf.Resize(1400, 1200);
        return leaf;
    }

    // The dentist runs 9-10 AM Eastern: its card (an hour less 2 px, 1 px below the 9:00 line) measures the grid
    static int HourPixels(AutomationElement dentist) => dentist.BoundingRectangle.Height + 2;

    static double LineY(AutomationElement dentist, int hour) => dentist.BoundingRectangle.Top - 1 + (hour - 9) * HourPixels(dentist);

    static void AssertNear(double actual, double expected, string what) =>
        Assert.True(Math.Abs(actual - expected) <= 2, $"{what} is at {actual}, expected {expected} (± 2 px).");

    [Fact]
    public void Default_ShadesBeforeNineAndAfterFive()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        LeafApp.WaitUntilStill(dentist);

        // Midnight to 9 AM ends on the 9:00 line; 5 PM to midnight starts on the 17:00 line
        AssertNear(leaf.WaitFor("OffHours_2026-10-01_0").BoundingRectangle.Bottom, LineY(dentist, 9), "The morning shade's bottom");
        AssertNear(leaf.WaitFor("OffHours_2026-10-01_1").BoundingRectangle.Top, LineY(dentist, 17), "The evening shade's top");

        // Nothing shades the working day itself
        Assert.False(leaf.Exists("OffHours_2026-10-01_2"));
    }

    [Fact]
    public void Weekend_IsShadedAllDay()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        LeafApp.WaitUntilStill(dentist);

        // One shade covers Saturday through the working hours
        var shade = leaf.WaitFor("OffHours_2026-10-03_0").BoundingRectangle;
        Assert.True(shade.Top <= LineY(dentist, 8) && shade.Bottom >= LineY(dentist, 12), $"Saturday's shade {shade} doesn't cover 8 AM to noon.");
        Assert.False(leaf.Exists("OffHours_2026-10-03_1"));
    }

    [Fact]
    public void Disabled_NoShading()
    {
        using var leaf = Launch(new LeafSettings { WorkingHours = new WorkingHours { Enabled = false } });
        leaf.WaitFor(Dentist);

        Assert.Empty(leaf.MainWindow.FindAllDescendants(cf => cf.ByAutomationId("OffHours_2026-10-01_0").Or(cf.ByAutomationId("OffHours_2026-10-03_0"))));
        Assert.DoesNotContain(leaf.MainWindow.FindAllDescendants(), e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("OffHours_", StringComparison.Ordinal));
    }

    // Settings › General's own controls are tested with that page; this checks the grid follows the saved hours
    [Fact]
    public void CustomHours_FromSettings()
    {
        using var leaf = Launch(new LeafSettings { WorkingHours = new WorkingHours { StartMinute = 8 * 60, EndMinute = 16 * 60 } });
        var dentist = leaf.WaitFor(Dentist);
        LeafApp.WaitUntilStill(dentist);

        AssertNear(leaf.WaitFor("OffHours_2026-10-01_0").BoundingRectangle.Bottom, LineY(dentist, 8), "The morning shade's bottom");
        AssertNear(leaf.WaitFor("OffHours_2026-10-01_1").BoundingRectangle.Top, LineY(dentist, 16), "The evening shade's top");
    }

    [Fact]
    public void AllDayExpanded_Seeded_StartsExpanded()
    {
        string[] ids = ["evt-allday-a", "evt-allday-b", "evt-allday-c", "evt-allday-d"];
        foreach (var id in ids)
        {
            _google.AddEvent(SeededProfile.Email, new JsonObject
            {
                ["id"]      = id,
                ["summary"] = $"All day {id[^1]}",
                ["start"]   = new JsonObject { ["date"] = "2026-10-01" },
                ["end"]     = new JsonObject { ["date"] = "2026-10-02" },
            });
        }

        using var leaf = Launch(new LeafSettings { AllDayExpanded = true });

        // Every lane shows without clicking AllDayExpand
        foreach (var id in ids)
        {
            Assert.True(Retry.WhileFalse(() => leaf.Exists($"AllDay_{id}_20261001"), TimeSpan.FromSeconds(10)).Success, $"{id} isn't on screen.");
        }
    }
}
