namespace LeafCalendar.Core.Events;

/// <summary>
/// Puts link targets back on text read from the description editor. The editor never holds a live link: each link loads
/// as text marked with its own slot (anchor <c>i</c> uses slot <c>i % Slots</c>; the editor keeps the mark in a format
/// no text shows), and its target is kept aside as an anchor. A run gets an anchor's target only when it carries that
/// anchor's slot and its text is unchanged (a link that a format change split into several runs counts as one).
/// </summary>
/// <remarks>
/// So an edited link becomes plain text, plain text that happens to read like a link never becomes one, and two links
/// with the same text keep their own targets. Anchors are taken in order, each once.
/// </remarks>
public static class DescriptionAnchors
{
    /// <summary>How many slots there are; anchor <c>i</c> uses slot <c>i % Slots</c>.</summary>
    public const int Slots = 64;

    /// <summary>
    /// The target for each run (null for plain text). A run with a slot takes the first unused anchor in that slot whose
    /// text it still reads exactly, on its own or together with the runs right after it in the same slot: a format
    /// change inside a link (bold on part of it) splits it into several runs, and each of them gets the target.
    /// </summary>
    public static IReadOnlyList<Uri?> Resolve(IReadOnlyList<(string Text, Uri Link)> anchors, IReadOnlyList<(string Text, int? Slot)> runs)
    {
        var used = new bool[anchors.Count];
        var targets = new Uri?[runs.Count];

        for (var r = 0; r < runs.Count; r++)
        {
            if (runs[r].Slot is not { } slot)
            {
                continue;
            }

            for (var i = slot; i < anchors.Count; i += Slots)
            {
                if (used[i])
                {
                    continue;
                }

                // Joined With The Next Runs In The Slot While They Still Spell The Start Of The Anchor's Text
                var whole = anchors[i].Text;
                var text = runs[r].Text;
                var end = r + 1;
                while (text.Length < whole.Length && whole.StartsWith(text, StringComparison.Ordinal) && end < runs.Count && runs[end].Slot == slot)
                {
                    text += runs[end++].Text;
                }

                if (text == whole)
                {
                    used[i] = true;
                    Array.Fill(targets, anchors[i].Link, r, end - r);
                    r = end - 1;
                    break;
                }
            }
        }

        return targets;
    }
}
