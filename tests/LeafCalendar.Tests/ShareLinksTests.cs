using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class ShareLinksTests
{
    private static readonly CalComProvider Cal = new(new HttpClient(), "cal_live_x", CalComProvider.DefaultApiBase);
    private static readonly SchedulingLink Link = new("calcom", "101", "30 min", 30, new Uri("https://cal.com/leaf-tester/30min"), AllowsGuests: false);
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly DateTimeOffset Ten = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_SplitsAndLinksEachTime()
    {
        var result = ShareLinks.Build(Cal, Link, [new BusyRange(Ten, Ten.AddHours(1))], null, [], "Pick one:\r\n{times}", Chicago, use24Hour: false);

        Assert.Equal(
            "Pick one:\r\nTue Oct 13, 10–10:30 AM CT: https://cal.com/leaf-tester/30min?date=2026-10-13&slot=2026-10-13T15:00:00.000Z\r\n"
            + "Tue Oct 13, 10:30–11 AM CT: https://cal.com/leaf-tester/30min?date=2026-10-13&slot=2026-10-13T15:30:00.000Z",
            result.Plain);
        Assert.Equal(2, result.Html.Split("<a href=", StringSplitOptions.None).Length - 1);
        Assert.False(result.GuestsDropped);
    }

    [Fact]
    public void Build_TwoGuestsOnLinkWithoutGuests_FlagsDropped()
    {
        var result = ShareLinks.Build(Cal, Link, [new BusyRange(Ten, Ten.AddMinutes(30))], null, [new("", "a@example.com"), new("", "b@example.com")], "", Chicago, false);

        Assert.True(result.GuestsDropped);
        Assert.DoesNotContain("b%40example.com", result.Plain, StringComparison.Ordinal);
    }
}
