using LeafCalendar.App.Controls;
using LeafCalendar.Core.Tray;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Asks for a new global shortcut by listening for it: "Press the keys you want to use". Save is on only for a
/// combination that works (Ctrl, Alt, or Win with a letter, number, or F-key) and that <c>problem</c> doesn't object to
/// (already used by the other shortcut, or taken by another app). Returns null when canceled.
/// </summary>
internal static class ShortcutDialog
{
    /// <summary>Shows the dialog.</summary>
    public static async Task<Hotkey?> AskAsync(FrameworkElement owner, string title, Func<Hotkey, string?> problem)
    {
        Hotkey? picked = null;

        // Content (this method's own references; nothing is read back through the dialog)
        var hint    = new TextBlock { Text = "Press the keys you want to use, like Ctrl+Alt+J.", TextWrapping = TextWrapping.Wrap };
        var preview = new TextBlock { Text = "…", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var error   = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        AutomationProperties.SetAutomationId(preview, "ShortcutPreview");
        AutomationProperties.SetAutomationId(error, "ShortcutProblem");

        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(hint);
        content.Children.Add(preview);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot               = owner.XamlRoot,
            RequestedTheme         = owner.ActualTheme,
            Title                  = title,
            Content                = content,
            PrimaryButtonText      = "Save",
            CloseButtonText        = "Cancel",
            DefaultButton          = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        // Listen For The Combination
        dialog.PreviewKeyDown += (_, e) =>
        {
            // Modifier keys alone wait for the real key; Esc, Enter, and Tab on their own work as usual
            if (IsModifier(e.Key))
            {
                return;
            }

            var modifiers = Modifiers();
            if (modifiers == HotkeyModifiers.None && e.Key is VirtualKey.Escape or VirtualKey.Enter or VirtualKey.Tab)
            {
                return;
            }

            e.Handled = true;
            if (!Hotkey.TryCreate(modifiers, (int)e.Key, out var hotkey))
            {
                picked        = null;
                error.Message = "Use Ctrl, Alt, or the Windows key with a letter, a number, or F1–F24 (not F12).";
                error.IsOpen  = true;
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            preview.Text = hotkey.ToString();
            var objection = problem(hotkey);
            error.Message = objection ?? "";
            error.IsOpen  = objection is not null;
            picked        = objection is null ? hotkey : null;
            dialog.IsPrimaryButtonEnabled = picked is not null;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? picked : null;
    }

    static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    static HotkeyModifiers Modifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (KeyState.IsDown(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Ctrl;
        }

        if (KeyState.IsDown(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (KeyState.IsDown(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (KeyState.IsDown(VirtualKey.LeftWindows) || KeyState.IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }
}
