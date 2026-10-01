using System.Runtime.InteropServices;
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
    bool _registered;

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
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            services.Log.Error("notifications.register.failed", ex);
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
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            services.Log.Error("notifications.show.failed", ex);
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
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            services.Log.Error("notifications.remove.failed", ex);
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
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            services.Log.Error("notifications.unregister.failed", ex);
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
        catch (IOException ex)
        {
            services.Log.Error("notifications.record.failed", ex);
        }
    }
}
