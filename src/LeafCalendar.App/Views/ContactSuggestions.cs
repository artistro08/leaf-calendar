using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// Google contact suggestions for a box you type a person into (the people picker's and the share panel's guest box):
/// they wait until typing pauses, come from the same search as the people picker
/// (<see cref="CalendarViewModel.SearchPeopleAsync"/>), and each row shows "Name &lt;email&gt;" (or the address alone).
/// Only the newest search lands, and a result for text no longer in the box is dropped.
/// </summary>
internal sealed class ContactSuggestions : IDisposable
{
    private readonly AutoSuggestBox _box;
    private readonly CalendarViewModel _vm;
    private readonly DispatcherQueueTimer _timer;
    private readonly LatestSearch<ContactResults> _search = new();
    private readonly List<(ContactSuggestion View, Contact Person)> _shown = [];

    /// <summary>Suggests contacts in <paramref name="box"/> as you type in it.</summary>
    public ContactSuggestions(AutoSuggestBox box, CalendarViewModel vm)
    {
        _box = box;
        _vm = vm;

        // Suggestions Wait Until Typing Pauses
        _timer = box.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => vm.Fire(SuggestAsync, "people.suggest.failed");
        box.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _timer.Stop();
                _timer.Start();
            }
        };
    }

    /// <summary>The contact behind a suggestion the box raised (found by reference in the list shown), or null.</summary>
    public Contact? Find(object? suggestion) =>
        suggestion is null ? null : _shown.Find(s => ReferenceEquals(s.View, suggestion)).Person;

    /// <summary>Stops waiting for a pause in typing (a search already running still lands).</summary>
    public void StopWaiting() => _timer.Stop();

    /// <summary>Drops a search waiting for a pause or still running, so its suggestions never show.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _search.Cancel();
    }

    /// <summary>Cancels any search.</summary>
    public void Dispose() => Cancel();

    private async Task SuggestAsync()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            _search.Cancel();
            return;
        }

        // A result for text that's no longer in the box (a person was picked, or the box changed) is dropped
        if (await _search.RunAsync(ct => _vm.SearchPeopleAsync(text, ct)) is not { } results || _box.Text.Trim() != text)
        {
            return;
        }

        _shown.Clear();
        _shown.AddRange(results.Contacts.Select(c => (new ContactSuggestion(c.Name, c.Email), c)));
        _box.ItemsSource = _shown.Select(s => s.View).ToList();
        _box.IsSuggestionListOpen = _shown.Count > 0;
    }
}
