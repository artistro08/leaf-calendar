using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LeafCalendar.UITests.Support;

/// <summary>Where the ink sits in a captured region, in screen pixels (<see cref="Right"/> and <see cref="Bottom"/> exclusive).</summary>
public readonly record struct InkBox(int Left, int Top, int Right, int Bottom, double WeightedCenterY)
{
    /// <summary>Halfway between the top and bottom ink rows.</summary>
    public double CenterY => (Top + Bottom) / 2.0;

    /// <summary>Ink width in pixels.</summary>
    public int Width => Right - Left;

    /// <inheritdoc />
    public override string ToString() => $"box {Left},{Top}-{Right},{Bottom} (mid {CenterY:0.00}, weighted {WeightedCenterY:0.00})";
}

/// <summary>
/// Measures ink (glyphs, text) in screen captures. Ink is any pixel whose lightness differs from the background by
/// more than 0.2; the background is the median lightness of the region's outermost pixels, so a stray corner pixel
/// or a button's rounded edge doesn't decide it.
/// </summary>
public sealed class Ink : IDisposable
{
    const float Threshold = 0.2f;

    readonly Bitmap _bitmap;
    readonly Point _origin;
    readonly float _background;

    Ink(Rectangle region)
    {
        using var shot = FlaUI.Core.Capturing.Capture.Rectangle(region);
        _bitmap     = new Bitmap(shot.Bitmap);
        _origin     = region.Location;
        _background = Median(Border(_bitmap).ToList());
    }

    /// <summary>The background's lightness (0 black to 1 white).</summary>
    public float Background => _background;

    /// <summary>Captures <paramref name="region"/> (screen pixels).</summary>
    public static Ink Capture(Rectangle region) => new(region);

    /// <summary>The ink box of the whole region, or of columns [<paramref name="fromX"/>, <paramref name="toX"/>) in screen x; <paramref name="inset"/> pixels at each edge are skipped. Null when there's no ink.</summary>
    public InkBox? Measure(int inset = 0, int? fromX = null, int? toX = null)
    {
        // No bounds means the whole region (an int.MinValue default minus the origin would wrap around to a huge left edge)
        var left   = Math.Max(inset, fromX is { } from ? from - _origin.X : 0);
        var right  = Math.Min(_bitmap.Width - inset, toX is { } to ? to - _origin.X : _bitmap.Width);
        int top    = int.MaxValue, bottom = -1, first = int.MaxValue, last = -1;
        double sum = 0, weights = 0;

        for (var x = left; x < right; x++)
        {
            for (var y = inset; y < _bitmap.Height - inset; y++)
            {
                var weight = Math.Abs(_bitmap.GetPixel(x, y).GetBrightness() - _background);
                if (weight <= Threshold)
                {
                    continue;
                }

                (top, bottom, first, last) = (Math.Min(top, y), Math.Max(bottom, y), Math.Min(first, x), Math.Max(last, x));
                sum     += weight * (y + 0.5);
                weights += weight;
            }
        }

        return bottom < 0 ? null : new InkBox(_origin.X + first, _origin.Y + top, _origin.X + last + 1, _origin.Y + bottom + 1, _origin.Y + sum / weights);
    }

    /// <summary>Each run of neighboring ink columns, left to right, as screen x ranges [From, To).</summary>
    public List<(int From, int To)> Runs(int inset = 0)
    {
        var runs = new List<(int From, int To)>();
        int? start = null;
        for (var x = inset; x <= _bitmap.Width - inset; x++)
        {
            var ink = x < _bitmap.Width - inset && Enumerable.Range(inset, _bitmap.Height - 2 * inset).Any(y => Math.Abs(_bitmap.GetPixel(x, y).GetBrightness() - _background) > Threshold);
            if (ink && start is null)
            {
                start = x;
            }
            else if (!ink && start is { } s)
            {
                runs.Add((_origin.X + s, _origin.X + x));
                start = null;
            }
        }

        return runs;
    }

    /// <summary>Saves the capture blown up <paramref name="zoom"/> times (nearest neighbor) as a PNG, with a red guide line across at screen y <paramref name="guideY"/>.</summary>
    public void SaveZoomed(string path, int zoom, double guideY)
    {
        using var zoomed   = new Bitmap(_bitmap.Width * zoom, _bitmap.Height * zoom);
        using var graphics = Graphics.FromImage(zoomed);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode   = PixelOffsetMode.Half;
        graphics.DrawImage(_bitmap, 0, 0, zoomed.Width, zoomed.Height);

        var y = (float)((guideY - _origin.Y) * zoom);
        using var pen = new Pen(Color.Red, 1);
        graphics.DrawLine(pen, 0, y, zoomed.Width, y);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        zoomed.Save(path, ImageFormat.Png);
    }

    /// <inheritdoc />
    public void Dispose() => _bitmap.Dispose();

    static IEnumerable<float> Border(Bitmap b)
    {
        for (var x = 0; x < b.Width; x++)
        {
            yield return b.GetPixel(x, 0).GetBrightness();
            yield return b.GetPixel(x, b.Height - 1).GetBrightness();
        }

        for (var y = 1; y < b.Height - 1; y++)
        {
            yield return b.GetPixel(0, y).GetBrightness();
            yield return b.GetPixel(b.Width - 1, y).GetBrightness();
        }
    }

    static float Median(List<float> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
