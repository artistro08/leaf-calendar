namespace LeafCalendar.Core.Views;

/// <summary>
/// "E then ..." key sequences (spec 8.7): E then Y / N / M replies, E then E emails guests, E then U edits the
/// duration. The second key must come within <see cref="Timeout"/>. E on its own edits the selected event once
/// the sequence times out: the app calls <see cref="Expire"/> from a 1.5 s timer.
/// </summary>
public sealed class KeySequence(TimeProvider time)
{
    /// <summary>How long the second key may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1.5);

    DateTimeOffset? _startedAt;

    /// <summary>True while waiting for the second key.</summary>
    public bool IsPending => _startedAt is not null;

    /// <summary>Resolves a key press, finishing or starting a sequence first.</summary>
    public ShortcutResult Resolve(string key, bool ctrl, bool shift, bool alt)
    {
        // Second Key Of "E Then ..."
        if (_startedAt is { } started)
        {
            _startedAt = null;
            if (!ctrl && !shift && !alt && time.GetUtcNow() - started <= Timeout && Second(key) is { } command)
            {
                return new ShortcutResult(command);
            }
        }

        // E Starts A Sequence
        if (key == "E" && !ctrl && !shift && !alt)
        {
            _startedAt = time.GetUtcNow();
            return new ShortcutResult(CalendarCommand.SequenceStarted);
        }

        return ShortcutMap.Resolve(key, ctrl, shift, alt);
    }

    /// <summary>The timer ran out: a lone E means "edit" (None when no sequence is waiting).</summary>
    public ShortcutResult Expire()
    {
        if (_startedAt is null)
        {
            return default;
        }

        _startedAt = null;
        return new ShortcutResult(CalendarCommand.EditEvent);
    }

    static CalendarCommand? Second(string key) => key switch
    {
        "Y" => CalendarCommand.RsvpYes,
        "N" => CalendarCommand.RsvpNo,
        "M" => CalendarCommand.RsvpMaybe,
        "E" => CalendarCommand.EmailGuests,
        "U" => CalendarCommand.EditDuration,
        _   => null,
    };
}
