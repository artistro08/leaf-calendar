using LeafCalendar.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Notifications (spec 9): reminders, the persistent "Join now", invitations, and sound. Every change saves
/// right away; the next notification follows it. There's no pause switch: Windows Do Not Disturb and Focus do that.
/// </summary>
public sealed partial class NotificationsPage : Page
{
    private SettingsContext _context = null!;

    // True while the saved values are being shown (the switches' Toggled events are ignored meanwhile)
    private bool _loading = true;

    /// <summary>Creates the page.</summary>
    public NotificationsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Host.SettingsChanged += OnSettingsChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Host.SettingsChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Show The Saved Values
    private void Load()
    {
        var s = _context.Calendar.Settings;
        _loading = true;

        RemindersSwitch.IsOn = s.ReminderNotifications;
        JoinNowSwitch.IsOn = s.JoinNowNotifications;
        PersistRows.IsEnabled = s.JoinNowNotifications;
        PersistMeetingsBox.IsChecked = s.PersistForMeetings;
        PersistAloneBox.IsChecked = s.PersistWhenAlone;
        InvitesSwitch.IsOn = s.InviteNotifications;
        SoundSwitch.IsOn = s.NotificationSound;

        _loading = false;
    }

    private void OnRemindersToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = RemindersSwitch.IsOn;
            _context.Save(s => s with { ReminderNotifications = on });
        }
    }

    private void OnJoinNowToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = JoinNowSwitch.IsOn;
            PersistRows.IsEnabled = on;
            _context.Save(s => s with { JoinNowNotifications = on });
        }
    }

    // Which events' notifications stay on screen: one with a meeting link, or one with nobody but you on it
    private void OnPersistMeetingsClick(object sender, RoutedEventArgs e)
    {
        var on = PersistMeetingsBox.IsChecked == true;
        _context.Save(s => s with { PersistForMeetings = on });
    }

    private void OnPersistAloneClick(object sender, RoutedEventArgs e)
    {
        var on = PersistAloneBox.IsChecked == true;
        _context.Save(s => s with { PersistWhenAlone = on });
    }

    private void OnInvitesToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = InvitesSwitch.IsOn;
            _context.Save(s => s with { InviteNotifications = on });
        }
    }

    private void OnSoundToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = SoundSwitch.IsOn;
            _context.Save(s => s with { NotificationSound = on });
        }
    }
}
