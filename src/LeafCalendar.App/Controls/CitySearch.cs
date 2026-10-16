using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Places;
using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Google Maps cities for a time zone search box, while city suggestions are on in Settings: searches wait until typing
/// pauses and only the newest answer is handed back. The searches and the one lookup of a picked city's time zone share a
/// session, so Google bills the pick and not each search.
/// </summary>
internal sealed class CitySearch
{
    /// <summary>The row that ends a list of cities, as Google requires (picking it does nothing).</summary>
    public const string Attribution = "Google Maps";

    private readonly CalendarViewModel _vm;
    private readonly DispatcherQueueTimer _timer;
    private string _text = "";
    private Action<IReadOnlyList<City>>? _show;
    private string? _session;

    // Counts searches so an older answer can't replace a newer one (or land after Cancel)
    private int _searches;

    /// <summary>Searches through <paramref name="vm"/>, pausing on <paramref name="queue"/>.</summary>
    public CitySearch(DispatcherQueue queue, CalendarViewModel vm)
    {
        _vm = vm;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _vm.Fire(SearchAsync, "places.cities.suggest.failed");
    }

    /// <summary>True when city suggestions are on (both switches, and a Maps API key saved).</summary>
    public bool IsOn => _vm.CitySuggestionsOn;

    /// <summary>A city's row: "Austin, TX, USA", or the name alone.</summary>
    public static string Row(City city)
    {
        ArgumentNullException.ThrowIfNull(city);

        return city.Region.Length > 0 ? $"{city.Name}, {city.Region}" : city.Name;
    }

    /// <summary>Searches <paramref name="text"/> once typing pauses, handing the cities (none when Google has none or fails) to <paramref name="show"/>.</summary>
    public void Typed(string text, Action<IReadOnlyList<City>> show)
    {
        _text = text.Trim();
        _show = show;
        _searches++;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Drops a search waiting for a pause or still running, so its cities never show.</summary>
    public void Cancel()
    {
        _searches++;
        _timer.Stop();
    }

    /// <summary>The picked city's IANA time zone, or null; it ends the session, so the next search starts a new one.</summary>
    public async Task<string?> ZoneOfAsync(City city)
    {
        Cancel();
        var session = _session ?? Guid.NewGuid().ToString();
        _session = null;

        return await _vm.CityTimeZoneAsync(city, session, CancellationToken.None);
    }

    private async Task SearchAsync()
    {
        var mine = _searches;
        var text = _text;
        var show = _show;
        _session ??= Guid.NewGuid().ToString();

        var cities = text.Length == 0 ? [] : await _vm.SearchCitiesAsync(text, _session, CancellationToken.None);
        if (mine == _searches)
        {
            show?.Invoke(cities);
        }
    }
}
