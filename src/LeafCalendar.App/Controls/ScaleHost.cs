using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Draws its one child at <see cref="Scale"/> (Settings › General › Interface scale). The child is laid out in the
/// host's size divided by the scale, then scaled back up, so it fills the host exactly at any scale; hit testing
/// follows the transform. At 1 it lays out exactly as if the host weren't there.
/// </summary>
public sealed partial class ScaleHost : Panel
{
    readonly ScaleTransform _transform = new();
    double _scale = 1;

    /// <summary>The scale, from 0.5 to 2. Set it in code: it hides <see cref="UIElement.Scale"/> (a composition Vector3), which a XAML attribute would set instead.</summary>
    public new double Scale
    {
        get => _scale;
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0.5, 2) : 1;
            if (clamped == _scale)
            {
                return;
            }

            _scale                                 = clamped;
            (_transform.ScaleX, _transform.ScaleY) = (clamped, clamped);
            InvalidateMeasure();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        // The transform is ours, set on the child; never read back (AOT read-back rule)
        var child = Children[0];
        child.RenderTransform = _transform;
        child.Measure(new Size(availableSize.Width / _scale, availableSize.Height / _scale));
        return new Size(child.DesiredSize.Width * _scale, child.DesiredSize.Height * _scale);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A child that wants more room than it gets is arranged at what it wants, and the host clips at its own edge. Arranged
    /// smaller, XAML would clip the child to its unscaled slot, so a scaled-up child showed only its first 1/scale.
    /// </remarks>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        var child    = Children[0];
        var width    = finalSize.Width / _scale;
        var height   = finalSize.Height / _scale;
        var overflow = child.DesiredSize.Width > width || child.DesiredSize.Height > height;

        child.Arrange(new Rect(0, 0, Math.Max(width, child.DesiredSize.Width), Math.Max(height, child.DesiredSize.Height)));
        Clip = overflow ? new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) } : null;
        return finalSize;
    }
}
