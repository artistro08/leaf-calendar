using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LeafCalendar.App.Controls;

/// <summary>
/// An event that can't be dragged still gives a little with the pointer (less the farther it's pulled) and springs back
/// when the button is let go, so the press reads as "held, but not going anywhere". The element's translate transform
/// is created and held by its owner (never read back from the element: a Native AOT trap). While it gives, the element
/// draws over every other event, in its own day and the next.
/// </summary>
internal static class ElasticNudge
{
    // How much of the pointer's travel the event follows, and how far it goes at most
    private const double Give = 0.3;
    private const double Reach = 24;

    // Above every event and the drag ghost (20)
    private const int OnTop = 30;

    private static readonly TimeSpan SnapDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>Moves <paramref name="element"/> by a fraction of the pointer's travel from the press, up to its reach.</summary>
    public static void Pull(UIElement element, TranslateTransform transform, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(transform);

        Raise(element, true);
        transform.X = Math.Clamp(dx * Give, -Reach, Reach);
        transform.Y = Math.Clamp(dy * Give, -Reach, Reach);
    }

    /// <summary>Springs <paramref name="element"/> back to its place with an elastic ease, then lets it draw in order again.</summary>
    public static void SnapBack(UIElement element, TranslateTransform transform)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(transform);

        // The base value is put back at 0 first, and the animation stops rather than holds at its end, so the next
        // pull sets the property directly again (a held animation would ignore it)
        var (fromX, fromY) = (transform.X, transform.Y);
        transform.X = 0;
        transform.Y = 0;

        // With Windows Animation Effects Off It Just Goes Back
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            Raise(element, false);
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(transform, "X", fromX));
        storyboard.Children.Add(Animate(transform, "Y", fromY));
        // Unless a newer pull (a press again within the snap) has moved it since, so it keeps drawing on top
        storyboard.Completed += (_, _) =>
        {
            if (transform.X == 0 && transform.Y == 0)
            {
                Raise(element, false);
            }
        };
        storyboard.Begin();
    }

    private static DoubleAnimation Animate(TranslateTransform transform, string property, double from)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = SnapDuration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 6 },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    // The element over its siblings, and its panel (a day column or week row) over the next one it reaches into
    private static void Raise(UIElement element, bool on)
    {
        foreach (var target in new[] { element, VisualTreeHelper.GetParent(element) as UIElement })
        {
            if (target is null)
            {
                continue;
            }

            if (on)
            {
                Canvas.SetZIndex(target, OnTop);
            }
            else
            {
                target.ClearValue(Canvas.ZIndexProperty);
            }
        }
    }
}
