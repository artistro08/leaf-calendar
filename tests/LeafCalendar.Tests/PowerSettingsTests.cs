using LeafCalendar.Core.Settings;

namespace LeafCalendar.Tests;

public sealed class PowerSettingsTests
{
    [Fact]
    public void Defaults_MatchTheOwnerRulings()
    {
        var s = new LeafSettings().Normalize();

        Assert.Equal(1.0, s.InterfaceScale);
        Assert.True(s.WorkingHours.Enabled);
        Assert.Equal(9 * 60, s.WorkingHours.StartMinute);
        Assert.Equal(17 * 60, s.WorkingHours.EndMinute);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], s.WorkingHours.Days);
        Assert.False(s.AllDayExpanded);
        Assert.Equal(MapProvider.Google, s.MapProvider);
        Assert.Equal(8, s.UpcomingHours);
        Assert.Null(s.PrimaryTimeZone);
        Assert.True(s.PromptOnZoneChange);
        Assert.Null(s.MainAccountId);
        Assert.Empty(s.MeetByDefaultAccounts);
        Assert.Empty(s.TrayExcludedCalendars);
    }

    [Theory]
    [InlineData(0.7, 1.0)]
    [InlineData(1.33, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.25, 1.25)]
    public void Normalize_ScaleNotAChoice_ResetsTo100(double saved, double expected) =>
        Assert.Equal(expected, (new LeafSettings { InterfaceScale = saved }).Normalize().InterfaceScale);

    [Fact]
    public void Normalize_WorkingHoursBackwardsOrOutOfRange_ResetToDefaults()
    {
        var backwards = new LeafSettings { WorkingHours = new WorkingHours { StartMinute = 1000, EndMinute = 600 } }.Normalize();
        var outside   = new LeafSettings { WorkingHours = new WorkingHours { StartMinute = -5, EndMinute = 2000 } }.Normalize();

        Assert.Equal((540, 1020), (backwards.WorkingHours.StartMinute, backwards.WorkingHours.EndMinute));
        Assert.Equal((540, 1020), (outside.WorkingHours.StartMinute, outside.WorkingHours.EndMinute));
    }

    [Fact]
    public void Normalize_WorkingDays_DistinctAndKnownOnly()
    {
        var s = new LeafSettings { WorkingHours = new WorkingHours { Days = [DayOfWeek.Monday, DayOfWeek.Monday, (DayOfWeek)9] } }.Normalize();

        Assert.Equal([DayOfWeek.Monday], s.WorkingHours.Days);
    }

    [Fact]
    public void Normalize_UnknownEnumsAndChoices_Reset()
    {
        var s = new LeafSettings { MapProvider = (MapProvider)7, UpcomingHours = 5 }.Normalize();

        Assert.Equal(MapProvider.Google, s.MapProvider);
        Assert.Equal(8, s.UpcomingHours);
    }

    [Fact]
    public void Normalize_UnknownPrimaryZone_FollowsWindows()
    {
        Assert.Null(new LeafSettings { PrimaryTimeZone = "Mars/Olympus" }.Normalize().PrimaryTimeZone);
        Assert.Equal("Asia/Tokyo", new LeafSettings { PrimaryTimeZone = "Asia/Tokyo" }.Normalize().PrimaryTimeZone);
    }

    [Fact]
    public void Normalize_BlankAndDuplicateIds_Dropped()
    {
        var s = new LeafSettings
        {
            MainAccountId         = "  ",
            MeetByDefaultAccounts = ["a", "a", " ", "b"],
            TrayExcludedCalendars = [new("a", "x"), new("a", "x"), new("", "y")],
        }.Normalize();

        Assert.Null(s.MainAccountId);
        Assert.Equal(["a", "b"], s.MeetByDefaultAccounts);
        Assert.Equal([new CalendarRef("a", "x")], s.TrayExcludedCalendars);
    }

    [Fact]
    public void Normalize_Twice_EqualsOnce()
    {
        // Record equality compares lists by reference, so Normalize keeps a list it didn't change (the pane-only fast path in Update relies on it)
        var s = new LeafSettings
        {
            TimeZones             = [new("Asia/Tokyo", "HQ")],
            MeetByDefaultAccounts = ["a"],
            TrayExcludedCalendars = [new("a", "x")],
        }.Normalize();

        Assert.Equal(s, s.Normalize());
        Assert.Equal(new LeafSettings().Normalize(), new LeafSettings().Normalize().Normalize());
    }

    [Fact]
    public void ForAccounts_DropsADisconnectedAccountsSettings()
    {
        var s = new LeafSettings
        {
            MainAccountId         = "gone",
            MeetByDefaultAccounts = ["gone", "kept"],
            TrayExcludedCalendars = [new("gone", "x"), new("kept", "y")],
        };

        var pruned = s.ForAccounts(["kept"]);

        Assert.Null(pruned.MainAccountId);
        Assert.Equal(["kept"], pruned.MeetByDefaultAccounts);
        Assert.Equal([new CalendarRef("kept", "y")], pruned.TrayExcludedCalendars);
    }

    [Fact]
    public void ForAccounts_AllStillConnected_IsEqual()
    {
        var s = new LeafSettings { MainAccountId = "a", MeetByDefaultAccounts = ["a"], TrayExcludedCalendars = [new("a", "x")] };

        Assert.Equal(s, s.ForAccounts(["a", "b"]));
    }

    [Fact]
    public void SavedBeforeM5_LoadsWithDefaults()
    {
        // A settings row written by Milestone 4 has none of the new keys (camelCase, string enums, as LeafJsonContext writes)
        var json = """{"weekStart":"Monday","showWeekends":false,"viewMode":"Month","flyoutDays":5}""";
        var s    = System.Text.Json.JsonSerializer.Deserialize(json, LeafJsonContext.Default.LeafSettings)!.Normalize();

        Assert.False(s.ShowWeekends);
        Assert.Equal(CalendarViewMode.Month, s.ViewMode);
        Assert.Equal(5, s.FlyoutDays);
        Assert.Equal(1.0, s.InterfaceScale);
        Assert.True(s.WorkingHours.Enabled);
        Assert.Equal(8, s.UpcomingHours);
        Assert.Empty(s.MeetByDefaultAccounts);
    }
}
