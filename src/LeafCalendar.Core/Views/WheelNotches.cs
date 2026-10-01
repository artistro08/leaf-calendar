namespace LeafCalendar.Core.Views;

/// <summary>
/// Turns mouse wheel deltas into whole notches.
/// </summary>
/// <remarks>
/// A plain wheel sends ±120 per notch; smooth-scrolling wheels and some drivers send smaller pieces. The pieces
/// add up here, and each full 120 counts once, so a notch is one step whatever the wheel sends.
/// </remarks>
public sealed class WheelNotches
{
    /// <summary>One notch of a standard mouse wheel.</summary>
    public const int Notch = 120;

    int _pending;

    /// <summary>Adds <paramref name="delta"/> and returns the whole notches it completes (negative for down), keeping the rest.</summary>
    public int Add(int delta)
    {
        _pending += delta;
        var notches = _pending / Notch;
        _pending -= notches * Notch;
        return notches;
    }
}
