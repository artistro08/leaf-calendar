using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Shortcuts (spec 8.6, 9): the two global shortcuts, each changed by pressing the new keys. A combination
/// another app holds shows a warning under its row and asks for another. Below them, a button opens the in-app cheat sheet.
/// </summary>
public sealed partial class ShortcutsPage : Page
{
    SettingsContext _context = null!;

    // Why a shortcut isn't what the user picked (the new one was taken and the old one is back); cleared on the next change
    readonly Dictionary<ShortcutAction, string> _notes = [];

    /// <summary>Creates the page.</summary>
    public ShortcutsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged     += OnChanged;
        _context.Services.Shortcuts.Changed += OnChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Window.SettingsChanged     -= OnChanged;
        _context.Services.Shortcuts.Changed -= OnChanged;
    }

    void OnChanged(object? sender, EventArgs e) => Load();

    // The cheat sheet, over the Settings window
    async void OnShowCheatSheetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShortcutSheet.ShowAsync(XamlRoot, _context.Calendar.Settings);
        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("shortcuts.sheet.failed", $"error={ex.GetType().Name}");
        }
    }

    void Load()
    {
        var s = _context.Calendar.Settings;
        Show(JoinShortcutButton, JoinShortcutWarning, s.JoinShortcut, ShortcutAction.Join);
        Show(FlyoutShortcutButton, FlyoutShortcutWarning, s.FlyoutShortcut, ShortcutAction.Flyout);
    }

    // The shortcut is the button's text and its help text (screen readers read the row's name, then the shortcut); the
    // warning is collapsed when there's nothing to say, so it takes no room between the rows
    void Show(Button button, InfoBar warning, string shortcut, ShortcutAction action)
    {
        var text      = shortcut.Length == 0 ? "None" : shortcut;
        var shortcuts = _context.Services.Shortcuts;
        button.Content = text;
        AutomationProperties.SetHelpText(button, text);

        // Without the tray icon's window nothing can be registered, which isn't another app's doing
        var message = !shortcuts.IsAvailable ? "Shortcuts aren't available because the tray icon couldn't start."
            : _notes.TryGetValue(action, out var note) ? note
            : shortcuts.IsTaken(action) ? $"Another app is using {shortcut}. Pick a different shortcut."
            : null;
        warning.Message    = message ?? string.Empty;
        warning.IsOpen     = message is not null;
        warning.Visibility = message is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnJoinShortcutClick(object sender, RoutedEventArgs e) => await ChangeAsync(ShortcutAction.Join, "Join meeting shortcut");

    async void OnFlyoutShortcutClick(object sender, RoutedEventArgs e) => await ChangeAsync(ShortcutAction.Flyout, "Tray flyout shortcut");

    // Leaf's own shortcuts let go while the dialog listens (so pressing them reaches it), and come back from the settings after
    async Task ChangeAsync(ShortcutAction action, string title)
    {
        var shortcuts = _context.Services.Shortcuts;
        var before    = _context.Calendar.Settings;
        var other     = Shortcut(before, action == ShortcutAction.Join ? ShortcutAction.Flyout : ShortcutAction.Join);
        var otherName = action == ShortcutAction.Join ? "Show or hide the tray flyout" : "Join meeting";

        _notes.Remove(action);
        shortcuts.Suspend();
        try
        {
            var picked = await ShortcutDialog.AskAsync(XamlRoot, title, hotkey =>
                hotkey.ToString() == other ? $"“{otherName}” already uses {hotkey}."
                : !shortcuts.IsFree(hotkey) ? $"Windows or another app is using {hotkey}. Try a different one."
                : null);

            if (picked is { } chosen)
            {
                var text = chosen.ToString();
                _context.Save(s => With(s, action, text));
            }
        }
        catch (Exception ex)
        {
            // async void callers: nothing may escape; the type only
            _context.Services.Log.Info("settings.shortcut.failed", $"error={ex.GetType().Name}");
        }
        finally
        {
            Reapply(action, before);
        }
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

    static string Shortcut(LeafSettings settings, ShortcutAction action) =>
        action == ShortcutAction.Join ? settings.JoinShortcut : settings.FlyoutShortcut;

    static LeafSettings With(LeafSettings settings, ShortcutAction action, string shortcut) =>
        action == ShortcutAction.Join ? settings with { JoinShortcut = shortcut } : settings with { FlyoutShortcut = shortcut };
}
