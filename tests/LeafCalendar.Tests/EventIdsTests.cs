using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public class EventIdsTests
{
    [Fact]
    public void NewId_IsLowercaseBase32HexAndUnique()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => EventIds.NewId()).ToList();

        Assert.All(ids, id =>
        {
            Assert.Equal(26, id.Length);
            Assert.All(id, c => Assert.Contains(c, "0123456789abcdefghijklmnopqrstuv"));
        });
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void InstanceId_Timed_MatchesGoogleFormat()
    {
        var start = new DateTimeOffset(2026, 10, 7, 9, 30, 0, TimeSpan.FromHours(-4));

        Assert.Equal("evt-weekly_20261007T133000Z", EventIds.InstanceId("evt-weekly", start, isAllDay: false));
    }

    [Fact]
    public void InstanceId_AllDay_UsesDateOnly()
    {
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("holiday_20261012", EventIds.InstanceId("holiday", start, isAllDay: true));
    }
}
