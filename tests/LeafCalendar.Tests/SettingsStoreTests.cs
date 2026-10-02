using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Load_NothingSaved_ReturnsDefaults()
    {
        using var conn = _db.Database.Open();

        var settings = SettingsStore.Load(conn);

        Assert.Equal(CalendarViewMode.Week, settings.ViewMode);
        Assert.True(settings.ShowWeekends);
        Assert.False(settings.ShowDeclined);
        Assert.Equal(LeafSettings.DefaultHourHeight, settings.HourHeight);
        Assert.Empty(settings.TimeZones);
    }

    [Fact]
    public void PowerSettings_RoundTrip()
    {
        using var conn = _db.Database.Open();
        var saved = new LeafSettings
        {
            WorkingHours          = new WorkingHours { Enabled = false, StartMinute = 8 * 60, EndMinute = 18 * 60, Days = [DayOfWeek.Sunday, DayOfWeek.Wednesday] },
            MapProvider           = MapProvider.Bing,
            MeetByDefaultAccounts = ["acct1"],
            CollapsedAccounts     = ["acct2"],
        };

        SettingsStore.Save(conn, saved);
        var loaded = SettingsStore.Load(conn);

        Assert.False(loaded.WorkingHours.Enabled);
        Assert.Equal(8 * 60, loaded.WorkingHours.StartMinute);
        Assert.Equal(18 * 60, loaded.WorkingHours.EndMinute);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Wednesday], loaded.WorkingHours.Days);
        Assert.Equal(MapProvider.Bing, loaded.MapProvider);
        Assert.Equal(["acct1"], loaded.MeetByDefaultAccounts);
        Assert.Equal(["acct2"], loaded.CollapsedAccounts);
    }

    [Fact]
    public void UnknownMapProvider_FallsBackToGoogle_KeepingOtherSettings()
    {
        using var conn = _db.Database.Open();
        SettingsStore.Save(conn, new LeafSettings { ShowDeclined = true, MapProvider = MapProvider.Bing });
        conn.Execute(null, "UPDATE settings SET value = replace(value, 'Bing', 'Waze') WHERE key = 'app';");

        var loaded = SettingsStore.Load(conn);

        Assert.Equal(MapProvider.Google, loaded.MapProvider);
        Assert.True(loaded.ShowDeclined);
    }

    [Fact]
    public void DefaultCalendar_RoundTrips()
    {
        using var conn = _db.Database.Open();
        var calendar = new CalendarRef("109876543210", "family123@group.calendar.google.com");

        SettingsStore.Save(conn, new LeafSettings { DefaultCalendar = calendar });

        Assert.Equal(calendar, SettingsStore.Load(conn).DefaultCalendar);
    }

    [Theory]
    [InlineData("", "cal")]
    [InlineData("acct", " ")]
    public void Normalize_BlankDefaultCalendar_BecomesNull(string account, string calendar)
    {
        var settings = new LeafSettings { DefaultCalendar = new CalendarRef(account, calendar) }.Normalize();

        Assert.Null(settings.DefaultCalendar);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var conn = _db.Database.Open();
        var saved = new LeafSettings
        {
            WeekStart      = DayOfWeek.Monday,
            ShowWeekends   = false,
            ViewMode       = CalendarViewMode.Days,
            CustomDayCount = 4,
            HourHeight     = 64,
            Theme          = AppTheme.Dark,
            TimeZones      = [new ExtraTimeZone("Asia/Tokyo", "Tokyo office")],
        };

        SettingsStore.Save(conn, saved);
        var loaded = SettingsStore.Load(conn);

        Assert.Equal(DayOfWeek.Monday, loaded.WeekStart);
        Assert.False(loaded.ShowWeekends);
        Assert.Equal(CalendarViewMode.Days, loaded.ViewMode);
        Assert.Equal(4, loaded.CustomDayCount);
        Assert.Equal(64, loaded.HourHeight);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(new ExtraTimeZone("Asia/Tokyo", "Tokyo office"), Assert.Single(loaded.TimeZones));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"viewMode":"Hologram","customDayCount":500,"hourHeight":2,"weekStart":"Funday","timeZones":[{"id":"Mars/Olympus","label":null}]}""")]
    [InlineData("""{"timeZones":null}""")]
    public void Load_CorruptOrOutOfRange_FallsBackToSafeValues(string storedJson)
    {
        using var conn = _db.Database.Open();
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO settings (key, value) VALUES ('app', $value);";
            insert.Parameters.AddWithValue("$value", storedJson);
            insert.ExecuteNonQuery();
        }

        var settings = SettingsStore.Load(conn);

        Assert.InRange(settings.CustomDayCount, 1, 31);
        Assert.InRange(settings.HourHeight, LeafSettings.MinHourHeight, LeafSettings.MaxHourHeight);
        Assert.True(Enum.IsDefined(settings.ViewMode));
        Assert.True(Enum.IsDefined(settings.WeekStart));
        Assert.Empty(settings.TimeZones);
    }

    [Fact]
    public void Normalize_TooManyAndDuplicateZones_KeepsFirstFourDistinctAndTrimsLabels()
    {
        var settings = new LeafSettings
        {
            TimeZones =
            [
                new("Asia/Tokyo", "  Tokyo  "),
                new("Asia/Tokyo", "dup"),
                new("Europe/London", ""),
                new("America/New_York", null),
                new("Australia/Sydney", null),
                new("Europe/Paris", null),
            ],
        }.Normalize();

        Assert.Equal(["Asia/Tokyo", "Europe/London", "America/New_York", "Australia/Sydney"], settings.TimeZones.Select(z => z.Id));
        Assert.Equal("Tokyo", settings.TimeZones[0].Label);
        Assert.Null(settings.TimeZones[1].Label);
    }
}
