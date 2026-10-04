namespace LeafCalendar.Core.Events;

/// <summary>
/// Keeps event instances in memory for 3 months before and 3 months after what's on screen.
/// </summary>
/// <remarks>
/// Data is loaded a month at a time through the loader (which should run the database work off
/// the UI thread), nearest month first. <see cref="Changed"/> is raised after each month, so the
/// visible month appears first. Months outside the window are dropped. Calls are serialized; call
/// from the UI thread. Instances are bucketed by local day, so a view asks
/// <see cref="ForDay"/> without touching the database. The same event on several shown calendars is held once
/// (<see cref="SharedEvents"/>), with <see cref="StripesOf"/> giving each calendar's color.
/// </remarks>
/// <param name="load">Loads a month's instances.</param>
/// <param name="zone">The zone that buckets instances into local days.</param>
/// <param name="preference">Ranks the copies of a shared event for which one is drawn (see <see cref="SharedEvents.Merge"/>); null draws the first.</param>
public sealed class EventWindowCache(Func<DateOnly, DateOnly, CancellationToken, Task<IReadOnlyList<CalendarOccurrence>>> load, TimeZoneInfo zone, Func<CalendarOccurrence, int>? preference = null) : IDisposable
{
    /// <summary>Months kept on each side of the visible range.</summary>
    public const int MonthsAround = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<DateOnly, IReadOnlyList<CalendarOccurrence>> _months = [];
    private Dictionary<DateOnly, List<CalendarOccurrence>> _byDay = [];
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _stripes = new Dictionary<string, IReadOnlyList<string>>();
    private IReadOnlyDictionary<string, CalendarOccurrence> _aliases = new Dictionary<string, CalendarOccurrence>();

    /// <summary>Raised on the calling thread whenever the cached data changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The zone that decides which local days an instance touches. After changing it, <see cref="RefreshAsync"/> re-buckets the held months.</summary>
    public TimeZoneInfo Zone { get; set; } = zone;

    /// <summary>First days of the months currently held.</summary>
    public IReadOnlyCollection<DateOnly> LoadedMonths => _months.Keys;

    /// <summary>Makes sure the months around <c>[visibleStart, visibleEnd)</c> are loaded.</summary>
    public async Task EnsureAsync(DateOnly visibleStart, DateOnly visibleEnd, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var first = MonthOf(visibleStart).AddMonths(-MonthsAround);
            var last = MonthOf(visibleEnd.AddDays(-1)).AddMonths(MonthsAround);

            // Evict
            var evicted = false;
            foreach (var month in _months.Keys.Where(m => m < first || m > last).ToList())
            {
                _months.Remove(month);
                evicted = true;
            }

            // Load Missing, Nearest First
            var missing = Months(first, last)
                .Where(m => !_months.ContainsKey(m))
                .OrderBy(m => Math.Abs(m.DayNumber - MonthOf(visibleStart).DayNumber))
                .ToList();

            if (missing.Count == 0)
            {
                if (evicted)
                {
                    Rebuild();
                }

                return;
            }

            foreach (var month in missing)
            {
                _months[month] = await load(month, month.AddMonths(1), ct);
                Rebuild();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reloads every held month (after a sync or a filter change) and swaps them in at once.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var fresh = new Dictionary<DateOnly, IReadOnlyList<CalendarOccurrence>>();
            foreach (var month in _months.Keys.ToList())
            {
                fresh[month] = await load(month, month.AddMonths(1), ct);
            }

            _months = fresh;
            Rebuild();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Instances touching <paramref name="day"/> in local time (empty when not loaded).</summary>
    public IReadOnlyList<CalendarOccurrence> ForDay(DateOnly day) => _byDay.TryGetValue(day, out var list) ? list : [];

    /// <summary>
    /// The accent of every calendar <paramref name="occurrence"/> is shown on, its own first, when it's on more than one;
    /// otherwise empty (the card has its one accent).
    /// </summary>
    public IReadOnlyList<string> StripesOf(CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        return _stripes.TryGetValue(occurrence.Key, out var stripes) ? stripes : [];
    }

    /// <summary>
    /// The occurrence the views draw for <paramref name="occurrence"/>: itself, or, for a copy of a shared event that's
    /// drawn once, the copy drawn instead (so selecting it from search or the upcoming list highlights the card).
    /// </summary>
    public CalendarOccurrence Drawn(CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        return _aliases.TryGetValue(occurrence.Key, out var drawn) ? drawn : occurrence;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private void Rebuild()
    {
        var byDay = new Dictionary<DateOnly, List<CalendarOccurrence>>();
        var merged = SharedEvents.Merge(_months.Values.SelectMany(m => m).DistinctBy(o => o.Key), preference);

        foreach (var occurrence in merged.Shown)
        {
            var (first, last) = occurrence.IsAllDay
                ? (occurrence.AllDayStart, occurrence.AllDayEnd.AddDays(-1))
                : (LocalDate(occurrence.Start), LocalDate(occurrence.End.AddTicks(-1)));

            for (var day = first; day <= (last < first ? first : last); day = day.AddDays(1))
            {
                if (!byDay.TryGetValue(day, out var list))
                {
                    byDay[day] = list = [];
                }

                list.Add(occurrence);
            }
        }

        _byDay = byDay;
        _stripes = merged.Stripes;
        _aliases = merged.Aliases;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    private static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    private static IEnumerable<DateOnly> Months(DateOnly first, DateOnly last)
    {
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            yield return month;
        }
    }
}
