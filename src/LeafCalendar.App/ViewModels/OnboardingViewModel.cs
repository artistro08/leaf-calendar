using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Scheduling;
using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.ViewModels;

/// <summary>
/// The onboarding window's view model: runs each step's work (save the OAuth client, sign in, first sync) and exposes
/// the <see cref="OnboardingFlow"/> state the footer shows. Every state change raises one "everything changed"
/// notification, so the window's bindings and the step pages stay in step. Errors show in the step's InfoBar, never
/// as a popup, and never as raw exception text. While the first sync runs, what it has saved so far is read back
/// every 500 ms. Disposing (the window closed) cancels any work and ignores whatever it finishes with; the
/// cancellation source is released once that work has ended.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    // Shown when a step fails for a reason the user can't act on
    private const string NoConnection = OnboardingFlow.NoConnection;

    // Where the OAuth client is created (the setup guide's link)
    private static readonly Uri ConsoleUri = new("https://console.cloud.google.com/");

    // How often the Syncing step reads back what the sync has saved so far
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    // How long the primary action stays off after a step change (Windows' default double-click time), so the second
    // click of a double-click doesn't run the new step's action
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(500);

    private readonly LeafServices _services;
    private readonly OnboardingFlow _flow = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly DispatcherQueueTimer _progress;
    private readonly DispatcherQueueTimer _settle;
    private CancellationTokenSource? _signIn;
    private Account? _account;
    private bool _closed;
    private bool _progressFailed;
    private bool _settling;

    /// <summary>Starts on Welcome. The client step's form prefills a saved client ID. The dispatcher runs the sync progress timer.</summary>
    public OnboardingViewModel(LeafServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        Client = new SetupViewModel(services.Tokens, OnClientSavedAsync, services.Log);

        // Next Waits For Both Boxes (or a saved client kept as it is)
        Client.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SetupViewModel.ClientId) or nameof(SetupViewModel.ClientSecret))
            {
                UpdateClientReady();
            }
        };
        UpdateClientReady();

        _progress = dispatcher.CreateTimer();
        _progress.Interval = ProgressInterval;
        _progress.Tick += (_, _) => ShowProgress();

        _settle = dispatcher.CreateTimer();
        _settle.Interval = SettleTime;
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) =>
        {
            _settling = false;
            Changed();
        };
    }

    /// <summary>The step moved: true going forward, false going back. The window slides the new step in.</summary>
    public event EventHandler<bool>? StepChanged;

    /// <summary>"Open Leaf Calendar" was pressed after the first sync finished.</summary>
    public event EventHandler? Finished;

    /// <summary>The client step's form (same validation and Credential Locker storage as Settings).</summary>
    public SetupViewModel Client { get; }

    /// <summary>The step showing now.</summary>
    public OnboardingStep Step => _flow.Step;

    /// <summary>True when Skip shows (the Cal.com step, while nothing runs).</summary>
    public bool CanSkip => _flow.CanSkip;

    /// <summary>The Cal.com step's client ID box.</summary>
    public string CalClientId
    {
        get;
        set
        {
            field = value;
            UpdateSchedulingReady();
        }
    } = "";

    /// <summary>The Cal.com step's client secret box (cleared once it's saved).</summary>
    public string CalClientSecret
    {
        get;
        set
        {
            field = value;
            UpdateSchedulingReady();
        }
    } = "";

    /// <summary>The redirect URI to register on the Cal.com OAuth client.</summary>
    public static string CalRedirectUri => $"http://127.0.0.1:{CalCom.OAuth.RedirectPort}{CalCom.OAuth.RedirectPath}";

    // Cal.com, the one provider the onboarding offers
    private static SchedulingProviderInfo CalCom => SchedulingProviders.Find(CalComProvider.ProviderId)!;

    /// <summary>The step indicator's selected pip.</summary>
    public int PageIndex => _flow.PageIndex;

    /// <summary>The step indicator's accessible name.</summary>
    public string StepName => _flow.StepName;

    /// <summary>The primary button's text.</summary>
    public string PrimaryText => _flow.PrimaryText;

    /// <summary>True when the primary button is enabled (off for a moment after each step change).</summary>
    public bool CanRunPrimary => _flow.CanRunPrimary && !_settling;

    /// <summary>True when Back shows.</summary>
    public bool CanGoBack => _flow.CanGoBack;

    /// <summary>True when Cancel shows in Back's place (sign-in is waiting for the browser).</summary>
    public bool CanCancel => _flow.CanCancel;

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

    /// <summary>What the first sync has found so far, then in total ("2 calendars · 6 events").</summary>
    public string SyncSummary { get; private set; } = "";

    /// <summary>
    /// Runs the step's primary action: move on, save the client, sign in, retry the sync, or finish. It never throws
    /// (a failure is logged and shown on the step), so the UI can fire it and forget it.
    /// </summary>
    public async Task RunPrimaryAsync()
    {
        if (!CanRunPrimary)
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
                case OnboardingStep.Scheduling:
                    await SignInCalComAsync();
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
        Error = null;
        Status = null;
        Move(_flow.GoBack(), forward: false);
    }

    /// <summary>Leaves the Cal.com step without signing in (Settings › Accounts can do it later).</summary>
    public void Skip()
    {
        Error = null;
        Status = null;
        Move(_flow.Skip(), forward: true);
    }

    /// <summary>Opens Cal.com's OAuth client page (a fixed address).</summary>
    public Task OpenCalClientPageAsync() => _services.LaunchAsync(CalCom.OAuth.ClientPage);

    /// <summary>Stops a sign-in that's waiting for the browser; the sign-in step is ready to try again.</summary>
    public void CancelSignIn()
    {
        if (_flow.CanCancel)
        {
            _signIn?.Cancel();
        }
    }

    /// <summary>
    /// The window closed: cancels any sign-in or sync, stops the progress timer, and ignores what the work finishes
    /// with. The cancellation source is released now, or when running work ends.
    /// </summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _progress.Stop();
        _settle.Stop();
        _cancel.Cancel();
        if (!_flow.IsBusy)
        {
            _cancel.Dispose();
        }
    }

    // =========================================================================
    // STEPS
    // =========================================================================

    // Client: an unchanged saved client moves on as it is; anything else is validated and saved by the form
    private async Task SaveClientAsync()
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
    private async Task OnClientSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        Move(_flow.Advance(), forward: true);
    }

    // Sign In: the browser opens Google's consent page; on success the first sync starts right away
    private async Task SignInAsync()
    {
        if (_closed || _services.Google is not { } google)
        {
            return;
        }

        // Its Own Cancellation (Cancel stops just this sign-in; closing the window stops everything)
        using var signIn = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        _signIn = signIn;

        Error = null;
        Status = "Finish signing in with Google in your browser.";
        SetBusy(true);
        try
        {
            _account = await _services.SignInAsync(google, null, null, signIn.Token);
        }
        catch (Exception) when (signIn.IsCancellationRequested)
        {
            // Canceled (or the window closed): the sign-in step is ready again
            Status = null;
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
            _signIn = null;
            SetBusy(false);
        }

        if (_closed)
        {
            return;
        }

        Status = null;
        Move(_flow.Advance(), forward: true);
        await SyncAsync();
    }

    // Cal.com: saves the client (encrypted, as Settings › Accounts does), then signs in through the browser; done, it
    // moves on to Done. A refusal or failure says why and stays, ready to try again or skip
    private async Task SignInCalComAsync()
    {
        if (_closed)
        {
            return;
        }

        using var signIn = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        _signIn = signIn;
        Error = null;
        Status = "Finish signing in in your browser.";
        SetBusy(true);
        try
        {
            _services.Scheduling.SaveClient(CalCom.Id, new ProviderClient(CalClientId.Trim(), CalClientSecret.Trim()));
            await _services.Scheduling.SignInAsync(CalCom.Id, _services.OpenSignInPageAsync, signIn.Token);
        }
        catch (Exception) when (signIn.IsCancellationRequested)
        {
            Status = null;
            return;
        }
        catch (SchedulingAuthException ex)
        {
            LogCalFailure(ex);
            Fail($"{CalCom.Name} didn't accept this OAuth client. Check the client ID and secret, and that the redirect URI is registered.");
            return;
        }
        catch (SchedulingPortBusyException)
        {
            Fail($"Port {CalCom.OAuth.RedirectPort} is busy, so Leaf can't finish signing in. Close the app using it and try again.");
            return;
        }
        catch (TimeoutException)
        {
            Fail("Sign-in timed out. Try again.");
            return;
        }
        catch (SignInException ex)
        {
            Fail(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is SchedulingUnavailableException or InvalidDataException or HttpRequestException)
        {
            LogCalFailure(ex);
            Fail($"Couldn't reach {CalCom.Name}. Check your connection and try again.");
            return;
        }
        finally
        {
            _signIn = null;
            SetBusy(false);
        }

        if (_closed)
        {
            return;
        }

        CalClientSecret = "";
        Status = null;
        Move(_flow.Skip(), forward: true);
    }

    // Leaf's own fixed reason only (it carries the provider's OAuth error code), never a secret
    private void LogCalFailure(Exception ex) =>
        _services.Log.Info("onboarding.calcom.signin.failed", $"error={ex.GetType().Name} reason=\"{(ex is SchedulingAuthException or SchedulingUnavailableException ? ex.Message : "")}\"");

    // First Sync: off the UI thread, with what it has saved so far read back every 500 ms; judged by what it saved,
    // since the engine logs and swallows Google failures. Google signing the account out sends you back to sign in.
    private async Task SyncAsync()
    {
        if (_closed || _services.Google is not { } google || _account is not { } account)
        {
            return;
        }

        var ct = _cancel.Token;
        _flow.SyncStarted();
        Error = null;
        Status = "Syncing your calendars…";
        SyncSummary = "";
        SetBusy(true);
        _progress.Start();
        try
        {
            await Task.Run(() => google.Sync.SyncAccountAsync(account.Id, ct), ct);
            if (_closed)
            {
                return;
            }

            var (calendars, events, status) = Counts(account.Id);
            SyncSummary = OnboardingFlow.Summary(calendars, events);
            if (status == AccountStatus.NeedsSignIn)
            {
                _flow.SignInExpired();
                Fail("Google needs you to sign in again.");
                StepChanged?.Invoke(this, false);
                return;
            }

            if (!OnboardingFlow.FirstSyncWorked(calendars, status))
            {
                _flow.SyncFailed();
                Fail(OnboardingFlow.FirstSyncError(google.Sync.IsOffline, google.Sync.LastRefusal));
                return;
            }

            Status = null;
            _flow.SyncSucceeded();

            // Already Signed In To Cal.com (setup run again): straight on to Done
            if (_services.Scheduling.StateOf(CalCom.Id) == ProviderState.SignedIn)
            {
                _flow.Skip();
            }

            StepChanged?.Invoke(this, true);
        }
        catch (Exception) when (_closed)
        {
            // The window closed; nothing to show
        }
        catch (Exception ex)
        {
            _services.Log.Error("onboarding.sync.failed", ex);
            _flow.SyncFailed();
            Fail("Couldn't sync your calendars. Try again.");
        }
        finally
        {
            _progress.Stop();
            SetBusy(false);
        }
    }

    // Live Progress: what the sync has saved so far
    private void ShowProgress()
    {
        if (_closed || _account is not { } account)
        {
            return;
        }

        // A Busy Or Failed Read Skips This Tick (the next one tries again; a failure streak is logged once)
        try
        {
            var (calendars, events, _) = Counts(account.Id);
            SyncSummary = OnboardingFlow.Summary(calendars, events);
            _progressFailed = false;
            Changed();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_progressFailed)
            {
                _progressFailed = true;
                _services.Log.Error("onboarding.progress.failed", ex);
            }
        }
    }

    // The account's calendars, events, and sign-in status, as saved so far
    private (int Calendars, int Events, AccountStatus Status) Counts(string accountId)
    {
        using var conn = _services.Database.Open();
        var calendars = CalendarStore.GetForAccount(conn, accountId).Where(c => !c.Hidden).ToList();
        var events = calendars.Sum(c => EventStore.Count(conn, accountId, c.Id));
        var status = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == accountId)?.Status ?? AccountStatus.NeedsSignIn;
        return (calendars.Count, events, status);
    }

    // =========================================================================
    // STATE
    // =========================================================================

    private void Fail(string message)
    {
        Status = null;
        Error = message;
        Changed();
    }

    private void SetBusy(bool busy)
    {
        _flow.IsBusy = busy;

        // Closed While Working: the work has ended, so its cancellation source can go
        if (!busy && _closed)
        {
            _cancel.Dispose();
            return;
        }

        Changed();
    }

    private void Move(bool moved, bool forward)
    {
        // The Primary Action Waits Out A Double-Click (the button shows off until the settle timer ticks)
        if (moved && !_closed)
        {
            _settling = true;
            _settle.Start();
        }

        Changed();
        if (moved && !_closed)
        {
            StepChanged?.Invoke(this, forward);
        }
    }

    // Only the primary button's state is told: a full refresh would push the boxes' own text back into them as they type
    private void UpdateClientReady()
    {
        // Secrets Can't Be Read Right Now: judged as if none were saved (runs on every keystroke, so nothing may escape)
        OAuthClientCredentials? saved;
        try
        {
            saved = _services.Tokens.GetClientCredentials();
        }
        catch (InvalidDataException)
        {
            saved = null;
        }

        _flow.IsClientReady = OnboardingFlow.HasClientInput(Client.ClientId, Client.ClientSecret, saved);
        if (!_closed)
        {
            OnPropertyChanged(nameof(CanRunPrimary));
        }
    }

    // Only the primary button's state is told, as for the Google client boxes
    private void UpdateSchedulingReady()
    {
        _flow.IsSchedulingReady = CalClientId.Trim().Length > 0 && CalClientSecret.Trim().Length > 0;
        if (!_closed)
        {
            OnPropertyChanged(nameof(CanRunPrimary));
        }
    }

    // One notification for everything: x:Bind refreshes every binding on this object (nothing once the window closed)
    private void Changed()
    {
        if (!_closed)
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
