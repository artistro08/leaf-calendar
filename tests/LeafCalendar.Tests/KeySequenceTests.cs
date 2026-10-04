using LeafCalendar.Core.Views;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class KeySequenceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static CalendarCommand Press(KeySequence keys, string key, bool ctrl = false) => keys.Resolve(key, ctrl, shift: false, alt: false).Command;

    [Theory]
    [InlineData("Y", CalendarCommand.RsvpYes)]
    [InlineData("N", CalendarCommand.RsvpNo)]
    [InlineData("M", CalendarCommand.RsvpMaybe)]
    [InlineData("E", CalendarCommand.EmailGuests)]
    [InlineData("U", CalendarCommand.EditDuration)]
    public void EThenKey_WithinTimeout_IsSequence(string second, CalendarCommand expected)
    {
        var keys = new KeySequence(_time);

        Assert.Equal(CalendarCommand.SequenceStarted, Press(keys, "E"));
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(expected, Press(keys, second));
        Assert.False(keys.IsPending);
    }

    [Fact]
    public void Resolve_TypedCharacter_ReachesShortcutMap()
    {
        var keys = new KeySequence(_time);

        // French: Shift and the US "," key type "?"
        Assert.Equal(CalendarCommand.ShortcutSheet, keys.Resolve("188", ctrl: false, shift: true, alt: false, typed: '?').Command);
    }

    [Fact]
    public void EThenN_AfterTimeout_IsNextEvent()
    {
        var keys = new KeySequence(_time);
        Press(keys, "E");
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(CalendarCommand.NextEvent, Press(keys, "N"));
    }

    [Fact]
    public void EThenOtherKey_DropsSequenceAndRunsThatKey()
    {
        var keys = new KeySequence(_time);
        Press(keys, "E");

        Assert.Equal(CalendarCommand.Today, Press(keys, "T"));
        Assert.Equal(CalendarCommand.None, keys.Expire().Command);
    }

    [Fact]
    public void Expire_AfterE_IsEditEvent()
    {
        var keys = new KeySequence(_time);
        Press(keys, "E");

        Assert.Equal(CalendarCommand.EditEvent, keys.Expire().Command);
        Assert.Equal(CalendarCommand.None, keys.Expire().Command);
    }

    [Theory]
    [InlineData("Z", CalendarCommand.EditTimeZone)]
    [InlineData("F", CalendarCommand.ParticipantOverlay)]
    public void EThen_M5Keys(string second, CalendarCommand expected)
    {
        var keys = new KeySequence(_time);

        Press(keys, "E");
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(expected, Press(keys, second));
    }

    [Fact]
    public void FAlone_IsMeetWith() =>
        Assert.Equal(CalendarCommand.MeetWith, Press(new KeySequence(_time), "F"));

    [Fact]
    public void CtrlE_IsNotASequence()
    {
        Assert.Equal(CalendarCommand.None, Press(new KeySequence(_time), "E", ctrl: true));
    }
}
