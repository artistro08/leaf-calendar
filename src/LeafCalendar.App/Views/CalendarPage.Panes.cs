using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side panes' slide. Each pane lies over the page and slides in or out on the compositor's own thread (an
/// independent transform animation; the island's fill edge is a composition animation on the same timing), so it
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

    // The panes' slide transforms (kept: reading RenderTransform back fails its cast under Native AOT), and their slides
    readonly TranslateTransform _sidebarShift = new();
    readonly TranslateTransform _detailsShift = new();
    Storyboard? _sidebarStory;
    Storyboard? _detailsStory;

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

        // The Island's Final Room
        IslandArea.Margin = new Thickness(_sidebarOpen ? SidebarWidth : 0, 0, _detailsOpen ? DetailsWidth : 0, 0);

        var pane     = sidebar ? (UIElement)Sidebar : DetailsPane;
        var shift    = sidebar ? _sidebarShift : _detailsShift;
        var width    = (float)(sidebar ? SidebarWidth : DetailsWidth);
        var paneTo   = open ? 0 : sidebar ? -width : width;
        var fillTo   = open ? width : 0;
        var inset    = sidebar ? "LeftInset" : "RightInset";
        var duration = open ? PaneOpenDuration : PaneCloseDuration;

        // Already Headed There (a slide that's running keeps going)
        if (animate && was == open)
        {
            return;
        }

        // No Slide (the first layout, or animations turned off in Windows)
        (sidebar ? _sidebarStory : _detailsStory)?.Stop();
        pane.Visibility = Visibility.Visible;
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            _fillClip!.StopAnimation(inset);
            _islandClip!.StopAnimation(inset);
            shift.X = paneTo;
            SetInset(_fillClip, sidebar, fillTo);
            SetInset(_islandClip, sidebar, 0);
            pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // Pane Slide (an independent animation, run by the compositor; from where it is now, so a toggle mid-slide turns around)
        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime   = duration,
            KeySpline = new KeySpline { ControlPoint1 = new Windows.Foundation.Point(0, 0.35), ControlPoint2 = new Windows.Foundation.Point(0.15, 1) },
            Value     = paneTo,
        });
        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, "X");

        var story = new Storyboard { Children = { slide } };
        story.Completed += (_, _) =>
        {
            shift.X = paneTo;
            if (!open && !(sidebar ? _sidebarOpen : _detailsOpen))
            {
                pane.Visibility = Visibility.Collapsed;
            }
        };

        if (sidebar)
        {
            _sidebarStory = story;
        }
        else
        {
            _detailsStory = story;
        }

        story.Begin();

        // Island Fill Edge (the same timing and curve, from where it is now)
        var compositor = _fillClip!.Compositor;
        _fillClip.StartAnimation(inset, Slide(compositor, fillTo, duration));

        // The Island Shows Only Inside The Fill's Edge (closing: the space it already fills shows as the pane leaves it)
        var follow = compositor.CreateExpressionAnimation($"Max(fill.{inset} - room, 0)");
        follow.SetReferenceParameter("fill", _fillClip);
        follow.SetScalarParameter("room", open ? width : 0);
        _islandClip!.StartAnimation(inset, follow);
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
