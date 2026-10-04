namespace LeafCalendar.Core.Views;

/// <summary>Calendar keyboard commands (spec Section 8.7).</summary>
public enum CalendarCommand
{
    /// <summary>Not a shortcut.</summary>
    None,

    /// <summary>T.</summary>
    Today,

    /// <summary>Left arrow or K.</summary>
    Previous,

    /// <summary>Right arrow or J.</summary>
    Next,

    /// <summary>D or 1.</summary>
    DayView,

    /// <summary>W or 0.</summary>
    WeekView,

    /// <summary>M.</summary>
    MonthView,

    /// <summary>2-9: that many days.</summary>
    Days,

    /// <summary>Period.</summary>
    GoToDate,

    /// <summary>Ctrl+Shift+E.</summary>
    ToggleWeekends,

    /// <summary>Ctrl+Shift+D.</summary>
    ToggleDeclined,

    /// <summary>Ctrl+= (grid taller).</summary>
    ZoomIn,

    /// <summary>Ctrl+- (grid shorter).</summary>
    ZoomOut,

    /// <summary>Ctrl+0.</summary>
    ZoomReset,

    /// <summary>Ctrl+Shift+L.</summary>
    ToggleTheme,

    /// <summary>N.</summary>
    NextEvent,

    /// <summary>B or Shift+N.</summary>
    PreviousEvent,

    /// <summary>C.</summary>
    CreateEvent,

    /// <summary>E (runs when the "E then ..." sequence times out).</summary>
    EditEvent,

    /// <summary>E then U.</summary>
    EditDuration,

    /// <summary>E then Y.</summary>
    RsvpYes,

    /// <summary>E then N.</summary>
    RsvpNo,

    /// <summary>E then M.</summary>
    RsvpMaybe,

    /// <summary>E then E.</summary>
    EmailGuests,

    /// <summary>Delete (emails guests).</summary>
    DeleteSelected,

    /// <summary>Ctrl+Shift+Delete (no emails).</summary>
    CancelEventQuietly,

    /// <summary>Ctrl+J.</summary>
    JoinMeeting,

    /// <summary>V.</summary>
    OpenMeetingLink,

    /// <summary>X.</summary>
    ToggleSelect,

    /// <summary>Ctrl+A.</summary>
    SelectAll,

    /// <summary>Ctrl+C.</summary>
    Copy,

    /// <summary>Ctrl+X.</summary>
    Cut,

    /// <summary>Ctrl+V.</summary>
    Paste,

    /// <summary>Ctrl+Z: undo the newest delete (this session).</summary>
    Undo,

    /// <summary>Alt+Left (or the mouse back button).</summary>
    NavigateBack,

    /// <summary>Alt+Right (or the mouse forward button).</summary>
    NavigateForward,

    /// <summary>Ctrl+K: the command menu.</summary>
    CommandMenu,

    /// <summary>Ctrl+F or /: the command menu, searching events.</summary>
    Search,

    /// <summary>? (Shift+/): the keyboard shortcut sheet.</summary>
    ShortcutSheet,

    /// <summary>Ctrl+, (comma): Settings.</summary>
    OpenSettings,

    /// <summary>Z: time travel (view the calendar in another time zone).</summary>
    TimeTravel,

    /// <summary>S: share availability.</summary>
    ShareAvailability,

    /// <summary>P: overlay people's calendars.</summary>
    PeopleOverlay,

    /// <summary>F: meet with (find a time with people).</summary>
    MeetWith,

    /// <summary>E then Z: edit the selected event's time zone.</summary>
    EditTimeZone,

    /// <summary>E then F: overlay the selected event's guests.</summary>
    ParticipantOverlay,

    /// <summary>E was pressed; waiting up to 1.5 s for the second key.</summary>
    SequenceStarted,
}

/// <summary>A resolved shortcut. <see cref="Days"/> is set for <see cref="CalendarCommand.Days"/>.</summary>
public readonly record struct ShortcutResult(CalendarCommand Command, int Days = 0);

