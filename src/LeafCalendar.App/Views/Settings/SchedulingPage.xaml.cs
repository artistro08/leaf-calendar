using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.Core.Scheduling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Scheduling (scheduling providers spec 9.1): one card per signed-in provider, picking its default link and
/// which of its links Leaf Calendar shows. Signing in and out lives in Settings › Accounts; the Settings list only shows
/// this page while a provider is signed in (or needs reconnecting).
/// </summary>
/// <remarks>
/// The hub's <see cref="SchedulingHub.Changed"/> is raised on any thread, so it's marshaled to the UI thread, and it's
/// only listened to while the page is loaded (and let go when Settings closes on this page).
/// </remarks>
public sealed partial class SchedulingPage : Page
{
    // Segoe Fluent "DateTime", the Scheduling nav item's icon
    private const string Glyph = "";

    private readonly List<LinkCard> _cards = [];
    private SettingsContext _context = null!;
    private bool _loaded;

    /// <summary>Creates the page.</summary>
    public SchedulingPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Host.Closed += OnHostClosed;
        foreach (var info in SchedulingProviders.All)
        {
            var card = new LinkCard(this, info);
            _cards.Add(card);
            ProviderList.Children.Add(card.Row);
        }

        ShowNote();
    }

    // The note as saved (the box shows \r line breaks, the setting keeps \r\n like the share message's)
    private void ShowNote() => NoteBox.Text = _context.Calendar.Settings.BookingLinkNote.Replace("\r\n", "\r", StringComparison.Ordinal);

    // Losing focus saves it, the way every Settings change saves at once
    private void OnNoteLostFocus(object sender, RoutedEventArgs e) => SaveNote();

    // Leaving the page or Settings saves it too, so typing that never lost focus isn't lost; only a change is saved
    private void SaveNote()
    {
        var note = NoteBox.Text.Replace("\r", "\r\n", StringComparison.Ordinal);
        if (note != _context.Calendar.Settings.BookingLinkNote)
        {
            _context.Save(s => s with { BookingLinkNote = note });
        }
    }

    private void OnNoteReset(object sender, RoutedEventArgs e)
    {
        _context.Save(s => s with { BookingLinkNote = ShareLinks.DefaultNote });
        ShowNote();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Host.Closed -= OnHostClosed;

    // Settings Closed On This Page (back to the calendar, or the window closed with Leaf Calendar staying in the tray): the page
    // may not get Unloaded, so the app-lifetime hub is let go here, or the hub would keep the Settings view alive
    private void OnHostClosed(object? sender, EventArgs e)
    {
        _context.Host.Closed -= OnHostClosed;
        SaveNote();
        Release();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _context.Services.Scheduling.Changed += OnSchedulingChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SaveNote();
        Release();
    }

    private void Release()
    {
        _context.Services.Scheduling.Changed -= OnSchedulingChanged;
        _loaded = false;
    }

    // Raised on any thread (a sign-in, a sign-out, or a provider signing Leaf Calendar out)
    private void OnSchedulingChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        if (!_loaded)
        {
            return;
        }

        foreach (var card in _cards)
        {
            card.Refresh();
        }
    }

    // "30 min meeting · 30 min"
    private static string LinkLabel(SchedulingLink link) => string.Create(CultureInfo.CurrentCulture, $"{link.Name} · {link.Minutes} min");

    // =========================================================================
    // LINK CARD
    // =========================================================================

    // One provider's card, shown while it's signed in (or needs reconnecting, when its links can't load): an expander
    // whose header picks the default link and whose rows switch each link on or off (every link, hidden ones on the
    // provider's profile too). Only links switched on are in the dropdown
    private sealed class LinkCard
    {
        private readonly SchedulingPage _page;
        private readonly SchedulingProviderInfo _info;
        private readonly ComboBox _links;
        private readonly StackPanel _linkRows = new();
        private List<SchedulingLink> _allLinks = [];
        private List<SchedulingLink> _shownLinks = [];
        private int _linksGeneration;
        private bool _loading;

        public LinkCard(SchedulingPage page, SchedulingProviderInfo info)
        {
            _page = page;
            _info = info;
            _links = new ComboBox { Width = 240 };
            AutomationProperties.SetName(_links, "Default link");
            AutomationProperties.SetAutomationId(_links, $"SchedulingDefaultLink_{info.Id}");
            _links.SelectionChanged += (_, _) => OnLinkChanged();

            var header = new SettingRow { Style = (Style)Application.Current.Resources["LeafExpanderHeaderRowStyle"], Glyph = Glyph, Header = "Default link", Description = info.Name, Content = _links };
            Row = new Expander { Style = (Style)Application.Current.Resources["LeafSettingExpanderStyle"], Header = header, Content = _linkRows, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(Row, info.Name);
            AutomationProperties.SetAutomationId(Row, $"SchedulingCard_{info.Id}");
        }

        public Expander Row { get; }

        public void Refresh()
        {
            var state = _page._context.Services.Scheduling.StateOf(_info.Id);
            var shown = state is ProviderState.SignedIn or ProviderState.NeedsReconnect;
            Row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown)
            {
                LoadLinks();
            }
        }

        // This provider's links in the dropdown, the saved default picked (the first when none is saved, as the share
        // panel does); only the newest read lands
        private async void LoadLinks()
        {
            var generation = ++_linksGeneration;
            try
            {
                var links = await _page._context.Services.Scheduling.GetLinksAsync(CancellationToken.None);
                if (generation != _linksGeneration || !_page._loaded)
                {
                    return;
                }

                _allLinks = [.. links.Where(l => l.ProviderId == _info.Id)];
                ShowLinkRows();
                ShowDefaultLinks();
            }
            catch (Exception ex)
            {
                _page._context.Services.Log.Info("scheduling.settings.links.failed", $"provider={_info.Id} error={ex.GetType().Name}");
            }
        }

        // The dropdown: the links switched on, the saved default picked (the first when it's missing or switched off)
        private void ShowDefaultLinks()
        {
            var settings = _page._context.Calendar.Settings;
            var saved = settings.ProviderSettingsFor(_info.Id).DefaultLinkId;
            _shownLinks = [.. settings.ShownLinks(_allLinks)];
            _loading = true;
            try
            {
                _links.ItemsSource = _shownLinks.Select(LinkLabel).ToList();
                _links.SelectedIndex = _shownLinks.Count == 0 ? -1 : Math.Max(_shownLinks.FindIndex(l => l.Id == saved), 0);
            }
            finally
            {
                _loading = false;
            }
        }

        // One row per link with its switch, the way Settings › Calendars switches calendars
        private void ShowLinkRows()
        {
            var off = _page._context.Calendar.Settings.ProviderSettingsFor(_info.Id).OffLinkIds;
            var rowStyle = (Style)Application.Current.Resources["LeafExpanderItemRowStyle"];
            var switchStyle = (Style)Application.Current.Resources["LeafCardToggleSwitchStyle"];
            _linkRows.Children.Clear();
            foreach (var link in _allLinks)
            {
                var toggle = new ToggleSwitch { Style = switchStyle, IsOn = !off.Contains(link.Id, StringComparer.Ordinal) };
                AutomationProperties.SetName(toggle, $"Show {link.Name}");
                AutomationProperties.SetAutomationId(toggle, $"SchedulingLinkShown_{_info.Id}_{link.Id}");
                var linkId = link.Id;
                toggle.Toggled += (_, _) => OnLinkToggled(linkId, toggle.IsOn);

                var row = new SettingRow { Style = rowStyle, Header = LinkLabel(link), Content = toggle };
                if (_linkRows.Children.Count == 0)
                {
                    row.BorderThickness = new Thickness(0);
                }

                _linkRows.Children.Add(row);
            }
        }

        // Saves the link as switched on or off, then lists the links switched on in the dropdown. The default link
        // switched off stops being the default, so the first link shown is (as the dropdown then shows)
        private void OnLinkToggled(string linkId, bool isOn)
        {
            var id = _info.Id;
            _page._context.Save(s =>
            {
                var saved = s.ProviderSettingsFor(id);
                List<string> off = [.. saved.OffLinkIds.Where(l => l != linkId)];
                if (!isOn)
                {
                    off.Add(linkId);
                }

                var defaultLinkId = !isOn && saved.DefaultLinkId == linkId ? "" : saved.DefaultLinkId;
                return s.WithProvider(saved with { DefaultLinkId = defaultLinkId, OffLinkIds = off });
            });
            ShowDefaultLinks();
        }

        private void OnLinkChanged()
        {
            var index = _links.SelectedIndex;
            if (_loading || index < 0 || index >= _shownLinks.Count)
            {
                return;
            }

            var id = _info.Id;
            var linkId = _shownLinks[index].Id;
            _page._context.Save(s => s.WithProvider(s.ProviderSettingsFor(id) with { DefaultLinkId = linkId }));
        }
    }
}
