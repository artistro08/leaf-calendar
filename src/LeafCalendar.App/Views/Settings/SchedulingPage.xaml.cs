using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.Core.Scheduling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Scheduling (scheduling providers spec 9.1): one Default link card per signed-in provider. Signing in and out
/// lives in Settings › Accounts; the Settings list only shows this page while a provider is signed in (or needs
/// reconnecting).
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

    // One provider's Default link card, shown while it's signed in (or needs reconnecting, when its links can't load)
    private sealed class LinkCard
    {
        private readonly SchedulingPage _page;
        private readonly SchedulingProviderInfo _info;
        private readonly ComboBox _links;
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

            Row = new SettingRow { Glyph = Glyph, Header = "Default link", Description = info.Name, Content = _links, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(Row, info.Name);
            AutomationProperties.SetAutomationId(Row, $"SchedulingCard_{info.Id}");
        }

        public SettingRow Row { get; }

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

                var saved = _page._context.Calendar.Settings.ProviderSettingsFor(_info.Id).DefaultLinkId;
                _shownLinks = [.. links.Where(l => l.ProviderId == _info.Id)];
                _loading = true;
                _links.ItemsSource = _shownLinks.Select(LinkLabel).ToList();
                _links.SelectedIndex = _shownLinks.Count == 0 ? -1 : Math.Max(_shownLinks.FindIndex(l => l.Id == saved), 0);
            }
            catch (Exception ex)
            {
                _page._context.Services.Log.Info("scheduling.settings.links.failed", $"provider={_info.Id} error={ex.GetType().Name}");
            }
            finally
            {
                _loading = false;
            }
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
