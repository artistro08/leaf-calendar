using LeafCalendar.Core.Events;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Moves a description between <see cref="DescriptionLine"/>s and a <see cref="RichEditBox"/>: one paragraph per line,
/// bold, italic, underline, and bullet or numbered paragraphs. Links never become live in the box; they show in the
/// accent color, their targets are kept as anchors, and they're put back on read when the link text is unchanged.
/// </summary>
internal static class RichDescription
{
    /// <summary>Fills the box and clears its undo history. Returns the link anchors to pass to <see cref="Read"/>.</summary>
    public static List<(string Text, Uri Link)> Load(RichEditBox box, IReadOnlyList<DescriptionLine> lines)
    {
        var document  = box.Document;
        var anchors   = new List<(string Text, Uri Link)>();
        var linkColor = LeafBrushes.Accent(box.ActualTheme == ElementTheme.Dark).Color;
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

                // A Link Is Colored Text (its own run, so it reads back whole) With Its Target Kept Aside
                if (run.Link is { } link)
                {
                    format.ForegroundColor = linkColor;
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
        document.ClearUndoRedoHistory();
        return anchors;
    }

    /// <summary>The box's content as lines, walking one character-format run at a time.</summary>
    public static IReadOnlyList<DescriptionLine> Read(RichEditBox box, IReadOnlyList<(string Text, Uri Link)> anchors)
    {
        var document = box.Document;
        document.GetText(TextGetOptions.None, out var all);

        // RichEdit always ends with a paragraph mark
        all = all.TrimEnd('\r');
        var lines = new List<DescriptionLine>();
        var start = 0;

        foreach (var paragraph in all.Split('\r'))
        {
            var end  = start + paragraph.Length;
            var runs = new List<DescriptionRun>();
            var at   = start;

            while (at < end)
            {
                var range = document.GetRange(at, at);
                range.Expand(TextRangeUnit.CharacterFormat);
                var runEnd = Math.Min(range.EndPosition, end);
                if (runEnd <= at)
                {
                    runEnd = end;
                }

                var text   = all[at..runEnd];
                var format = range.CharacterFormat;
                // ponytail: exact-text anchors; a link whose text the user edits becomes plain text. Track ranges if owners want link editing.
                var link   = anchors.FirstOrDefault(a => a.Text == text).Link;
                runs.Add(new DescriptionRun(text, format.Bold == FormatEffect.On, format.Italic == FormatEffect.On, format.Underline != UnderlineType.None, link));
                at = runEnd;
            }

            var listType = document.GetRange(start, start).ParagraphFormat.ListType;
            var list     = listType switch
            {
                MarkerType.None or MarkerType.Undefined => ListKind.None,
                MarkerType.Bullet                        => ListKind.Bullet,
                _                                        => ListKind.Numbered,
            };

            lines.Add(new DescriptionLine(runs, list));
            start = end + 1;
        }

        return lines;
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
}
