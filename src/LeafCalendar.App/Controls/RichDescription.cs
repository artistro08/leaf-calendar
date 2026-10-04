using LeafCalendar.Core.Events;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Moves a description between <see cref="DescriptionLine"/>s and a <see cref="RichEditBox"/>: one paragraph per line,
/// bold, italic, underline, and bullet or numbered paragraphs. Links never become live in the box; each shows
/// underlined in the accent color, carries its anchor's slot (<see cref="DescriptionAnchors"/>) as a kerning threshold
/// no text ever reaches, its target is kept as an anchor, and it's put back on read when the link text is unchanged.
/// </summary>
/// <remarks>
/// A link is told apart by that mark, never by its color: RichEdit reads a link's color back as the body text's black
/// in light theme (the theme it applies after a load wipes it), so a color mark let the link's underline spread into
/// text typed after it and be saved.
/// </remarks>
internal static class RichDescription
{
    // A link's mark: kerning only from LinkKerning + slot points up (so never at any real text size), the slot after it
    private const float LinkKerning = 1000;

    /// <summary>Fills the box and clears its undo history. Returns the link anchors to pass to <see cref="Read"/>.</summary>
    public static List<(string Text, Uri Link)> Load(RichEditBox box, IReadOnlyList<DescriptionLine> lines)
    {
        var document = box.Document;
        var anchors = new List<(string Text, Uri Link)>();
        var accent = LinkColor(box);
        document.SetText(TextSetOptions.None, string.Join("\r", lines.Select(l => string.Concat(l.Runs.Select(r => r.Text)))));

        // Nothing Carries Over From The Last Description (bold, link color, or a list on the first paragraph)
        var whole = document.GetRange(0, int.MaxValue);
        whole.CharacterFormat = document.GetDefaultCharacterFormat();
        whole.ParagraphFormat = document.GetDefaultParagraphFormat();

        var position = 0;
        foreach (var line in lines)
        {
            var lineStart = position;
            foreach (var run in line.Runs)
            {
                // Character Format For This Run
                var format = document.GetRange(position, position + run.Text.Length).CharacterFormat;
                format.Bold = run.Bold ? FormatEffect.On : FormatEffect.Off;
                format.Italic = run.Italic ? FormatEffect.On : FormatEffect.Off;
                format.Underline = run.Underline ? UnderlineType.Single : UnderlineType.None;

                // A Link Is Underlined Accent Text With Its Slot's Mark (its own run, so it reads back whole) And Its
                // Target Kept Aside. The underline is only how a link looks: Read drops it from marked text
                if (run.Link is { } link)
                {
                    format.ForegroundColor = accent;
                    format.Underline = UnderlineType.Single;
                    format.Kerning = LinkKerning + anchors.Count % DescriptionAnchors.Slots;
                    anchors.Add((run.Text, link));
                }

                position += run.Text.Length;
            }

            // List Paragraph
            if (line.List != ListKind.None)
            {
                SetList(document.GetRange(lineStart, position).ParagraphFormat, line.List == ListKind.Bullet ? MarkerType.Bullet : MarkerType.Arabic);
            }

            position += 1;
        }

        document.Selection.SetRange(0, 0);
        PlainInsertion(box);
        document.ClearUndoRedoHistory();
        return anchors;
    }

    /// <summary>The box's content as lines, walking one character-format run at a time.</summary>
    public static IReadOnlyList<DescriptionLine> Read(RichEditBox box, IReadOnlyList<(string Text, Uri Link)> anchors)
    {
        var document = box.Document;
        document.GetText(TextGetOptions.None, out var all);

        // RichEdit always ends with a paragraph mark; a soft break (Shift+Enter) is a line too (same length, so positions hold)
        all = all.TrimEnd('\r').Replace('\v', '\r');
        var runs = new List<(int Line, DescriptionRun Run, int? Slot)>();
        var lists = new List<ListKind>();
        var start = 0;

        foreach (var paragraph in all.Split('\r'))
        {
            var end = start + paragraph.Length;
            var at = start;

            while (at < end)
            {
                var range = document.GetRange(at, at);
                range.Expand(TextRangeUnit.CharacterFormat);
                var runEnd = Math.Min(range.EndPosition, end);
                if (runEnd <= at)
                {
                    runEnd = end;
                }

                var format = range.CharacterFormat;
                var run = new DescriptionRun(all[at..runEnd], format.Bold == FormatEffect.On, format.Italic == FormatEffect.On, IsUnderlined(format));
                runs.Add((lists.Count, run, SlotOf(format)));
                at = runEnd;
            }

            lists.Add(document.GetRange(start, start).ParagraphFormat.ListType switch
            {
                MarkerType.None or MarkerType.Undefined => ListKind.None,
                MarkerType.Bullet => ListKind.Bullet,
                _ => ListKind.Numbered,
            });
            start = end + 1;
        }

        // Links Come Back Only On Their Own Unchanged Text
        var targets = DescriptionAnchors.Resolve(anchors, [.. runs.Select(r => (r.Run.Text, r.Slot))]);
        var linked = runs.Select((r, i) => (r.Line, Run: r.Run with { Link = targets[i] })).ToLookup(r => r.Line, r => r.Run);

        return [.. lists.Select((list, line) => new DescriptionLine([.. linked[line]], list))];
    }

