using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side panes' slide. Each pane lies over the page and slides in or out on the compositor's own thread (one
/// composition animation on the island fill's edge, which the pane's translation and the island's clip follow), so it
/// starts on the next frame and stays smooth however busy the UI thread gets. The calendar slides with the panes in
/// the same animation: its fill grows or shrinks with the sliding pane's edge, its left edge rides the sidebar's edge,
/// and its clip follows the details panel's edge. Its columns lay out only once per toggle: at the start of a close
/// (the room it gains is still under the closing pane) and at the end of an open (the opening pane slides over it until
/// then), so nothing snaps at either end. An inline SplitView did it the other way: it resized the island at once and
/// then slid all of it, details panel included, by the pane's width, so the calendar's center jumped on every toggle.
/// </summary>
public sealed partial class CalendarPage
{
    // WinUI SplitView's timing and curve (the title bar toolbar slides on the same ones)
    static readonly TimeSpan PaneOpenDuration  = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan PaneCloseDuration = TimeSpan.FromMilliseconds(100);

    bool _sidebarOpen = true;
    bool _detailsOpen = true;

    // Composition objects, built once and kept (never read back from XAML)
    InsetClip? _fillClip;
    InsetClip? _islandClip;
    CubicBezierEasingFunction? _paneEasing;

    // Each slide's number: only the latest one for a pane may collapse it when it ends
    int _sidebarSlide;
    int _detailsSlide;

    /// <summary>True when the sidebar takes up room (from the moment it starts to open until it starts to close).</summary>
    public bool IsSidebarOpen => _sidebarOpen;

    /// <summary>True when the details panel takes up room.</summary>
    public bool IsDetailsOpen => _detailsOpen;

    // The island's composition clip (it also keeps the time grid's scrolling content from drawing under the panes). A
    // new size means new margins, so the edge expressions take them in the same frame as the layout that applied them
    void OnIslandSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsurePaneVisuals();
        FollowEdges();
    }

    // The island rides the sidebar's sliding edge (its left edge is the fill's left edge), and its clip cuts off what
    // has slid under the details panel. Both are measured from the island's laid-out margins
    void FollowEdges()
    {
        var margin     = IslandArea.Margin;
        var compositor = _fillClip!.Compositor;

        var ride = compositor.CreateExpressionAnimation("fill.LeftInset - left");
        ride.SetReferenceParameter("fill", _fillClip);
        ride.SetScalarParameter("left", (float)margin.Left);
        ElementCompositionPreview.GetElementVisual(Island).StartAnimation("Translation.X", ride);

        var clip = compositor.CreateExpressionAnimation("Max(fill.RightInset - right + fill.LeftInset - left, 0)");
        clip.SetReferenceParameter("fill", _fillClip);
        clip.SetScalarParameter("left", (float)margin.Left);
        clip.SetScalarParameter("right", (float)margin.Right);
        _islandClip!.StartAnimation("RightInset", clip);
    }

    // The island's room for the panes that are open
    void ApplyIslandRoom() => IslandArea.Margin = new Thickness(_sidebarOpen ? SidebarWidth : 0, 0, _detailsOpen ? DetailsWidth : 0, 0);

    void EnsurePaneVisuals()
    {
        if (_islandClip is not null)
        {
            return;
        }

        var island  = ElementCompositionPreview.GetElementVisual(Island);
        var fill    = ElementCompositionPreview.GetElementVisual(IslandFill);
        _islandClip = island.Compositor.CreateInsetClip();
        _fillClip   = fill.Compositor.CreateInsetClip();
        _paneEasing = island.Compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0.35f), new Vector2(0.15f, 1));
        island.Clip = _islandClip;
        fill.Clip   = _fillClip;
        _fillClip.LeftInset  = _sidebarOpen ? (float)SidebarWidth : 0;
        _fillClip.RightInset = _detailsOpen ? (float)DetailsWidth : 0;

        ElementCompositionPreview.SetIsTranslationEnabled(Sidebar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(DetailsPane, true);
        ElementCompositionPreview.SetIsTranslationEnabled(Island, true);
    }

    // Opens or closes one pane: the pane, the fill's edge, the island's left edge, and its clip slide there from
    // wherever they are. The island's columns lay out once: at the start of a close (the room it gains is still under
    // the pane) and at the end of an open (until then the pane slides over it)
    void SlidePane(bool sidebar, bool open, bool animate)
    {
        EnsurePaneVisuals();
        var was = sidebar ? _sidebarOpen : _detailsOpen;
        if (sidebar)
        {
            _sidebarOpen = open;
        }
        else
        {
            _detailsOpen = open;
        }

        var pane     = sidebar ? (UIElement)Sidebar : DetailsPane;
        var width    = (float)(sidebar ? SidebarWidth : DetailsWidth);
        var fillTo   = open ? width : 0;
        var inset    = sidebar ? "LeftInset" : "RightInset";
        var duration = open ? PaneOpenDuration : PaneCloseDuration;

        // Already Headed There (a slide that's running keeps going)
        if (animate && was == open)
        {
            return;
        }

        // One Driver: the fill's edge. The pane rides on it (its edge is the fill's edge), and the island's edge and
        // clip follow it (FollowEdges), all in the compositor's same frame, so nothing shows between them, and a toggle
        // mid-slide turns around from wherever the edge is now
        var compositor = _fillClip!.Compositor;
        var visual     = ElementCompositionPreview.GetElementVisual(pane);
        var ride       = compositor.CreateExpressionAnimation(sidebar ? "fill.LeftInset - width" : "width - fill.RightInset");
        ride.SetReferenceParameter("fill", _fillClip);
        ride.SetScalarParameter("width", width);
        visual.StartAnimation("Translation.X", ride);

        pane.Visibility = Visibility.Visible;
        var slide = sidebar ? ++_sidebarSlide : ++_detailsSlide;

        // No Slide (the first layout, or animations turned off in Windows): the edge jumps there
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            _fillClip.StopAnimation(inset);
            SetInset(_fillClip, sidebar, fillTo);
            pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ApplyIslandRoom();
            return;
        }

        // Closing: the island takes its new room now (the part it gains is still under the pane)
        if (!open)
        {
            ApplyIslandRoom();
        }

        // Slide The Edge (opening, the island takes its room once the pane is there; a closed pane is collapsed once its
        // own slide ends, so its controls leave the tab order)
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _fillClip.StartAnimation(inset, Slide(compositor, fillTo, duration));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (slide != (sidebar ? _sidebarSlide : _detailsSlide))
            {
                return;
            }

            if (open)
            {
                ApplyIslandRoom();
            }
            else
            {
                pane.Visibility = Visibility.Collapsed;
            }
        };
    }
    ScalarKeyFrameAnimation Slide(Compositor compositor, float to, TimeSpan duration)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, _paneEasing);
        animation.Duration = duration;
        return animation;
    }

    static void SetInset(InsetClip clip, bool left, float value)
    {
        if (left)
        {
            clip.LeftInset = value;
        }
        else
        {
            clip.RightInset = value;
        }
    }
}
