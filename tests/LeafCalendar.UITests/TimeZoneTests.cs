using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// Extra time zones: added, renamed, and removed in Settings › Time zones (the time zones button opens it, in the main
/// window in place of the calendar), and shown as columns beside the calendar's hours once Settings closes.
/// </summary>
public sealed class TimeZoneTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // Opens Settings › Time zones from the time zones button beside the calendar's hours
    private static void OpenTimeZones(LeafApp leaf)
    {
        leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();
        leaf.WaitInSettings("TimeZoneSearch");
    }

    // Types a city into the search box and presses Enter, which adds its zone
    private static void AddZone(LeafApp leaf, string city)
    {
        leaf.WaitInSettings("TimeZoneSearch").Focus();
        Thread.Sleep(500);
        Keyboard.Type(city);
        Thread.Sleep(500);
        Keyboard.Press(VirtualKeyShort.RETURN);
    }

    [Fact]
    public void AddTokyo_ShowsColumnAndPersists()
    {
        using (var leaf = Launch())
        {
            OpenTimeZones(leaf);
            AddZone(leaf, "Tokyo");
            Assert.NotNull(leaf.WaitInSettings("ZoneLabelBox_Asia/Tokyo"));

            leaf.CloseSettings();
            Assert.NotNull(leaf.WaitFor("ZoneLabel_Asia/Tokyo"));
        }

        using var relaunched = Launch();
        Assert.NotNull(relaunched.WaitFor("ZoneLabel_Asia/Tokyo"));
    }

    [Fact]
    public void RemoveZone_HidesColumn()
    {
        using var leaf = Launch();
        OpenTimeZones(leaf);
        AddZone(leaf, "London");
        leaf.WaitInSettings("ZoneLabelBox_Europe/London");
        leaf.CloseSettings();
        leaf.WaitFor("ZoneLabel_Europe/London");

        OpenTimeZones(leaf);
        leaf.WaitInSettings("ZoneRemove_Europe/London").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.SettingsView.FindFirstDescendant(cf => cf.ByAutomationId("ZoneLabelBox_Europe/London")) is not null, TimeSpan.FromSeconds(5)).Success, "London is still listed in Settings.");
        leaf.CloseSettings();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("ZoneLabel_Europe/London"), TimeSpan.FromSeconds(5)).Success, "London's column still shows.");
    }

    [Fact]
    public void RenameZone_ShowsLabelAndPersists()
    {
        using (var leaf = Launch())
        {
            OpenTimeZones(leaf);
            AddZone(leaf, "Tokyo");

            // The Label Saves When Focus Leaves Its Box
            var box = leaf.WaitInSettings("ZoneLabelBox_Asia/Tokyo").AsTextBox();
            box.Focus();
            Keyboard.Type("HQ");
            leaf.WaitInSettings("TimeZoneSearch").Focus();
            Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("ZoneLabelBox_Asia/Tokyo").AsTextBox().Text == "HQ", TimeSpan.FromSeconds(5)).Success);

            leaf.CloseSettings();
            Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ZoneLabel_Asia/Tokyo").Name == "HQ", TimeSpan.FromSeconds(5)).Success, "The column isn't labeled HQ.");
        }

        using var relaunched = Launch();
        Assert.Equal("HQ", relaunched.WaitFor("ZoneLabel_Asia/Tokyo").Name);
    }

    [Fact]
    public void AddedZones_SitLeftOfThePcZone_NewestNextToIt()
    {
        using var leaf = Launch();
        OpenTimeZones(leaf);
        foreach (var (city, id) in new[] { ("Tokyo", "Asia/Tokyo"), ("London", "Europe/London") })
        {
            AddZone(leaf, city);
            leaf.WaitInSettings($"ZoneLabelBox_{id}");
        }

        leaf.CloseSettings();

        // Left To Right: Tokyo (added first), London (added last), then the PC's zone next to the days
        var tokyo = leaf.WaitFor("ZoneLabel_Asia/Tokyo").BoundingRectangle;
        var london = leaf.WaitFor("ZoneLabel_Europe/London").BoundingRectangle;
        var local = leaf.WaitFor("ZoneLabel_Local").BoundingRectangle;
        Assert.True(tokyo.Right <= london.Left, $"Tokyo {tokyo} should be left of London {london}");
        Assert.True(london.Right <= local.Left, $"London {london} should be left of the PC's zone {local}");
        Assert.True(local.Left - london.Right < london.Width, "London should be the column right next to the PC's zone");
    }

    [Fact]
    public void FourZones_ShowsLimit()
    {
        using var leaf = Launch();
        OpenTimeZones(leaf);
        var zones = new[] { ("Tokyo", "Asia/Tokyo"), ("London", "Europe/London"), ("Paris", "Europe/Paris"), ("Sydney", "Australia/Sydney") };
        foreach (var (city, id) in zones)
        {
            AddZone(leaf, city);
            leaf.WaitInSettings($"ZoneLabelBox_{id}");
        }

        Assert.NotNull(leaf.WaitInSettings("TimeZoneLimit"));

        // All Four Show As Columns
        leaf.CloseSettings();
        foreach (var (_, id) in zones)
        {
            Assert.NotNull(leaf.WaitFor($"ZoneLabel_{id}"));
        }
    }
}
