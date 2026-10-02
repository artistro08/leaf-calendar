using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The sidebar and details panel toggles' icon: a rounded window outline (16 × 12, like Segoe Fluent's dock glyph)
/// with a narrow panel at one side. The panel is filled with the icon's color while its pane is open and empty while
/// it's closed. Drawn as vector paths laid out in whole physical pixels for the screen's scale (the stroke a whole
/// number of pixels thick, centered on pixel centers), so every edge is crisp and drawn once. Every glyph for a pane
/// follows <see cref="SetOpen"/>; the colors follow its <see cref="Control.Foreground"/> (set by its button: the title bar's
/// template binds it, the details toggle binds it by name), repainted on theme and contrast changes too, and copied as values,
/// never read back through a cast (a Native AOT trap).
/// </summary>
public sealed partial class PaneGlyph : UserControl
{
    // The glyphs on screen, per pane (a glyph inside a control template has no name to reach it by)
    static readonly List<PaneGlyph> Shown = [];
    static readonly Dictionary<string, bool> OpenPanes = [];

    // The Design (DIPs): the outline's size and corner, and the panel's width
    const double GlyphWidth  = 16;
    const double GlyphHeight = 12;
    const double Corner      = 3;
    const double PanelWidth  = 6;

    readonly Path _outline = new();
    readonly Path _panel   = new();
    readonly TranslateTransform _snap = new();
    double _scale;

    /// <summary>Creates the glyph.</summary>
    public PaneGlyph()
    {
        IsTabStop = false;
        Width     = GlyphWidth;
        Height    = GlyphHeight;
        Content   = new Canvas { Children = { _panel, _outline }, RenderTransform = _snap };
        LayoutUpdated += (_, _) => SnapToPixels();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Paint());

