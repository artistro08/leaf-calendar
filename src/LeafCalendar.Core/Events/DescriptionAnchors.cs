namespace LeafCalendar.Core.Events;

/// <summary>
/// Puts link targets back on text read from the description editor. The editor never holds a live link: each link loads
/// as text tinted with its own slot (<see cref="Tint"/>), and its target is kept aside as an anchor. A run gets an
/// anchor's target only when it carries that anchor's tint and its text is unchanged.
/// </summary>
/// <remarks>
/// So an edited link becomes plain text, plain text that happens to read like a link never becomes one, and two links
/// with the same text keep their own targets. Anchors are taken in order, each once.
/// </remarks>
public static class DescriptionAnchors
{
    /// <summary>How many tints there are; anchor <c>i</c> uses slot <c>i % Slots</c>.</summary>
    public const int Slots = 64;

    /// <summary>
    /// The link color for <paramref name="slot"/>: <paramref name="color"/> with its two low bits per channel flipped
    /// (at most 3 of 255, too little to see).
    /// </summary>
    public static (byte R, byte G, byte B) Tint((byte R, byte G, byte B) color, int slot)
    {
        var s = slot % Slots;
        return ((byte)(color.R ^ (s & 3)), (byte)(color.G ^ ((s >> 2) & 3)), (byte)(color.B ^ ((s >> 4) & 3)));
    }

    /// <summary>The slot <paramref name="color"/> is a tint of <paramref name="baseColor"/> for, or null when it isn't one.</summary>
    public static int? SlotOf((byte R, byte G, byte B) color, (byte R, byte G, byte B) baseColor)
    {
        int r = color.R ^ baseColor.R, g = color.G ^ baseColor.G, b = color.B ^ baseColor.B;
        if (((r | g | b) & ~3) != 0)
        {
            return null;
        }

        return r | (g << 2) | (b << 4);
    }

    /// <summary>
    /// The target for each run (null for plain text). A run with a slot takes the first unused anchor in that slot whose
    /// text it still reads exactly.
    /// </summary>
    public static IReadOnlyList<Uri?> Resolve(IReadOnlyList<(string Text, Uri Link)> anchors, IReadOnlyList<(string Text, int? Slot)> runs)
    {
        var used    = new bool[anchors.Count];
        var targets = new Uri?[runs.Count];

        for (var r = 0; r < runs.Count; r++)
        {
            if (runs[r].Slot is not { } slot)
            {
                continue;
            }

            for (var i = slot; i < anchors.Count; i += Slots)
            {
                if (!used[i] && anchors[i].Text == runs[r].Text)
                {
                    used[i]    = true;
                    targets[r] = anchors[i].Link;
                    break;
                }
            }
        }

        return targets;
    }
}
