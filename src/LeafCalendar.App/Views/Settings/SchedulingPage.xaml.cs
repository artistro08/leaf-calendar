using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Scheduling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Scheduling (scheduling providers spec 9.1): one card per provider in <see cref="SchedulingProviders.All"/>.
/// Signed in, it picks the default link and signs out; when the provider refused its sign-in, it says so.
/// </summary>
/// <remarks>
/// TODO: stopgap until Task R4 moves signing in to Settings › Accounts; the old API key form is gone, so there's no way
/// to connect from here. The hub's <see cref="SchedulingHub.Changed"/> is raised on any thread, so it's marshaled to the
/// UI thread, and it's only listened to while the page is loaded.
/// </remarks>
public sealed partial class SchedulingPage : Page
{
    // Segoe Fluent "DateTime", not used by another Settings nav item
    private const string Glyph = "";

    private readonly List<ProviderCard> _cards = [];
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
            var card = new ProviderCard(this, info);
            _cards.Add(card);
            ProviderList.Children.Add(card.Expander);
        }
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Host.Closed -= OnHostClosed;

    // Settings Closed On This Page (back to the calendar, or the window closed with Leaf staying in the tray): the page
    // may not get Unloaded, so the app-lifetime hub is let go here, or the hub would keep the Settings view alive
    private void OnHostClosed(object? sender, EventArgs e)
    {
        _context.Host.Closed -= OnHostClosed;
        Release();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _context.Services.Scheduling.Changed += OnSchedulingChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Release();

    // Stops following the hub and hides every card's error
    private void Release()
    {
        _context.Services.Scheduling.Changed -= OnSchedulingChanged;
        _loaded = false;
        foreach (var card in _cards)
        {
            card.ShowError(null);
        }
    }

    // Raised on any thread (a sign-in, a sign-out, or the sync finding the sign-in refused)
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

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    // "30 min meeting · 30 min"
    private static string LinkLabel(SchedulingLink link) => string.Create(CultureInfo.CurrentCulture, $"{link.Name} · {link.Minutes} min");

    // =========================================================================
    // PROVIDER CARD
    // =========================================================================

    // One provider's expander: the header names it, and the body holds the error bar, then the signed-in rows (default
    // link, Disconnect)
    private sealed class ProviderCard
    {
        private readonly SchedulingPage _page;
        private readonly SchedulingProviderInfo _info;
        private readonly SettingRow _header;
        private readonly InfoBar _error;
        private readonly StackPanel _connectedRows;
        private readonly ComboBox _links;
        private readonly Button _disconnect;
        private List<SchedulingLink> _shownLinks = [];
        private ProviderState? _state;
        private int _linksGeneration;
        private bool _loading;

        public ProviderCard(SchedulingPage page, SchedulingProviderInfo info)
        {
            _page = page;
            _info = info;
            var id = info.Id;
            var itemRow = StyleOf("LeafExpanderItemRowStyle");

            // Header
            _header = new SettingRow
            {
                Style = StyleOf("LeafExpanderHeaderRowStyle"),
                Glyph = Glyph,
                Header = info.Name,
                Description = info.Description,
            };

            // Error (collapsed while closed: a closed InfoBar still takes its margin)
            _error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Margin = new Thickness(48, 12, 16, 0), Visibility = Visibility.Collapsed };
            AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
            AutomationProperties.SetAutomationId(_error, $"SchedulingError_{id}");

            // Connected Rows (default link, then Disconnect)
            _links = new ComboBox { Width = 240 };
            AutomationProperties.SetName(_links, "Default link");
            AutomationProperties.SetAutomationId(_links, $"SchedulingDefaultLink_{id}");
            _links.SelectionChanged += (_, _) => OnLinkChanged();
            _disconnect = new Button { Content = "Disconnect" };
            AutomationProperties.SetAutomationId(_disconnect, $"SchedulingDisconnect_{id}");
            _disconnect.Click += OnDisconnectClick;
            _connectedRows = new StackPanel
            {
                Children =
                {
                    new SettingRow { Style = itemRow, BorderThickness = new Thickness(0), Header = "Default link", Content = _links },
                    new SettingRow { Style = itemRow, Header = "Disconnect", Content = _disconnect },
                },
            };

            // Card
            Expander = new Expander
            {
                Style = StyleOf("LeafSettingExpanderStyle"),
                Header = _header,
                Content = new StackPanel { Children = { _error, _connectedRows } },
            };
            AutomationProperties.SetName(Expander, info.Name);
            AutomationProperties.SetAutomationId(Expander, $"SchedulingCard_{id}");
            Expander.Collapsed += (_, _) => ShowError(null);
        }

        public Expander Expander { get; }

        private string Unreachable => $"Couldn't reach {_info.Name}. Check your connection and try again.";

        // Shows the provider's state; a change of state (signed in, signed out, or the sign-in refused) opens the card on
        // its links or closes it
        public void Refresh()
        {
            var state = _page._context.Services.Scheduling.StateOf(_info.Id);
            var connected = state == ProviderState.SignedIn;
            var refused = state == ProviderState.NeedsReconnect;

            _header.Description = refused ? $"{_info.Name} stopped accepting your API key. Reconnect to keep sharing booking links." : _info.Description;
            _connectedRows.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

            if (_state is not null && _state != state)
            {
                ShowError(null);
                Expander.IsExpanded = connected;
            }

            _state = state;
            if (connected)
            {
                LoadLinks();
            }
        }

        public void ShowError(string? message)
        {
            _error.Message = message ?? "";
            _error.IsOpen = message is not null;
            _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        // This provider's links in the dropdown, the saved default picked; only the newest read lands
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
                _links.SelectedIndex = _shownLinks.FindIndex(l => l.Id == saved);
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

        // Disconnect: signs out first (a failure there keeps the card signed in), then the settings, then the
        // provider's saved groups become plain groups (off the UI thread) and the calendar reloads them. The button is
        // off meanwhile, so a double click can't run it twice.
        private async void OnDisconnectClick(object sender, RoutedEventArgs e)
        {
            var services = _page._context.Services;
            var id = _info.Id;
            _disconnect.IsEnabled = false;
            try
            {
                services.Scheduling.SignOut(id);
            }
            catch (Exception ex)
            {
                // TODO: the spec has no text for a key that couldn't be removed from this PC; the closest error shows until the owner picks one
                services.Log.Info("scheduling.settings.disconnect.failed", $"provider={id} error={ex.GetType().Name}");
                ShowError(Unreachable);
                _disconnect.IsEnabled = true;
                return;
            }

            _page._context.Save(s => s.WithoutProvider(id));
            try
            {
                await Task.Run(() =>
                {
                    using var conn = services.Database.Open();
                    ShareGroupStore.ClearProvider(conn, id);
                });
                _page._context.Calendar.ReloadGroups();
            }
            catch (Exception ex)
            {
                services.Log.Info("scheduling.settings.groups.clear.failed", $"provider={id} error={ex.GetType().Name}");
            }
            finally
            {
                _disconnect.IsEnabled = true;
            }
        }
    }
}
