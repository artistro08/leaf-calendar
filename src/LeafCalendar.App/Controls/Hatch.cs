using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace LeafCalendar.App.Controls;

/// <summary>
/// A repeating diagonal-line overlay (WinUI has no hatch brush): 1 DIP lines at 45°, <see cref="Spacing"/> apart, drawn as
/// one path sized to what it covers. While you mark times to share, the time grid's days wear it behind the faded
/// events, so the calendar reads as a surface to mark rather than events to click.
/// </summary>
public static class Hatch
{
    /// <summary>Distance between the lines, along the edge.</summary>
    public const double Spacing = 8;

    /// <summary>A hidden overlay path, ready for <see cref="Draw"/> (it never takes clicks).</summary>
    public static Path Create() => new() { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    /// <summary>Shows <paramref name="hatch"/> over a <paramref name="width"/> × <paramref name="height"/> area in <paramref name="stroke"/>, or hides it.</summary>
    public static void Draw(Path hatch, bool show, double width, double height, Brush? stroke)
    {
        ArgumentNullException.ThrowIfNull(hatch);

        if (!show || width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            hatch.Visibility = Visibility.Collapsed;
            return;
        }

        // Lines Only For A New Size (a column redraws on every scroll step and data change; the lines rarely change)
        var size = new Size(width, height);
        if (hatch.Tag is not Size drawn || drawn != size)
        {
            hatch.Data = Lines(width, height);
            hatch.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
            hatch.Tag = size;
        }

        hatch.Stroke = stroke;
        hatch.Visibility = Visibility.Visible;
    }

    // Lines from the top edge down-left at 45°, starting far enough right that the bottom-right corner is covered too
    private static PathGeometry Lines(double width, double height)
    {
        var geometry = new PathGeometry();
        for (var x = Spacing / 2; x < width + height; x += Spacing)
        {
            var figure = new PathFigure { StartPoint = new Point(x, 0), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new LineSegment { Point = new Point(x - height, height) });
            geometry.Figures.Add(figure);
        }

        return geometry;
    }
}
