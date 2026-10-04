// Track B (Milestone 5 Tasks 7-8) owns this file: the people overlay, meet with, the participant overlay, and share availability.
using System.ComponentModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using Microsoft.UI.Xaml;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    private OverlayBar? _overlayBar;
    private ShareSlotsPanel? _slotsPanel;

    // Whether the details panel was open when sharing started (it opens for the picked times and goes back after)
    private bool? _detailsBeforeSharing;

    // Called once when the page opens: the overlay bar, the share panel (in the details pane), and the view model events
    // they follow
    private void AttachPeople()
    {
        // At the bottom with the other toasts, above the share hint and the notice
        _overlayBar = new OverlayBar();
        Toasts.Children.Insert(0, _overlayBar);
        Float(_overlayBar.Card);
        _overlayBar.Update(ViewModel);

        _slotsPanel = new ShareSlotsPanel { Visibility = Visibility.Collapsed };
        DetailsPane.Children.Add(_slotsPanel);
        ShowSharing();

        ViewModel.ShareChanged += OnShareChanged;
        ViewModel.OverlayChanged += OnOverlayChanged;
        ViewModel.LayoutChanged += OnOverlayChanged;
        ViewModel.PropertyChanged += OnPeoplePropertyChanged;
        Sidebar.ShareAvailabilityRequested += OnShareAvailabilityRequested;
    }

    // Called from Detach: undo everything AttachPeople wired to the long-lived view model
    private void DetachPeople()
    {
        ViewModel.ShareChanged -= OnShareChanged;
        ViewModel.OverlayChanged -= OnOverlayChanged;
        ViewModel.LayoutChanged -= OnOverlayChanged;
        ViewModel.PropertyChanged -= OnPeoplePropertyChanged;
        Sidebar.ShareAvailabilityRequested -= OnShareAvailabilityRequested;

        // Sharing Opened The Details Panel For Itself, So What's Saved Goes Back To How It Was (a reopened window or the next
        // launch starts from that, and sharing opens the panel again)
        if (_detailsBeforeSharing is { } before)
        {
            _detailsBeforeSharing = null;
            if (ViewModel.Settings.DetailsPanelOpen != before)
            {
                ViewModel.Remember(s => s with { DetailsPanelOpen = before });
            }
        }

        if (_overlayBar is not null)
        {
            Toasts.Children.Remove(_overlayBar);
            _overlayBar = null;
        }

        if (_slotsPanel is not null)
        {
            DetailsPane.Children.Remove(_slotsPanel);
            _slotsPanel = null;
        }
    }

    private void OnOverlayChanged(object? sender, EventArgs e) => _overlayBar?.Update(ViewModel);

    private void OnShareChanged(object? sender, EventArgs e) => ShowSharing();

    // While sharing: the share panel in the right pane in place of the details (the pane opens for it and goes back to how it
    // was when sharing stops)
    private void ShowSharing()
    {
        _slotsPanel?.Update(ViewModel);
        var sharing = ViewModel.IsSharing;

        // The Hint Toast Stays Up While You Mark Times (the events behind it are faded and lined)
        SharingHint.IsOpen = sharing;
        if (_slotsPanel is null || (_slotsPanel.Visibility == Visibility.Visible) == sharing)
        {
            return;
        }

        // Started Or Stopped: the grid takes or drops the scheduling look itself (TimeGridView.OnShareChanged redraws its days)
        _slotsPanel.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = sharing ? Visibility.Collapsed : Visibility.Visible;
        if (sharing)
        {
            _detailsBeforeSharing = IsDetailsOpen;
            SetDetailsOpen(true, animate: true);
        }
        else if (_detailsBeforeSharing is { } before)
        {
            _detailsBeforeSharing = null;
            SetDetailsOpen(before, animate: true);
        }
    }

    private void OnShareAvailabilityRequested(object? sender, EventArgs e) => StartShareAvailability();

    private void OnPeoplePropertyChanged(object? sender, PropertyChangedEventArgs e)
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
    private void ShowPeopleOverlay() => PickPeople("Show busy times", "Show", meetWith: false);

    // F
    private void ShowMeetWith() => PickPeople("Meet with", "Find a time", meetWith: true);

    private void PickPeople(string title, string primaryText, bool meetWith) =>
        ViewModel.Fire(async () =>
        {
            if (await PeoplePickerDialog.ShowAsync(this, ViewModel, title, primaryText) is { Count: > 0 } picked)
            {
                await ViewModel.ShowOverlayAsync(picked, meetWith);
            }
        }, "people.pick.failed");

    // E then F: the selected event's guests, without you and without rooms
    private void ShowParticipantOverlay()
    {
        if (ViewModel.SelectedInfo is not { } info)
        {
            ViewModel.ShowMessage("Select one event");
            return;
        }

        var mine = ViewModel.AccountEmails.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
    private void StartShareAvailability()
    {
        if (ViewModel.IsSharing)
        {
            ViewModel.StopSharing();
            return;
        }

        ViewModel.StartSharing();
    }
}
