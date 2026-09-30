using System.Text;

namespace LeafCalendar.Core.Tray;

/// <summary>Event text made safe for one-line places: the tray tooltip, flyout rows, and notifications.</summary>
public static class DisplayText
{
    /// <summary>
    /// Control characters (line breaks, tabs) become spaces, the ends are trimmed, and text longer than
    /// <paramref name="max"/> is clipped with "&#x2026;", never between the two halves of an emoji.
    /// </summary>
    /// <remarks>
    /// Bidi, zero-width, and format characters are dropped, and so are characters XML can't carry (a lone surrogate
    /// half, U+FFFE, U+FFFF), so the result is always safe to put in notification XML.
    /// </remarks>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return "";
        }

        // Control Characters Become One Space Per Run; Bidi And Zero-Width Characters Are Dropped (They Can Reorder The Tooltip)
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Surrogate Pairs Stay Together; A Lone Half Is Dropped
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[++i]);
                }

                continue;
            }

            if (IsInvisible(c))
            {
                continue;
            }

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

        return clean[..cut].TrimEnd() + "\u2026";
    }

    // Bidi Marks/Embeddings/Overrides/Isolates, Zero-Width And Format Characters, Line/Paragraph Separators, XML Noncharacters
    static bool IsInvisible(char c) => c is '\u200E' or '\u200F' or '\u061C' or '\u2028' or '\u2029' or '\u2060' or '\uFEFF' or '\uFFFE' or '\uFFFF' or (>= '\u200B' and <= '\u200D') or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
