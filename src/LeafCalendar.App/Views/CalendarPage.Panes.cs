using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side panes' slide. Each pane lies over the page and slides in or out on the compositor's own thread (one
/// composition animation on the island fill's edge, which everything else follows), so it starts on the next frame
/// and stays smooth however busy the UI thread gets. The island is never stretched: while a pane slides it keeps the
/// wider of its old and new room, its left edge rides the sidebar's edge, and the part a pane covers is clipped. The
/// one relayout to the narrower room happens when an opening pane has finished sliding over it (a closing pane frees
/// its room at the start, under the pane, before the slide begins), so the day columns never redraw per frame.
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

    // Each slide's number: only the latest one for a pane may collapse it or settle its room when it ends
    int _sidebarSlide;
    int _detailsSlide;

    /// <summary>True when the sidebar takes up room (from the moment it starts to open until it starts to close).</summary>
    public bool IsSidebarOpen => _sidebarOpen;

    /// <summary>True when the details panel takes up room.</summary>
    public bool IsDetailsOpen => _detailsOpen;

    /// <summary>
    /// Moves an element with the sidebar's edge on the compositor.
    /// </summary>
    /// <remarks>
    /// The element is drawn at <paramref name="closed"/> while the sidebar is shut, at <paramref name="open"/> while it's
    /// open, and eased between them in step with the slide, so it can't run ahead of or behind the pane.
    /// <paramref name="atRest"/> is where its layout puts it now (its margin), so a resting element has no offset and
    /// its click target matches what's drawn. All three share one axis (any origin).
    /// </remarks>
    /// <param name="element">The element to move (its Translation is taken over).</param>
    /// <param name="closed">Its spot with the sidebar closed.</param>
    /// <param name="open">Its spot with the sidebar open.</param>
    /// <param name="atRest">Its spot in the current layout.</param>
    public void RideSidebarEdge(UIElement element, double closed, double open, double atRest)
    {
        EnsurePaneVisuals();
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var ride   = visual.Compositor.CreateExpressionAnimation("closed + (open - closed) * fill.LeftInset / width - rest");
        ride.SetReferenceParameter("fill", _fillClip!);
        ride.SetScalarParameter("closed", (float)closed);
        ride.SetScalarParameter("open", (float)open);
        ride.SetScalarParameter("width", (float)SidebarWidth);
        ride.SetScalarParameter("rest", (float)atRest);
        visual.StartAnimation("Translation.X", ride);
    }

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

        ElementCompositionPreview.SetIsTranslationEnabled(IslandArea, true);
        ElementCompositionPreview.SetIsTranslationEnabled(Sidebar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(DetailsPane, true);
        SetIslandRoom(IslandArea.Margin);
    }

    // Opens or closes one pane: the pane and the fill's edge slide there from wherever they are; the island keeps the
    // wider room until the slide no longer needs it
    void SlidePane(bool sidebar, bool open, bool animate, Action? started = null)
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

        // Already Headed There (a slide that's running keeps going)
        if (animate && was == open)
        {
            started?.Invoke();
            return;
        }

        var pane     = sidebar ? (UIElement)Sidebar : DetailsPane;
        var width    = (float)(sidebar ? SidebarWidth : DetailsWidth);
        var fillTo   = open ? width : 0;
        var inset    = sidebar ? "LeftInset" : "RightInset";
        var duration = open ? PaneOpenDuration : PaneCloseDuration;

        // One Driver: the fill's edge. The pane rides on it (its edge is the fill's edge), and so do the island and its
        // clip, all in the compositor's same frame, so nothing shows between them, and a toggle mid-slide turns around
        // from wherever the edge is now
        var compositor = _fillClip!.Compositor;
        var visual     = ElementCompositionPreview.GetElementVisual(pane);
        var ride       = compositor.CreateExpressionAnimation(sidebar ? "fill.LeftInset - width" : "width - fill.RightInset");
        ride.SetReferenceParameter("fill", _fillClip);
        ride.SetScalarParameter("width", width);
        visual.StartAnimation("Translation.X", ride);

        pane.Visibility = Visibility.Visible;
        var slide = sidebar ? ++_sidebarSlide : ++_detailsSlide;

        // No Slide (the first layout, or animations turned off in Windows): the edge and the island's room jump there
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            _fillClip.StopAnimation(inset);
            SetInset(_fillClip, sidebar, fillTo);
            pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SetIslandRoom(FinalRoom());
            started?.Invoke();
            return;
        }

        // Room While Sliding: the wider of old and new on each side (a closing pane frees its room now, an opening one
        // takes it once it's done)
        var room    = FinalRoom();
        var current = IslandArea.Margin;
        var during  = new Thickness(Math.Min(current.Left, room.Left), 0, Math.Min(current.Right, room.Right), 0);
        var changed = during != current;
        SetIslandRoom(during);

        // Slide The Edge, once the island has been laid out in its wider room (that relayout stalls the UI thread; the
        // compositor would run the slide through the stall). When it ends, an opened pane's side settles into its
        // narrower room, and a closed pane is collapsed, so its controls leave the tab order
        void Start()
        {
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _fillClip.StartAnimation(inset, Slide(compositor, fillTo, duration));
            batch.End();
            batch.Completed += (_, _) =>
            {
                if (slide != (sidebar ? _sidebarSlide : _detailsSlide))
                {
                    return;
                }

                if (!open)
                {
                    pane.Visibility = Visibility.Collapsed;
                    return;
                }

                var settled = IslandArea.Margin;
                SetIslandRoom(sidebar
                    ? new Thickness(SidebarWidth, 0, settled.Right, 0)
                    : new Thickness(settled.Left, 0, DetailsWidth, 0));
            };
            started?.Invoke();
        }

        if (changed)
        {
            AfterIslandLayout(Start);
        }
        else
        {
            Start();
        }
    }

    // The island's room with the panes as they are now
    Thickness FinalRoom() => new(_sidebarOpen ? SidebarWidth : 0, 0, _detailsOpen ? DetailsWidth : 0, 0);

    // Runs after the next layout pass of the island has finished and the UI thread has caught up
    readonly List<Action> _afterLayout = [];

    void AfterIslandLayout(Action run)
    {
        _afterLayout.Add(run);
        if (_afterLayout.Count > 1)
        {
            return;
        }

        void Settled(object? sender, object e)
        {
            IslandArea.LayoutUpdated -= Settled;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var pending = _afterLayout.ToArray();
                _afterLayout.Clear();
                foreach (var action in pending)
                {
                    action();
                }
            });
        }

        IslandArea.LayoutUpdated += Settled;
    }

    // Lays the island out in this room, then has the compositor draw it riding the fill's edges: its left edge on the
    // sidebar's edge (a shift, never a stretch), and its right end clipped where the details pane's edge is. At rest
    // both are zero. The margin and the expressions land in the same frame, so the island never shows a stray offset.
    void SetIslandRoom(Thickness room)
    {
        IslandArea.Margin = room;
        if (_fillClip is null || _islandClip is null)
        {
            return;
        }

        var compositor = _fillClip.Compositor;
        var left       = (float)room.Left;
        var right      = (float)room.Right;

        var shift = compositor.CreateExpressionAnimation("fill.LeftInset - left");
        shift.SetReferenceParameter("fill", _fillClip);
        shift.SetScalarParameter("left", left);
        ElementCompositionPreview.GetElementVisual(IslandArea).StartAnimation("Translation.X", shift);

        var clip = compositor.CreateExpressionAnimation("Max(0, fill.LeftInset + fill.RightInset - room)");
        clip.SetReferenceParameter("fill", _fillClip);
        clip.SetScalarParameter("room", left + right);
        _islandClip.StartAnimation("RightInset", clip);
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