        // Theme And Contrast Changes Repaint Once They've Settled (an inherited color changes without a callback)
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(Paint);
        void OnContrast(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Paint);
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs e) => Paint();

        // Listed Once While On Screen (Loaded can come again without an Unloaded between)
        Loaded   += (_, _) =>
        {
            if (!Shown.Contains(this))
            {
                Shown.Add(this);
                LeafBrushes.ContrastChanged += OnContrast;
                XamlRoot.Changed            += OnRootChanged;
            }

            Paint();
        };
        Unloaded += (_, _) =>
        {
            Shown.Remove(this);
            LeafBrushes.ContrastChanged -= OnContrast;
            if (XamlRoot is not null)
            {
                XamlRoot.Changed -= OnRootChanged;
            }
        };
    }

    /// <summary>The pane this glyph stands for ("Sidebar" or "Details"); its panel sits on the sidebar's left or the details panel's right.</summary>
    public string Pane { get; set; } = "Details";

    /// <summary>Fills (open) or empties (closed) the panel of every glyph for <paramref name="pane"/>.</summary>
    public static void SetOpen(string pane, bool open)
    {
        OpenPanes[pane] = open;
        foreach (var glyph in Shown.Where(g => g.Pane == pane))
        {
            glyph.Paint();
        }
    }

    // True while the glyph's panel is filled
    bool IsFilled => OpenPanes.GetValueOrDefault(Pane);

    void Paint()
    {
        // Shapes For This Scale (rebuilt only when the scale changes)
        var scale = XamlRoot?.RasterizationScale ?? 1;
        if (scale != _scale)
        {
            _scale = scale;
            BuildShapes(scale);
        }

        // The Icon's Color (set as a value, never read back as a typed brush); the panel filled only while open
        var color = GetValue(ForegroundProperty);
        _outline.SetValue(Shape.StrokeProperty, color);
        _panel.SetValue(Shape.FillProperty, IsFilled ? color : null);
    }

    // Lays the outline, the divider, and the panel out in physical pixels, then hands them over in DIPs: the stroke is
    // a whole number of pixels and sits on pixel centers, and the panel fills exactly the pixels inside the stroke
    void BuildShapes(double scale)
    {
        var left   = Pane == "Sidebar";
        var width  = Math.Round(GlyphWidth * scale);
        var height = Math.Round(GlyphHeight * scale);
        var stroke = Math.Max(1, Math.Round(scale * 1.25, MidpointRounding.AwayFromZero));
        var corner = Math.Round(Corner * scale);
        var panel  = Math.Round(PanelWidth * scale);
        var half   = stroke / 2;

        // Outline And Divider (one stroked path, so no edge is drawn twice)
        var divider = left ? panel - half : width - panel + half;
        var outline = new PathGeometry();
        outline.Figures.Add(RoundedRect(new Rect(half, half, width - stroke, height - stroke), corner - half, corner - half, corner - half, corner - half, scale));
        outline.Figures.Add(Line(new Point(divider, stroke), new Point(divider, height - stroke), scale));
        _outline.Data            = outline;
        _outline.StrokeThickness = stroke / scale;

        // Panel Fill (inside the stroke and up to the divider, never under it, its outer corners following the outline's
        // inner curve). The shapes must not overlap: while the window is inactive the title bar fades its content to half,
        // each shape on its own, and a pixel both cover came out brighter as a line beside the panel
        var inner = Math.Max(0, corner - stroke);
        var fillW = Math.Max(0, panel - stroke * 2);
        var box   = left ? new Rect(stroke, stroke, fillW, height - stroke * 2) : new Rect(width - panel + stroke, stroke, fillW, height - stroke * 2);
        var fill  = new PathGeometry();
        fill.Figures.Add(left ? RoundedRect(box, inner, 0, 0, inner, scale) : RoundedRect(box, 0, inner, inner, 0, scale));
        _panel.Data = fill;
    }

    // Moves the drawing by the fraction of a pixel the glyph's spot is off the screen's pixel grid (a 16 DIP glyph
    // centered in a 32 DIP button lands half a pixel off at 125%), so its pixel-aligned lines stay sharp
    void SnapToPixels()
    {
        if (XamlRoot is null || _scale <= 0)
        {
            return;
        }

        var at = TransformToVisual(null).TransformPoint(default);
        var x  = (Math.Round(at.X * _scale) - at.X * _scale) / _scale;
        var y  = (Math.Round(at.Y * _scale) - at.Y * _scale) / _scale;
        if (x != _snap.X || y != _snap.Y)
        {
            _snap.X = x;
            _snap.Y = y;
        }
    }

    // A rectangle (physical pixels) with its own radius at each corner: top left, top right, bottom right, bottom left
    static PathFigure RoundedRect(Rect r, double tl, double tr, double br, double bl, double scale)
    {
        var figure = new PathFigure { StartPoint = Dip(new Point(r.Left + tl, r.Top), scale), IsClosed = true, IsFilled = true };
        Edge(figure, new Point(r.Right - tr, r.Top), new Point(r.Right, r.Top + tr), tr, scale);
        Edge(figure, new Point(r.Right, r.Bottom - br), new Point(r.Right - br, r.Bottom), br, scale);
        Edge(figure, new Point(r.Left + bl, r.Bottom), new Point(r.Left, r.Bottom - bl), bl, scale);
        Edge(figure, new Point(r.Left, r.Top + tl), new Point(r.Left + tl, r.Top), tl, scale);
        return figure;
    }

    // A straight side to the corner, then the corner's quarter circle (none when the radius is 0)
    static void Edge(PathFigure figure, Point lineTo, Point arcTo, double radius, double scale)
    {
        figure.Segments.Add(new LineSegment { Point = Dip(lineTo, scale) });
        if (radius > 0)
        {
            figure.Segments.Add(new ArcSegment { Point = Dip(arcTo, scale), Size = new Size(radius / scale, radius / scale), SweepDirection = SweepDirection.Clockwise });
        }
    }

    static PathFigure Line(Point from, Point to, double scale)
    {
        var figure = new PathFigure { StartPoint = Dip(from, scale), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = Dip(to, scale) });
        return figure;
    }

    static Point Dip(Point px, double scale) => new(px.X / scale, px.Y / scale);
}
