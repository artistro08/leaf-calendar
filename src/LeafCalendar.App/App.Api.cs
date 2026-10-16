using System.Diagnostics;
using LeafCalendar.App.Api;
using LeafCalendar.App.Views.Api;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LeafCalendar.App;

// =========================================================================
// LOCAL API (docs/superpowers/specs/2026-10-13-local-api-design.md): the pipe runs while Settings › Connected apps is on
// =========================================================================
public partial class App
{
    private ApiPipeServer? _api;
    private ApiApprovals? _approvals;
    private bool _apiOn;
    private Task _apiStopped = Task.CompletedTask;
    private readonly Queue<PendingApproval> _waitingApprovals = new();
    private ApprovalWindow? _approvalWindow;

    // Starts or stops the pipe to match the switch (at startup and after every settings change); nothing may escape
    private void UpdateApi()
    {
        if (_services is not { } services)
        {
            return;
        }

        try
        {
            var on = CurrentSettings().AllowApiConnections;
            if (on == _apiOn)
            {
                return;
            }

            _apiOn = on;
            if (!on)
            {
                var running = _api;
                _api = null;
                services.ApiNameTaken = false;
                if (running is not null)
                {
                    _apiStopped = running.DisposeAsync().AsTask();
                }

                return;
            }

            StartApi(services);
        }
        catch (Exception ex)
        {
            services.Log.Info("api.update.failed", $"error={ex.GetType().Name}");
        }
    }

    // Opens the pipe once the last one has fully closed (a quick off and on would otherwise find its own old pipe and say
    // the name is taken); async void on the UI thread, so nothing may escape
    private async void StartApi(LeafServices services)
    {
        try
        {
            await _apiStopped;
            if (!_apiOn || _api is not null)
            {
                return;
            }

            var approvals = _approvals ??= new ApiApprovals(services.Time);
            var context = new ApiContext(services.Database, services.Editor, services.Scheduling, new ApiHost(this), services.Time, LeafVersion(), services.Options.Profile, services.Bookings);
            var server = new ApiPipeServer(
                ApiClients.PipeName(Environment.UserName, services.Options.Profile),
                new ApiDispatcher(context, services.Log),
                IsApprovedApp,
                ApproveAsync,
                WaitForLink,
                approvals.IsWaitingForLink,
                services.Log);
            var started = server.Start();
            _api = started ? server : null;
            services.ApiNameTaken = !started;
        }
        catch (Exception ex)
        {
            services.Log.Info("api.update.failed", $"error={ex.GetType().Name}");
        }
    }

    // Whether a connecting program is approved (on a pipe thread). An approved packaged app that updated into a new folder
    // has its approval moved there, on the UI thread, so Settings shows its current path and icon
    private bool IsApprovedApp(string path, string? packageKey, string? appProgram)
    {
        var settings = CurrentSettings();
        if (!settings.IsApproved(path, packageKey))
        {
            return false;
        }

        if (packageKey is not null && settings.ApprovedApps.Any(a => string.Equals(a.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    UpdateSettings(s => s.WithAppMoved(packageKey, path, appProgram));
                }
                catch (Exception ex)
                {
                    _services?.Log.Info("api.approval.move-failed", $"error={ex.GetType().Name}");
                }
            });
        }

