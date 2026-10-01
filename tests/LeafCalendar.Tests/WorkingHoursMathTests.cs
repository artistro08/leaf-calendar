using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class WorkingHoursMathTests
{
    [Fact]
    public void Workday_ShadesBeforeAndAfter() =>
        Assert.Equal([(0, 540), (1020, 1440)], WorkingHoursMath.OffHours(new WorkingHours(), DayOfWeek.Thursday));

    [Fact]
    public void DayOff_ShadesAllDay() =>
        Assert.Equal([(0, 1440)], WorkingHoursMath.OffHours(new WorkingHours(), DayOfWeek.Saturday));

    [Fact]
    public void Disabled_ShadesNothing() =>
        Assert.Empty(WorkingHoursMath.OffHours(new WorkingHours { Enabled = false }, DayOfWeek.Saturday));

    [Fact]
    public void MidnightToMidnight_ShadesNothingOnWorkdays() =>
        Assert.Empty(WorkingHoursMath.OffHours(new WorkingHours { StartMinute = 0, EndMinute = 1440 }, DayOfWeek.Monday));
}
