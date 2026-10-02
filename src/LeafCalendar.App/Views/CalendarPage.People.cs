// Track B (Milestone 5 Tasks 7-8) owns this file: the people overlay, meet with, the participant overlay, and share availability.
using System.ComponentModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    OverlayBar? _overlayBar;
    ShareSlotsPanel? _slotsPanel;

    // Whether the details panel was open when sharing started (it opens for the picked times and goes back after)
    bool? _detailsBeforeSharing;

    // Called once when the page opens: the overlay bar, the share panel (in the details pane), and the view model events
    // they follow
    void AttachPeople()
    {
        _overlayBar = new OverlayBar();
        IslandBars.Children.Add(_overlayBar);
        _overlayBar.Update(ViewModel);

        _slotsPanel = new ShareSlotsPanel { Visibility = Visibility.Collapsed };
        DetailsPane.Children.Add(_slotsPanel);
        ShowSharing();

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


        if (_slotsPanel is not null)
        {
            DetailsPane.Children.Remove(_slotsPanel);
            _slotsPanel = null;
        }
    }

    void OnOverlayChanged(object? sender, EventArgs e) => _overlayBar?.Update(ViewModel);

    void OnShareChanged(object? sender, EventArgs e) => ShowSharing();

    // While sharing: the share panel in the right pane in place of the details (the pane opens for it and goes back to how it
    // was when sharing stops)
    void ShowSharing()
    {
        _slotsPanel?.Update(ViewModel);
        var sharing = ViewModel.IsSharing;

        // The Hint Toast Stays Up While You Mark Times (the events behind it are faded and lined)
        SharingHint.IsOpen = sharing;
        if (_slotsPanel is null || (_slotsPanel.Visibility == Visibility.Visible) == sharing)
        {
            return;
        }

        _slotsPanel.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility     = sharing ? Visibility.Collapsed : Visibility.Visible;
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
            if (await PeoplePickerDialog.ShowAsync(this, ViewModel, title, primaryText) is { Count: > 0 } picked)
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

    // True while the "Stop scheduling?" question is up (a second Esc in the meantime is the dialog's own)
    bool _confirmingStop;

    // Esc while scheduling: warns before the picked times are dropped; Keep scheduling (or Esc again) goes back to picking
    void ConfirmStopSharing()
    {
        if (_confirmingStop)
        {
            return;
        }

        _confirmingStop = true;
        var picked = ViewModel.ShareSlots.Count;
        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            RequestedTheme    = ActualTheme,
            Title             = "Stop scheduling?",
            Content           = picked == 0
                ? "You'll leave scheduling."
                : picked == 1
                    ? "You'll leave scheduling, and the time you picked won't be kept."
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You'll leave scheduling, and the {picked} times you picked won't be kept."),
            PrimaryButtonText = "Stop scheduling",
            CloseButtonText   = "Keep scheduling",
            DefaultButton     = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "StopSchedulingDialog");

        ViewModel.Fire(async () =>
        {
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && ViewModel.IsSharing)
                {
                    ViewModel.StopSharing();
                }
            }
            finally
            {
                _confirmingStop = false;
            }
        }, "share.stop.failed");
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
