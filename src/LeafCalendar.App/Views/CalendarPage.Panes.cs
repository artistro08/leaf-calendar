using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side panes' slide. Each pane lies over the page and slides in or out on the compositor's own thread (one
/// composition animation on the island fill's edge, which the pane's translation and the island's clip follow), so it
/// starts on the next frame and stays smooth however busy the UI thread gets. The island never
/// slides: it takes its final size the moment a pane opens or closes, so the day columns, the scroll position, and
/// the right edge (with the toolbar over it) stay put. Its fill grows or shrinks with the sliding pane's edge, and
/// while a pane closes the island's clip uncovers the newly freed space at the same pace. An inline SplitView did
/// it the other way: it resized the island at once and then slid all of it, details panel included, by the
/// pane's width, so the calendar's center jumped on every toggle.
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

    // The island's composition clip (it also keeps the time grid's scrolling content from drawing under the panes)
    void OnIslandSizeChanged(object sender, SizeChangedEventArgs e) => EnsurePaneVisuals();

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
    }

    // Opens or closes one pane: the island takes its final room now; the pane, the fill's edge, and (closing)
    // the island's clip slide there from wherever they are
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

        // The Island's Final Room (it grows or shrinks with the pane's edge, a frame at a time, like Windows' navigation pane)
        var duration = open ? PaneOpenDuration : PaneCloseDuration;
        ResizeIsland(sidebar, open ? (sidebar ? SidebarWidth : DetailsWidth) : 0, animate && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled, duration);

        var pane     = sidebar ? (UIElement)Sidebar : DetailsPane;
        var width    = (float)(sidebar ? SidebarWidth : DetailsWidth);
        var fillTo   = open ? width : 0;
        var inset    = sidebar ? "LeftInset" : "RightInset";

        // Already Headed There (a slide that's running keeps going)
        if (animate && was == open)
        {
            return;
        }

        // One Driver: the fill's edge. The pane rides on it (its edge is the fill's edge) and the island's clip follows it,
        // all in the compositor's same frame, so nothing shows between them, and a toggle mid-slide turns around from
        // wherever the edge is now
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
            return;
        }

        // Slide The Edge (a closed pane is collapsed once its own slide ends, so its controls leave the tab order)
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _fillClip.StartAnimation(inset, Slide(compositor, fillTo, duration));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!open && slide == (sidebar ? _sidebarSlide : _detailsSlide))
            {
                pane.Visibility = Visibility.Collapsed;
            }
        };
    }

    // Island Margin Tween (the island's own room, eased like the pane's edge; the layout follows every frame)
    sealed class MarginTween
    {
        public double From, To;
        public long Start;
        public TimeSpan Duration;
        public bool Running;
    }

    readonly MarginTween _leftRoom  = new();
    readonly MarginTween _rightRoom = new();

    void ResizeIsland(bool left, double to, bool animate, TimeSpan duration)
    {
        var tween = left ? _leftRoom : _rightRoom;
        var now   = left ? IslandArea.Margin.Left : IslandArea.Margin.Right;

        tween.From     = now;
        tween.To       = to;
        tween.Start    = System.Diagnostics.Stopwatch.GetTimestamp();
        tween.Duration = duration;
        tween.Running  = animate && now != to;

        if (!tween.Running)
        {
            ApplyIslandRoom();
            return;
        }

        CompositionTarget.Rendering -= OnIslandFrame;
        CompositionTarget.Rendering += OnIslandFrame;
    }

    void OnIslandFrame(object? sender, object e)
    {
        ApplyIslandRoom();

        if (!_leftRoom.Running && !_rightRoom.Running)
        {
            CompositionTarget.Rendering -= OnIslandFrame;
        }
    }

    // Sets the island's margins to where each tween is now (a finished or stopped tween sits at its target)
    void ApplyIslandRoom()
    {
        IslandArea.Margin = new Thickness(Room(_leftRoom), 0, Room(_rightRoom), 0);

        static double Room(MarginTween tween)
        {
            if (!tween.Running)
            {
                return tween.To;
            }

            var t = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(tween.Start) / tween.Duration, 0, 1);
            tween.Running = t < 1;

            // Ease-out, near the pane's own curve (0, 0.35, 0.15, 1)
            return tween.From + (tween.To - tween.From) * (1 - Math.Pow(1 - t, 3));
        }
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
