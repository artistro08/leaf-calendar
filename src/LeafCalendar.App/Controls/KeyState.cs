using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace LeafCalendar.App.Controls;

/// <summary>Keyboard modifier state for keys and pointer gestures (Task 15 adds the click rule here).</summary>
public static class KeyState
{
    /// <summary>True while <paramref name="key"/> is held.</summary>
    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
