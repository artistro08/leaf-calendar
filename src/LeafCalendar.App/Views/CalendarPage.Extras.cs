// Track C (Milestone 5 Tasks 11-12) owns this file: the editor's time zone and the calendar extras.

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // Called once when the page opens. The calendar menu and the upcoming list talk to the view model directly, so
    // nothing is wired here (static, so it can't hold the page).
    static void AttachExtras()
    {
    }

    // Called from Detach: AttachExtras wired nothing to the long-lived view model, so there's nothing to undo
    static void DetachExtras()
    {
    }

    // E then Z (spec 8.7): open the editor on the event's time zone
    void EditTimeZone()
    {
        if (ViewModel.SelectedInfo is { Occurrence.IsAllDay: true })
        {
            ViewModel.ShowMessage("All-day events don't have a time zone");
            return;
        }

        if (ViewModel.Editing is null)
        {
            ViewModel.BeginEdit();
        }

        DispatcherQueue.TryEnqueue(() => Details.EditorView?.FocusTimeZone());
    }
}
