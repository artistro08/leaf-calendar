using System.Runtime.InteropServices;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace LeafCalendar.App.Interop;

/// <summary>What a global shortcut does. The values are the hotkey IDs registered with Windows.</summary>
public enum ShortcutAction
{
    /// <summary>Join the next meeting (spec 8.5), Ctrl+Alt+J by default.</summary>
    Join = 1,

    /// <summary>Show or hide the tray flyout, Ctrl+Alt+K by default.</summary>
    Flyout = 2,
}

/// <summary>
/// Leaf's global shortcuts (spec 8.6), registered with <c>RegisterHotKey</c> on the tray icon's hidden window, whose
/// <c>WM_HOTKEY</c> comes back through <see cref="OnHotkey"/>.
/// </summary>
/// <remarks>
/// A combination Windows or another app holds can't be taken (settings accept some reserved Win+letter combinations,
/// so this is where they fail): it's remembered as taken and logged, and Settings › Shortcuts warns about it and asks
/// for another. <c>MOD_NOREPEAT</c> keeps a held combination from firing over and over. Call on the UI thread.
/// </remarks>
/// <param name="log">Where a combination that couldn't be registered is noted (the action and the Win32 error only).</param>
public sealed class GlobalShortcuts(AppLog log)
{
    // A spare ID for checking whether a combination is free
    const int ProbeId = 0x7FFF;

    readonly HashSet<ShortcutAction> _registered = [];
    readonly HashSet<ShortcutAction> _taken      = [];
    HWND _hwnd;

    /// <summary>A shortcut was pressed.</summary>
    public event EventHandler<ShortcutAction>? Pressed;

    /// <summary>Which shortcuts are registered or taken changed (Settings › Shortcuts refreshes).</summary>
    public event EventHandler? Changed;

    /// <summary>Starts listening on the tray icon's window with the saved shortcuts.</summary>
    public void Attach(nint hwnd, LeafSettings settings)
    {
        _hwnd = new HWND(hwnd);
        Apply(settings);
    }

    /// <summary>Registers the saved shortcuts again (after a change, or after the shortcut dialog).</summary>
    public void Apply(LeafSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Suspend();
        _taken.Clear();
        Register(ShortcutAction.Join, settings.JoinShortcut);
        Register(ShortcutAction.Flyout, settings.FlyoutShortcut);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of Leaf's shortcuts (while the shortcut dialog listens for keys, and on Quit).</summary>
    public void Suspend()
    {
        foreach (var action in _registered)
        {
            PInvoke.UnregisterHotKey(_hwnd, (int)action);
        }

        _registered.Clear();
    }

    /// <summary>True when Windows would let Leaf register <paramref name="hotkey"/> now.</summary>
    public bool IsFree(Hotkey hotkey)
    {
        if (_hwnd.IsNull)
        {
            return true;
        }

        if (!PInvoke.RegisterHotKey(_hwnd, ProbeId, Modifiers(hotkey), (uint)hotkey.Key))
        {
            return false;
        }

        PInvoke.UnregisterHotKey(_hwnd, ProbeId);
        return true;
    }

    /// <summary>True when the saved combination for <paramref name="action"/> is held by Windows or another app.</summary>
    public bool IsTaken(ShortcutAction action) => _taken.Contains(action);

    /// <summary>A <c>WM_HOTKEY</c> arrived with this ID.</summary>
    public void OnHotkey(int id)
    {
        if (id is (int)ShortcutAction.Join or (int)ShortcutAction.Flyout)
        {
            Pressed?.Invoke(this, (ShortcutAction)id);
        }
    }

    // An empty (or unreadable) setting means no shortcut
    void Register(ShortcutAction action, string text)
    {
        if (_hwnd.IsNull || !Hotkey.TryParse(text, out var hotkey))
        {
            return;
        }

        if (PInvoke.RegisterHotKey(_hwnd, (int)action, Modifiers(hotkey), (uint)hotkey.Key))
        {
            _registered.Add(action);
            return;
        }

        // Taken: Settings › Shortcuts shows the warning; the log gets the action and the error code, never the keys
        var error = Marshal.GetLastPInvokeError();
        _taken.Add(action);
        log.Info("shortcut.register.failed", $"action={action} error={error}");
    }

    static HOT_KEY_MODIFIERS Modifiers(Hotkey hotkey) => (HOT_KEY_MODIFIERS)(uint)hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
}
