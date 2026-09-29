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
}

/// <summary>A resolved shortcut. <see cref="Days"/> is set for <see cref="CalendarCommand.Days"/>.</summary>
public readonly record struct ShortcutResult(CalendarCommand Command, int Days = 0);

/// <summary>Maps a key chord to a calendar command. Keys are <c>VirtualKey.ToString()</c> values.</summary>
public static class ShortcutMap
{
    /// <summary>Resolves a chord; Alt chords are never calendar shortcuts.</summary>
    public static ShortcutResult Resolve(string key, bool ctrl, bool shift, bool alt)
    {
        if (alt)
        {
            return default;
        }

        // Ctrl+Shift
        if (ctrl && shift)
        {
            return key switch
            {
                "E" => new(CalendarCommand.ToggleWeekends),
                "D" => new(CalendarCommand.ToggleDeclined),
                "L" => new(CalendarCommand.ToggleTheme),
                _   => default,
            };
        }

        // Ctrl
        if (ctrl)
        {
            return key switch
            {
                "187" or "Add"             => new(CalendarCommand.ZoomIn),
                "189" or "Subtract"        => new(CalendarCommand.ZoomOut),
                "Number0" or "NumberPad0"  => new(CalendarCommand.ZoomReset),
                _                          => default,
            };
        }

        // Shift
        if (shift)
        {
            return key == "N" ? new(CalendarCommand.PreviousEvent) : default;
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
            "T"                 => new(CalendarCommand.Today),
            "Left" or "K"       => new(CalendarCommand.Previous),
            "Right" or "J"      => new(CalendarCommand.Next),
            "D"                 => new(CalendarCommand.DayView),
            "W"                 => new(CalendarCommand.WeekView),
            "M"                 => new(CalendarCommand.MonthView),
            "190" or "Decimal"  => new(CalendarCommand.GoToDate),
            "N"                 => new(CalendarCommand.NextEvent),
            "B"                 => new(CalendarCommand.PreviousEvent),
            _                   => default,
        };
    }

    static int? DigitOf(string key) =>
        key.Length == 7 && key.StartsWith("Number", StringComparison.Ordinal) && char.IsAsciiDigit(key[6]) ? key[6] - '0'
        : key.Length == 10 && key.StartsWith("NumberPad", StringComparison.Ordinal) && char.IsAsciiDigit(key[9]) ? key[9] - '0'
        : null;
}
