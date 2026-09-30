using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>Settings › Accounts: the Google accounts (add, disconnect), the default calendar, Sync now, and the OAuth client.</summary>
public sealed partial class AccountsPage : Page
{
    SettingsContext _context = null!;

    // The Default Calendar Choices, Parallel To The Combo Box Items (index 0 is "your main Google calendar" = null)
    readonly List<CalendarRef?> _refs = [];

    // True while the combo box is being filled (its change event is ignored)
    bool _loading;

    /// <summary>Creates the page.</summary>
    public AccountsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <summary>x:Bind helper: failures show as errors, progress as information.</summary>
    public static InfoBarSeverity SeverityFor(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    /// <summary>Page view model (the window's, see <see cref="SettingsWindow.Accounts"/>).</summary>
    public AccountsViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context  = (SettingsContext)e.Parameter;
        ViewModel = _context.Window.Accounts;

        // Nothing Running: a finished action's message is stale by now
        if (!ViewModel.IsBusy)
        {
            ViewModel.Message = null;
            ViewModel.IsError = false;
            ViewModel.Refresh();
        }

        Bindings.Update();
        _context.Window.CalendarsChanged += OnCalendarsChanged;
        LoadDefaultCalendar();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Window.CalendarsChanged -= OnCalendarsChanged;

    void OnCalendarsChanged(object? sender, EventArgs e) => LoadDefaultCalendar();

    // Fill The Default Calendar Choices: your main Google calendar, then every calendar you can write to
    void LoadDefaultCalendar()
    {
        var calendar = _context.Calendar;
        var several  = calendar.AccountEmails.Count > 1;
        var chosen   = calendar.Settings.DefaultCalendar;
        var writable = calendar.Calendars
            .Where(c => c.AccessRole is "owner" or "writer" && calendar.AccountEmails.ContainsKey(c.AccountId))
            .ToList();

        _loading = true;
        _refs.Clear();
        DefaultCalendarBox.Items.Clear();

        DefaultCalendarBox.Items.Add("Your main Google calendar");
        _refs.Add(null);
        foreach (var c in writable)
        {
            DefaultCalendarBox.Items.Add(several ? $"{c.Summary} ({calendar.AccountEmails[c.AccountId]})" : c.Summary);
            _refs.Add(new CalendarRef(c.AccountId, c.Id));
        }

        // A stored default that is gone or read-only shows as the main calendar, matching DefaultCalendar.Pick
        var index = _refs.FindIndex(r => r is not null && r == chosen);
        DefaultCalendarBox.SelectedIndex = Math.Max(index, 0);
        _loading = false;
    }

    void OnDefaultCalendarChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DefaultCalendarBox.SelectedIndex >= 0)
        {
            var picked = _refs[DefaultCalendarBox.SelectedIndex];
            _context.Calendar.Update(s => s with { DefaultCalendar = picked });
        }
    }

    void OnChangeClientClick(object sender, RoutedEventArgs e) => _context.Window.ShowClientSetup();

    // Disconnect deletes local data (and any edits Google doesn't have yet), so confirm first
    async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string accountId })
        {
            return;
        }

        // Unsent Changes (async void: a database error here would otherwise terminate the process)
        int unsent;
        try
        {
            unsent = ViewModel.UnsentFor(accountId);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("account.disconnect.failed", ex);
            return;
        }

        var content ="Leaf will sign out of this Google account and remove its calendars from this PC. Your Google Calendar isn't changed.";
        if (unsent > 0)
        {
            content += unsent == 1
                ? "\n\n1 change you made here hasn't reached Google yet, and it will be lost."
                : string.Create(CultureInfo.GetCultureInfo("en-US"), $"\n\n{unsent} changes you made here haven't reached Google yet, and they will be lost.");
        }

        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            RequestedTheme    = ActualTheme,
            Title             = "Disconnect this account?",
            Content           = content,
            PrimaryButtonText = "Disconnect",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };

        // async void: anything that escapes here would terminate the process
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DisconnectAsync(accountId);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("account.disconnect.failed", ex);
        }
    }
}
