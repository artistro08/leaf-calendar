// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// Ported from PowerToys: src/settings-ui/Settings.UI/SettingsXAML/Controls/ShortcutControl/ShortcutControl.xaml.cs
// (https://github.com/microsoft/PowerToys). Leaf changes:
// - Conflicts come from Leaf's own check (CheckConflict: the other shortcut, or Windows or another app holding it, by a
//   RegisterHotKey probe), synchronously, and a taken combination keeps Save off (PowerToys saves it with a warning).
// - The hook is installed when the dialog opens and removed when it closes (and in a finally), not for the control's
//   lifetime, and it acts only while this window is in front (PowerToys unhooks on window deactivation).
// - Reset sets Leaf's default shortcut and Clear turns the shortcut off (PowerToys clears both ways).
// - Raises HotkeySettingsChanged, DialogOpening, and DialogClosed, so the page saves the shortcut and lets go of Leaf's
//   own shortcuts while the dialog listens (and registers them again after, restoring the old one if the new one fails).
// - Native AOT: the focused-button check reads the automation peer's class name instead of comparing CLR types; key
//   caps are added in code instead of through an ItemsControl; no telemetry, conflict window, or right-click disable.

using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Tray;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// PowerToys' shortcut picker: a button showing the shortcut as key caps (or "Assign shortcut"), which opens the
/// "Activation shortcut" dialog. While the dialog is open a low-level keyboard hook captures the keys before Windows and
/// other apps see them, so any combination can be pressed; Save is on only for a valid one that isn't taken.
/// </summary>
public sealed partial class ShortcutControl : UserControl, IDisposable
{
    private readonly nuint ignoreKeyEventFlag = 0x5555;
    private readonly HashSet<VirtualKey> _modifierKeysOnEntering = [];
    private HotkeySettings? hotkeySettings;
    private HotkeySettings internalSettings;
    private HotkeySettings? lastValidSettings;
    private HotkeySettingsControlHook? hook;
    private bool _isActive;
    private bool _hasConflict;
    private bool _dialogOpen;
    private bool _hookFailed;
    private nint _window;

    [ThreadStatic]
    private static bool _isDialogOpen;

    private readonly ShortcutDialogContentControl c = new();
    private readonly ContentDialog shortcutDialog;

