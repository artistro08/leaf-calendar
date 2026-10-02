using LeafCalendar.Core.Settings;

namespace LeafCalendar.Tests;

public sealed class PowerSettingsTests
{
    [Fact]
    public void Defaults_MatchTheOwnerRulings()
    {
        var s = new LeafSettings().Normalize();

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
        Assert.Empty(s.CollapsedAccounts);
    }

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
            CollapsedAccounts     = ["c", "", "c", "d"],
        }.Normalize();

        Assert.Null(s.MainAccountId);
        Assert.Equal(["a", "b"], s.MeetByDefaultAccounts);
        Assert.Equal(["c", "d"], s.CollapsedAccounts);
    }

    [Fact]
    public void Normalize_Twice_EqualsOnce()
    {
        // Record equality compares lists by reference, so Normalize keeps a list it didn't change (the pane-only fast path in Update relies on it)
        var s = new LeafSettings
        {
            TimeZones             = [new("Asia/Tokyo", "HQ")],
            MeetByDefaultAccounts = ["a"],
            CollapsedAccounts     = ["b"],
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
            CollapsedAccounts     = ["kept", "gone"],
        };

        var pruned = s.ForAccounts(["kept"]);

        Assert.Null(pruned.MainAccountId);
        Assert.Equal(["kept"], pruned.MeetByDefaultAccounts);
        Assert.Equal(["kept"], pruned.CollapsedAccounts);
    }

    [Fact]
    public void ForAccounts_AllStillConnected_IsEqual()
    {
        var s = new LeafSettings { MainAccountId = "a", MeetByDefaultAccounts = ["a"], CollapsedAccounts = ["b"] };

        Assert.Equal(s, s.ForAccounts(["a", "b"]));
    }

    [Fact]
    public void ShareMessage_DefaultsToTheGreeting_AndIsCappedInLength()
    {
        Assert.Equal(LeafCalendar.Core.People.AvailabilityText.DefaultMessage, new LeafSettings().Normalize().ShareMessage);
        Assert.Equal(LeafCalendar.Core.People.AvailabilityText.DefaultMessage, new LeafSettings { ShareMessage = null! }.Normalize().ShareMessage);
        Assert.Equal("", new LeafSettings { ShareMessage = "" }.Normalize().ShareMessage);
        Assert.Equal(LeafCalendar.Core.People.AvailabilityText.MaxMessageLength, new LeafSettings { ShareMessage = new string('x', 5000) }.Normalize().ShareMessage.Length);
    }

    [Fact]
    public void ShareMessage_SavedBeforeItExisted_LoadsAsTheDefault()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize("""{"flyoutDays":5}""", LeafJsonContext.Default.LeafSettings)!.Normalize();

        Assert.Equal(LeafCalendar.Core.People.AvailabilityText.DefaultMessage, s.ShareMessage);
    }

    [Theory]
    [InlineData(CalendarViewMode.Day, CalendarViewMode.Week, CalendarViewMode.Day)]
    [InlineData(CalendarViewMode.Days, CalendarViewMode.Week, CalendarViewMode.Days)]
    [InlineData(CalendarViewMode.Week, CalendarViewMode.Day, CalendarViewMode.Week)]
    [InlineData(CalendarViewMode.Month, CalendarViewMode.Days, CalendarViewMode.Days)]
    [InlineData(CalendarViewMode.Month, CalendarViewMode.Month, CalendarViewMode.Week)]
    [InlineData(CalendarViewMode.Month, (CalendarViewMode)42, CalendarViewMode.Week)]
    public void LastGridView_FollowsEveryViewButMonth(CalendarViewMode view, CalendarViewMode last, CalendarViewMode expected) =>
        Assert.Equal(expected, new LeafSettings { ViewMode = view, LastGridView = last }.Normalize().LastGridView);

    [Fact]
    public void LastGridView_SavedBeforeItExisted_IsWeek()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize("""{"viewMode":"Month"}""", LeafJsonContext.Default.LeafSettings)!.Normalize();

        Assert.Equal(CalendarViewMode.Week, s.LastGridView);
    }

    [Fact]
    public void WithAccountCollapsed_FoldsAndUnfolds()
    {
        var s = new LeafSettings();

        var folded = s.WithAccountCollapsed("a", collapsed: true).WithAccountCollapsed("b", collapsed: true);
        Assert.Equal(["a", "b"], folded.CollapsedAccounts);

        var unfolded = folded.WithAccountCollapsed("a", collapsed: false);
        Assert.Equal(["b"], unfolded.CollapsedAccounts);
    }

    [Fact]
    public void WithAccountCollapsed_AlreadyThatWay_ReturnsTheSameSettings()
    {
        var s = new LeafSettings { CollapsedAccounts = ["a"] };

        Assert.Same(s, s.WithAccountCollapsed("a", collapsed: true));
        Assert.Same(s, s.WithAccountCollapsed("b", collapsed: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void WithAccountCollapsed_BlankAccount_Throws(string accountId) =>
        Assert.Throws<ArgumentException>(() => new LeafSettings().WithAccountCollapsed(accountId, collapsed: true));

    [Fact]
    public void SavedWithTrayExcludedCalendars_LoadsWithoutThem()
    {
        // Settings › Tray's calendar choice is gone (the tray follows what's shown in Leaf): an older row that has it still loads
        var json = """{"flyoutDays":5,"trayExcludedCalendars":[{"accountId":"a","calendarId":"x"}],"meetByDefaultAccounts":["a"]}""";
        var s    = System.Text.Json.JsonSerializer.Deserialize(json, LeafJsonContext.Default.LeafSettings)!.Normalize();

        Assert.Equal(5, s.FlyoutDays);
        Assert.Equal(["a"], s.MeetByDefaultAccounts);
        Assert.DoesNotContain("trayExcluded", System.Text.Json.JsonSerializer.Serialize(s, LeafJsonContext.Default.LeafSettings), StringComparison.Ordinal);
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
        Assert.True(s.WorkingHours.Enabled);
        Assert.Equal(8, s.UpcomingHours);
        Assert.Empty(s.MeetByDefaultAccounts);
    }

    [Fact]
    public void SavedWithInterfaceScale_StillLoads()
    {
        // Milestone 5 builds wrote an interface scale; the setting is gone, and the rest of the row still loads
        var json = """{"weekStart":"Monday","interfaceScale":1.25,"upcomingHours":4,"mapProvider":"Bing"}""";
        var s    = System.Text.Json.JsonSerializer.Deserialize(json, LeafJsonContext.Default.LeafSettings)!.Normalize();

        Assert.Equal(DayOfWeek.Monday, s.WeekStart);
        Assert.Equal(4, s.UpcomingHours);
        Assert.Equal(MapProvider.Bing, s.MapProvider);
    }
}
