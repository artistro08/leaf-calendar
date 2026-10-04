using System.Collections.Specialized;
using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>A choice in the default calendar dropdown: the calendar's name, and under it the account's email (when there's more than one account).</summary>
public sealed record DefaultCalendarChoice(string Name, string? Email)
{
    /// <summary>x:Bind helper: the email line shows.</summary>
    public bool HasEmail => Email is not null;

    /// <summary>The name (what Narrator and UI Automation read for the item).</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Settings › Accounts: an expander per Google account (Meet by default and Disconnect inside), adding one, the main
/// account, the default calendar, Sync now, and the OAuth client.
/// </summary>
public sealed partial class AccountsPage : Page
{
    SettingsContext _context = null!;

    // The Default Calendar Choices, Parallel To The Combo Box Items (index 0 is "your main Google calendar" = null)
    readonly List<CalendarRef?> _refs = [];

    // The Combo Box Choices As Last Filled (an unchanged list isn't refilled, so a sync never closes an open dropdown)
    List<DefaultCalendarChoice> _choices = [];

    // The Main Account Choices, Parallel To Its Combo Box Items
    readonly List<string> _accountIds = [];

    // The Account Expanders On Screen
    List<AccountSettingsRow> _accountRows = [];

    // True while a rebuild after an account list change waits for the dispatcher (a refresh changes the list once per account)
    bool _accountsPending;

    // True while a combo box is being filled (its change event is ignored)
    bool _loading;

    /// <summary>Creates the page.</summary>
    public AccountsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <summary>x:Bind helper: failures show as errors, a finished sign-in as success, progress as information.</summary>
    public static InfoBarSeverity SeverityFor(AccountsMessageKind kind) => kind switch
    {
        AccountsMessageKind.Error   => InfoBarSeverity.Error,
        AccountsMessageKind.Success => InfoBarSeverity.Success,
        _                           => InfoBarSeverity.Informational,
    };

    /// <summary>x:Bind helper: Narrator interrupts for failures and waits its turn for progress and success.</summary>
    public static AutomationLiveSetting LiveFor(AccountsMessageKind kind) => kind == AccountsMessageKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    /// <summary>Page view model (the window's, see <see cref="SettingsPage.Accounts"/>).</summary>
    public AccountsViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context  = (SettingsContext)e.Parameter;
        ViewModel = _context.Host.Accounts;

        // Nothing Running: a finished action's message is stale by now
        if (!ViewModel.IsBusy)
        {
            ViewModel.ClearMessage();
            ViewModel.Refresh();
        }

        Bindings.Update();
        _context.Host.CalendarsChanged += OnCalendarsChanged;
        ViewModel.Accounts.CollectionChanged += OnAccountsChanged;
        LoadDefaultCalendar();
        LoadAccountChoices();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Host.CalendarsChanged     -= OnCalendarsChanged;
        ViewModel.Accounts.CollectionChanged -= OnAccountsChanged;
    }

    // An add, sync, or disconnect refreshed the accounts: rebuild once, after the refresh is done
    void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_accountsPending)
        {
            return;
        }

        _accountsPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _accountsPending = false;
            LoadAccountChoices();
        });
    }

    void OnCalendarsChanged(object? sender, EventArgs e)
    {
        LoadDefaultCalendar();
        LoadAccountChoices();
    }

    // =========================================================================
    // MAIN ACCOUNT AND MEET BY DEFAULT
    // =========================================================================

    // The main account (the first account when none is set; off with only one) and an expander per account
    void LoadAccountChoices()
    {
        var settings = _context.Calendar.Settings;
        var accounts = ViewModel.Accounts.ToList();

        // Same Accounts As Shown: only the pick follows the settings (a refill would close an open dropdown, and rebuilt
        // expanders would send keyboard and Narrator focus back to the top of the page)
        var unchanged = _accountRows.Count == accounts.Count && _accountRows.Zip(accounts).All(p =>
            p.First.AccountId == p.Second.Id && p.First.Email == p.Second.Email && p.First.Summary == p.Second.Summary
            && p.First.IsOn == settings.MeetByDefaultAccounts.Contains(p.Second.Id));

        _loading = true;
        if (!unchanged)
        {
            _accountIds.Clear();
            _accountIds.AddRange(accounts.Select(a => a.Id));
            MainAccountBox.Items.Clear();
            foreach (var account in accounts)
            {
                MainAccountBox.Items.Add(account.Email);
            }
        }

        MainAccountBox.SelectedIndex = accounts.Count == 0 ? -1 : Math.Max(_accountIds.IndexOf(settings.MainAccountId ?? ""), 0);
        MainAccountBox.IsEnabled     = accounts.Count > 1;
        _loading = false;

        if (unchanged)
        {
            return;
        }

        // The Account Expanders (rebuilt with fresh counts; the open ones stay open)
        var expanded = _accountRows.Where(r => r.IsExpanded).Select(r => r.AccountId).ToHashSet(StringComparer.Ordinal);
        _accountRows            = ViewModel.AccountRows(settings.MeetByDefaultAccounts, expanded, OnMeetToggled);
        AccountList.ItemsSource = _accountRows;
    }

    void OnMainAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && MainAccountBox.SelectedIndex >= 0)
        {
            var id = _accountIds[MainAccountBox.SelectedIndex];
            _context.Save(s => s with { MainAccountId = id });
        }
    }

    void OnMeetToggled(string accountId, bool on) =>
        _context.Save(s => s with { MeetByDefaultAccounts = on ? [.. s.MeetByDefaultAccounts.Append(accountId).Distinct()] : [.. s.MeetByDefaultAccounts.Where(a => a != accountId)] });

    // Fill The Default Calendar Choices: your main Google calendar, then every calendar you can write to
    void LoadDefaultCalendar()
    {
        var calendar = _context.Calendar;
        var several  = calendar.AccountEmails.Count > 1;
        var chosen   = calendar.Settings.DefaultCalendar;
        var writable = calendar.Calendars
            .Where(c => c.AccessRole is "owner" or "writer" && calendar.AccountEmails.ContainsKey(c.AccountId))
            .ToList();

        List<CalendarRef?>          refs    = [null, .. writable.Select(c => new CalendarRef(c.AccountId, c.Id))];
        List<DefaultCalendarChoice> choices = [new("Your main Google calendar", null), .. writable.Select(c => new DefaultCalendarChoice(c.Summary, several ? calendar.AccountEmails[c.AccountId] : null))];

        // Same Choices As Shown: leave the combo box alone
        if (refs.SequenceEqual(_refs) && choices.SequenceEqual(_choices))
        {
            return;
        }

        _loading = true;
        _refs.Clear();
        _refs.AddRange(refs);
        _choices                       = choices;
        DefaultCalendarBox.ItemsSource = choices;

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
            _context.Save(s => s with { DefaultCalendar = picked });
        }
    }

    void OnChangeClientClick(object sender, RoutedEventArgs e) => _context.Host.ShowClientSetup();

    // Disconnect deletes local data (and any edits Google doesn't have yet), so confirm first
    async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string accountId } || ViewModel.IsBusy)
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
