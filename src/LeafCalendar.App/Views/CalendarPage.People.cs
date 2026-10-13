// Track B (Milestone 5 Tasks 7-8) owns this file: the people overlay, meet with, the participant overlay, and share availability.
using System.ComponentModel;
using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    private OverlayBar? _overlayBar;
    private ShareSlotsPanel? _slotsPanel;

    // Whether the details panel was open when sharing started (it opens for the picked times and goes back after)
    private bool? _detailsBeforeSharing;

    // The scheduling messages on screen, by text, each with the timer that fades it out on its own
    private readonly Dictionary<string, ShareCard> _shareCards = new(StringComparer.Ordinal);

    // The view model's ShareMessagesSaid when the cards were last shown (what's newer was just said)
    private int _shareSaidShown;

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
        ViewModel.ShareMessagesChanged += OnShareMessagesChanged;
        _shareSaidShown = ViewModel.ShareMessagesSaid;
        ShowShareMessages();
        ViewModel.OverlayChanged += OnOverlayChanged;
        ViewModel.LayoutChanged += OnOverlayChanged;
        ViewModel.PropertyChanged += OnPeoplePropertyChanged;
        Sidebar.ShareAvailabilityRequested += OnShareAvailabilityRequested;
    }

    // Called from Detach: undo everything AttachPeople wired to the long-lived view model
    private void DetachPeople()
    {
        ViewModel.ShareChanged -= OnShareChanged;
        ViewModel.ShareMessagesChanged -= OnShareMessagesChanged;
        foreach (var card in _shareCards.Values)
        {
            card.Timer.Stop();
            card.Fading?.Stop();
        }

        _shareCards.Clear();
        ShareMessagesPanel.Children.Clear();
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

    // A message came or went (it can be raised off the UI thread)
    private void OnShareMessagesChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ShowShareMessages();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ShowShareMessages);
        }
    }

    // The scheduling messages over the calendar, newest on top: one raised card each (the other toasts' look). Cards are
    // added, moved and removed one by one, so a screen reader reads each new card once and never the ones already there.
    // A new card fades in, then fades out on its own after the notice's time and leaves the view model's list; a message
    // said again while shown starts its time over
    private void ShowShareMessages()
    {
        var messages = ViewModel.ShareMessages;
        foreach (var text in _shareCards.Keys.Where(t => !messages.Contains(t)).ToList())
        {
            var gone = _shareCards[text];
            gone.Timer.Stop();
            gone.Fading?.Stop();
            ShareMessagesPanel.Children.Remove(gone.Card);
            _shareCards.Remove(text);
        }

        // (the newest that many were just said; cards already shown for them start over)
        var said = Math.Min(ViewModel.ShareMessagesSaid - _shareSaidShown, messages.Count);
        _shareSaidShown = ViewModel.ShareMessagesSaid;

        // (collapsed while empty, so it adds no gap between the other toasts)
        ShareMessagesPanel.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < messages.Count; i++)
        {
            var text = messages[i];
            if (!_shareCards.TryGetValue(text, out var card))
            {
                card = new ShareCard(NewShareCard(text), NewShareTimer(text));
                _shareCards[text] = card;
                ShareMessagesPanel.Children.Insert(i, card.Card);
                card.Timer.Start();
            }
            else
            {
                if (i < said)
                {
                    card.Fading?.Stop();
                    card.Fading = null;
                    card.Card.Opacity = 1;
                    card.Timer.Stop();
                    card.Timer.Start();
                }

                var at = ShareMessagesPanel.Children.IndexOf(card.Card);
                if (at != i)
                {
                    ShareMessagesPanel.Children.RemoveAt(at);
                    ShareMessagesPanel.Children.Insert(i, card.Card);
                }
            }

            AutomationProperties.SetAutomationId(card.Card, string.Create(CultureInfo.InvariantCulture, $"ShareMessage_{i}"));
        }
    }

    // One message's card: raised like the other toasts, fading in (167 ms) where the system animates, and read out by a
    // screen reader the first time it's on screen only (a card moved up the stack loads again)
    private InfoBar NewShareCard(string text)
    {
        var card = new InfoBar { Style = (Style)Resources["LeafShareMessageStyle"], Message = text, IsOpen = true };
        AutomationProperties.SetName(card, text);
        Float(card);
        void Announce(object sender, RoutedEventArgs e)
        {
            card.Loaded -= Announce;
            FrameworkElementAutomationPeer.CreatePeerForElement(card)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        card.Loaded += Announce;
        if (Animates())
        {
            card.Opacity = 0;
            Fade(card, to: 1, new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        return card;
    }

    // After the notice's time the card fades out (167 ms, linear), then leaves the view model's list (unless it was said
    // again meanwhile, which cancels the fade)
    private DispatcherQueueTimer NewShareTimer(string text)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = NoticeTime;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_shareCards.TryGetValue(text, out var card) || !Animates())
            {
                ViewModel.DismissShareMessage(text);
                return;
            }

            var fading = Fade(card.Card, to: 0, easing: null);
            card.Fading = fading;
            fading.Completed += (_, _) =>
            {
                if (ReferenceEquals(card.Fading, fading))
                {
                    ViewModel.DismissShareMessage(text);
                }
            };
        };
        return timer;
    }

    private static Storyboard Fade(UIElement card, double to, EasingFunctionBase? easing)
    {
        var fade = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(167), EasingFunction = easing };
        Storyboard.SetTarget(fade, card);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        var story = new Storyboard { Children = { fade } };
        story.Begin();
        return story;
    }

    private static bool Animates() => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    // A scheduling message's card, its timer, and its fade-out while one runs
    private sealed class ShareCard(InfoBar card, DispatcherQueueTimer timer)
    {
        public InfoBar Card { get; } = card;

        public DispatcherQueueTimer Timer { get; } = timer;

        public Storyboard? Fading { get; set; }
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

    // S, the sidebar's share button, and the command menu: S again (like Close) stops, throwing away anything not saved
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
