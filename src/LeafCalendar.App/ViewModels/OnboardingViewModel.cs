using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Hosting;

namespace LeafCalendar.App.ViewModels;

/// <summary>
/// The onboarding window's view model: runs each step's work (save the OAuth client, sign in, first sync) and exposes
/// the <see cref="OnboardingFlow"/> state the footer shows. Every state change raises one "everything changed"
/// notification, so the window's bindings and the step pages stay in step. Errors show in the step's InfoBar, never
/// as a popup, and never as raw exception text.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    // Shown when a step fails for a reason the user can't act on
    const string NoConnection = "Couldn't reach Google. Check your connection and try again.";

    // Where the OAuth client is created (the setup guide's link)
    static readonly Uri ConsoleUri = new("https://console.cloud.google.com/");

    readonly LeafServices _services;
    readonly OnboardingFlow _flow = new();
    readonly CancellationTokenSource _cancel = new();
    Account? _account;

    /// <summary>Starts on Welcome. The client step's form prefills a saved client ID.</summary>
    public OnboardingViewModel(LeafServices services)
    {
        _services = services;
        Client    = new SetupViewModel(services.Tokens, OnClientSavedAsync, services.Log);
    }

    /// <summary>The step moved: true going forward, false going back. The window slides the new step in.</summary>
    public event EventHandler<bool>? StepChanged;

    /// <summary>"Open Leaf Calendar" was pressed after the first sync finished.</summary>
    public event EventHandler? Finished;

    /// <summary>The client step's form (same validation and Credential Locker storage as Settings).</summary>
    public SetupViewModel Client { get; }

    /// <summary>The step showing now.</summary>
    public OnboardingStep Step => _flow.Step;

    /// <summary>The step indicator's selected pip.</summary>
    public int PageIndex => _flow.PageIndex;

    /// <summary>The step indicator's accessible name.</summary>
    public string StepName => _flow.StepName;

    /// <summary>The primary button's text.</summary>
    public string PrimaryText => _flow.PrimaryText;

    /// <summary>True when the primary button is enabled.</summary>
    public bool CanRunPrimary => _flow.CanRunPrimary;

    /// <summary>True when Back shows.</summary>
    public bool CanGoBack => _flow.CanGoBack;

    /// <summary>True while a step's work runs.</summary>
    public bool IsBusy => _flow.IsBusy;

    /// <summary>True when closing should ask "Leave setup?" first.</summary>
    public bool AsksBeforeClosing => _flow.AsksBeforeClosing;

    /// <summary>What's running now ("Finish signing in with Google in your browser."), or null.</summary>
    public string? Status { get; private set; }

    /// <summary>True when <see cref="Status"/> is set.</summary>
    public bool HasStatus => Status is not null;

    /// <summary>The sign-in or sync error, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => Error is not null;

    /// <summary>The signed-in account's email, once there is one.</summary>
    public string Email => _account?.Email ?? "";

    /// <summary>What the first sync found ("2 calendars · 6 events").</summary>
    public string SyncSummary { get; private set; } = "";

    /// <summary>True when an account is saved (leaving setup then opens the main window instead of exiting).</summary>
    public bool HasAccount => App.HasAccount(_services);

    /// <summary>
    /// Runs the step's primary action: move on, save the client, sign in, retry the sync, or finish. It never throws
    /// (a failure is logged and shown on the step), so the UI can fire it and forget it.
    /// </summary>
    public async Task RunPrimaryAsync()
    {
        if (!_flow.CanRunPrimary)
        {
            return;
        }

        try
        {
            switch (_flow.Step)
            {
                case OnboardingStep.Welcome:
                    Move(_flow.Advance(), forward: true);
                    break;
                case OnboardingStep.Client:
                    await SaveClientAsync();
                    break;
                case OnboardingStep.SignIn:
                    await SignInAsync();
                    break;
                case OnboardingStep.Syncing:
                    await SyncAsync();
                    break;
                case OnboardingStep.Done:
                    Finished?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("onboarding.step.failed", ex);
            Fail("Something went wrong. Try again.");
        }
    }

    /// <summary>Opens Google Cloud Console (a fixed address; fake-Google mode records it instead).</summary>
    public Task OpenConsoleAsync() => _services.LaunchAsync(ConsoleUri);

    /// <summary>Goes back one step (client and sign-in only, while nothing runs).</summary>
    public void GoBack()
    {
        Error  = null;
        Status = null;
        Move(_flow.GoBack(), forward: false);
    }

    /// <summary>Stops a sign-in or sync that's still running (the window is closing).</summary>
    public void Cancel() => _cancel.Cancel();

    /// <inheritdoc />
    public void Dispose() => _cancel.Dispose();

    // =========================================================================
    // STEPS
    // =========================================================================

    // Client: an unchanged saved client moves on as it is; anything else is validated and saved by the form
    async Task SaveClientAsync()
    {
        if (OnboardingFlow.KeepsSavedClient(Client.ClientId, Client.ClientSecret, _services.Tokens.GetClientCredentials()))
        {
            Client.Error = null;
            if (_services.Google is null)
            {
                await _services.ReloadGoogleAsync();
            }

            Move(_flow.Advance(), forward: true);
            return;
        }

        SetBusy(true);
        try
        {
            await Client.SaveCommand.ExecuteAsync(null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Saved and valid: Google is rebuilt on the new client, then sign-in shows
    async Task OnClientSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        Move(_flow.Advance(), forward: true);
    }

    // Sign In: the browser opens Google's consent page; on success the first sync starts right away
    async Task SignInAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        Error  = null;
        Status = "Finish signing in with Google in your browser.";
        SetBusy(true);
        try
        {
            _account = await google.CreateSignIn(_services.OpenSignInPageAsync).RunAsync(null, _cancel.Token);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            return;
        }
        catch (SignInException ex)
        {
            Fail(ex.Message);
            return;
        }
        catch (HttpRequestException)
        {
            Fail(NoConnection);
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Error("onboarding.signin.failed", ex);
            Fail("Sign-in didn't finish. Try again.");
            return;
        }
        finally
        {
            SetBusy(false);
        }

        Status = null;
        Move(_flow.Advance(), forward: true);
        await SyncAsync();
    }

    // First Sync: off the UI thread; judged by what it saved, since the engine logs and swallows Google failures
    async Task SyncAsync()
    {
        if (_services.Google is not { } google || _account is not { } account)
        {
            return;
        }

        var ct = _cancel.Token;
        _flow.SyncStarted();
        Error  = null;
        Status = "Syncing your calendars…";
        SetBusy(true);
        try
        {
            await Task.Run(() => google.Sync.SyncAccountAsync(account.Id, ct), ct);

            var (calendars, events, status) = Counts(account.Id);
            if (!OnboardingFlow.FirstSyncWorked(calendars, status))
            {
                _flow.SyncFailed();
                Fail(status == AccountStatus.NeedsSignIn ? "Google signed Leaf out. Close setup and try again." : NoConnection);
                return;
            }

            SyncSummary = $"{calendars} {(calendars == 1 ? "calendar" : "calendars")} · {events} {(events == 1 ? "event" : "events")}";
            Status      = null;
            _flow.SyncSucceeded();
            StepChanged?.Invoke(this, true);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Error("onboarding.sync.failed", ex);
            _flow.SyncFailed();
            Fail("Couldn't sync your calendars. Try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // The account's calendars, events, and sign-in status, read back after the sync
    (int Calendars, int Events, AccountStatus Status) Counts(string accountId)
    {
        using var conn = _services.Database.Open();
        var calendars = CalendarStore.GetForAccount(conn, accountId);
        var events    = calendars.Sum(c => EventStore.Count(conn, accountId, c.Id));
        var status    = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == accountId)?.Status ?? AccountStatus.NeedsSignIn;
        return (calendars.Count, events, status);
    }

    // =========================================================================
    // STATE
    // =========================================================================

    void Fail(string message)
    {
        Status = null;
        Error  = message;
        Changed();
    }

    void SetBusy(bool busy)
    {
        _flow.IsBusy = busy;
        Changed();
    }

    void Move(bool moved, bool forward)
    {
        Changed();
        if (moved)
        {
            StepChanged?.Invoke(this, forward);
        }
    }

    // One notification for everything: x:Bind refreshes every binding on this object
    void Changed() => OnPropertyChanged(string.Empty);
}
