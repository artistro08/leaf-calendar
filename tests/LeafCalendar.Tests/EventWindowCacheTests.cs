using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public sealed class EventWindowCacheTests : IDisposable
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly List<DateOnly> _loads = [];
    readonly List<CalendarOccurrence> _data = [];
    readonly EventWindowCache _cache;

    public EventWindowCacheTests() =>
        _cache = new EventWindowCache(
            (from, to, _) =>
            {
                _loads.Add(from);
                var f = OccurrenceQuery.LocalMidnight(from, Zone);
                var t = OccurrenceQuery.LocalMidnight(to, Zone);
                return Task.FromResult<IReadOnlyList<CalendarOccurrence>>(_data.Where(o => o.Start < t && o.End > f).ToList());
            },
            Zone);

    public void Dispose() => _cache.Dispose();

    static DateOnly D(int year, int month, int day) => new(year, month, day);

    static CalendarOccurrence Timed(string id, DateTimeOffset start, DateTimeOffset end) =>
        new("a", "c", id, null, null, start, end, false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static CalendarOccurrence AllDay(string id, DateOnly start, int days)
    {
        var s = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new("a", "c", id, null, null, s, s.AddDays(days), true, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);
    }

    [Fact]
    public async Task EnsureAsync_OneWeek_LoadsSevenMonthsNearestFirst()
    {
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), TestContext.Current.CancellationToken);

        Assert.Equal(7, _loads.Count);
        Assert.Equal(D(2026, 10, 1), _loads[0]);
        Assert.Equal(Enumerable.Range(-3, 7).Select(m => D(2026, 10, 1).AddMonths(m)).Order(), _cache.LoadedMonths.Order());
    }

    [Fact]
    public async Task EnsureAsync_SameRangeAgain_LoadsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), ct);
        _loads.Clear();

        await _cache.EnsureAsync(D(2026, 10, 18), D(2026, 10, 25), ct);

        Assert.Empty(_loads);
    }

    [Fact]
    public async Task EnsureAsync_NextMonth_LoadsOneAndEvictsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), ct);
        _loads.Clear();

        await _cache.EnsureAsync(D(2026, 11, 1), D(2026, 11, 8), ct);

        Assert.Equal([D(2027, 2, 1)], _loads);
        Assert.DoesNotContain(D(2026, 7, 1), _cache.LoadedMonths);
    }

    [Fact]
    public async Task ForDay_OvernightTimedEvent_OnBothLocalDays()
    {
        _data.Add(Timed("night", new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 10, 2, 2, 0, 0, TimeSpan.FromHours(-4))));

        await _cache.EnsureAsync(D(2026, 10, 1), D(2026, 10, 8), TestContext.Current.CancellationToken);

        Assert.Single(_cache.ForDay(D(2026, 10, 1)));
        Assert.Single(_cache.ForDay(D(2026, 10, 2)));
        Assert.Empty(_cache.ForDay(D(2026, 10, 3)));
    }

    [Fact]
    public async Task ForDay_EventEndingAtMidnight_NotOnNextDay()
    {
        _data.Add(Timed("eve", new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.FromHours(-4))));

        await _cache.EnsureAsync(D(2026, 10, 1), D(2026, 10, 8), TestContext.Current.CancellationToken);

        Assert.Empty(_cache.ForDay(D(2026, 10, 2)));
    }

    [Fact]
    public async Task ForDay_MultiDayAllDayAcrossMonthBoundary_EachDayOnce()
    {
        _data.Add(AllDay("trip", D(2026, 10, 30), 4));

        await _cache.EnsureAsync(D(2026, 10, 25), D(2026, 11, 1), TestContext.Current.CancellationToken);

        foreach (var day in new[] { D(2026, 10, 30), D(2026, 10, 31), D(2026, 11, 1), D(2026, 11, 2) })
        {
            Assert.Single(_cache.ForDay(day));
        }

        Assert.Empty(_cache.ForDay(D(2026, 11, 3)));
    }

    [Fact]
    public async Task RefreshAsync_ReloadsEveryLoadedMonthAndRaisesChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), ct);
        _loads.Clear();
        var changed = 0;
        _cache.Changed += (_, _) => changed++;
        _data.Add(Timed("new", new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(-4))));

        await _cache.RefreshAsync(ct);

        Assert.Equal(7, _loads.Count);
        Assert.Equal(1, changed);
        Assert.Single(_cache.ForDay(D(2026, 10, 5)));
    }
}
