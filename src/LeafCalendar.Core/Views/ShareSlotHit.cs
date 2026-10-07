using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Views;

/// <summary>Where on a picked time the pointer is: its top edge, its bottom edge, or inside.</summary>
public enum SlotEdge
{
    /// <summary>Inside, away from both edges.</summary>
    Inside,

    /// <summary>On the top edge (dragging it moves the start).</summary>
    Top,

    /// <summary>On the bottom edge (dragging it moves the end).</summary>
    Bottom,
}

/// <summary>A picked time under the pointer: its place in the list and which part was hit.</summary>
/// <param name="Index">The time's place in the list.</param>
/// <param name="Edge">Which part of the time was hit.</param>
public readonly record struct SlotHitResult(int Index, SlotEdge Edge);

/// <summary>Finds the picked time under the pointer on the time grid, so its edges can be dragged.</summary>
public static class ShareSlotHit
{
    /// <summary>
    /// The time under <paramref name="at"/>, searched from the end of the list (drawn last, so on top). An instant within
    /// <paramref name="edge"/> of a time's start or end is on that edge, even just outside it; a time shorter than two
    /// edges splits at its middle. Null when nothing is under the pointer.
    /// </summary>
    /// <param name="slots">The picked times, in draw order.</param>
    /// <param name="at">The instant under the pointer.</param>
    /// <param name="edge">How close to a start or end counts as that edge.</param>
    public static SlotHitResult? At(IReadOnlyList<BusyRange> slots, DateTimeOffset at, TimeSpan edge)
    {
        for (var i = slots.Count - 1; i >= 0; i--)
        {
            var s = slots[i];
            if (at < s.Start - edge || at > s.End + edge)
            {
                continue;
            }

            var middle = s.Start + (s.End - s.Start) / 2;
            if (at <= s.Start + edge && at <= middle)
            {
                return new SlotHitResult(i, SlotEdge.Top);
            }

            if (at >= s.End - edge)
            {
                return new SlotHitResult(i, SlotEdge.Bottom);
            }

            if (at >= s.Start)
            {
                return new SlotHitResult(i, SlotEdge.Inside);
            }
        }

        return null;
    }
}
