using System.Diagnostics;
using LeafCalendar.App.Api;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;

namespace LeafCalendar.App;

// =========================================================================
// LOCAL API (docs/superpowers/specs/2026-10-13-local-api-design.md): the pipe runs while Settings › Connected apps is on
// =========================================================================
public partial class App
{
    private ApiPipeServer? _api;
    private ApiApprovals? _approvals;
    private bool _apiOn;

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
                    _ = running.DisposeAsync().AsTask();
                }

                return;
            }

            _approvals ??= new ApiApprovals(services.Time);
            var context = new ApiContext(services.Database, services.Editor, services.Scheduling, new ApiHost(this), services.Time, LeafVersion(), services.Options.Profile);
            var server = new ApiPipeServer(
                ApiClients.PipeName(Environment.UserName, services.Options.Profile),
                new ApiDispatcher(context, services.Log),
                path => CurrentSettings().IsApproved(path),
                ApproveAsync,
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

    // A new program asks: one notification, a 60-second wait, and the notification goes once it's answered or timed out
    private async Task<bool> ApproveAsync(string path, CancellationToken ct)
    {
        PendingApproval? shown = null;
        var allowed = await _approvals!.AskAsync(path, ClientName(path), request =>
        {
            shown = request;
            _dispatcher.TryEnqueue(() => ShowApproval(request));
        }, ct);

        if (shown is { } asked && _notifier is { } notifier)
        {
            await notifier.RemoveAsync(ToastContent.ApprovalTag(asked.Token), ToastContent.ApiGroup);
        }

        return allowed;
    }

    private void ShowApproval(PendingApproval request)
    {
        try
        {
            _notifier?.Show(ToastContent.ApproveApp(request, ApiClients.IsGeneralRuntime(request.Path), _services!.Options.Profile, CurrentSettings().NotificationSound));
        }
        catch (Exception ex)
        {
            // A dispatcher callback, so nothing may escape
            _services?.Log.Info("api.prompt.failed", $"error={ex.GetType().Name}");
        }
    }

    // Allow or Don't allow on the notification (from HandleToast, on the UI thread): an approval is saved before the waiting connection hears it
    private void AnswerApproval(string token, bool allow)
    {
        if (_approvals?.Find(token) is not { } request)
        {
            return;
        }

        if (allow)
        {
            UpdateSettings(s => s.WithApprovedApp(new ApprovedApp(request.Path, request.Name, _services!.Time.GetUtcNow())));
        }

        _approvals.Answer(token, allow);
        _services!.Log.Info(allow ? "api.allowed" : "api.denied", $"client={Path.GetFileName(request.Path)}");
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

    // The program's product name, else its description, else its file name
    private static string ClientName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName : info.FileDescription;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // The file name below
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static string LeafVersion()
    {
        var v = Package.Current.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>The app as the local API sees it: the shown zone and settings, Leaf's link launcher, and the main window.</summary>
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
    }
}
