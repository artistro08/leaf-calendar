using LeafCalendar.Core.Data;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SunsamaStoreTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private static readonly DateOnly Day = new(2026, 10, 16);

    public void Dispose() => _db.Dispose();

    private static SunsamaBlock Block(DateOnly day, string task, SunsamaBlockKind kind = SunsamaBlockKind.Projected) =>
        new(day, task, "Title ' \"; DROP", "Channel 💖", new DateTimeOffset(2026, 10, 16, 15, 50, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 16, 30, 0, TimeSpan.Zero),
            120, 84, false, kind, kind == SunsamaBlockKind.Matched ? "acct" : null, kind == SunsamaBlockKind.Matched ? "cal" : null, kind == SunsamaBlockKind.Matched ? "ev" : null);

    [Fact]
    public void ReplaceDay_ThenRange_RoundTripsEveryField()
    {
        using var conn = _db.Database.Open();
        var saved = new[]
        {
            Block(Day, "t1"),
            Block(Day, "t2", SunsamaBlockKind.Matched),
            Block(Day, "t3", SunsamaBlockKind.Timebox) with { EstimateMinutes = null, TrackedMinutes = null, Completed = true },
        };

        SunsamaStore.ReplaceDay(conn, null, Day, saved);

        Assert.Equal(saved, SunsamaStore.Range(conn, Day, Day.AddDays(1)));
    }

    [Fact]
    public void ReplaceDay_SecondTime_ReplacesOnlyThatDay()
    {
        using var conn = _db.Database.Open();
        SunsamaStore.ReplaceDay(conn, null, Day, [Block(Day, "old")]);
        SunsamaStore.ReplaceDay(conn, null, Day.AddDays(1), [Block(Day.AddDays(1), "other")]);

        SunsamaStore.ReplaceDay(conn, null, Day, [Block(Day, "new")]);

        Assert.Equal(["new", "other"], SunsamaStore.Range(conn, Day, Day.AddDays(2)).Select(b => b.TaskId));
    }

    [Fact]
    public void Range_EndIsExclusive()
    {
        using var conn = _db.Database.Open();
        SunsamaStore.ReplaceDay(conn, null, Day.AddDays(1), [Block(Day.AddDays(1), "next")]);

        Assert.Empty(SunsamaStore.Range(conn, Day, Day.AddDays(1)));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        using var conn = _db.Database.Open();
        SunsamaStore.ReplaceDay(conn, null, Day, [Block(Day, "t1")]);

        SunsamaStore.Clear(conn);

        Assert.Empty(SunsamaStore.Range(conn, Day, Day.AddDays(1)));
    }
}
