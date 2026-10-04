using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Keeps a <see cref="ScrollViewer"/>'s vertical scroll indicator out of sight unless the pointer is
/// over that scroller.
/// </summary>
/// <remarks>
/// WinUI shows the indicator whenever a scroller's view or extent changes, including changes the app
/// makes (a sync rebuilding the sidebar's calendar list, a pager or a pane toggle moving the grid), and
/// only hides it again on its own timer, which a busy list keeps restarting. So the bar is
/// <see cref="ScrollBarVisibility.Hidden"/> (still scrollable, nothing drawn) until the pointer moves,
/// wheels, or presses over the scroller, when WinUI's own auto-hiding indicator takes over, and hidden
/// again when the pointer leaves.
/// </remarks>
public static class ScrollIndicator
{
    /// <summary>Hides <paramref name="scroll"/>'s vertical indicator until the pointer is over it.</summary>
    public static void ShowOnHover(ScrollViewer scroll)
    {
        Hide(scroll);

        // Pointer Over: WinUI's Indicator
        var show = new PointerEventHandler((_, _) => Show(scroll));
        scroll.AddHandler(UIElement.PointerEnteredEvent, show, handledEventsToo: true);
        scroll.AddHandler(UIElement.PointerMovedEvent, show, handledEventsToo: true);
        scroll.AddHandler(UIElement.PointerPressedEvent, show, handledEventsToo: true);
        scroll.AddHandler(UIElement.PointerWheelChangedEvent, show, handledEventsToo: true);

        // Pointer Gone: Nothing
        var hide = new PointerEventHandler((_, _) => Hide(scroll));
        scroll.AddHandler(UIElement.PointerExitedEvent, hide, handledEventsToo: true);
        scroll.AddHandler(UIElement.PointerCanceledEvent, hide, handledEventsToo: true);
    }

    /// <summary>Hides the indicator now (before a scroll made in code), until the pointer next moves over the scroller.</summary>
    public static void Hide(ScrollViewer scroll)
    {
        if (scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Hidden)
        {
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
    }

    private static void Show(ScrollViewer scroll)
    {
        if (scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
        {
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }
}
