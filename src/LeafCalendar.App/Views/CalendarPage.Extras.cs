// Track C (Milestone 5 Tasks 11-12) owns this file: the editor's time zone and the calendar extras.

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // E then Z (spec 8.7): open the editor on the event's time zone
    private void EditTimeZone()
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
