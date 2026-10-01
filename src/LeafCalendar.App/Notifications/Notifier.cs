using LeafCalendar.Core.Alerts;
using Microsoft.Windows.AppNotifications;

namespace LeafCalendar.App.Notifications;

/// <summary>
/// Shows Leaf's notifications as Windows App SDK app notifications (spec 8.4) and reports clicks.
/// </summary>
/// <remarks>
/// <para>
/// Leaf is packaged, so Windows activates the COM class declared in Package.appxmanifest when a notification is clicked.
/// While Leaf runs, the click arrives here as <see cref="Invoked"/>; when it doesn't, Windows starts Leaf and
/// <see cref="Program"/> hands the click to the App. Windows Do Not Disturb and Focus apply on their own; Leaf has no
/// pause switch (spec 1.4).
/// </para>
/// <para>
/// In fake-Google mode (UI tests) nothing registers with Windows: each notification is appended to
/// <c>notifications.txt</c> in the profile folder as <c>verb, group, tag, XML</c> (tab-separated, one line), where the
/// UI tests read it. A notification Windows refuses is logged by the error only, never its content.
/// </para>
/// </remarks>
internal sealed class Notifier(LeafServices services) : IDisposable
{
    readonly Lock _fileGate = new();
    // Read on the scheduler's and the sync threads, written on the UI thread
    volatile bool _registered;

    /// <summary>A notification or one of its buttons was clicked; the argument is its activation text. Raised on a background thread.</summary>
    public event EventHandler<string>? Invoked;

    bool IsFake => services.Options.FakeGoogle is not null;

    /// <summary>Registers with Windows: the click handler first, then the registration, as Windows App SDK requires.</summary>
    public void Register()
    {
        if (IsFake || _registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Runs while the tray starts, so nothing may escape: Leaf runs on without notifications
            services.Log.Info("notifications.register.failed", $"error={ex.GetType().Name}");
        }
    }

    /// <summary>Shows a notification (replacing one with the same tag and group).</summary>
    public void Show(ToastMessage message)
    {
        if (IsFake)
        {
            Record("show", message.Group, message.Tag, message.Xml);
            return;
        }

        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Show(new AppNotification(message.Xml) { Tag = message.Tag, Group = message.Group });
        }
        catch (Exception ex)
        {
            // Called from the alert handlers, which must never throw
            services.Log.Info("notifications.show.failed", $"error={ex.GetType().Name}");
        }
    }

    /// <summary>Withdraws a notification from the screen and Notification Center.</summary>
    public async Task RemoveAsync(string tag, string group)
    {
        if (IsFake)
        {
            Record("remove", group, tag, "");
            return;
        }

        if (!_registered)
        {
            return;
        }

        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group);
        }
        catch (Exception ex)
        {
            // Callers fire and forget this task, so nothing may fault it
            services.Log.Info("notifications.remove.failed", $"error={ex.GetType().Name}");
        }
    }

    /// <summary>Unregisters, so Windows starts a new Leaf for a later click (Quit).</summary>
    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            // Runs during Quit, which must still reach the database and sync teardown
            services.Log.Info("notifications.unregister.failed", $"error={ex.GetType().Name}");
        }
    }

    void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Invoked?.Invoke(this, args.Argument);

    void Record(string verb, string group, string tag, string xml)
    {
        try
        {
            lock (_fileGate)
            {
                File.AppendAllText(Path.Combine(services.Paths.ProfileDirectory, "notifications.txt"), $"{verb}\t{group}\t{tag}\t{xml.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            // IO, access, or a bad profile path: the test log is best effort and never ends an alert pass
            services.Log.Info("notifications.record.failed", $"error={ex.GetType().Name}");
        }
    }
}
