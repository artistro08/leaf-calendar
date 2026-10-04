using LeafCalendar.Core.Events;
using LeafCalendar.Core.Tray;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Tray;

/// <summary>What the flyout shows: the agenda by day, the next event (or null), and the header's empty sentence.</summary>
public sealed record AgendaModel(IReadOnlyList<AgendaDay> Days, NextUp? Next, string NothingNext);

/// <summary>One day of the flyout's agenda, as its list shows it (an App type, so no Core type reaches a WinRT list under AOT).</summary>
public sealed record AgendaDayRow(string Header, List<AgendaRow> Items);

/// <summary>
/// One flyout row: title, time, the calendar's color bar, and the Join button when there's a link ("Join Standup" for
/// Narrator, the link's address as its tooltip). The row and Join are sibling buttons that x:Bind their clicks to
/// <see cref="Open"/> and <see cref="Join"/>, so nothing is read back from a control.
/// </summary>
public sealed record AgendaRow(string Title, string When, SolidColorBrush Accent, Visibility JoinVisibility, MeetingProvider? Provider, string JoinName, string? JoinTip, string RowId, string JoinId, Action OnOpen, Action OnJoin)
{
    /// <summary>Row click: opens the event in the main window.</summary>
    public void Open() => OnOpen();

    /// <summary>Join click: opens the meeting.</summary>
    public void Join() => OnJoin();
}
