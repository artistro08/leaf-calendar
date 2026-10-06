using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AlertSettingsTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var s = new LeafSettings().Normalize();

        Assert.True(s.ReminderNotifications);
        Assert.True(s.JoinNowNotifications);
        Assert.True(s.PersistForMeetings);
        Assert.False(s.PersistWhenAlone);
        Assert.True(s.InviteNotifications);
        Assert.True(s.NotificationSound);
        Assert.Equal(3, s.FlyoutDays);
        Assert.True(s.FlyoutAllDay);
        Assert.Equal(60, s.TrayLookaheadMinutes);
        Assert.Equal("Ctrl+Alt+J", s.JoinShortcut);
        Assert.Equal("Ctrl+Alt+K", s.FlyoutShortcut);
    }

    [Fact]
    public void Load_RowSavedBeforeMilestone4_KeepsItsValuesAndGetsDefaults()
    {
        using var conn = _db.Database.Open();
        conn.Execute(null, "INSERT INTO settings (key, value) VALUES ('app', $value);", ("$value", """{"showWeekends":false,"theme":"Dark"}"""));

        var s = SettingsStore.Load(conn);

        Assert.False(s.ShowWeekends);
        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.True(s.ReminderNotifications);
        Assert.Equal("Ctrl+Alt+J", s.JoinShortcut);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99, 14)]
    [InlineData(7, 7)]
    public void Normalize_FlyoutDays_Clamped(int stored, int expected)
    {
        Assert.Equal(expected, (new LeafSettings { FlyoutDays = stored }).Normalize().FlyoutDays);
    }

    [Theory]
    [InlineData(15, 15)]
    [InlineData(480, 480)]
    [InlineData(45, 60)]
    [InlineData(-1, 60)]
    public void Normalize_Lookahead_OnlyTheOfferedChoices(int stored, int expected)
    {
        Assert.Equal(expected, (new LeafSettings { TrayLookaheadMinutes = stored }).Normalize().TrayLookaheadMinutes);
    }

    [Fact]
    public void Normalize_Shortcuts_CanonicalOrDefaultOrNone()
    {
        var s = new LeafSettings { JoinShortcut = "alt+ctrl+m", FlyoutShortcut = "nonsense" }.Normalize();
        Assert.Equal("Ctrl+Alt+M", s.JoinShortcut);
        Assert.Equal("Ctrl+Alt+K", s.FlyoutShortcut);

        var none = new LeafSettings { JoinShortcut = "", FlyoutShortcut = null! }.Normalize();
        Assert.Equal("", none.JoinShortcut);
        Assert.Equal("Ctrl+Alt+K", none.FlyoutShortcut);
    }

    [Fact]
    public void Normalize_SameShortcutTwice_TurnsTheFlyoutOneOff()
    {
        var s = new LeafSettings { JoinShortcut = "Ctrl+Alt+K", FlyoutShortcut = "Ctrl+Alt+K" }.Normalize();

        Assert.Equal("Ctrl+Alt+K", s.JoinShortcut);
        Assert.Equal("", s.FlyoutShortcut);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheNewFields()
    {
        using var conn = _db.Database.Open();
        SettingsStore.Save(conn, new LeafSettings
        {
            ReminderNotifications = false,
            JoinNowNotifications = false,
            PersistForMeetings = false,
            PersistWhenAlone = true,
            InviteNotifications = false,
            NotificationSound = false,
            FlyoutDays = 5,
            FlyoutAllDay = false,
            TrayLookaheadMinutes = 240,
            JoinShortcut = "Ctrl+Alt+Shift+F9",
            FlyoutShortcut = "Ctrl+Alt+Shift+F10",
        });

        var s = SettingsStore.Load(conn);

        Assert.False(s.ReminderNotifications);
        Assert.False(s.JoinNowNotifications);
        Assert.False(s.PersistForMeetings);
        Assert.True(s.PersistWhenAlone);
        Assert.False(s.InviteNotifications);
        Assert.False(s.NotificationSound);
        Assert.Equal(5, s.FlyoutDays);
        Assert.False(s.FlyoutAllDay);
        Assert.Equal(240, s.TrayLookaheadMinutes);
        Assert.Equal("Ctrl+Alt+Shift+F9", s.JoinShortcut);
        Assert.Equal("Ctrl+Alt+Shift+F10", s.FlyoutShortcut);
    }
}
