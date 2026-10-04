using System.Globalization;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class MonthViewTests : IDisposable
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
        try
        {
            leaf.WaitFor("Event_evt-single_202610011300");
            leaf.WaitFor("ViewModeButton").AsButton().Invoke();
            leaf.WaitForAnywhere("ViewMonth").AsMenuItem().Invoke();
            leaf.WaitFor("MonthGrid");
            return leaf;
        }
        catch
        {
            // Close The App So A Failed Setup Doesn't Leave It Running
            leaf.Dispose();
            throw;
        }
    }

    [Fact]
    public void MonthView_October2026_ShowsChipsAndTitle()
    {
        using var leaf = LaunchInMonth();

        Assert.Equal("October 2026", leaf.WaitFor("PeriodTitle").Name);
        Assert.NotNull(leaf.WaitFor("Chip_evt-single_20261001"));
        Assert.NotNull(leaf.WaitFor("Chip_evt-allday_20261012"));
        Assert.NotNull(leaf.WaitFor("Chip_evt-weekly_20261005"));
        Assert.False(leaf.Exists("Chip_evt-weekly_20261007"));
    }

    [Fact]
    public void MonthDay_Click_OpensDayView()
    {
        using var leaf = LaunchInMonth();

        leaf.WaitFor("MonthDay_2026-10-12").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("TimeGrid"));
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-12"));
    }

    [Fact]
    public void Next_InMonth_ShowsNovember()
    {
        using var leaf = LaunchInMonth();

        leaf.WaitFor("NextButton").AsButton().Invoke();

        Assert.True(FlaUI.Core.Tools.Retry.WhileFalse(() => leaf.WaitFor("PeriodTitle").Name == "November 2026", TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void PastChips_AreFaded()
    {
        // The test clock is 8:00 on the PC's clock on Oct 1, so both events are seeded in the PC's zone
        _google.AddEvent("leaf.tester@gmail.com", Seed("evt-past", new DateTime(2026, 10, 1, 6, 0, 0)));
        _google.AddEvent("leaf.tester@gmail.com", Seed("evt-later", new DateTime(2026, 10, 1, 10, 0, 0)));
        using var leaf = LaunchInMonth();

        Assert.Equal("Past", leaf.WaitFor("Chip_evt-past_20261001").Properties.ItemStatus.ValueOrDefault);
        Assert.Equal("", leaf.WaitFor("Chip_evt-later_20261001").Properties.ItemStatus.ValueOrDefault ?? "");
    }

    // A one-hour event starting at a time on the PC's clock
    private static JsonObject Seed(string id, DateTime localStart)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local);
        return new JsonObject
        {
            ["id"] = id,
            ["summary"] = id,
            ["start"] = new JsonObject { ["dateTime"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
            ["end"] = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
        };
    }
}
