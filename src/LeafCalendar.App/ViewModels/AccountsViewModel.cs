using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using Windows.System;

namespace LeafCalendar.App.ViewModels;

/// <summary>One row in the accounts list.</summary>
public sealed record AccountRow(string Id, string Email, string Summary);

/// <summary>Accounts page: add, sync, and disconnect Google accounts.</summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    readonly LeafServices _services;

    /// <summary>Loads the account list.</summary>
    public AccountsViewModel(LeafServices services, Action openSetup)
    {
        _services = services;
        OpenSetup = openSetup;
        Refresh();
    }

    /// <summary>Navigates to the OAuth client setup page.</summary>
    public Action OpenSetup { get; }

    /// <summary>Accounts shown in the list.</summary>
    public ObservableCollection<AccountRow> Accounts { get; } = [];

    /// <summary>True while signing in or syncing.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Status or error text, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>True when <see cref="Message"/> is set.</summary>
    public bool HasMessage => Message is not null;

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
    }

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
            await google.DisconnectAsync(accountId, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    async Task AddAccountAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy  = true;
        Message = "Finish signing in with Google in your browser.";
        try
        {
            var account = await google.CreateSignIn(uri => Launcher.LaunchUriAsync(uri).AsTask()).RunAsync(null, CancellationToken.None);
            Message = $"Signed in as {account.Email}. Syncing…";
            await google.Sync.SyncAccountAsync(account.Id, CancellationToken.None);
            Message = null;
        }
        catch (SignInException ex)
        {
            Message = ex.Message;
        }
        catch (HttpRequestException)
        {
            Message = "Couldn't reach Google. Check your connection and try again.";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    async Task SyncNowAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await google.Sync.SyncAllAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}
