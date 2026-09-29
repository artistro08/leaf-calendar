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
            Keyboard.Press(VirtualKeyShort.ESCAPE);
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
}
