// Track B (Milestone 5 Tasks 7-8) owns this file: the people overlay, meet with, the participant overlay, and share availability.
using System.ComponentModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    OverlayBar? _overlayBar;
    ShareBar? _shareBar;

    // Called once when the page opens: the overlay and share bars, and the view model events they follow
    void AttachPeople()
    {
        _overlayBar = new OverlayBar();
        IslandBars.Children.Add(_overlayBar);
        _overlayBar.Update(ViewModel);
        _shareBar = new ShareBar();
        IslandBars.Children.Add(_shareBar);
        _shareBar.Update(ViewModel);

        ViewModel.ShareChanged             += OnShareChanged;
        ViewModel.OverlayChanged           += OnOverlayChanged;
        ViewModel.LayoutChanged            += OnOverlayChanged;
        ViewModel.PropertyChanged          += OnPeoplePropertyChanged;
        Sidebar.ShareAvailabilityRequested += OnShareAvailabilityRequested;
    }

    // Called from Detach: undo everything AttachPeople wired to the long-lived view model
    void DetachPeople()
    {
        ViewModel.ShareChanged             -= OnShareChanged;
        ViewModel.OverlayChanged           -= OnOverlayChanged;
        ViewModel.LayoutChanged            -= OnOverlayChanged;
        ViewModel.PropertyChanged          -= OnPeoplePropertyChanged;
        Sidebar.ShareAvailabilityRequested -= OnShareAvailabilityRequested;

        if (_overlayBar is not null)
        {
            IslandBars.Children.Remove(_overlayBar);
            _overlayBar = null;
        }

        if (_shareBar is not null)
        {
            IslandBars.Children.Remove(_shareBar);
            _shareBar = null;
        }
    }

    void OnOverlayChanged(object? sender, EventArgs e) => _overlayBar?.Update(ViewModel);

    void OnShareChanged(object? sender, EventArgs e) => _shareBar?.Update(ViewModel);

    void OnShareAvailabilityRequested(object? sender, EventArgs e) => StartShareAvailability();

    void OnPeoplePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Paging Loads Busy Times For The New Days
        if (e.PropertyName == nameof(CalendarViewModel.PeriodStart) && ViewModel.OverlayPeople.Count > 0)
        {
            ViewModel.Fire(ViewModel.RefreshOverlayAsync, "freebusy.refresh.failed");
            return;
        }

        // Meet With: every way of starting a new event (drag, double-click, C, the month and all-day rows) invites them
        if (e.PropertyName == nameof(CalendarViewModel.Editing) && ViewModel.IsMeetWith && ViewModel.Editing is { IsNew: true })
        {
            ViewModel.AddOverlayGuests();
        }
    }

    // P
    void ShowPeopleOverlay() => PickPeople("Overlay a teammate", "Show", meetWith: false);

    // F
    void ShowMeetWith() => PickPeople("Meet with", "Find a time", meetWith: true);

    void PickPeople(string title, string primaryText, bool meetWith) =>
        ViewModel.Fire(async () =>
        {
            if (await PeoplePickerDialog.ShowAsync(XamlRoot, ViewModel, title, primaryText) is { Count: > 0 } picked)
            {
                await ViewModel.ShowOverlayAsync(picked, meetWith);
            }
        }, "people.pick.failed");

    // E then F: the selected event's guests, without you and without rooms
    void ShowParticipantOverlay()
    {
        if (ViewModel.SelectedInfo is not { } info)
        {
            ViewModel.ShowMessage("Select one event");
            return;
        }

        var mine   = ViewModel.AccountEmails.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var guests = info.Draft.Guests
            .Select(g => g.Email.Trim())
            .Where(e => CalendarViewModel.IsAddress(e) && !mine.Contains(e) && !e.EndsWith("@resource.calendar.google.com", StringComparison.OrdinalIgnoreCase))
            .Select(e => new Contact("", e))
            .ToList();

        if (guests.Count == 0)
        {
            ViewModel.ShowMessage("This event has no other guests");
            return;
        }

        ViewModel.Fire(() => ViewModel.ShowOverlayAsync(guests, meetWith: false), "people.overlay.failed");
    }

    // S, the sidebar's share button, and the command menu: S again (or Cancel) stops
    void StartShareAvailability()
    {
        if (ViewModel.IsSharing)
        {
            ViewModel.StopSharing();
            return;
        }

        ViewModel.StartSharing();
    }
}