    /// <summary>Creates the picker.</summary>
    public ShortcutControl()
    {
        InitializeComponent();
        internalSettings = new HotkeySettings();

        c.ResetClick += C_ResetClick;
        c.ClearClick += C_ClearClick;
        Unloaded += ShortcutControl_Unloaded;

        // We create the Dialog in C# because doing it in XAML is giving WinUI/XAML Island bugs when using dark theme.
        shortcutDialog = new ContentDialog
        {
            Title = "Activation shortcut",
            Content = c,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        shortcutDialog.PrimaryButtonClick += ShortcutDialog_PrimaryButtonClick;
        shortcutDialog.Opened += ShortcutDialog_Opened;
        shortcutDialog.Closing += ShortcutDialog_Closing;

        AutomationProperties.SetName(EditButton, "Activation shortcut");
    }

    /// <summary>The shortcut was saved, reset, or cleared (<see cref="HotkeySettings"/> has it; empty means none).</summary>
    public event EventHandler? HotkeySettingsChanged;

    /// <summary>The dialog is about to listen for keys.</summary>
    public event EventHandler? DialogOpening;

    /// <summary>The dialog closed, whichever way.</summary>
    public event EventHandler? DialogClosed;

    /// <summary>The shortcut on the button; empty (or null) for none.</summary>
    public HotkeySettings? HotkeySettings
    {
        get => hotkeySettings;
        set
        {
            if (hotkeySettings != value)
            {
                hotkeySettings = value;
                SetKeys();
                c.Keys = HotkeySettings?.GetKeysList();
            }
        }
    }

    /// <summary>What Reset sets.</summary>
    public HotkeySettings DefaultHotkeySettings { get; set; } = new();

    /// <summary>Says who already has a combination, or null when it's free (Leaf's GlobalShortcuts check).</summary>
    public Func<Hotkey, string?>? CheckConflict { get; set; }

    /// <summary>The saved shortcut is taken: its key caps show the warning state and the tooltip says why.</summary>
    public bool HasConflict
    {
        get => _hasConflict;
        set
        {
            _hasConflict = value;
            SetKeys();
        }
    }

    /// <summary>Why the saved shortcut is taken (the button's tooltip), or null.</summary>
    public string? Tooltip
    {
        get => ToolTipService.GetToolTip(EditButton) as string;
        set => ToolTipService.SetToolTip(EditButton, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>The button's automation ID (the page's, for the UI tests).</summary>
    public string ButtonAutomationId
    {
        get => AutomationProperties.GetAutomationId(EditButton);
        set => AutomationProperties.SetAutomationId(EditButton, value);
    }

    /// <summary>The button's accessible name (the setting's header).</summary>
    public string ButtonName
    {
        get => AutomationProperties.GetName(EditButton);
        set => AutomationProperties.SetName(EditButton, value);
    }

    private void KeyEventHandler(int key, bool matchValue, int matchValueCode)
    {
        VirtualKey virtualKey = (VirtualKey)key;
        switch (virtualKey)
        {
            case VirtualKey.LeftWindows:
            case VirtualKey.RightWindows:
                if (!matchValue && _modifierKeysOnEntering.Contains(virtualKey))
                {
                    SendSingleKeyboardInput((short)virtualKey, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
                    _ = _modifierKeysOnEntering.Remove(virtualKey);
                }

                internalSettings.Win = matchValue;
                break;
            case VirtualKey.Control:
            case VirtualKey.LeftControl:
            case VirtualKey.RightControl:
                if (!matchValue && _modifierKeysOnEntering.Contains(VirtualKey.Control))
                {
                    SendSingleKeyboardInput((short)virtualKey, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
                    _ = _modifierKeysOnEntering.Remove(VirtualKey.Control);
                }

                internalSettings.Ctrl = matchValue;
                break;
            case VirtualKey.Menu:
            case VirtualKey.LeftMenu:
            case VirtualKey.RightMenu:
                if (!matchValue && _modifierKeysOnEntering.Contains(VirtualKey.Menu))
                {
                    SendSingleKeyboardInput((short)virtualKey, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
                    _ = _modifierKeysOnEntering.Remove(VirtualKey.Menu);
                }

                internalSettings.Alt = matchValue;
                break;
            case VirtualKey.Shift:
            case VirtualKey.LeftShift:
            case VirtualKey.RightShift:
                if (!matchValue && _modifierKeysOnEntering.Contains(VirtualKey.Shift))
                {
                    SendSingleKeyboardInput((short)virtualKey, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
                    _ = _modifierKeysOnEntering.Remove(VirtualKey.Shift);
                }

                internalSettings.Shift = matchValue;
                break;
            case VirtualKey.Escape:
                internalSettings = new HotkeySettings();
                shortcutDialog.IsPrimaryButtonEnabled = false;
                return;
            default:
                internalSettings.Code = matchValueCode;
                break;
        }
    }

    // Function to send a single key event to the system which would be ignored by the hotkey control.
    private unsafe void SendSingleKeyboardInput(short keyCode, KEYBD_EVENT_FLAGS keyStatus)
    {
        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
        };
        input.Anonymous.ki = new KEYBDINPUT
        {
            wVk = (VIRTUAL_KEY)keyCode,
            dwFlags = keyStatus,

            // Any keyevent with the extraInfo set to this value will be ignored by the keyboard hook and sent to the system instead.
            dwExtraInfo = ignoreKeyEventFlag,
        };

        _ = PInvoke.SendInput(1, &input, sizeof(INPUT));
    }

    private bool FilterAccessibleKeyboardEvents(int key, nuint extraInfo)
    {
        // A keyboard event sent with this value in the extra Information field should be ignored by the hook so that it can be captured by the system instead.
        if (extraInfo == ignoreKeyEventFlag)
        {
            return false;
        }

        // If the current key press is tab, based on the other keys ignore the key press so as to shift focus out of the hotkey control.
        if ((VirtualKey)key == VirtualKey.Tab)
        {
            // Shift was not pressed while entering and Shift is not pressed while leaving the hotkey control, treat it as a normal tab key press.
            if (!internalSettings.Shift && !_modifierKeysOnEntering.Contains(VirtualKey.Shift) && !internalSettings.Win && !internalSettings.Alt && !internalSettings.Ctrl)
            {
                return false;
            }

            // Shift was not pressed while entering but it was pressed while leaving the hotkey, therefore simulate a shift key press as the system does not know about shift being pressed in the hotkey.
            else if (internalSettings.Shift && !_modifierKeysOnEntering.Contains(VirtualKey.Shift) && !internalSettings.Win && !internalSettings.Alt && !internalSettings.Ctrl)
            {
                // This is to reset the shift key press within the control as it was not used within the control but rather was used to leave the hotkey.
                internalSettings.Shift = false;

                SendSingleKeyboardInput((short)VirtualKey.Shift, 0);

                return false;
            }

            // Shift was pressed on entering and remained pressed, therefore only ignore the tab key so that it can be passed to the system.
            // As the shift key is already assumed to be pressed by the system while it entered the hotkey control, shift would still remain pressed, hence ignoring the tab input would simulate a Shift+Tab key press.
            else if (!internalSettings.Shift && _modifierKeysOnEntering.Contains(VirtualKey.Shift) && !internalSettings.Win && !internalSettings.Alt && !internalSettings.Ctrl)
            {
                return false;
            }
        }

        // Either the cancel or save button has keyboard focus (the class name, not a CLR type test, under Native AOT).
        if (FocusManager.GetFocusedElement(XamlRoot) is UIElement focused
            && FrameworkElementAutomationPeer.CreatePeerForElement(focused)?.GetClassName() == "Button")
        {
            return false;
        }

        return true;
    }

    private void Hotkey_KeyDown(int key)
    {
        KeyEventHandler(key, true, key);
        List<object> newKeys = internalSettings.GetKeysList();
        if (c.Keys == null || !c.JudgeIfKeyValueSame(newKeys))
        {
            c.Keys = newKeys;
        }

        c.KeysName = KeysName(internalSettings);
        c.ConflictMessage = string.Empty;
        c.HasConflict = false;

        if (internalSettings.GetKeysList().Count == 0)
        {
            // Empty, disable save button (Leaf: and no invalid bar left over from before Esc)
            shortcutDialog.IsPrimaryButtonEnabled = false;
            c.IsError = false;
        }
        else if (internalSettings.GetKeysList().Count == 1)
        {
            // 1 key, disable save button
            shortcutDialog.IsPrimaryButtonEnabled = false;

            // Check if the one key is a hotkey
            if (internalSettings.Shift || internalSettings.Win || internalSettings.Alt || internalSettings.Ctrl)
            {
                c.IsError = false;
            }
            else
            {
                c.IsError = true;
            }
        }

        // Tab and Shift+Tab are accessible keys and should not be displayed in the hotkey control.
        if (internalSettings.Code > 0 && !internalSettings.IsAccessibleShortcut())
        {
            lastValidSettings = internalSettings with { };

            if (!ComboIsValid(lastValidSettings))
            {
                DisableKeys();
            }
            else
            {
                EnableKeys();

                if (lastValidSettings.IsValid())
                {
                    if (hotkeySettings != null && string.Equals(lastValidSettings.ToString(), hotkeySettings.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        c.HasConflict = false;
                        c.ConflictMessage = string.Empty;
                    }
                    else
                    {
                        // Check for conflicts with the new hotkey settings
                        CheckForConflicts(lastValidSettings);
                    }
                }
            }
        }

        c.IsWarningAltGr = internalSettings.Ctrl && internalSettings.Alt && !internalSettings.Win && (internalSettings.Code > 0);
    }

    // Leaf: the other shortcut, or Windows or another app, may hold it; a taken combination can't be saved
    private void CheckForConflicts(HotkeySettings settings)
    {
        var message = settings.ToHotkey() is { } hotkey ? CheckConflict?.Invoke(hotkey) : null;
        c.ConflictMessage = message ?? string.Empty;
        c.HasConflict = message is not null;
        if (message is not null)
        {
            shortcutDialog.IsPrimaryButtonEnabled = false;
        }
    }

    private void EnableKeys()
    {
        shortcutDialog.IsPrimaryButtonEnabled = true;
        c.IsError = false;
    }

    private void DisableKeys()
    {
        shortcutDialog.IsPrimaryButtonEnabled = false;
        c.IsError = true;
    }

    private void Hotkey_KeyUp(int key)
    {
        KeyEventHandler(key, false, 0);
    }

    // Leaf: only while the dialog listens and this window is in front (the keyboard goes back to an app you switch to)
    private bool Hotkey_IsActive()
    {
        return _isActive && PInvoke.GetForegroundWindow() == new HWND(_window);
    }

    private void ShortcutDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (!ComboIsValid(hotkeySettings))
        {
            DisableKeys();
        }
        else
        {
            EnableKeys();
        }

        // Reset the status on entering the hotkey each time.
        _modifierKeysOnEntering.Clear();

        // To keep track of the modifier keys, whether it was pressed on entering.
        if (KeyboardHook.IsDown((int)VirtualKey.Shift))
        {
            _modifierKeysOnEntering.Add(VirtualKey.Shift);
        }

        if (KeyboardHook.IsDown((int)VirtualKey.Control))
        {
            _modifierKeysOnEntering.Add(VirtualKey.Control);
        }

        if (KeyboardHook.IsDown((int)VirtualKey.Menu))
        {
            _modifierKeysOnEntering.Add(VirtualKey.Menu);
        }

        if (KeyboardHook.IsDown((int)VirtualKey.LeftWindows))
        {
            _modifierKeysOnEntering.Add(VirtualKey.LeftWindows);
        }

        if (KeyboardHook.IsDown((int)VirtualKey.RightWindows))
        {
            _modifierKeysOnEntering.Add(VirtualKey.RightWindows);
        }

        // Leaf: the hook lives only while the dialog is open
        hook?.Dispose();
        hook = new HotkeySettingsControlHook(Hotkey_KeyDown, Hotkey_KeyUp, Hotkey_IsActive, FilterAccessibleKeyboardEvents);
        _isActive = true;

        // Leaf: Windows refused the hook, so nothing can be pressed: close, and say so once the dialog is gone
        if (!hook.IsHooked)
        {
            _hookFailed = true;
            shortcutDialog.Hide();
        }
    }

    private async void OpenDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogOpen)
        {
            return;
        }

        _isDialogOpen = true;
        _dialogOpen   = true;
        _hookFailed   = false;
        try
        {
            List<object> newKeys = HotkeySettings?.GetKeysList() ?? [];

            if (c.Keys == null || !c.JudgeIfKeyValueSame(newKeys))
            {
                c.Keys = null;
                c.Keys = newKeys;
            }

            internalSettings = new HotkeySettings();
            lastValidSettings = hotkeySettings;
            c.KeysName = KeysName(HotkeySettings);
            c.HasConflict = false;
            c.ConflictMessage = string.Empty;
            c.IsError = false;

            // The logic is: warning should be visible if the shortcut contains Alt AND contains Ctrl AND NOT contains Win.
            // Additional key must be present, as this is a valid, previously used shortcut shown at dialog open. Check for presence of non-modifier-key is not necessary therefore
            c.IsWarningAltGr = HotkeySettings is { Ctrl: true, Alt: true, Win: false };

            _window = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            DialogOpening?.Invoke(this, EventArgs.Empty);
            shortcutDialog.XamlRoot = this.XamlRoot;
            shortcutDialog.RequestedTheme = this.ActualTheme;
            await shortcutDialog.ShowAsync();
        }
        catch (Exception)
        {
            // async void: nothing may escape (PowerToys logs it; the dialog just doesn't show)
        }
        finally
        {
            EndDialog();
        }

        // Leaf: the hook failed (PowerToys shows its keyboard-hook error the same way, once the picker is closed)
        if (_hookFailed && XamlRoot is { } root)
        {
            try
            {
                await new ContentDialog
                {
                    XamlRoot = root,
                    RequestedTheme = ActualTheme,
                    Title = "Activation shortcut",
                    Content = "Leaf couldn't listen to the keyboard, so the shortcut can't be changed right now. Try again.",
                    CloseButtonText = "OK",
                }.ShowAsync();
            }
            catch (Exception)
            {
                // async void: nothing may escape
            }
        }
    }

    /// <summary>
    /// Closes the dialog without saving and lets go of the keyboard (Leaf: the Settings window closed, or the page
    /// unloaded, while it was open).
    /// </summary>
    public void CloseDialog()
    {
        if (!_dialogOpen)
        {
            return;
        }

        try
        {
            shortcutDialog.Hide();
        }
        catch (Exception)
        {
            // The window is already going away; the cleanup below still runs
        }

        EndDialog();
    }

    // Leaf: everything a closed dialog must undo, once, whichever way it closed (the hook goes, and DialogClosed lets the
    // page register Leaf's shortcuts again); ShowAsync may never return when the window closes under it
    private void EndDialog()
    {
        if (!_dialogOpen)
        {
            return;
        }

        _dialogOpen = false;
        _isActive = false;
        hook?.Dispose();
        hook = null;
        _isDialogOpen = false;
        DialogClosed?.Invoke(this, EventArgs.Empty);
    }

    private void ShortcutControl_Unloaded(object sender, RoutedEventArgs e)
    {
        CloseDialog();
        Dispose();
    }

    private void C_ResetClick(object sender, RoutedEventArgs e)
    {
        // Leaf: Reset is the default shortcut
        hotkeySettings = DefaultHotkeySettings with { };
        SetKeys();

        lastValidSettings = hotkeySettings;
        shortcutDialog.Hide();
        HotkeySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void C_ClearClick(object sender, RoutedEventArgs e)
    {
        hotkeySettings = new HotkeySettings();
        SetKeys();

        lastValidSettings = hotkeySettings;
        shortcutDialog.Hide();
        HotkeySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShortcutDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ComboIsValid(lastValidSettings) && lastValidSettings is { } saved)
        {
            HotkeySettings = saved;
            HotkeySettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        SetKeys();
        shortcutDialog.Hide();
    }

    private static bool ComboIsValid(HotkeySettings? settings)
    {
        if (settings != null && (settings.IsValid() || settings.IsEmpty()))
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    private void ShortcutDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _isActive = false;
        hook?.Dispose();
        hook = null;
        lastValidSettings = hotkeySettings;
    }

    private void SetKeys()
    {
        var keys = HotkeySettings?.GetKeysList();

        if (keys != null && keys.Count > 0)
        {
            VisualStateManager.GoToState(this, "Configured", true);
            PreviewKeysControl.Children.Clear();
            foreach (var key in keys)
            {
                var keyVisual = new KeyVisual
                {
                    MinWidth = 36,
                    Padding = new Thickness(8, 8, 8, 8),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = key,
                    CornerRadius = new CornerRadius(4),
                    IsTabStop = false,
                    RenderKeyAsGlyph = true,
                    State = _hasConflict ? KeyVisualState.Warning : KeyVisualState.Normal,
                    Style = (Style)Application.Current.Resources["AccentKeyVisualStyle"],
                };
                AutomationProperties.SetAccessibilityView(keyVisual, AccessibilityView.Raw);
                PreviewKeysControl.Children.Add(keyVisual);
            }

            AutomationProperties.SetHelpText(EditButton, HotkeySettings!.ToHotkey()?.ToString() ?? HotkeySettings.ToString());
        }
        else
        {
            VisualStateManager.GoToState(this, "Normal", true);
            AutomationProperties.SetHelpText(EditButton, "None");
        }
    }

    /// <summary>Removes the keyboard hook if a dialog is still open.</summary>
    public void Dispose()
    {
        hook?.Dispose();
        hook = null;
    }

    // The keys as text for screen readers and tests: Leaf's text for a shortcut, else the caps joined ("Shift+J")
    private static string KeysName(HotkeySettings? settings) =>
        settings is null || settings.IsEmpty() ? "No keys" : settings.ToHotkey()?.ToString() ?? settings.ToString();
}
