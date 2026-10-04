// Track A owns this file: command-menu search and the jump-back state.
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Search;
using LeafCalendar.Core.Views;

namespace LeafCalendar.App.ViewModels;

public sealed partial class CalendarViewModel
{
    // The period a command-menu jump landed on (Back shows until the calendar moves elsewhere)
    private DateOnly? _jumpedTo;

    /// <summary>True right after a command-menu jump, until you go back or move elsewhere (spec 6.2 item 2).</summary>
    [ObservableProperty]
    public partial bool ShowBack { get; set; }

    // The events read for the menu's search (null until read, or after they changed), and the read in progress
    private EventSearch.Index? _searchIndex;
    private Task? _searchIndexBuild;

    // Bumped when the data changes, so a read that started before the change isn't kept
    private int _searchIndexGeneration;

    // Read again after this long, so the rows kept are the ones nearest to now
    private static readonly TimeSpan SearchIndexLife = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Reads the events for the command menu's search ahead of the first keystroke, off the UI thread (nothing to do
    /// while they're read and fresh). The menu calls it as it opens.
    /// </summary>
    public void WarmSearch()
    {
        if (_searchIndexBuild is not null || (_searchIndex is { } index && Now - index.BuiltAt < SearchIndexLife))
        {
            return;
        }

        _searchIndexBuild = BuildSearchIndexAsync();
    }

    private async Task BuildSearchIndexAsync()
    {
        var now = Now;
        var generation = _searchIndexGeneration;
        try
        {
            var index = await Task.Run(() =>
            {
                using var conn = _services.Database.Open();
                return EventSearch.Index.Build(conn, now);
            });

            // The Data Changed During The Read: the next search reads again
            if (generation == _searchIndexGeneration)
            {
                _searchIndex = index;
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            _services.Log.Error("command.index.failed", ex);
        }
        finally
        {
            _searchIndexBuild = null;
        }
    }

    /// <summary>
    /// The events matching <paramref name="query"/>, from the warmed index, on this thread (the time it takes to type the
    /// key); null when the index isn't ready yet (it's started) or holds too many events, so the caller searches with
    /// <see cref="SearchEventsAsync"/> instead. The query is never logged.
    /// </summary>
    public IReadOnlyList<SearchHit>? SearchEventsNow(string query)
    {
        if (_searchIndex is not { } index)
        {
            WarmSearch();
            return null;
        }

        try
        {
            using var conn = _services.Database.Open();
            return index.Find(conn, query, Now, Zone);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            _services.Log.Error("command.search.failed", ex);
            return null;
        }
    }

    /// <summary>Searches stored events off the UI thread; canceling stops the search between rows. The query is never logged.</summary>
    public Task<IReadOnlyList<SearchHit>> SearchEventsAsync(string query, CancellationToken ct)
    {
        var (now, zone) = (Now, Zone);
        return Task.Run(() =>
        {
            using var conn = _services.Database.Open();
            return EventSearch.Find(conn, query, now, zone, ct);
        }, ct);
    }

    /// <summary>
    /// Opens a search result: its details (Enter), or the calendar moved to it as well (Alt+Enter, which shows Back
    /// when the calendar really moved). False when the event is gone (deleted since the search).
    /// </summary>
    public bool OpenSearchHit(SearchHit hit, bool jump)
    {
        CalendarOccurrence? occurrence;
        using (var conn = _services.Database.Open())
        {
            occurrence = OccurrenceLookup.Find(conn, hit.AccountId, hit.CalendarId, hit.EventId, hit.Start, Zone);
        }

        if (occurrence is null)
        {
            ShowMessage("That event isn't in Leaf anymore");
            return false;
        }

        // An Edit Is Open (its panel was hidden): the calendar moves to the event and selects it, keeping the edit for C or E
        if (Editing is not null)
        {
            NavigateTo(DayOf(occurrence));
            ShowSelected(occurrence);
            ScrollToTimeRequested?.Invoke(this, occurrence.Start);
            return true;
        }

        // Details Only: the calendar stays where it is
        if (!jump)
        {
            Select(occurrence);
            DetailsOpenRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // Jump: Reveal's steps, on the event's own day (an all-day event's date, not its UTC midnight's local day)
        var before = PeriodStart;

        // Back Returns To Where You Were, Scrolled There Or Not (scrolling records no history)
        _history.Visit(new ViewPlace(Mode, Settings.CustomDayCount, before));
        NavigateTo(DayOf(occurrence));
        Select(occurrence);
        ScrollToTimeRequested?.Invoke(this, occurrence.Start);
        DetailsOpenRequested?.Invoke(this, EventArgs.Empty);

        // Back Only When There's Somewhere To Go Back To
        if (PeriodStart != before)
        {
            _jumpedTo = PeriodStart;
            ShowBack = true;
        }

        return true;
    }

    /// <summary>Writes an Info line to Leaf's log (IDs, counts, and types only; never content).</summary>
    public void LogInfo(string eventName, string details) => _services.Log.Info(eventName, details);

    /// <summary>The title bar's Back after a jump.</summary>
    public void BackFromJump()
    {
        ShowBack = false;
        _jumpedTo = null;
        GoBack();
    }

    // Any other move hides Back
    partial void OnPeriodStartChanged(DateOnly value)
    {
        if (ShowBack && value != _jumpedTo)
        {
            ShowBack = false;
            _jumpedTo = null;
        }
    }
}
