using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The sidebar and details panel toggles' icon: a rounded window outline (16 × 12, like Segoe Fluent's dock glyph)
/// with a narrow panel at one side. The panel is filled with the icon's color while its pane is open and outlined
/// while it's closed. Drawn from borders, so the edges land on whole pixels at every scale. Every glyph for a pane
/// follows <see cref="SetOpen"/>; the colors follow its <see cref="Control.Foreground"/> (set by its button: the title bar's
/// template binds it, the details toggle binds it by name), repainted on theme and contrast changes too, and copied as values,
/// never read back through a cast (a Native AOT trap).
/// </summary>
public sealed partial class PaneGlyph : UserControl
{
    // The glyphs on screen, per pane (a glyph inside a control template has no name to reach it by)
    static readonly List<PaneGlyph> Shown = [];
    static readonly Dictionary<string, bool> OpenPanes = [];

    readonly Border _outline = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
    readonly Border _panel   = new() { Width = 6, BorderThickness = new Thickness(1) };

    /// <summary>Creates the glyph.</summary>
    public PaneGlyph()
    {
        IsTabStop = false;
        Width     = 16;
        Height    = 12;
        Content   = new Grid { Children = { _outline, _panel } };
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Paint());

        // Theme And Contrast Changes Repaint Once They've Settled (an inherited color changes without a callback)
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(Paint);
        void OnContrast(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Paint);

        // Listed Once While On Screen (Loaded can come again without an Unloaded between)
        Loaded   += (_, _) =>
        {
            if (!Shown.Contains(this))
            {
                Shown.Add(this);
                LeafBrushes.ContrastChanged += OnContrast;
            }

            Paint();
        };
        Unloaded += (_, _) =>
        {
            Shown.Remove(this);
            LeafBrushes.ContrastChanged -= OnContrast;
        };
    }

    /// <summary>The pane this glyph stands for ("Sidebar" or "Details"); its panel sits on the sidebar's left or the details panel's right.</summary>
    public string Pane { get; set; } = "Details";

    /// <summary>Fills (open) or outlines (closed) the panel of every glyph for <paramref name="pane"/>.</summary>
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
        var left = Pane == "Sidebar";
        _panel.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _panel.CornerRadius        = left ? new CornerRadius(3, 0, 0, 3) : new CornerRadius(0, 3, 3, 0);

        // The Icon's Color (set as a value, never read back as a typed brush); the panel filled only while open
        var color = GetValue(ForegroundProperty);
        _outline.SetValue(Border.BorderBrushProperty, color);
        _panel.SetValue(Border.BorderBrushProperty, color);
        _panel.SetValue(Border.BackgroundProperty, IsFilled ? color : null);
    }
}