/// <summary>Maps a key chord to a calendar command. Keys are <c>VirtualKey.ToString()</c> values.</summary>
public static class ShortcutMap
{
    /// <summary>
    /// Resolves a chord; the only Alt chords are Alt+Left and Alt+Right (back and forward).
    /// </summary>
    /// <remarks>
    /// The punctuation shortcuts (? / . , - and =) are defined by the character they type, and punctuation keys sit on
    /// different keys from one layout to the next. So for a punctuation key (<c>VK_OEM_*</c>) the app passes
    /// <paramref name="typed"/>, the character the key types on the user's layout with Shift as held, and the shortcut
    /// follows that character: on a French layout Shift+, types "?" and opens the cheat sheet, while on a German layout
    /// the US "/" key types "#" and does nothing. Ctrl++ zooms in like Ctrl+=, for layouts where + has its own key.
    /// Without <paramref name="typed"/> (unknown), the US key codes apply.
    /// </remarks>
    /// <param name="key">The <c>VirtualKey.ToString()</c> value.</param>
    /// <param name="ctrl">True while Ctrl is held.</param>
    /// <param name="shift">True while Shift is held.</param>
    /// <param name="alt">True while Alt is held.</param>
    /// <param name="typed">For a punctuation key, the character it types on the current layout; null for every other key.</param>
    public static ShortcutResult Resolve(string key, bool ctrl, bool shift, bool alt, char? typed = null)
    {
        // Punctuation Follows The Character It Types (as the US key and Shift that type it there)
        if (typed is { } character)
        {
            (key, shift) = UsKeyTyping(character);
        }

        // Alt: Only Back And Forward (Windows' own history keys)
        if (alt)
        {
            return ctrl || shift ? default : key switch
            {
                "Left" => new(CalendarCommand.NavigateBack),
                "Right" => new(CalendarCommand.NavigateForward),
                _ => default,
            };
        }

        // Ctrl+Shift
        if (ctrl && shift)
        {
            return key switch
            {
                "E" => new(CalendarCommand.ToggleWeekends),
                "D" => new(CalendarCommand.ToggleDeclined),
                "L" => new(CalendarCommand.ToggleTheme),
                "Delete" => new(CalendarCommand.CancelEventQuietly),
                _ => default,
            };
        }

        // Ctrl
        if (ctrl)
        {
            return key switch
            {
                "187" or "Add" => new(CalendarCommand.ZoomIn),
                "189" or "Subtract" => new(CalendarCommand.ZoomOut),
                "Number0" or "NumberPad0" => new(CalendarCommand.ZoomReset),
                "A" => new(CalendarCommand.SelectAll),
                "C" => new(CalendarCommand.Copy),
                "X" => new(CalendarCommand.Cut),
                "V" => new(CalendarCommand.Paste),
                "Z" => new(CalendarCommand.Undo),
                "J" => new(CalendarCommand.JoinMeeting),
                "K" => new(CalendarCommand.CommandMenu),
                "F" => new(CalendarCommand.Search),
                "188" => new(CalendarCommand.OpenSettings),
                _ => default,
            };
        }

        // Shift
        if (shift)
        {
            return key switch
            {
                "N" => new(CalendarCommand.PreviousEvent),
                "191" => new(CalendarCommand.ShortcutSheet),
                _ => default,
            };
        }

        // Plain Keys
        if (DigitOf(key) is { } digit)
        {
            return digit switch
            {
                0 => new(CalendarCommand.WeekView),
                1 => new(CalendarCommand.DayView),
                _ => new(CalendarCommand.Days, digit),
            };
        }

        return key switch
        {
            "T" => new(CalendarCommand.Today),
            "Left" or "K" => new(CalendarCommand.Previous),
            "Right" or "J" => new(CalendarCommand.Next),
            "D" => new(CalendarCommand.DayView),
            "W" => new(CalendarCommand.WeekView),
            "M" => new(CalendarCommand.MonthView),
            "190" or "Decimal" => new(CalendarCommand.GoToDate),
            "N" => new(CalendarCommand.NextEvent),
            "B" => new(CalendarCommand.PreviousEvent),
            "C" => new(CalendarCommand.CreateEvent),
            "E" => new(CalendarCommand.EditEvent),
            "V" => new(CalendarCommand.OpenMeetingLink),
            "X" => new(CalendarCommand.ToggleSelect),
            "Delete" => new(CalendarCommand.DeleteSelected),
            "191" or "Divide" => new(CalendarCommand.Search),
            "Z" => new(CalendarCommand.TimeTravel),
            "S" => new(CalendarCommand.ShareAvailability),
            "P" => new(CalendarCommand.PeopleOverlay),
            "F" => new(CalendarCommand.MeetWith),
            _ => default,
        };
    }

    // The US key code and Shift state that type a shortcut's character; any other character matches no shortcut
    private static (string Key, bool Shift) UsKeyTyping(char typed) => typed switch
    {
        '=' or '+' => ("187", false),
        ',' => ("188", false),
        '-' => ("189", false),
        '.' => ("190", false),
        '/' => ("191", false),
        '?' => ("191", true),
        _ => ("", false),
    };

    private static int? DigitOf(string key) =>
        key.Length == 7 && key.StartsWith("Number", StringComparison.Ordinal) && char.IsAsciiDigit(key[6]) ? key[6] - '0'
        : key.Length == 10 && key.StartsWith("NumberPad", StringComparison.Ordinal) && char.IsAsciiDigit(key[9]) ? key[9] - '0'
        : null;
}
