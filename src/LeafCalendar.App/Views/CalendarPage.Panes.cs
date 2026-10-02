using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side panes' slide. Each pane sits in its own column whose width is the pane's slot; opening or closing animates
/// the slot's width with WinUI SplitView's timing and curve. The pane keeps its full width inside the slot (anchored to
/// the island's side), so it slides in or out from under the slot's edge, while the calendar island, star sized between
/// the slots, lays out at every step of the slide exactly as it does while the window is resized: its edge moves with
/// the pane's from the first frame to the last, with nothing to snap at either end.
/// </summary>
public sealed partial class CalendarPage
{
    // WinUI SplitView's timing and curve (the title bar toolbar slides on the same ones)
    static readonly TimeSpan PaneOpenDuration  = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan PaneCloseDuration = TimeSpan.FromMilliseconds(100);

    bool _sidebarOpen = true;
    bool _detailsOpen = true;

    // Each slot's running slide (a new one takes over from wherever the slot's width is)
    Storyboard? _sidebarSlide;
    Storyboard? _detailsSlide;

    /// <summary>True when the sidebar takes up room (from the moment it starts to open until it starts to close).</summary>
    public bool IsSidebarOpen => _sidebarOpen;

    /// <summary>True when the details panel takes up room.</summary>
    public bool IsDetailsOpen => _detailsOpen;

    // Opens or closes one pane by animating its slot's width from wherever it is now
    void SlidePane(bool sidebar, bool open, bool animate)
    {
        var was = sidebar ? _sidebarOpen : _detailsOpen;
        if (sidebar)
        {
            _sidebarOpen = open;
        }
        else
        {
            _detailsOpen = open;
        }

        // Already Headed There (a slide that's running keeps going)
        if (animate && was == open)
        {
            return;
        }

        var slot  = sidebar ? SidebarSlot : DetailsSlot;
        var pane  = sidebar ? (UIElement)Sidebar : DetailsPane;
        var to    = open ? (sidebar ? SidebarWidth : DetailsWidth) : 0;
        var from  = slot.ActualWidth;
        var slide = sidebar ? _sidebarSlide : _detailsSlide;
        slide?.Stop();
        pane.Visibility = Visibility.Visible;

        // No Slide (the first layout, or animations turned off in Windows): the slot takes its width at once
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            Settle(slot, pane, to, open);
            return;
        }

        // Slide The Slot's Width (a dependent animation: the island lays out each frame, as during a window resize)
        var width = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        width.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        width.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = open ? PaneOpenDuration : PaneCloseDuration, KeySpline = new KeySpline { ControlPoint1 = new(0, 0.35), ControlPoint2 = new(0.15, 1) }, Value = to });
        Storyboard.SetTarget(width, slot);
        Storyboard.SetTargetProperty(width, "Width");

        var storyboard = new Storyboard { Children = { width } };
        storyboard.Completed += (_, _) =>
        {
            if (storyboard == (sidebar ? _sidebarSlide : _detailsSlide))
            {
                Settle(slot, pane, to, open);
            }
        };

        if (sidebar)
        {
            _sidebarSlide = storyboard;
        }
        else
        {
            _detailsSlide = storyboard;
        }

        storyboard.Begin();
    }

    // The slot's resting width; a closed pane is collapsed so its controls leave the tab order
    static void Settle(FrameworkElement slot, UIElement pane, double width, bool open)
    {
        slot.Width      = width;
        pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
}
