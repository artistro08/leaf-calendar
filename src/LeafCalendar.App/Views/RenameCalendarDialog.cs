using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>Asks for a calendar's new name (the sidebar's and Settings › Calendars' "Rename…").</summary>
public static class RenameCalendarDialog
{
    /// <summary>
    /// Shows the dialog with the current name filled in. <c>Ok</c> is false when canceled; <c>Text</c> is what was
    /// typed (empty means Google's name). The caller checks whether it differs from the name shown.
    /// </summary>
    public static async Task<(bool Ok, string? Text)> ShowAsync(XamlRoot root, CalendarInfo calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        // Name Box (the placeholder is Google's own name, what an empty box goes back to)
        var box = new TextBox
        {
            Text            = calendar.Summary,
            PlaceholderText = calendar.GoogleName,
            MaxLength       = CalendarEdits.MaxName,
        };
        AutomationProperties.SetName(box, "Calendar name");
        AutomationProperties.SetAutomationId(box, "RenameCalendarBox");
        box.SelectAll();

        // Caption Line (caption size, set directly: no style read back from resources)
        var hint = new TextBlock
        {
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
            Text         = "Leave it empty to use the name from Google. Google Calendar shows this name too.",
        };

        var dialog = new ContentDialog
        {
            XamlRoot          = root,
            Title             = "Rename calendar",
            Content           = new StackPanel { Spacing = 8, Children = { box, hint } },
            PrimaryButtonText = "Rename",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
        };

        // The box is this method's own reference (never read back through the dialog)
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (true, box.Text) : (false, null);
    }

    /// <summary>
    /// The whole "Rename…" step: asks, then renames on Google only when the text changed (the name shown may be a
    /// cleaned or shortened copy of Google's, which must not be written back unasked).
    /// </summary>
    public static async Task RenameAsync(XamlRoot root, ViewModels.CalendarViewModel calendars, CalendarInfo calendar)
    {
        ArgumentNullException.ThrowIfNull(calendars);

        var (ok, text) = await ShowAsync(root, calendar);
        if (ok && text != calendar.Summary)
        {
            await calendars.RenameCalendarAsync(calendar, text);
        }
    }
}
