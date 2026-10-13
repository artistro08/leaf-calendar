using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class BookingMatchTests
{
    private static readonly DateTimeOffset Saved = new(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NineCt = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

    private static ShareGroup Group(long id, string link = "101", DateTimeOffset? created = null) =>
        new(id, "", "", "America/Chicago", [new BusyRange(NineCt, NineCt.AddHours(2))], [], "calcom", link, "https://cal.com/x", 30, created ?? Saved);

    private static SchedulingBooking Booking(DateTimeOffset start, DateTimeOffset? created = null) => new("bk", start, created ?? Saved.AddHours(1), "Jane");

    [Fact]
    public void Find_StartOnARangeStart_Matches()
    {
        var result = BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt)]);

        Assert.Equal([1L], Assert.Single(result).GroupIds);
    }

    [Fact]
    public void Find_InBetweenStart_Matches()
    {
        Assert.Single(BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt.AddMinutes(45))]));
    }

    [Fact]
    public void Find_StartAtRangeEnd_DoesNotMatch()
    {
        Assert.Empty(BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt.AddHours(2))]));
    }

    [Fact]
    public void Find_OtherLink_DoesNotMatch()
    {
        Assert.Empty(BookingMatch.Find([Group(1, link: "102")], "calcom", "101", [Booking(NineCt)]));
    }

    [Fact]
    public void Find_BookedBeforeTheGroupWasSaved_DoesNotMatch()
    {
        Assert.Empty(BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt, created: Saved.AddMinutes(-1))]));
    }

    [Fact]
    public void Find_TwoGroupsSameRange_BothGo()
    {
        var result = BookingMatch.Find([Group(1), Group(2)], "calcom", "101", [Booking(NineCt.AddMinutes(30))]);

        Assert.Equal([1L, 2L], Assert.Single(result).GroupIds);
    }

    [Fact]
    public void Find_TwoBookingsOneGroup_GroupReportedOnce()
    {
        var result = BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt), Booking(NineCt.AddMinutes(30))]);

        Assert.Equal([1L], Assert.Single(result).GroupIds);
    }
}
