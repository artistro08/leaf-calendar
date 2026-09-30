using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace LeafCalendar.App.Controls;

/// <summary>Keyboard modifier state for keys and pointer gestures, and the shared click-to-select rule.</summary>
public static class KeyState
{
    /// <summary>True while <paramref name="key"/> is held.</summary>
    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

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
