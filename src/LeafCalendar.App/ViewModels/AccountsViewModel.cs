using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;

namespace LeafCalendar.App.ViewModels;

/// <summary>One row in the accounts list.</summary>
public sealed record AccountRow(string Id, string Email, string Summary);

/// <summary>
/// One Google account's expander in Settings › Accounts: the email and its calendar and event counts in the header, and
/// inside, its "Add a Google Meet link to new events" switch and Disconnect.
/// </summary>
public sealed partial class AccountSettingsRow(string accountId, string email, string summary, bool isOn, bool isExpanded, Action<string, bool> toggled) : ObservableObject
{
    /// <summary>Google account ID.</summary>
    public string AccountId { get; } = accountId;

    /// <summary>The account's email (the header).</summary>
    public string Email { get; } = email;

    /// <summary>"3 calendars · 120 events", or "Needs sign-in" (under the email).</summary>
    public string Summary { get; } = summary;

    /// <summary>Automation ID of the expander.</summary>
    public string ExpanderId => $"AccountExpander_{AccountId}";

    /// <summary>Automation ID of the Meet switch.</summary>
    public string SwitchId => $"MeetByDefault_{AccountId}";

    /// <summary>Automation ID of the Disconnect button.</summary>
    public string DisconnectId => $"Disconnect_{AccountId}";

    /// <summary>The expander is open (kept when the list is rebuilt after a sync).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = isExpanded;

    /// <summary>New events in this account get a Meet link.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;

    partial void OnIsOnChanged(bool value) => toggled(AccountId, value);
}

/// <summary>Settings › Accounts: add, sync, and disconnect Google accounts, and show the OAuth client.</summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    // Shown when a command fails for a reason the user can't act on
    const string GenericFailure = "Something went wrong. Try again.";

    readonly LeafServices _services;
    readonly Action _accountsChanged;

    /// <summary>Loads the account list. <paramref name="accountsChanged"/> runs after an add, sync, or disconnect (the main window reloads its calendars).</summary>
    public AccountsViewModel(LeafServices services, Action accountsChanged)
    {
        _services        = services;
        _accountsChanged = accountsChanged;
        Refresh();
    }

    /// <summary>The saved OAuth client ID, or a note that there isn't one.</summary>
    public string ClientId => _services.Tokens.GetClientCredentials()?.ClientId ?? "No OAuth client saved";

    /// <summary>Accounts shown in the list.</summary>
    public ObservableCollection<AccountRow> Accounts { get; } = [];

    /// <summary>True while signing in or syncing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(AddAccountCommand), nameof(SyncNowCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>True when no sign-in or sync is running.</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>Status or error text, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>True when <see cref="Message"/> is set.</summary>
    public bool HasMessage => Message is not null;

    /// <summary>True when <see cref="Message"/> reports a failure (shown as an error), false for progress.</summary>
    [ObservableProperty]
    public partial bool IsError { get; set; }

    /// <summary>Reloads rows from the database.</summary>
    public void Refresh()
    {
        Accounts.Clear();
        using var conn = _services.Database.Open();

        foreach (var account in AccountStore.GetAll(conn))
        {
            var calendars = CalendarStore.GetForAccount(conn, account.Id);
            var events    = calendars.Sum(c => EventStore.Count(conn, account.Id, c.Id));
            var summary   = account.Status == AccountStatus.NeedsSignIn
                ? "Needs sign-in"
                : $"{calendars.Count} calendars · {events} events";

            Accounts.Add(new AccountRow(account.Id, account.Email, summary));
        }

        HasNoAccounts = Accounts.Count == 0;
        OnPropertyChanged(nameof(ClientId));
    }

    /// <summary>True when no account is connected.</summary>
    [ObservableProperty]
    public partial bool HasNoAccounts { get; set; }

    /// <summary>
    /// One expander row per account: its Meet switch is on for the accounts in <paramref name="meetAccounts"/> (a toggle
    /// calls <paramref name="toggled"/> with the account and its new state), and it starts open for the accounts in
    /// <paramref name="expanded"/>.
    /// </summary>
    public List<AccountSettingsRow> AccountRows(IReadOnlyList<string> meetAccounts, IReadOnlySet<string> expanded, Action<string, bool> toggled) =>
        [.. Accounts.Select(a => new AccountSettingsRow(a.Id, a.Email, a.Summary, meetAccounts.Contains(a.Id), expanded.Contains(a.Id), toggled))];

    /// <summary>Edits of an account Google doesn't have yet (disconnecting would lose them).</summary>
    public int UnsentFor(string accountId) => _services.Conflicts.UnsentFor(accountId);

    /// <summary>Disconnects an account (after the page confirms).</summary>
    public async Task DisconnectAsync(string accountId)
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy = true;
        try
        {
            try
            {
                await google.DisconnectAsync(accountId, CancellationToken.None);
            }
            finally
            {
                IsBusy = false;
                Reload();
            }
        }
        catch (Exception ex)
        {
            ShowError("account.disconnect.failed", ex);
        }
    }

    // The list here and the main window's calendars
    void Reload()
    {
        Refresh();
        _accountsChanged();
    }

    /// <summary>Logs a failure the UI caught and shows <paramref name="message"/>.</summary>
    public void ShowError(string eventName, Exception exception, string message = GenericFailure)
    {
        _services.Log.Error(eventName, exception);
        ShowFailure(message);
    }

    // A failure the user can act on, shown as an error
    void ShowFailure(string message)
    {
        IsError = true;
        Message = message;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    async Task AddAccountAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy  = true;
        IsError = false;
        Message = "Finish signing in with Google in your browser.";
        try
        {
            try
            {
                var account = await google.CreateSignIn(_services.OpenSignInPageAsync).RunAsync(null, CancellationToken.None);
                Message = $"Signed in as {account.Email}. Syncing…";

                // Sync Off The UI Thread; Property Updates Resume On It After The Await
                await Task.Run(() => google.Sync.SyncAccountAsync(account.Id, CancellationToken.None));
                Message = null;
            }
            finally
            {
                IsBusy = false;
                Reload();
            }
        }
        catch (SignInException ex)
        {
            ShowFailure(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowFailure("Couldn't reach Google. Check your connection and try again.");
        }
        catch (Exception ex)
        {
            ShowError("account.add.failed", ex, "Sign-in didn't finish. Try again.");
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    async Task SyncNowAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy  = true;
        IsError = false;
        Message = null;
        try
        {
            try
            {
                await Task.Run(() => google.Sync.SyncAllAsync(refreshCalendarLists: true, CancellationToken.None));
            }
            finally
            {
                IsBusy = false;
                Reload();
            }
        }
        catch (Exception ex)
        {
            ShowError("sync.now.failed", ex);
        }
    }
}
