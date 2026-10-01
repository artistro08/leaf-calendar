using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// Right panel, on the window's Mica like the sidebar. With nothing selected it lists upcoming events (next 8 hours)
/// with Join buttons. With an event selected it shows the details, the Join button, your reply, the guests,
/// and the description. Event content is plain text; description links are clickable only when the allowlist
/// allows them, and every click goes through <see cref="LeafServices.LaunchAsync"/>.
/// </summary>
public sealed partial class DetailsPanel : UserControl
{
    CalendarViewModel? _vm;
    string? _shownKey;
    bool _placingJoinMenu;

    /// <summary>Creates the panel.</summary>
    public DetailsPanel()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(ContentScroll);
    }

    /// <summary>x:Bind helper: a brush for a hex color.</summary>
    public static SolidColorBrush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>x:Bind helper: shown when true.</summary>
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Connects to the view model.</summary>
    public void Attach(CalendarViewModel vm)
    {
        _vm = vm;
        UpcomingList.ItemsSource = vm.Upcoming;
        vm.Upcoming.CollectionChanged += OnUpcomingChanged;
        vm.PropertyChanged            += OnViewModelPropertyChanged;
        UpdateUpcomingEmpty();
        ShowCurrent();
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Upcoming.CollectionChanged -= OnUpcomingChanged;
        _vm.PropertyChanged            -= OnViewModelPropertyChanged;
        EditorView?.Detach();
        _vm = null;
    }

    void OnUpcomingChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateUpcomingEmpty();

    void UpdateUpcomingEmpty() =>
        UpcomingEmpty.Visibility = _vm?.Upcoming.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.SelectedInfo) or nameof(CalendarViewModel.Editing) or nameof(CalendarViewModel.Selection))
        {
            ShowCurrent();
        }
    }

    // The editor wins while it's open; otherwise the selection summary (several events), the selected event, else the upcoming list
    void ShowCurrent()
    {
        if (_vm?.Editing is { } editing)
        {
            // The editor is created the first time it's needed (FindName fills the EditorView field)
            if (EditorView is null)
            {
                FindName(nameof(EditorView));
            }

            var view = EditorView!;
            ContentScroll.Visibility = Visibility.Collapsed;
            view.Visibility          = Visibility.Visible;

            // A new editor starts at the top (a refresh behind an open editor keeps the scroll position)
            if (!ReferenceEquals(view.Editor, editing))
            {
                _shownKey = null;
                view.Attach(_vm, editing);
            }

            return;
        }

        // Several Events Selected
        ContentScroll.Visibility = Visibility.Visible;
        var count = _vm?.Selection.Count ?? 0;
        SelectionView.Visibility = Visible(count > 1);
        if (count > 1)
        {
            EditorView?.Detach();
            EditorView?.Visibility  = Visibility.Collapsed;
            UpcomingView.Visibility = Visibility.Collapsed;
            DetailsView.Visibility  = Visibility.Collapsed;
            SelectionSummary.Text   = string.Create(CultureInfo.InvariantCulture, $"{count} events selected");
            return;
        }

        if (EditorView is not null)
        {
            EditorView.Detach();
            EditorView.Visibility = Visibility.Collapsed;
        }

        Show(_vm?.SelectedInfo);
    }

    void Show(SelectedEventInfo? info)
    {
        UpcomingView.Visibility = Visible(info is null);
        DetailsView.Visibility  = Visible(info is not null);

        // Back To The Top For Another Event (a refresh of the same event keeps the scroll position and note)
        if (info?.Occurrence.Key != _shownKey)
        {
            _shownKey = info?.Occurrence.Key;
            ScrollIndicator.Hide(ContentScroll);
            ContentScroll.ChangeView(null, 0, null, true);
            RsvpNote.Text = "";
        }

        if (info is null)
        {
            return;
        }

        var d = info.Details;
        TitleText.Text    = d.Title;
        WhenText.Text     = info.When;
        CalendarText.Text = info.CalendarName;
        StatusText.Text   = info.StatusText;
        CalendarDot.Fill  = LeafBrushes.FromHex(info.CalendarColor);

        // Join (it shows where it really goes)
        var call = d.ConferenceUri is { } uri ? LinkSafety.DisplayForm(uri) ?? "" : "";
        JoinButton.Visibility   = Visible(d.ConferenceUri is not null);
        JoinButton.Content      = JoinLabel(d.ConferenceUri);
        ToolTipService.SetToolTip(JoinButton, $"Join (Ctrl+J)\n{call}");

        // Location And Call
        LocationText.Text         = d.Location ?? "";
        LocationRow.Visibility    = Visible(d.Location is { Length: > 0 });
        ConferenceText.Text       = $"Video call: {call}";
        ConferenceText.Visibility = Visible(d.ConferenceUri is not null);
        ToolTipService.SetToolTip(MapsLink, d.Location is { Length: > 0 } location ? LinkSafety.DisplayForm(LinkSafety.MapsSearch(location)) : null);

        // Your Reply
        RsvpRow.Visibility = Visible(info.CanRespond);
        ShowResponse(d.SelfResponse);

        // Guests
        var guests = info.Draft.Guests;
        var mailto = CalendarViewModel.GuestsMailto(info);
        EmailGuestsLink.Visibility = Visible(mailto is not null);
        ToolTipService.SetToolTip(EmailGuestsLink, mailto is null ? null : $"Email guests (E then E)\n{LinkSafety.DisplayForm(mailto)}");
        GuestsRow.Visibility  = Visible(guests.Count > 0);
        GuestsText.Text       = guests.Count == 1 ? "1 guest" : string.Create(CultureInfo.InvariantCulture, $"{guests.Count} guests");
        GuestList.ItemsSource = guests.Select(g => new GuestItem(g.Email, GuestDetail(g))).ToList();

        RenderDescription(info.DescriptionRuns);
    }

    // SplitButton opens its menu left-aligned whatever Placement says; show it again right-aligned (ShowAt on an open
    // flyout just moves it)
    void OnJoinMenuOpened(object? sender, object e)
    {
        if (_placingJoinMenu)
        {
            return;
        }

        _placingJoinMenu = true;
        JoinMenu.ShowAt(JoinButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        _placingJoinMenu = false;
    }

    // What the Join button joins, from the link's host (Core's provider detection)
    static string JoinLabel(Uri? link) => (link is null ? null : LinkSafety.ProviderOf(link)) switch
    {
        MeetingProvider.GoogleMeet => "Join Google Meet",
        MeetingProvider.Zoom       => "Join Zoom meeting",
        MeetingProvider.Teams      => "Join Microsoft Teams meeting",
        MeetingProvider.Webex      => "Join Webex meeting",
        MeetingProvider.Around     => "Join Around meeting",
        MeetingProvider.Whereby    => "Join Whereby meeting",
        MeetingProvider.BlueJeans  => "Join BlueJeans meeting",
        MeetingProvider.DoxyMe     => "Join doxy.me call",
        _                          => "Join meeting",
    };

    // Styled runs as native text. Links never get a NavigateUri from event content; a click goes through the allowlist.
    void RenderDescription(IReadOnlyList<DescriptionRun> runs)
    {
        DescriptionBlock.Blocks.Clear();
        DescriptionBlock.Visibility = Visible(runs.Count > 0);

        var paragraph = new Paragraph();
        foreach (var run in runs)
        {
            var lines = run.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                if (lines[i].Length > 0)
                {
                    paragraph.Inlines.Add(Styled(run, lines[i]));
                }
            }
        }

        DescriptionBlock.Blocks.Add(paragraph);
    }

    Inline Styled(DescriptionRun run, string text)
    {
        Inline inline = new Run { Text = text };

        // The link text can say anything, so hovering (or a screen reader) shows the real address; no ASCII form, no link
        var target = run.Link is { } shown ? LinkSafety.DisplayForm(shown) : null;
        if (run.Link is { } link && target is not null)
        {
            var hyperlink = new Hyperlink();
            hyperlink.Inlines.Add(inline);
            hyperlink.Click += (_, _) => Act(vm => vm.OpenLinkAsync(link), "details.link.failed");
            ToolTipService.SetToolTip(hyperlink, target);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(hyperlink, target);
            inline = hyperlink;
        }

        if (run.Underline && target is null)
        {
            inline = Wrap(new Underline(), inline);
        }

        if (run.Italic)
        {
            inline = Wrap(new Italic(), inline);
        }

        if (run.Bold)
        {
            inline = Wrap(new Bold(), inline);
        }

        return inline;
    }

    static Span Wrap(Span span, Inline inner)
    {
        span.Inlines.Add(inner);
        return span;
    }

    void ShowResponse(ResponseStatus response)
    {
        ResponseText.Text   = ResponseLine(response);
        RsvpYes.IsChecked   = response == ResponseStatus.Accepted;
        RsvpMaybe.IsChecked = response == ResponseStatus.Tentative;
        RsvpNo.IsChecked    = response == ResponseStatus.Declined;
    }

    static string ResponseLine(ResponseStatus response) => response switch
    {
        ResponseStatus.Accepted  => "Your response: Going",
        ResponseStatus.Tentative => "Your response: Maybe",
        ResponseStatus.Declined  => "Your response: Not going",
        _                        => "Your response: Not answered yet",
    };

    static string GuestDetail(Guest guest)
    {
        var parts = new List<string>();
        if (guest.IsOrganizer)
        {
            parts.Add("Organizer");
        }

        parts.Add(guest.Response switch
        {
            ResponseStatus.Accepted  => "Going",
            ResponseStatus.Tentative => "Maybe",
            ResponseStatus.Declined  => "Not going",
            _                        => "Not answered",
        });

        if (guest.Optional)
        {
            parts.Add("Optional");
        }

        if (guest.Comment is { Length: > 0 } comment)
        {
            parts.Add($"“{comment}”");
        }

        return string.Join(" · ", parts);
    }

    // =========================================================================
    // ACTIONS
    // =========================================================================

    void OnDeleteClick(object sender, RoutedEventArgs e) => Act(vm => vm.DeleteAsync([.. vm.Selection], sendUpdates: true), "details.delete.failed");

    void OnJoinClick(SplitButton sender, SplitButtonClickEventArgs e) => Act(vm => vm.JoinAsync(vm.SelectedInfo?.Occurrence), "details.join.failed");

    void OnCopyMeetingLinkClick(object sender, RoutedEventArgs e) => _vm?.CopyMeetingLink();

    void OnMapsClick(object sender, RoutedEventArgs e) => Act(vm => vm.OpenLocationAsync(), "details.maps.failed");

    void OnEmailGuestsClick(object sender, RoutedEventArgs e) => Act(vm => vm.EmailGuestsAsync(), "details.email.failed");

    void OnRsvpYesClick(object sender, RoutedEventArgs e) => Reply(ResponseStatus.Accepted);

    void OnRsvpMaybeClick(object sender, RoutedEventArgs e) => Reply(ResponseStatus.Tentative);

    void OnRsvpNoClick(object sender, RoutedEventArgs e) => Reply(ResponseStatus.Declined);

    // A toggle flips itself on click; show the stored reply until the new one is saved and the event reloads
    void Reply(ResponseStatus response)
    {
        if (_vm?.SelectedInfo is { } info)
        {
            ShowResponse(info.Details.SelfResponse);
        }

        Act(
            async vm =>
            {
                // The note went out with the reply, so the box is cleared for the next one
                if (await vm.RespondAsync(response, RsvpNote.Text, RsvpEmail.IsChecked == true))
                {
                    RsvpNote.Text = "";
                }
            },
            "details.respond.failed");
    }

    // The view model runs the work and logs a failure under the given name, so nothing escapes into the dispatcher
    void Act(Func<CalendarViewModel, Task> work, string eventName)
    {
        if (_vm is { } vm)
        {
            vm.Fire(() => work(vm), eventName);
        }
    }
}
