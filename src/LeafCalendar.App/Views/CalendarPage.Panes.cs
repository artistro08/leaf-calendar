using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side panes' slide. Each pane lies over the page and slides in or out on the compositor's own thread (one
/// composition animation on the island fill's edge, which everything else follows), so it starts on the next frame
/// and stays smooth however busy the UI thread gets. The island is laid out at its final size once, right after the
/// slide's first frames are on screen (the day columns redraw once, not per frame), and a composition scale and shift
/// stretch whichever layout it shows between the old and new edges while the pane slides, so the calendar resizes with
/// the pane. Only the day columns and what looks the same stretched are stretched (fills, grid lines, cards): text,
/// icons, round shapes, buttons, and thin lines are drawn at their own size the whole way, and the title, bars, and
/// hour labels keep to their side of the island (see <see cref="Unstretch"/>), so nothing looks squashed or jumps.
/// </summary>
public sealed partial class CalendarPage
{
    // WinUI SplitView's timing and curve (the title bar toolbar slides on the same ones)
    private static readonly TimeSpan PaneOpenDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PaneCloseDuration = TimeSpan.FromMilliseconds(100);

    private bool _sidebarOpen = true;
    private bool _detailsOpen = true;

    // Composition objects, built once and kept (never read back from XAML)
    private InsetClip? _fillClip;
    private CubicBezierEasingFunction? _paneEasing;

    // Each slide's number: only the latest one for a pane may collapse it when it ends
    private int _sidebarSlide;
    private int _detailsSlide;

    // Slides running now, and the visuals drawn at their own size while they run, with the center point each had
    // (released when the last slide ends)
    private int _slidesRunning;
    private readonly Dictionary<Visual, Held> _unstretched = [];

    // The day areas stretched while a slide runs, with the free room each was stretched for (its left and right)
    private readonly Dictionary<Visual, (float Left, float Right)> _days = [];

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
        var ride = visual.Compositor.CreateExpressionAnimation("closed + (open - closed) * fill.LeftInset / width - rest");
        ride.SetReferenceParameter("fill", _fillClip!);
        ride.SetScalarParameter("closed", (float)closed);
        ride.SetScalarParameter("open", (float)open);
        ride.SetScalarParameter("width", (float)SidebarWidth);
        ride.SetScalarParameter("rest", (float)atRest);
        visual.StartAnimation("Translation.X", ride);
    }

    // The island's stretch, and the details panel's width, follow the window's width
    private void OnIslandSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsurePaneVisuals();
        FitDetailsWidth();
        FollowFillWithIsland();
    }

    // The details panel grows with the window (DetailsWidthFor): the pane, and while it's open the fill's edge it rides
    // on and the island's room, move to the new width at once (a window resize, so no slide). Its own new room resizes
    // the island again, which finds the width unchanged
    private void FitDetailsWidth()
    {
        var width = DetailsWidthFor(Root.ActualWidth);
        if (width == DetailsWidth || Root.ActualWidth <= 0)
        {
            return;
        }

        DetailsWidth = width;
        DetailsPane.Width = width;
        RideFillEdge(sidebar: false);
        if (_detailsOpen)
        {
            _fillClip!.StopAnimation("RightInset");
            _fillClip.RightInset = (float)width;
            IslandArea.Margin = new Thickness(_sidebarOpen ? SidebarWidth : 0, 0, width, 0);
        }
    }

    // A pane rides on the fill's edge (its edge is the fill's edge), so the island's stretch and the pane move in the
    // compositor's same frame
    private void RideFillEdge(bool sidebar)
    {
        var pane = sidebar ? (UIElement)Sidebar : DetailsPane;
        var ride = _fillClip!.Compositor.CreateExpressionAnimation(sidebar ? "fill.LeftInset - width" : "width - fill.RightInset");
        ride.SetReferenceParameter("fill", _fillClip);
        ride.SetScalarParameter("width", (float)(sidebar ? SidebarWidth : DetailsWidth));
        ElementCompositionPreview.GetElementVisual(pane).StartAnimation("Translation.X", ride);
    }

    private void EnsurePaneVisuals()
    {
        if (_fillClip is not null)
        {
            return;
        }

        // The Island's Clip (keeps the time grid's scrolling content from drawing under the panes)
        var island = ElementCompositionPreview.GetElementVisual(Island);
        var fill = ElementCompositionPreview.GetElementVisual(IslandFill);
        _fillClip = fill.Compositor.CreateInsetClip();
        _paneEasing = fill.Compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0.35f), new Vector2(0.15f, 1));
        island.Clip = island.Compositor.CreateInsetClip();
        fill.Clip = _fillClip;
        _fillClip.LeftInset = _sidebarOpen ? (float)SidebarWidth : 0;
        _fillClip.RightInset = _detailsOpen ? (float)DetailsWidth : 0;

        ElementCompositionPreview.SetIsTranslationEnabled(IslandArea, true);
        ElementCompositionPreview.SetIsTranslationEnabled(Sidebar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(DetailsPane, true);
        FollowFillWithIsland();
    }

    // Opens or closes one pane: the island takes its final room now; the pane, the fill's edge, and the island
    // (stretched to the room between the edges) slide there from wherever they are
    private void SlidePane(bool sidebar, bool open, bool animate, Action? started = null)
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

        var pane = sidebar ? (UIElement)Sidebar : DetailsPane;
        var width = (float)(sidebar ? SidebarWidth : DetailsWidth);
        var fillTo = open ? width : 0;
        var inset = sidebar ? "LeftInset" : "RightInset";
        var duration = open ? PaneOpenDuration : PaneCloseDuration;
        var room = new Thickness(_sidebarOpen ? SidebarWidth : 0, 0, _detailsOpen ? DetailsWidth : 0, 0);

        // Already Headed There (a slide that's running keeps going)
        if (animate && was == open)
        {
            IslandArea.Margin = room;
            started?.Invoke();
            return;
        }

        // One Driver: the fill's edge. The pane rides on it (its edge is the fill's edge) and the island's stretch
        // follows it, all in the compositor's same frame, so nothing shows between them, and a toggle mid-slide turns
        // around from wherever the edge is now
        var compositor = _fillClip!.Compositor;
        RideFillEdge(sidebar);

        pane.Visibility = Visibility.Visible;
        var slide = sidebar ? ++_sidebarSlide : ++_detailsSlide;

        // No Slide (the first layout, or animations turned off in Windows): the edge jumps there
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            _fillClip.StopAnimation(inset);
            SetInset(_fillClip, sidebar, fillTo);
            IslandArea.Margin = room;
            pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            started?.Invoke();
            return;
        }

        // Hold The Island's Text And Icons At Their Own Size, the ones on screen now (in the frame that starts the slide)
        // and every new layout's until the slide is over
        HoldWhileSliding();

        // The Island's Final Room, once that frame is on screen (the relayout can hold the UI thread for a while, and the
        // frame would wait for it). The stretch reads the size the layout gives it, so the island fills the room between
        // the edges whichever layout it shows
        AfterNextFrame();

        // Slide The Edge, starting now. A closed pane is collapsed once its own slide ends, so its controls leave the tab order
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _fillClip.StartAnimation(inset, Slide(compositor, fillTo, duration));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!open && slide == (sidebar ? _sidebarSlide : _detailsSlide))
            {
                pane.Visibility = Visibility.Collapsed;
            }

            LetGoAfterSliding();
        };
        started?.Invoke();
    }

    // Waiting for the frame that starts a slide to be rendered (one wait covers toggles in the same frame)
    private bool _framePending;
    private bool _holding;

    private void AfterNextFrame()
    {
        if (_framePending)
        {
            return;
        }

        _framePending = true;
        CompositionTarget.Rendered += OnSlideFrame;
    }

    private void OnSlideFrame(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnSlideFrame;
        _framePending = false;
        IslandArea.Margin = new Thickness(_sidebarOpen ? SidebarWidth : 0, 0, _detailsOpen ? DetailsWidth : 0, 0);
    }

    // Counts the running slides; while any runs, every layout pass of the island holds its new elements at their size
    private void HoldWhileSliding()
    {
        Unstretch(IslandArea);
        _slidesRunning++;
        if (!_holding)
        {
            _holding = true;
            IslandArea.LayoutUpdated += OnSlidingLayout;
        }
    }

    // The last running slide is over: back to plain XAML
    private void LetGoAfterSliding()
    {
        if (--_slidesRunning > 0)
        {
            return;
        }

        if (_holding)
        {
            _holding = false;
            IslandArea.LayoutUpdated -= OnSlidingLayout;
        }

        ReleaseUnstretched();
    }

    private void OnSlidingLayout(object? sender, object e) => Unstretch(IslandArea);

    // The island is laid out at its final size at once (one relayout), then drawn stretched to the room the sliding
    // panes' edges leave, all on the compositor: scale and shift follow the fill's insets, so the island eases
    // to its place like the pane does, however long that one relayout stalls the UI thread. Both read the island's
    // laid-out spot and size from its own visual, so a new layout and its stretch always land in the same frame
    // (whenever the layout gets there). At rest both are identity.
    private void FollowFillWithIsland()
    {
        var width = (float)Root.ActualWidth;
        if (_fillClip is null || width <= 0)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(IslandArea);
        var compositor = visual.Compositor;

        var scale = compositor.CreateExpressionAnimation("(width - fill.LeftInset - fill.RightInset) / Max(1, this.Target.Size.X)");
        scale.SetReferenceParameter("fill", _fillClip);
        scale.SetScalarParameter("width", width);
        visual.StartAnimation("Scale.X", scale);

        var shift = compositor.CreateExpressionAnimation("fill.LeftInset - this.Target.Offset.X");
        shift.SetReferenceParameter("fill", _fillClip);
        visual.StartAnimation("Translation.X", shift);
    }

    // =========================================================================
    // DRAWN AT THEIR OWN SIZE WHILE THE ISLAND STRETCHES
    // =========================================================================

    // What the island holds at its own size while it's stretched: the visual's center point before (restored after),
    // and the width it was held at (a later layout pass only redoes what changed)
    private sealed record Held(Vector3 Center, float Width);

    // Walks the island and undoes the stretch on everything that would look squashed or moved by it. Each held element
    // is scaled by 1 / the island's scale around a center point that keeps it where it belongs:
    //   - The day columns (the time grid's day area, the whole month grid) stretch from their own left edge, which keeps
    //     its distance from the island's left (so the hour labels' edge never moves), to the island's right edge less
    //     whatever they leave free there. In them things move with their day: text, icons, dots, circles, and thin
    //     lines keep their size around the point their alignment holds still, and text that fills its slot and trims
    //     to it is clipped to the slot's stretched width, so it never runs past a narrower card.
    //   - Everywhere else (the period title, the bars, the time grid's corner and hour labels, the floating cards, the
    //     notice) nothing moves but with its own side of the island: each text, icon, and control keeps its size and
    //     its distance from the island's left edge, center, or right edge, whichever it's nearest.
    // Called as a slide starts and after each layout pass while it runs.
    private void Unstretch(UIElement root)
    {
        var island = ElementCompositionPreview.GetElementVisual(IslandArea);
        var compositor = island.Compositor;
        var room = (float)IslandArea.ActualWidth;
        var scale = compositor.CreateExpressionAnimation("1 / island.Scale.X");
        scale.SetReferenceParameter("island", island);
        Walk(root, null);

        void Walk(DependencyObject parent, Visual? days)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(parent, i) is not FrameworkElement child || child.Visibility != Visibility.Visible || child.ActualWidth <= 0)
                {
                    continue;
                }

                // Rows And Columns Kept Ready Off Screen (a repeater's cache) Stay As They Are: nobody sees them slide
                if (parent is ItemsRepeater && OffScreen(child))
                {
                    continue;
                }

                // The Day Columns Start Here
                var zone = days;
                if (zone is null && (child is Controls.MonthGridView || parent is Controls.TimeGridView && Grid.GetColumn(child) == 1))
                {
                    zone = StretchDays(child);
                }

                // A Row Of Text And Icons In A Day (a chip's dot and title, a card's title and time) Is Held As One
                var text = child is TextBlock or RichTextBlock || zone is not null && child is StackPanel { Orientation: Orientation.Horizontal };
                if (text || IsUnstretchable(child) || zone is null && child is Control)
                {
                    if (zone is not null)
                    {
                        Hold(child, zone, AnchorOf(child) * (float)child.ActualWidth, clip: FillsItsSlot(child));
                    }
                    else
                    {
                        Pin(child);
                    }

                    continue;
                }

                Walk(child, zone);
            }
        }

        // Outside the island's box (sideways or up and down)
        bool OffScreen(FrameworkElement element)
        {
            var box = element.TransformToVisual(IslandArea).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return box.Right < 0 || box.Left > room || box.Bottom < 0 || box.Top > IslandArea.ActualHeight;
        }

        // Stretches a day area from its left edge (its distance from the island's left kept) to the island's right edge
        // less the room it leaves free there. Inside the island's scale s it's scaled by k / s, k = (s * room - left -
        // right) / (room - left - right), and shifted by left * (1 - s) / s, so its left edge lands where it's laid out
        // and its right edge on the stretched island's right less that free room
        Visual StretchDays(FrameworkElement area)
        {
            var visual = ElementCompositionPreview.GetElementVisual(area);
            var left = (float)area.TransformToVisual(IslandArea).TransformPoint(default).X;
            var right = room - left - (float)area.ActualWidth;
            ElementCompositionPreview.SetIsTranslationEnabled(area, true);

            var key = (left, right);
            if (_days.TryGetValue(visual, out var laid) && laid == key)
            {
                return visual;
            }

            _days[visual] = key;
            visual.CenterPoint = visual.CenterPoint with { X = 0 };

            var stretch = compositor.CreateExpressionAnimation("(island.Scale.X * island.Size.X - left - right) / Max(1, island.Size.X - left - right) / island.Scale.X");
            stretch.SetReferenceParameter("island", island);
            stretch.SetScalarParameter("left", left);
            stretch.SetScalarParameter("right", right);
            visual.StartAnimation("Scale.X", stretch);

            var shift = compositor.CreateExpressionAnimation("left * (1 - island.Scale.X) / island.Scale.X");
            shift.SetReferenceParameter("island", island);
            shift.SetScalarParameter("left", left);
            visual.StartAnimation("Translation.X", shift);
            return visual;
        }

        // Keeps the element's distance from the island side it's nearest. Scaled by 1 / s around a point c (its own
        // coordinates) inside an island scaled by s, a point p at x in the island lands at s * x + (s - 1) * (c - p);
        // with c = side - x0 (x0 the element's left; side 0, half the island, or all of it), every point keeps its
        // distance from that side
        void Pin(FrameworkElement element)
        {
            var left = (float)element.TransformToVisual(IslandArea).TransformPoint(default).X;
            var middle = left + (float)element.ActualWidth / 2;
            var side = middle < room / 3 ? 0 : middle > room * 2 / 3 ? room : room / 2;
            Hold(element, null, side - left, clip: false);
        }

        // Scales the element by 1 / its stretch (the island's, times its day area's if it's in one) around the center point
        void Hold(FrameworkElement element, Visual? zone, float center, bool clip)
        {

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var width = (float)element.ActualWidth;
            var held = _unstretched.TryGetValue(visual, out var was);

            // Already Held At This Width (a later layout pass): only the center point can have moved
            if (!held)
            {
                _unstretched[visual] = new Held(visual.CenterPoint, width);
                if (zone is null)
                {
                    visual.StartAnimation("Scale.X", scale);
                }
                else
                {
                    var both = compositor.CreateExpressionAnimation("1 / (island.Scale.X * days.Scale.X)");
                    both.SetReferenceParameter("island", island);
                    both.SetReferenceParameter("days", zone);
                    visual.StartAnimation("Scale.X", both);
                }
            }

            visual.CenterPoint = new Vector3(center, visual.CenterPoint.Y, visual.CenterPoint.Z);
            if (held && was!.Width == width)
            {
                return;
            }

            _unstretched[visual] = new Held(held ? was!.Center : _unstretched[visual].Center, width);

            // The Slot's Stretched Width (only narrower than the text while the island is squeezed; at rest the insets are 0)
            if (clip)
            {
                var share = width == 0 ? 0 : center / width;
                var box = compositor.CreateInsetClip();
                StartInset(box, zone!, "LeftInset", share * width);
                StartInset(box, zone!, "RightInset", (1 - share) * width);
                visual.Clip = box;
            }
        }

        void StartInset(InsetClip box, Visual zone, string side, float share)
        {
            var inset = compositor.CreateExpressionAnimation("Max(0, share * (1 - island.Scale.X * days.Scale.X))");
            inset.SetReferenceParameter("island", island);
            inset.SetReferenceParameter("days", zone);
            inset.SetScalarParameter("share", share);
            box.StartAnimation(side, inset);
        }
    }

    // Text that fills its slot and trims to it (an event's title, a chip's dot and title): only it can run past a narrower slot
    private static bool FillsItsSlot(FrameworkElement element) =>
        element.HorizontalAlignment == HorizontalAlignment.Stretch
        && element is StackPanel { Orientation: Orientation.Horizontal } or TextBlock { TextTrimming: not TextTrimming.None };

    // Icons, images, dots, circles and pills (a corner radius of half the height or more), buttons (their rounded box
    // would cut their text off as it squeezes), and lines 3 px or thinner
    private static bool IsUnstretchable(FrameworkElement element) => element switch
    {
        IconElement or Image or Ellipse => true,
        ButtonBase => true,
        Rectangle line => line.ActualWidth <= 3,
        Border round when round.CornerRadius.TopLeft > 0 => round.CornerRadius.TopLeft * 2 >= Math.Min(round.ActualWidth, round.ActualHeight) - 1,
        _ => false,
    };

    // The point an element holds still when it keeps its own size in a stretched day: 0 left, 0.5 center, 1 right
    private static float AnchorOf(FrameworkElement element)
    {
        var alignment = element.HorizontalAlignment;
        if (alignment == HorizontalAlignment.Stretch && element is TextBlock text)
        {
            return text.TextAlignment switch
            {
                TextAlignment.Right => 1,
                TextAlignment.Center => 0.5f,
                _ => 0,
            };
        }

        return alignment switch
        {
            HorizontalAlignment.Right => 1,
            HorizontalAlignment.Center => 0.5f,
            _ => 0,
        };
    }

    // Back to plain XAML once the slides are over (a clip at rest would cut off text that draws past its box)
    private void ReleaseUnstretched()
    {
        foreach (var (visual, held) in _unstretched)
        {
            visual.StopAnimation("Scale.X");
            visual.Scale = Vector3.One;
            visual.CenterPoint = held.Center;
            visual.Clip = null;
        }

        foreach (var visual in _days.Keys)
        {
            visual.StopAnimation("Scale.X");
            visual.StopAnimation("Translation.X");
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }

        _unstretched.Clear();
        _days.Clear();
    }

    private ScalarKeyFrameAnimation Slide(Compositor compositor, float to, TimeSpan duration)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, _paneEasing);
        animation.Duration = duration;
        return animation;
    }

    private static void SetInset(InsetClip clip, bool left, float value)
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
