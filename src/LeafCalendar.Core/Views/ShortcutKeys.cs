namespace LeafCalendar.Core.Views;

/// <summary>One piece of a written shortcut: a key drawn as its own key cap, or the words between keys ("or", "then", "/", "drag").</summary>
public sealed record ShortcutKeyPart(string Text, bool IsKey)
{
    /// <summary>The part's text (what Narrator would read for it).</summary>
    public override string ToString() => Text;
}

/// <summary>
/// Splits a shortcut as the cheat sheet writes it ("Ctrl+Shift+L", "B or Shift+N", "E then Y / N / M", "Shift+drag")
/// into its keys and the words between them, so each key can be drawn as a separate key cap, the way PowerToys draws
/// its shortcut legends.
/// </summary>
/// <remarks>
/// Words are split on spaces, and each word on <c>+</c> (a lone <c>+</c> or <c>-</c> after one is that key, as in
/// <c>Ctrl+-</c>). "or", "then", "and", "/", and a trailing comma are words between keys, and so are the mouse's
/// actions ("click", "drag", "wheel", "mouse", "back", "forward"), which aren't keys you press.
/// </remarks>
public static class ShortcutKeys
{
    static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase) { "or", "then", "and", "/", "," };
    static readonly HashSet<string> MouseWords = new(StringComparer.OrdinalIgnoreCase) { "click", "drag", "wheel", "mouse", "back", "forward" };

    /// <summary>The keys and words of <paramref name="shortcut"/>, in order; empty for a blank one.</summary>
    public static IReadOnlyList<ShortcutKeyPart> Parse(string? shortcut)
    {
        var parts = new List<ShortcutKeyPart>();
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return parts;
        }

        foreach (var raw in shortcut.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // "Alt+Right," is a combination, then a comma ("Ctrl+," is the comma key)
            var word  = raw;
            var comma = word.Length > 1 && word.EndsWith(',') && word[^2] != '+';
            if (comma)
            {
                word = word[..^1];
            }

            if (Joiners.Contains(word) || MouseWords.Contains(word))
            {
                AddWords(parts, word);
            }
            else
            {
                foreach (var key in Keys(word))
                {
                    parts.Add(MouseWords.Contains(key) ? new ShortcutKeyPart(key, IsKey: false) : new ShortcutKeyPart(key, IsKey: true));
                }
            }

            if (comma)
            {
                AddWords(parts, ",");
            }
        }

        return parts;
    }

    // Words in a row read as one ("mouse back"), and a comma sticks to what's before it
    static void AddWords(List<ShortcutKeyPart> parts, string word)
    {
        if (parts.Count > 0 && parts[^1] is { IsKey: false } last)
        {
            parts[^1] = new ShortcutKeyPart(word == "," ? last.Text + "," : $"{last.Text} {word}", IsKey: false);
            return;
        }

        parts.Add(new ShortcutKeyPart(word, IsKey: false));
    }

    // "Ctrl+Shift+L" → Ctrl, Shift, L; "Ctrl+-" → Ctrl, -; "Ctrl++" → Ctrl, +; a lone "+" or "-" is that key
    static List<string> Keys(string word)
    {
        var keys  = new List<string>();
        var start = 0;
        for (var i = 0; i < word.Length; i++)
        {
            // A '+' joins keys only between two of them (not first, and not right after another join)
            if (word[i] == '+' && i > start)
            {
                keys.Add(word[start..i]);
                start = i + 1;
            }
        }

        if (start < word.Length)
        {
            keys.Add(word[start..]);
        }

        return keys;
    }
}
