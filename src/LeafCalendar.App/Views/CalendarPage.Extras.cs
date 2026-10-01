// Track C (Milestone 5 Tasks 11-12) owns this file: the editor's time zone and the calendar extras.
// Empty hooks until the owning track fills them in; the owner deletes this line once every method uses the page
#pragma warning disable CA1822 // Mark members as static

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // Called once when the page opens (Task 12 fills it in)
    void AttachExtras()
    {
    }

    // Called from Detach: undo everything AttachExtras wired to the long-lived view model (Task 12)
    void DetachExtras()
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
