using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Shortcuts (spec 8.6, 9): the two global shortcuts, each a PowerToys shortcut picker (press the new keys in its
/// dialog; Reset picks the default, Clear turns it off). A combination
/// another app holds shows a warning under its row and asks for another. Below them, a button opens the in-app cheat sheet.
/// </summary>
public sealed partial class ShortcutsPage : Page
{
    SettingsContext _context = null!;

    // The settings when a shortcut dialog opened (to restore a shortcut that turns out taken)
    LeafSettings? _before;

    // Why a shortcut isn't what the user picked (the new one was taken and the old one is back); cleared on the next change
    readonly Dictionary<ShortcutAction, string> _notes = [];

    /// <summary>Creates the page.</summary>
    public ShortcutsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        Wire(JoinShortcutControl, ShortcutAction.Join, "Join meeting", "JoinShortcutButton");
        Wire(FlyoutShortcutControl, ShortcutAction.Flyout, "Show or hide the tray flyout", "FlyoutShortcutButton");
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged     += OnChanged;
        _context.Services.Shortcuts.Changed += OnChanged;
        _context.Window.Closed              += OnWindowClosed;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Window.SettingsChanged     -= OnChanged;
        _context.Services.Shortcuts.Changed -= OnChanged;
        _context.Window.Closed              -= OnWindowClosed;
    }

    // Settings Closed With A Shortcut Dialog Open: close it, which lets go of the keyboard and registers Leaf's shortcuts again
    void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _context.Window.Closed -= OnWindowClosed;
        JoinShortcutControl.CloseDialog();
        FlyoutShortcutControl.CloseDialog();
    }

    void OnChanged(object? sender, EventArgs e) => Load();

    // The cheat sheet, over the Settings window
    async void OnShowCheatSheetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShortcutSheet.ShowAsync(this, _context.Calendar.Settings);
        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("shortcuts.sheet.failed", $"error={ex.GetType().Name}");
        }
    }

    void Load()
    {
        var s = _context.Calendar.Settings;
        Show(JoinShortcutControl, JoinShortcutWarning, s.JoinShortcut, ShortcutAction.Join);
        Show(FlyoutShortcutControl, FlyoutShortcutWarning, s.FlyoutShortcut, ShortcutAction.Flyout);
    }

    // The shortcut as key caps (screen readers read the row's name, then the shortcut as help text); a taken one shows
    // its caps in the warning state, and the warning under the row is collapsed when there's nothing to say
    void Show(ShortcutControl control, InfoBar warning, string shortcut, ShortcutAction action)
    {
        var shortcuts = _context.Services.Shortcuts;
        control.HotkeySettings = Hotkey.TryParse(shortcut, out var hotkey) ? HotkeySettings.FromHotkey(hotkey) : new HotkeySettings();

        // Without the tray icon's window nothing can be registered, which isn't another app's doing
        var message = !shortcuts.IsAvailable ? "Shortcuts aren't available because the tray icon couldn't start."
            : _notes.TryGetValue(action, out var note) ? note
            : shortcuts.IsTaken(action) ? $"Another app is using {shortcut}. Pick a different shortcut."
            : null;
        control.HasConflict = shortcuts.IsAvailable && shortcuts.IsTaken(action);
        control.Tooltip     = message;
        warning.Message     = message ?? string.Empty;
        warning.IsOpen      = message is not null;
        warning.Visibility  = message is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // Wires A Picker To Its Shortcut: the default for Reset, Leaf's conflict check, and saving
    void Wire(ShortcutControl control, ShortcutAction action, string name, string automationId)
    {
        control.ButtonName            = name;
        control.ButtonAutomationId    = automationId;
        control.DefaultHotkeySettings = Hotkey.TryParse(DefaultFor(action), out var fallback) ? HotkeySettings.FromHotkey(fallback) : new HotkeySettings();
        control.CheckConflict         = hotkey => Conflict(action, hotkey);
        control.DialogOpening        += (_, _) => Opening(action);
        control.DialogClosed         += (_, _) => Closed(action);
        control.HotkeySettingsChanged += (_, _) =>
        {
            // A New Combination, The Default (Reset), Or "" (cleared)
            var picked = control.HotkeySettings is { } settings && !settings.IsEmpty() ? settings.ToHotkey()?.ToString() : "";
            if (picked is not null)
            {
                _context.Save(s => With(s, action, picked));
            }
        };
    }

    // The other shortcut has it, or Windows or another app holds it (a RegisterHotKey probe)
    string? Conflict(ShortcutAction action, Hotkey hotkey)
    {
        var other     = Shortcut(_context.Calendar.Settings, action == ShortcutAction.Join ? ShortcutAction.Flyout : ShortcutAction.Join);
        var otherName = action == ShortcutAction.Join ? "Show or hide the tray flyout" : "Join meeting";
        return hotkey.ToString() == other ? $"“{otherName}” already uses {hotkey}."
            : !_context.Services.Shortcuts.IsFree(hotkey) ? $"Windows or another app is using {hotkey}. Try a different one."
            : null;
    }

    // Leaf's own shortcuts let go while the dialog listens (so pressing them reaches it)
    void Opening(ShortcutAction action)
    {
        _before = _context.Calendar.Settings;
        _notes.Remove(action);
        _context.Services.Shortcuts.Suspend();
    }

    // ...and come back from the settings after, whichever way the dialog closed
    void Closed(ShortcutAction action)
    {
        try
        {
            Reapply(action, _before ?? _context.Calendar.Settings);
        }
        catch (Exception ex)
        {
            // Raised from the picker's async void click; the type only
            _context.Services.Log.Info("settings.shortcut.failed", $"error={ex.GetType().Name}");
        }

        Load();
    }
    // Registers the saved shortcuts again, also after Cancel (Apply lets go of the old keys first); a new combination
    // another app took since the dialog checked it gives way to the one that worked before
    void Reapply(ShortcutAction action, LeafSettings before)
    {
        var shortcuts = _context.Services.Shortcuts;
        var saved     = _context.Calendar.Settings;
        shortcuts.Apply(saved);

        var previous = Shortcut(before, action);
        if (shortcuts.IsTaken(action) && Shortcut(saved, action) != previous)
        {
            _notes[action] = $"{Shortcut(saved, action)} was taken by another app, so {(previous.Length == 0 ? "no shortcut" : previous)} is back.";
            _context.Save(s => With(s, action, previous));
            shortcuts.Apply(_context.Calendar.Settings);
        }
    }

    static string DefaultFor(ShortcutAction action) =>
        action == ShortcutAction.Join ? LeafSettings.DefaultJoinShortcut : LeafSettings.DefaultFlyoutShortcut;

    static string Shortcut(LeafSettings settings, ShortcutAction action) =>
        action == ShortcutAction.Join ? settings.JoinShortcut : settings.FlyoutShortcut;

    static LeafSettings With(LeafSettings settings, ShortcutAction action, string shortcut) =>
        action == ShortcutAction.Join ? settings with { JoinShortcut = shortcut } : settings with { FlyoutShortcut = shortcut };
}
