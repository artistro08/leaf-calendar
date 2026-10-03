using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LeafCalendar.App.Controls;

/// <summary>
/// An event that can't be dragged still gives a little with the pointer (less the farther it's pulled) and springs back
/// when the button is let go, so the press reads as "held, but not going anywhere". The element's translate transform
/// is created and held by its owner (never read back from the element: a Native AOT trap).
/// </summary>
internal static class ElasticNudge
{
    // How much of the pointer's travel the event follows, and how far it goes at most
    const double Give  = 0.3;
    const double Reach = 24;

    static readonly TimeSpan SnapDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>Moves the element by a fraction of the pointer's travel from the press, up to its reach.</summary>
    public static void Pull(TranslateTransform transform, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(transform);

        transform.X = Math.Clamp(dx * Give, -Reach, Reach);
        transform.Y = Math.Clamp(dy * Give, -Reach, Reach);
    }

    /// <summary>Springs the element back to its place with an elastic ease.</summary>
    public static void SnapBack(TranslateTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        // The base value is put back at 0 first, and the animation stops rather than holds at its end, so the next
        // pull sets the property directly again (a held animation would ignore it)
        var (fromX, fromY) = (transform.X, transform.Y);
        transform.X = 0;
        transform.Y = 0;

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(transform, "X", fromX));
        storyboard.Children.Add(Animate(transform, "Y", fromY));
        storyboard.Begin();
    }

    static DoubleAnimation Animate(TranslateTransform transform, string property, double from)
    {
        var animation = new DoubleAnimation
        {
            From           = from,
            To             = 0,
            Duration       = SnapDuration,
            FillBehavior   = FillBehavior.Stop,
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 6 },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }
}
