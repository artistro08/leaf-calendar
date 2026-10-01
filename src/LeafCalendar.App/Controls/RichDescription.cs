using LeafCalendar.Core.Events;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Moves a description between <see cref="DescriptionLine"/>s and a <see cref="RichEditBox"/>: one paragraph per line,
/// bold, italic, underline, and bullet or numbered paragraphs. Links never become live in the box; each shows in its
/// own tint of the accent color (<see cref="DescriptionAnchors"/>), its target is kept as an anchor, and it's put back
/// on read when the link text is unchanged.
/// </summary>
internal static class RichDescription
{
    /// <summary>Fills the box and clears its undo history. Returns the link anchors to pass to <see cref="Read"/>.</summary>
    public static List<(string Text, Uri Link)> Load(RichEditBox box, IReadOnlyList<DescriptionLine> lines)
    {
        var document = box.Document;
        var anchors  = new List<(string Text, Uri Link)>();
        var accent   = Accent(box);
        document.SetText(TextSetOptions.None, string.Join("\r", lines.Select(l => string.Concat(l.Runs.Select(r => r.Text)))));

        // Nothing Carries Over From The Last Description (bold, link color, or a list on the first paragraph)
        var whole             = document.GetRange(0, int.MaxValue);
        whole.CharacterFormat = document.GetDefaultCharacterFormat();
        whole.ParagraphFormat = document.GetDefaultParagraphFormat();

        var position = 0;
        foreach (var line in lines)
        {
            var lineStart = position;
            foreach (var run in line.Runs)
            {
                // Character Format For This Run
                var format       = document.GetRange(position, position + run.Text.Length).CharacterFormat;
                format.Bold      = run.Bold ? FormatEffect.On : FormatEffect.Off;
                format.Italic    = run.Italic ? FormatEffect.On : FormatEffect.Off;
                format.Underline = run.Underline ? UnderlineType.Single : UnderlineType.None;

                // A Link Is Text In Its Own Tint (its own run, so it reads back whole) With Its Target Kept Aside
                if (run.Link is { } link)
                {
                    format.ForegroundColor = ToColor(DescriptionAnchors.Tint(accent, anchors.Count));
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
        var runs  = new List<(int Line, DescriptionRun Run, int? Slot)>();
        var lists = new List<ListKind>();
        var start = 0;

        foreach (var paragraph in all.Split('\r'))
        {
            var end = start + paragraph.Length;
            var at  = start;

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
                var run    = new DescriptionRun(all[at..runEnd], format.Bold == FormatEffect.On, format.Italic == FormatEffect.On, format.Underline != UnderlineType.None);
                runs.Add((lists.Count, run, SlotOf(format.ForegroundColor)));
                at = runEnd;
            }

            lists.Add(document.GetRange(start, start).ParagraphFormat.ListType switch
            {
                MarkerType.None or MarkerType.Undefined => ListKind.None,
                MarkerType.Bullet                        => ListKind.Bullet,
                _                                        => ListKind.Numbered,
            });
            start = end + 1;
        }

        // Links Come Back Only On Their Own Unchanged Text
        var targets = DescriptionAnchors.Resolve(anchors, [.. runs.Select(r => (r.Run.Text, r.Slot))]);
        var linked  = runs.Select((r, i) => (r.Line, Run: r.Run with { Link = targets[i] })).ToLookup(r => r.Line, r => r.Run);

        return [.. lists.Select((list, line) => new DescriptionLine([.. linked[line]], list))];
    }

    /// <summary>Puts paragraphs in a list of <paramref name="kind"/> (the marker hangs left of the text), or out of any list for <see cref="MarkerType.None"/>.</summary>
    public static void SetList(ITextParagraphFormat paragraph, MarkerType kind)
    {
        paragraph.ListType  = kind;
        paragraph.ListStyle = MarkerStyle.Period;

        if (kind == MarkerType.None)
        {
            paragraph.SetIndents(0, 0, 0);
            return;
        }

        paragraph.ListStart = 1;
        paragraph.ListTab   = 12;
        paragraph.SetIndents(-12, 18, 0);
    }

    /// <summary>
    /// Typing at a link's edge (or inside it) gets plain text, not the link's tint: the caret's format is reset to the
    /// default, keeping bold, italic, and underline.
    /// </summary>
    public static void PlainInsertion(RichEditBox box)
    {
        if (box.Document.Selection.Length == 0)
        {
            Untint(box, box.Document.Selection);
        }
    }

    /// <summary>Gives a link-tinted range (text pasted at a link, or the caret) the default format, keeping bold, italic, and underline.</summary>
    public static void Untint(RichEditBox box, ITextRange range)
    {
        var current = range.CharacterFormat;
        if (SlotOf(current.ForegroundColor) is null)
        {
            return;
        }

        var plain             = box.Document.GetDefaultCharacterFormat();
        plain.Bold            = current.Bold;
        plain.Italic          = current.Italic;
        plain.Underline       = current.Underline;
        range.CharacterFormat = plain;
    }

    /// <summary>Re-tints every link for the box's current theme (each keeps its slot).</summary>
    public static void Recolor(RichEditBox box)
    {
        var document = box.Document;
        var accent   = Accent(box);
        document.GetText(TextGetOptions.None, out var all);

        for (var at = 0; at < all.Length;)
        {
            var range = document.GetRange(at, at);
            range.Expand(TextRangeUnit.CharacterFormat);
            if (SlotOf(range.CharacterFormat.ForegroundColor) is { } slot)
            {
                range.CharacterFormat.ForegroundColor = ToColor(DescriptionAnchors.Tint(accent, slot));
            }

            at = Math.Max(range.EndPosition, at + 1);
        }
    }

    // The accent for the box's theme
    static (byte R, byte G, byte B) Accent(RichEditBox box) => FromColor(LeafBrushes.Accent(box.ActualTheme == ElementTheme.Dark).Color);

    // A link's slot from its color, in either theme's tint (a theme change may not have re-tinted it yet)
    static int? SlotOf(Color color) =>
        DescriptionAnchors.SlotOf(FromColor(color), FromColor(LeafBrushes.Accent(true).Color))
        ?? DescriptionAnchors.SlotOf(FromColor(color), FromColor(LeafBrushes.Accent(false).Color));

    static (byte R, byte G, byte B) FromColor(Color color) => (color.R, color.G, color.B);

    static Color ToColor((byte R, byte G, byte B) rgb) => Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
}
