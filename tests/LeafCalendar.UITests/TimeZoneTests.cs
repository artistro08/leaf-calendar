using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TimeZoneTests : IDisposable
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
    public void AddTokyo_ShowsColumnAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();
            var search = leaf.WaitForAnywhere("TimeZoneSearch");
            search.Focus();
            Keyboard.Type("Tokyo");
            Keyboard.Press(VirtualKeyShort.RETURN);

            Assert.NotNull(leaf.WaitFor("ZoneLabel_Asia/Tokyo"));
        }

        using var relaunched = Launch();
        Assert.NotNull(relaunched.WaitFor("ZoneLabel_Asia/Tokyo"));
    }

    [Fact]
    public void RemoveZone_HidesColumn()
    {
        using var leaf = Launch();
        leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();
        leaf.WaitForAnywhere("TimeZoneSearch").Focus();
        Keyboard.Type("London");
        Keyboard.Press(VirtualKeyShort.RETURN);
        leaf.WaitFor("ZoneLabel_Europe/London");

        leaf.WaitForAnywhere("ZoneRemove_Europe/London").AsButton().Invoke();

        Assert.True(FlaUI.Core.Tools.Retry.WhileTrue(() => leaf.Exists("ZoneLabel_Europe/London"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void RenameZone_ShowsLabelAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();
            leaf.WaitForAnywhere("TimeZoneSearch").Focus();
            Keyboard.Type("Tokyo");
            Keyboard.Press(VirtualKeyShort.RETURN);
            leaf.WaitFor("ZoneLabel_Asia/Tokyo");

            var box = leaf.WaitForAnywhere("ZoneLabelBox_Asia/Tokyo").AsTextBox();
            box.Focus();
            Keyboard.Type("HQ");
            leaf.WaitForAnywhere("TimeZoneSearch").Focus();

            Assert.True(FlaUI.Core.Tools.Retry.WhileFalse(() => leaf.WaitFor("ZoneLabel_Asia/Tokyo").Name == "HQ", TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        Assert.Equal("HQ", relaunched.WaitFor("ZoneLabel_Asia/Tokyo").Name);
    }

    [Fact]
    public void AddedZones_SitLeftOfThePcZone_NewestNextToIt()
    {
        using var leaf = Launch();
        leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();

        foreach (var (query, id) in new[] { ("Tokyo", "Asia/Tokyo"), ("London", "Europe/London") })
        {
            leaf.WaitForAnywhere("TimeZoneSearch").Focus();
            Thread.Sleep(500);
            Keyboard.Type(query);
            Thread.Sleep(500);
            Keyboard.Press(VirtualKeyShort.RETURN);
            leaf.WaitFor($"ZoneLabel_{id}");
        }

        // Left To Right: Tokyo (added first), London (added last), then the PC's zone next to the days
        var tokyo  = leaf.WaitFor("ZoneLabel_Asia/Tokyo").BoundingRectangle;
        var london = leaf.WaitFor("ZoneLabel_Europe/London").BoundingRectangle;
        var local  = leaf.WaitFor("ZoneLabel_Local").BoundingRectangle;
        Assert.True(tokyo.Right <= london.Left, $"Tokyo {tokyo} should be left of London {london}");
        Assert.True(london.Right <= local.Left, $"London {london} should be left of the PC's zone {local}");
        Assert.True(local.Left - london.Right < london.Width, "London should be the column right next to the PC's zone");
    }

    [Fact]
    public void FourZones_ShowsLimit()
    {
        using var leaf = Launch();
        leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();

        foreach (var (query, id) in new[] { ("Tokyo", "Asia/Tokyo"), ("London", "Europe/London"), ("Paris", "Europe/Paris"), ("Sydney", "Australia/Sydney") })
        {
            leaf.WaitForAnywhere("TimeZoneSearch").Focus();
            Thread.Sleep(500);
            Keyboard.Type(query);
            Thread.Sleep(500);
            Keyboard.Press(VirtualKeyShort.RETURN);
            leaf.WaitFor($"ZoneLabel_{id}");
        }

        Assert.NotNull(leaf.WaitForAnywhere("TimeZoneLimit"));
    }
}
