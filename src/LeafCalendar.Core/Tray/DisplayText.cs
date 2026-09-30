using System.Text;

namespace LeafCalendar.Core.Tray;

/// <summary>Event text made safe for one-line places: the tray tooltip, flyout rows, and notifications.</summary>
public static class DisplayText
{
    /// <summary>
    /// Control characters (line breaks, tabs) become spaces, the ends are trimmed, and text longer than
    /// <paramref name="max"/> is clipped with "…", never between the two halves of an emoji.
    /// </summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return "";
        }

        // Control Characters Become One Space Per Run; Bidi Overrides/Isolates And Marks Are Dropped (They Can Reorder The Tooltip)
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Where(c => !IsBidi(c)))
        {
            if (char.IsControl(c))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        var clean = builder.ToString().Trim();
        if (clean.Length <= max)
        {
            return clean;
        }

        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(clean[cut - 1]))
        {
            cut--;
        }

        return clean[..cut].TrimEnd() + "…";
    }

    // Bidi Marks (LRM, RLM, ALM), Embeddings/Overrides (202A-202E), Isolates (2066-2069)
    static bool IsBidi(char c) => c is '‎' or '‏' or '؜' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');
}