        return true;
    }

    // A new program asks: its window (one on screen at a time), a 60-second wait, and the window closes once the
    // question is answered, timed out, or the API stopped
    private async Task<bool> ApproveAsync(ApiCaller caller, CancellationToken ct)
    {
        // What The App Sent (ok, none or refused for each; never the values)
        _services?.Log.Info("api.asked", $"client={Path.GetFileName(caller.Path)} {caller.Hello.Log}");

        PendingApproval? shown = null;
        try
        {
            return await _approvals!.AskAsync(caller, request =>
            {
                shown = request;
                _dispatcher.TryEnqueue(() => ShowApproval(request));
            }, ct);
        }
        finally
        {
            if (shown is { } asked)
            {
                _dispatcher.TryEnqueue(() => EndApproval(asked));
            }
        }
    }

    // A new program sent an askOnLink code (on a pipe thread): its question waits for its connect link; false when you
    // chose Don't allow for it since Leaf Calendar started
    private bool WaitForLink(ApiCaller caller) =>
        caller.Hello.LinkCode is { } code && _approvals is { } approvals && approvals.WaitForLink(caller, code);

    // Its connect link opened (on the UI thread): the same question, window and 60-second wait as a first connection,
    // closed if the API stops; the waiting connection hears the answer at its next line. Fire and forget, so nothing
    // may escape
    private async Task AskFromLinkAsync(ApiCaller caller, CancellationToken ct)
    {
        try
        {
            await ApproveAsync(caller, ct);
        }
        catch (Exception ex)
        {
            _services?.Log.Info("api.link.failed", $"error={ex.GetType().Name}");
        }
    }

    // Queues the question; it shows once no other is on screen (ShowNextApproval catches everything)
    private void ShowApproval(PendingApproval request)
    {
        _waitingApprovals.Enqueue(request);
        ShowNextApproval();
    }

    // The next waiting question that's still open (one that ended while it waited never shows). A window that fails to
    // open is skipped, so the queue never sticks behind it (its question times out as unanswered); a dispatcher callback,
    // so nothing may escape
    private void ShowNextApproval()
    {
        while (_approvalWindow is null && _waitingApprovals.TryDequeue(out var next))
        {
            if (_approvals?.Find(next.Token) is null)
            {
                continue;
            }

            try
            {
                var window = new ApprovalWindow(next, CurrentSettings().Theme, _services!.Log);
                window.Answered += (_, allow) => AnswerApproval(next, allow);
                window.Closed += (_, _) =>
                {
                    _approvalWindow = null;
                    ShowNextApproval();
                };
                window.Activate();
                _approvalWindow = window;
            }
            catch (Exception ex)
            {
                _services?.Log.Info("api.prompt.failed", $"error={ex.GetType().Name}");
            }
        }
    }

    // The question ended (answered, timed out, or the API stopped): its window closes; a queued one is skipped when its
    // turn comes. A dispatcher callback, so nothing may escape
    private void EndApproval(PendingApproval request)
    {
        try
        {
            if (_approvalWindow is { } window && window.Request == request)
            {
                window.Close();
            }
        }
        catch (Exception ex)
        {
            _services?.Log.Info("api.prompt.close-failed", $"error={ex.GetType().Name}");
        }
    }

    // The theme setting changed: an open approval window follows it
    private void ApplyApprovalTheme(AppTheme theme)
    {
        try
        {
            _approvalWindow?.ApplyTheme(theme);
        }
        catch (Exception ex)
        {
            _services?.Log.Info("api.prompt.theme-failed", $"error={ex.GetType().Name}");
        }
    }

    // Allow or Don't allow (from the window's click, Esc or Close, so nothing may escape): an approval is saved before the
    // waiting connection hears it, then Allow sends you back to the app. A save that fails still answers, so the app isn't
    // left waiting, and it's let in this once
    private void AnswerApproval(PendingApproval request, bool allow)
    {
        if (_approvals is not { } approvals || approvals.Find(request.Token) is null || _services is not { } services)
        {
            return;
        }

        var caller = request.Caller;
        try
        {
            if (allow)
            {
                UpdateSettings(s => s.WithApprovedApp(new ApprovedApp(caller.Path, caller.Name, services.Time.GetUtcNow(), caller.AppProgram, caller.PackageKey)));
            }
        }
        catch (Exception ex)
        {
            services.Log.Info("api.approval.save-failed", $"error={ex.GetType().Name}");
        }

        approvals.Answer(request.Token, allow);
        services.Log.Info(allow ? "api.allowed" : "api.denied", $"client={Path.GetFileName(caller.Path)}");
        if (allow)
        {
            _ = ReturnToAppAsync(caller);
        }
    }

    // After Allow: the app's return link when it sent a valid one, else its own window comes forward, restored when it was
    // minimized (Leaf Calendar may hand focus over, since you just clicked in its window; no visible window, nothing happens);
    // nothing may escape
    private async Task ReturnToAppAsync(ApiCaller caller)
    {
        try
        {
            if (caller.Hello.ReturnUrl is { } link)
            {
                await _services!.LaunchReturnLinkAsync(link);
                _services.Log.Info("api.returned", "by=link");
                return;
            }

            using var process = Process.GetProcessById(caller.ProcessId);
            if (process.MainWindowHandle != 0)
            {
                var window = new HWND(process.MainWindowHandle);
                if (PInvoke.IsIconic(window))
                {
                    PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
                }

                PInvoke.SetForegroundWindow(window);
                _services!.Log.Info("api.returned", "by=window");
            }
        }
        catch (Exception ex)
        {
            // The app already quit (ArgumentException) or can't be read
            _services?.Log.Info("api.return.failed", $"error={ex.GetType().Name}");
        }
    }

    // A settings change from outside Settings: through the calendar's view model when it exists (so it shows at once), else straight to the database
    private void UpdateSettings(Func<LeafSettings, LeafSettings> change)
    {
        if (_calendar is { } calendar)
        {
            calendar.Update(change);
            return;
        }

        using var conn = _services!.Database.Open();
        SettingsStore.Save(conn, change(SettingsStore.Load(conn)).Normalize());
    }

    private static string LeafVersion()
    {
        var v = Package.Current.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>The app as the local API sees it: the shown zone and settings, Leaf Calendar's link launcher, and the main window.</summary>
    private sealed class ApiHost(App app) : IApiHost
    {
        public TimeZoneInfo Zone => DisplayZone.Resolve(null, app.CurrentSettings().PrimaryTimeZone, app._zone.Zone);

        public LeafSettings Settings => app.CurrentSettings();

        public Task<bool> OpenLinkAsync(Uri link) => app._services!.LaunchAsync(link);

        public void ShowDay(DateOnly day) => app._dispatcher.TryEnqueue(() =>
        {
            try
            {
                app.ShowMainWindow();
                app._dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => app._window?.ShowDay(day));
            }
            catch (Exception ex)
            {
                app._services?.Log.Info("api.show-day.failed", $"error={ex.GetType().Name}");
            }
        });

        public void ShowEvent(CalendarOccurrence occurrence) => app._dispatcher.TryEnqueue(() => app.RevealEvent(occurrence));

        // ReloadGroups reads off the UI thread and lands on it, so it starts there
        public void SharesChanged() => app._dispatcher.TryEnqueue(() => app._calendar?.ReloadGroups());

        public LeafCalendar.Core.People.ContactSearch? Contacts => app._services?.Google?.Contacts;

        public LeafCalendar.Core.Places.PlaceSearch? Places => app._services?.Places;
    }
}
