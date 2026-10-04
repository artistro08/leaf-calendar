using System.Globalization;
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
    /// Bidi, zero-width, and every Unicode format character (soft hyphen, invisible operators, tag characters, and so
    /// on) are dropped, plus U+034F and the line/paragraph separators, and so are characters XML can't carry (a lone
    /// surrogate half, U+FFFE, U+FFFF), so the result is always safe to put in notification XML. The zero-width joiner
    /// (U+200D) stays, because emoji sequences such as a woman technologist need it, and so does the zero-width
    /// non-joiner (U+200C), which Persian and several Indic scripts need to spell words correctly.
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
                    // Tag Characters (U+E0000-E007F) And Other Format Characters Outside The BMP Are Dropped
                    if (Rune.GetUnicodeCategory(new Rune(c, text[i + 1])) != UnicodeCategory.Format)
                    {
                        builder.Append(c).Append(text[i + 1]);
                    }

                    i++;
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

    // Format Characters (Bidi Marks/Embeddings/Isolates, Zero-Width, Soft Hyphen, Invisible Operators, ...) Except The Joiner Emoji Need And The Non-Joiner Persian Needs;
    // Plus The Combining Grapheme Joiner, Line/Paragraph Separators, And XML Noncharacters
    private static bool IsInvisible(char c) =>
        c is not ('\u200C' or '\u200D') && (char.GetUnicodeCategory(c) == UnicodeCategory.Format || c is '\u034F' or '\u2028' or '\u2029' or '\uFFFE' or '\uFFFF');
}
