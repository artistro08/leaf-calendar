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

    // A series split with "this and following" gets the ID {original}_R{start}, but Google names its instances after
    // the original ID: "…_R20251010T173000_20261106T183000Z" is a 404, and a reply sent to it was lost
    [Fact]
    public void InstanceId_SplitSeries_UsesTheOriginalSeriesId()
    {
        var start = new DateTimeOffset(2026, 11, 6, 12, 30, 0, TimeSpan.FromHours(-6));

        Assert.Equal("1k4r22f863ud8abau7qnchhdv5_20261106T183000Z", EventIds.InstanceId("1k4r22f863ud8abau7qnchhdv5_R20251010T173000", start, isAllDay: false));
    }

    [Fact]
    public void InstanceId_SplitAllDaySeries_UsesTheOriginalSeriesId()
    {
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("holiday_20261012", EventIds.InstanceId("holiday_R20260101", start, isAllDay: true));
    }

    [Fact]
    public void InstanceId_AllDay_UsesDateOnly()
    {
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("holiday_20261012", EventIds.InstanceId("holiday", start, isAllDay: true));
    }

    [Fact]
    public void InstanceId_AllDay_UsesLocalDateForPositiveOffset()
    {
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(10));

        Assert.Equal("holiday_20261012", EventIds.InstanceId("holiday", start, isAllDay: true));
    }
}
