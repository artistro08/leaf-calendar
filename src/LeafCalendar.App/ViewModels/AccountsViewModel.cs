using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Hosting;

namespace LeafCalendar.App.ViewModels;

/// <summary>What the Accounts page's status message reports.</summary>
public enum AccountsMessageKind
{
    /// <summary>Something is under way ("Finish signing in…").</summary>
    Progress,

    /// <summary>It finished ("Signed in as…").</summary>
    Success,

    /// <summary>It failed.</summary>
    Error,
}

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
    private const string GenericFailure = "Something went wrong. Try again.";

    private readonly LeafServices _services;
    private readonly Action _accountsChanged;

    // Cancels the sign-in waiting on the browser; null when none is
    private CancellationTokenSource? _signIn;

    /// <summary>Loads the account list. <paramref name="accountsChanged"/> runs after an add, sync, or disconnect (the main window reloads its calendars).</summary>
    public AccountsViewModel(LeafServices services, Action accountsChanged)
    {
        _services = services;
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

    /// <summary>True while an account is being added: signing in, then its first sync (the Add row's progress ring).</summary>
    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    /// <summary>True while Sync now runs (the Sync now row's progress ring).</summary>
    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    /// <summary>True while a sign-in waits on the browser (it can be canceled; the sync after it can't).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelSignInCommand))]
    public partial bool IsSigningIn { get; set; }

    /// <summary>Status or error text, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>True when <see cref="Message"/> is set.</summary>
    public bool HasMessage => Message is not null;

    /// <summary>What <see cref="Message"/> reports: progress, success, or a failure.</summary>
    [ObservableProperty]
    public partial AccountsMessageKind MessageKind { get; set; }

    /// <summary>Clears the status message (a finished action's message is stale once the page is left).</summary>
    public void ClearMessage()
    {
        Message = null;
        MessageKind = AccountsMessageKind.Progress;
    }

    /// <summary>Reloads rows from the database; a failed read is logged and shown, and the rows already showing stay.</summary>
    public void Refresh()
    {
        // Read First, So A Busy Or Failed Database Leaves The List As It Was
        List<AccountRow> rows = [];
        try
        {
            using var conn = _services.Database.Open();
            foreach (var account in AccountStore.GetAll(conn))
            {
                var calendars = CalendarStore.GetForAccount(conn, account.Id).Where(c => !c.Hidden).ToList();
                var events = calendars.Sum(c => EventStore.Count(conn, account.Id, c.Id));
                var summary = account.Status == AccountStatus.NeedsSignIn
                    ? "Needs sign-in"
                    : $"{calendars.Count} calendars · {events} events";

                rows.Add(new AccountRow(account.Id, account.Email, summary));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            ShowError("accounts.refresh.failed", ex);
            return;
        }

        Accounts.Clear();
        foreach (var row in rows)
        {
            Accounts.Add(row);
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

    /// <summary>Disconnects an account (after the page confirms); not while a sign-in or sync runs, whose finish clears the busy state.</summary>
    public async Task DisconnectAsync(string accountId)
    {
        // A Sign-In Or Sync Already Running Owns The Busy State (it may have started while the dialog was open)
        if (IsBusy || _services.Google is not { } google)
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

    // A new account's first sync saved at least one calendar and left it signed in
    private bool FirstSyncWorked(string accountId)
    {
        using var conn = _services.Database.Open();
        var calendars = CalendarStore.GetForAccount(conn, accountId).Count(c => !c.Hidden);
        var status = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == accountId)?.Status ?? AccountStatus.NeedsSignIn;
        return OnboardingFlow.FirstSyncWorked(calendars, status);
    }

    // The list here and the main window's calendars
    private void Reload()
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
    private void ShowFailure(string message) => Show(AccountsMessageKind.Error, message);

    private void Show(AccountsMessageKind kind, string message)
    {
        MessageKind = kind;
        Message = message;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task AddAccountAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy = true;
        IsAdding = true;
        Show(AccountsMessageKind.Progress, "Finish signing in with Google in your browser.");

        // Cancel's own token (still readable after its source is disposed), so only Cancel counts as canceled
        CancellationToken canceled = default;
        try
        {
            try
            {
                // Sign In (Cancel stops the wait on the browser)
                Account account;
                using (var signIn = new CancellationTokenSource())
                {
                    canceled = signIn.Token;
                    _signIn = signIn;
                    IsSigningIn = true;
                    try
                    {
                        account = await _services.SignInAsync(google, null, null, signIn.Token);
                    }
                    finally
                    {
                        IsSigningIn = false;
                        _signIn = null;
                    }
                }

                // Signed In: a success already, while the first sync runs
                Show(AccountsMessageKind.Success, $"Signed in as {account.Email}. Syncing…");

                // Sync Off The UI Thread; Property Updates Resume On It After The Await
                await Task.Run(() => google.Sync.SyncAccountAsync(account.Id, CancellationToken.None));

                // The Sync Swallows Google And Network Failures, So Judge It By What It Saved
                if (!FirstSyncWorked(account.Id))
                {
                    ShowFailure(OnboardingFlow.FirstSyncError(google.Sync.IsOffline, google.Sync.LastRefusal));
                    return;
                }

                Show(AccountsMessageKind.Success, $"Signed in as {account.Email}. Its calendars are in Leaf now.");
            }
            finally
            {
                IsBusy = false;
                IsAdding = false;
                Reload();
            }
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested)
        {
            // Canceled With Cancel: nothing was saved, so there's nothing to report (a timeout falls through to the failure below)
            ClearMessage();
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

    /// <summary>Stops a sign-in that is waiting on the browser (nothing is saved); the page goes back to how it was.</summary>
    [RelayCommand(CanExecute = nameof(IsSigningIn))]
    public void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task SyncNowAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy = true;
        IsSyncing = true;
        ClearMessage();
        try
        {
            try
            {
                await Task.Run(() => google.Sync.SyncAllAsync(refreshCalendarLists: true, CancellationToken.None));
            }
            finally
            {
                IsBusy = false;
                IsSyncing = false;
                Reload();
            }
        }
        catch (Exception ex)
        {
            ShowError("sync.now.failed", ex);
        }
    }
}
