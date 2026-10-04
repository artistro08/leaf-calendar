using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.Win32;

namespace LeafCalendar.App.Controls;

/// <summary>Keyboard modifier state for keys and pointer gestures, and the shared click-to-select rule.</summary>
public static class KeyState
{
    /// <summary>True while <paramref name="key"/> is held.</summary>
    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// The character a punctuation key (<c>VK_OEM_*</c>) types on this thread's keyboard layout with Shift as given, for
    /// the shortcuts defined by a character (? / . , - =; see <c>ShortcutMap.Resolve</c>). Null for any other key, a
    /// dead key, or a key that types nothing or several characters. Reading it never disturbs a dead key that is
    /// waiting for the next key.
    /// </summary>
    public static unsafe char? Typed(VirtualKey key, bool shift)
    {
        if (key is not ((>= (VirtualKey)0xBA and <= (VirtualKey)0xC0) or (>= (VirtualKey)0xDB and <= (VirtualKey)0xDF) or (VirtualKey)0xE2))
        {
            return null;
        }

        // Only Shift Counts (with Ctrl held the layout would type a control character)
        Span<byte> state = stackalloc byte[256];
        state.Clear();
        state[(int)VirtualKey.Shift] = shift ? (byte)0x80 : (byte)0;

        Span<char> typed = stackalloc char[4];
        int count;
        fixed (byte* keys = state)
        fixed (char* buffer = typed)
        {
            // Flag 0x4: Leave The Keyboard State Alone (Windows 10 1607 and later)
            count = PInvoke.ToUnicodeEx((uint)key, 0, keys, buffer, typed.Length, 0x4, PInvoke.GetKeyboardLayout(0));
        }

        return count == 1 ? typed[0] : null;
    }

    /// <summary>A click on an event: Ctrl or Shift adds it to (or takes it out of) the selection; a plain click selects just it.</summary>
    public static void SelectClicked(CalendarViewModel vm, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift))
        {
            vm.ToggleSelect(occurrence);
        }
        else
        {
            vm.Select(occurrence);
        }
    }
}
