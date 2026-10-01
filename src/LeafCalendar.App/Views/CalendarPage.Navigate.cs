// Track A (Milestone 5 Tasks 3-5) owns this file: the command menu, the cheat sheet, time travel, and interface scale.
// Empty hooks until the owning track fills them in; the owner deletes this line once every method uses the page
#pragma warning disable CA1822 // Mark members as static

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // Called once when the page opens (Task 3 fills it in)
    void AttachNavigate()
    {
    }

    // Called from Detach: undo everything AttachNavigate wired to the long-lived view model (Task 3)
    void DetachNavigate()
    {
    }

    // Ctrl+K, Ctrl+F, /, and the title bar's search icon (Task 3)
    void OpenCommandMenu()
    {
    }

    // ? (Task 4)
    void ShowShortcutSheet()
    {
    }

    // Z (Task 5)
    void StartTimeTravel()
    {
    }
}
