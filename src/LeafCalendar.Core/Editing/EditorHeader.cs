using System.Globalization;

namespace LeafCalendar.Core.Editing;

/// <summary>The editor's header line.</summary>
public static class EditorHeader
{
    /// <summary>
    /// "Edit event" for an existing event. A new event shows its trimmed title, or "New event" while the title has nothing
    /// visible (only spaces, zero-width, or direction marks).
    /// </summary>
    public static string Text(bool isNew, string title)
    {
        if (!isNew)
        {
            return "Edit event";
        }

        return title.All(IsInvisible) ? "New event" : title.Trim();
    }

    private static bool IsInvisible(char c) => char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Control;
}