    /// <summary>Puts paragraphs in a list of <paramref name="kind"/> (the marker hangs left of the text), or out of any list for <see cref="MarkerType.None"/>.</summary>
    public static void SetList(ITextParagraphFormat paragraph, MarkerType kind)
    {
        paragraph.ListType = kind;
        paragraph.ListStyle = MarkerStyle.Period;

        if (kind == MarkerType.None)
        {
            paragraph.SetIndents(0, 0, 0);
            return;
        }

        paragraph.ListStart = 1;
        paragraph.ListTab = 12;
        paragraph.SetIndents(-12, 18, 0);
    }

    /// <summary>
    /// Typing at a link's edge (or inside it) gets plain text, not the link's color, mark, or underline: the caret's
    /// format is reset to the default, keeping bold and italic.
    /// </summary>
    public static void PlainInsertion(RichEditBox box)
    {
        if (box.IsReadOnly)
        {
            return;
        }

        if (box.Document.Selection.Length == 0)
        {
            Untint(box, box.Document.Selection);
        }
    }

    /// <summary>
    /// Gives a link's range (text pasted at a link, or the caret) the default format, keeping bold and italic (the
    /// underline and color were the link's own, so they go too).
    /// </summary>
    public static void Untint(RichEditBox box, ITextRange range)
    {
        if (box.IsReadOnly)
        {
            return;
        }

        var current = range.CharacterFormat;
        if (SlotOf(current) is null)
        {
            return;
        }

        var plain = box.Document.GetDefaultCharacterFormat();
        plain.Bold = current.Bold;
        plain.Italic = current.Italic;
        plain.Underline = UnderlineType.None;
        plain.Kerning = 0;
        range.CharacterFormat = plain;
    }

    /// <summary>True when the format is underlined by the user: a link's underline (on its marked text) is only how it looks.</summary>
    public static bool IsUnderlined(ITextCharacterFormat format) => format.Underline != UnderlineType.None && SlotOf(format) is null;

    /// <summary>Colors every link with the accent for the box's current theme.</summary>
    public static void Recolor(RichEditBox box)
    {
        if (box.IsReadOnly)
        {
            return;
        }

        var document = box.Document;
        var accent = LinkColor(box);
        document.GetText(TextGetOptions.None, out var all);

        for (var at = 0; at < all.Length;)
        {
            var range = document.GetRange(at, at);
            range.Expand(TextRangeUnit.CharacterFormat);
            if (SlotOf(range.CharacterFormat) is not null)
            {
                range.CharacterFormat.ForegroundColor = accent;
            }

            at = Math.Max(range.EndPosition, at + 1);
        }
    }

    // The link color for the box's theme: the accent (the highlight color in a contrast theme)
    private static Color LinkColor(RichEditBox box)
    {
        var accent = LeafBrushes.Accent(box.ActualTheme == ElementTheme.Dark).Color;
        return Color.FromArgb(255, accent.R, accent.G, accent.B);
    }

    // A link's slot from its mark, or null for anything else (a mixed range reads its kerning as undefined, far below)
    private static int? SlotOf(ITextCharacterFormat format) =>
        format.Kerning is >= LinkKerning and < LinkKerning + DescriptionAnchors.Slots ? (int)Math.Round(format.Kerning - LinkKerning) : null;
}
