namespace LeafCalendar.Core.Views;

/// <summary>
/// A window's remembered size, in DIPs (the whole window, frame included), and whether it was maximized. When
/// maximized, the size is the one it restores to.
/// </summary>
public sealed record WindowSize(double Width, double Height, bool Maximized = false)
{
    /// <summary>The main window's first-run size (the owner's own window: 1596 × 1017 pixels at 125%).</summary>
    public static WindowSize MainDefault { get; } = new(1277, 814);

    /// <summary>The size as a usable value: null when either side isn't a positive, finite number (a damaged settings row).</summary>
    public WindowSize? Clean() =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0 ? this : null;

    /// <summary>This size, grown to at least <paramref name="minWidth"/> × <paramref name="minHeight"/> (a size saved before the minimum grew).</summary>
    public WindowSize AtLeast(double minWidth, double minHeight) => this with { Width = Math.Max(Width, minWidth), Height = Math.Max(Height, minHeight) };

    /// <summary>
    /// Where the window goes, in screen pixels: this size at the monitor's <paramref name="scale"/>, no bigger than
    /// the work area, centered on it with the title bar never above its top.
    /// </summary>
    public (int X, int Y, int Width, int Height) PlaceIn(int workX, int workY, int workWidth, int workHeight, double scale)
    {
        var width = Math.Min((int)Math.Round(Width * scale), workWidth);
        var height = Math.Min((int)Math.Round(Height * scale), workHeight);
        return (workX + (workWidth - width) / 2, Math.Max(workY, workY + (workHeight - height) / 2), width, height);
    }
}
