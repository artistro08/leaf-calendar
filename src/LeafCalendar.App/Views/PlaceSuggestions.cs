using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Places;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// Google Maps suggestions for the editor's Location box: they wait until typing pauses, only the newest search lands,
/// and a result for text no longer in the box is dropped. The list ends with "Google Maps", as Google requires; picking
/// that row does nothing. While location suggestions are off no list ever opens, so the box works as a plain text box.
/// </summary>
internal sealed class PlaceSuggestions : IDisposable
{
    private readonly AutoSuggestBox _box;
    private readonly CalendarViewModel _vm;
    private readonly DispatcherQueueTimer _timer;
    private readonly LatestSearch<IReadOnlyList<Place>> _search = new();
    private readonly List<(PlaceSuggestion View, Place? Place)> _shown = [];

    /// <summary>Suggests places in <paramref name="box"/> as you type in it.</summary>
    public PlaceSuggestions(AutoSuggestBox box, CalendarViewModel vm)
    {
        _box = box;
        _vm = vm;

        // Suggestions Wait Until Typing Pauses
        _timer = box.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = false;
        _timer.Tick += OnTick;
        box.TextChanged += OnTextChanged;
    }

    /// <summary>The place behind a suggestion the box raised (found by reference in the list shown), or null (also for the attribution row).</summary>
    public Place? Find(object? suggestion) =>
        suggestion is null ? null : _shown.Find(s => ReferenceEquals(s.View, suggestion)).Place;

    /// <summary>Drops a search waiting for a pause or still running, so its suggestions never show.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _search.Cancel();
    }

    /// <summary>Cancels any search and lets go of the box.</summary>
    public void Dispose()
    {
        Cancel();
        _timer.Tick -= OnTick;
        _box.TextChanged -= OnTextChanged;
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => _vm.Fire(SuggestAsync, "places.suggest.failed");

    private void OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    private async Task SuggestAsync()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            _search.Cancel();
            return;
        }

        // A result for text that's no longer in the box (a place was picked, or the box changed) is dropped
        if (await _search.RunAsync(ct => _vm.SearchPlacesAsync(text, ct)) is not { } places || _box.Text.Trim() != text)
        {
            return;
        }

        // Places, Then Google's Attribution
        _shown.Clear();
        if (places.Count > 0)
        {
            _shown.AddRange(places.Select(p => (new PlaceSuggestion(p.Name, p.Address, false), (Place?)p)));
            _shown.Add((new PlaceSuggestion("Google Maps", "", true), null));
        }

        _box.ItemsSource = _shown.Select(s => s.View).ToList();
        _box.IsSuggestionListOpen = _shown.Count > 0;
    }
}
