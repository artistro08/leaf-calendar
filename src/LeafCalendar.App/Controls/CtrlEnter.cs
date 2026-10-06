using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace LeafCalendar.App.Controls;

/// <summary>Ctrl+Enter presses a dialog's primary button from anywhere in it, a text box included.</summary>
/// <remarks>
/// Not for a dialog whose primary button is destructive (Disconnect, Leave setup, the conflict choices) or one that
/// records keys (the shortcut recorder). Nothing happens while the primary button is off.
/// </remarks>
internal static class CtrlEnter
{
    /// <summary>
    /// Makes Ctrl+Enter submit <paramref name="dialog"/>. <paramref name="first"/> runs before the button is checked,
    /// for a dialog whose box holds a pick that Enter would have added (it may turn the button on).
    /// </summary>
    public static void Submits(ContentDialog dialog, Action? first = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // Seen on the way down, before a box takes Enter for itself
        dialog.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            // A fresh press only: Ctrl+Enter still held from the editor (its save opened this dialog) repeats into it
            if (e.Key != VirtualKey.Enter || !KeyState.IsDown(VirtualKey.Control) || e.KeyStatus.WasKeyDown)
            {
                return;
            }

            // After The Box Has Caught Up With The Typing (its text lags a quick key)
            e.Handled = true;
            dialog.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                first?.Invoke();
                if (!dialog.IsPrimaryButtonEnabled || string.IsNullOrEmpty(dialog.PrimaryButtonText) || TemplatePart.Find<Button>(dialog, "PrimaryButton") is not { IsEnabled: true } button)
                {
                    return;
                }

                // Focus leaves the field first, so a box that takes its value on losing focus (a number box) has it in
                button.Focus(FocusState.Programmatic);
                dialog.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => new ButtonAutomationPeer(button).Invoke());
            });
        }), handledEventsToo: true);
    }
}
