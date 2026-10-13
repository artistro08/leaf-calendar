using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Scheduling (scheduling providers spec 9.1): one card per provider in <see cref="SchedulingProviders.All"/>.
/// Not connected, the card offers Connect… and opens a form drawn from the provider's setup fields; connected, it picks
/// the default link and disconnects; when the provider refused its key, it says so and offers Reconnect….
/// </summary>
/// <remarks>
/// A secret typed into the form never stays in its box: it's cleared when a connect attempt ends, when the card closes,
/// and when the page unloads. A connect still running when you leave the page finishes (its key was proved, so it's
/// kept). The hub's <see cref="SchedulingHub.Changed"/> is raised on any thread, so it's marshaled to
/// the UI thread, and it's only listened to while the page is loaded.
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
        foreach (var info in SchedulingProviders.All)
        {
            var card = new ProviderCard(this, info);
            _cards.Add(card);
            ProviderList.Children.Add(card.Expander);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _context.Services.Scheduling.Changed += OnSchedulingChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _context.Services.Scheduling.Changed -= OnSchedulingChanged;
        _loaded = false;
        foreach (var card in _cards)
        {
            card.ClearForm();
        }
    }

    // Raised on any thread (a connect, a disconnect, or the sync finding the key refused)
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

    // One box of the connect form (a password box for a secret, else a text box)
    private sealed record FieldBox(SetupField Field, PasswordBox? Password, TextBox? Text)
    {
        public Control Box => (Control?)Password ?? Text!;

        public string Value => Password?.Password ?? Text?.Text ?? "";
    }

    // =========================================================================
    // PROVIDER CARD
    // =========================================================================

    // One provider's expander: the header names it (Connect… or Reconnect… on the right), and the body holds the error
    // bar, then either the connected rows (default link, Disconnect) or the connect form
    private sealed class ProviderCard
    {
        private readonly SchedulingPage _page;
        private readonly SchedulingProviderInfo _info;
        private readonly SettingRow _header;
        private readonly Button _connect;
        private readonly InfoBar _error;
        private readonly StackPanel _connectedRows;
        private readonly ComboBox _links;
        private readonly StackPanel _form;
        private readonly List<FieldBox> _boxes = [];
        private readonly Button _submit;
        private readonly Button _cancel;
        private readonly ProgressRing _progress;
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
            _connect = new Button { Content = "Connect…" };
            AutomationProperties.SetAutomationId(_connect, $"SchedulingConnect_{id}");
            _header = new SettingRow
            {
                Style = StyleOf("LeafExpanderHeaderRowStyle"),
                Glyph = Glyph,
                Header = info.Name,
                Description = info.Description,
                Content = _connect,
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
            var disconnect = new Button { Content = "Disconnect" };
            AutomationProperties.SetAutomationId(disconnect, $"SchedulingDisconnect_{id}");
            disconnect.Click += OnDisconnectClick;
            _connectedRows = new StackPanel
            {
                Children =
                {
                    new SettingRow { Style = itemRow, BorderThickness = new Thickness(0), Header = "Default link", Content = _links },
                    new SettingRow { Style = itemRow, Header = "Disconnect", Content = disconnect },
                },
            };

            // Connect Form (one box per setup field, then Connect and Cancel at the right like a dialog's buttons)
            _form = new StackPanel { Padding = new Thickness(48, 12, 16, 16), Spacing = 12 };
            foreach (var field in info.Fields)
            {
                var box = field.Secret
                    ? new FieldBox(field, new PasswordBox { Header = field.Label, PlaceholderText = field.Placeholder }, null)
                    : new FieldBox(field, null, new TextBox { Header = field.Label, PlaceholderText = field.Placeholder });
                AutomationProperties.SetName(box.Box, field.Label);
                AutomationProperties.SetAutomationId(box.Box, $"SchedulingField_{id}_{field.Key}");
                _boxes.Add(box);
                _form.Children.Add(box.Box);
            }

            _progress = new ProgressRing { Width = 20, Height = 20, IsActive = false };
            AutomationProperties.SetAutomationId(_progress, $"SchedulingProgress_{id}");
            _submit = new Button { Style = StyleOf("AccentButtonStyle"), Content = "Connect" };
            AutomationProperties.SetAutomationId(_submit, $"SchedulingSubmit_{id}");
            _submit.Click += OnSubmitClick;
            _cancel = new Button { Content = "Cancel" };
            AutomationProperties.SetAutomationId(_cancel, $"SchedulingCancel_{id}");
            _form.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { _progress, _submit, _cancel },
            });

            // Card (Connect… opens it on its form; closing it clears the form, so Cancel and the header's chevron both
            // forget what was typed)
            Expander = new Expander
            {
                Style = StyleOf("LeafSettingExpanderStyle"),
                Header = _header,
                Content = new StackPanel { Children = { _error, _connectedRows, _form } },
            };
            AutomationProperties.SetName(Expander, info.Name);
            AutomationProperties.SetAutomationId(Expander, $"SchedulingCard_{id}");
            Expander.Collapsed += (_, _) => ClearForm();
            _connect.Click += (_, _) => Expander.IsExpanded = true;
            _cancel.Click += (_, _) => Expander.IsExpanded = false;
        }

        public Expander Expander { get; }

        private string Unreachable => $"Couldn't reach {_info.Name}. Check your connection and try again.";

        // Shows the provider's state; a change of state (connected, disconnected, or the key refused) opens the card on
        // its links or closes it
        public void Refresh()
        {
            var state = _page._context.Services.Scheduling.StateOf(_info.Id);
            var connected = state == ProviderState.Connected;
            var refused = state == ProviderState.NeedsReconnect;

            _header.Description = refused ? $"{_info.Name} stopped accepting your API key. Reconnect to keep sharing booking links." : _info.Description;
            _connect.Content = refused ? "Reconnect…" : "Connect…";
            _connect.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            _connectedRows.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            _form.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

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

        // Empties every box and hides the error (the card closed, or the page unloaded)
        public void ClearForm()
        {
            foreach (var box in _boxes)
            {
                if (box.Password is not null)
                {
                    box.Password.Password = "";
                }

                if (box.Text is not null)
                {
                    box.Text.Text = "";
                }
            }

            ShowError(null);
        }

        // The secret boxes only, after a connect attempt (other values stay so a mistake can be fixed)
        private void ClearSecrets()
        {
            foreach (var box in _boxes)
            {
                if (box.Password is not null)
                {
                    box.Password.Password = "";
                }
            }
        }

        private void ShowError(string? message)
        {
            _error.Message = message ?? "";
            _error.IsOpen = message is not null;
            _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            foreach (var box in _boxes)
            {
                box.Box.IsEnabled = !busy;
            }

            _submit.IsEnabled = !busy;
            _cancel.IsEnabled = !busy;
            _connect.IsEnabled = !busy;
            _progress.IsActive = busy;
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

        // Connect: the values are checked first; a refused key or an unreachable provider saves nothing. The secret box is
        // cleared when the attempt ends, whatever happened.
        private async void OnSubmitClick(object sender, RoutedEventArgs e)
        {
            var services = _page._context.Services;
            var values = _boxes.ToDictionary(b => b.Field.Key, b => b.Value, StringComparer.Ordinal);
            try
            {
                if (SchedulingHub.Check(_info, values) is { } problem)
                {
                    ShowError(problem);
                    return;
                }

                ShowError(null);
                SetBusy(true);
                var links = await services.Scheduling.ConnectAsync(_info, values, CancellationToken.None);
                var settings = new ProviderSettings(_info.Id, (values.GetValueOrDefault("server") ?? "").Trim(), links.Count > 0 ? links[0].Id : "");
                _page._context.Save(s => s.WithProvider(settings));
                ClearForm();
                Refresh();
            }
            catch (SchedulingAuthException)
            {
                ShowError(_info.Fields.FirstOrDefault(f => f.Secret)?.InvalidMessage ?? Unreachable);
            }
            catch (SchedulingUnavailableException)
            {
                ShowError(Unreachable);
            }
            catch (Exception ex)
            {
                services.Log.Info("scheduling.settings.connect.failed", $"provider={_info.Id} error={ex.GetType().Name}");
                ShowError(Unreachable);
            }
            finally
            {
                ClearSecrets();
                SetBusy(false);
            }
        }

        // Disconnect: the secrets go first (a failure there keeps the card connected), then the settings, then the
        // provider's saved groups become plain groups (off the UI thread) and the calendar reloads them
        private async void OnDisconnectClick(object sender, RoutedEventArgs e)
        {
            var services = _page._context.Services;
            var id = _info.Id;
            try
            {
                services.Scheduling.Disconnect(id);
            }
            catch (Exception ex)
            {
                // TODO: the spec has no text for a key that couldn't be removed from this PC; the closest error shows until the owner picks one
                services.Log.Info("scheduling.settings.disconnect.failed", $"provider={id} error={ex.GetType().Name}");
                ShowError(Unreachable);
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
        }
    }
}
