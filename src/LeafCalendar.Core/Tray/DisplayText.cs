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

        // Control Characters Become One Space Per Run; Bidi And Zero-Width Characters Are Dropped (They Can Reorder The Tooltip)
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Where(c => !IsInvisible(c)))
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

    // Bidi Marks/Embeddings/Overrides/Isolates, Zero-Width And Format Characters, Line/Paragraph Separators
    static bool IsInvisible(char c) => c is '\u200E' or '\u200F' or '\u061C' or '\u2028' or '\u2029' or '\u2060' or '\uFEFF' or (>= '\u200B' and <= '\u200D') or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
