using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Tests;

public sealed class DefaultCalendarTests
{
    static readonly IReadOnlySet<string> Connected = new HashSet<string> { "a1", "a2" };

    static CalendarInfo Cal(string account, string id, string role, bool primary = false, bool hidden = false) =>
        new(account, id, id, null, role, primary, false, null, hidden, null, 0);

    [Fact]
    public void Pick_PreferredWritable_WinsOverPrimary()
    {
        var calendars = new[] { Cal("a1", "main", "owner", primary: true), Cal("a1", "family", "writer") };

        var pick = DefaultCalendar.Pick(calendars, Connected, new CalendarRef("a1", "family"));

        Assert.Equal("family", pick?.Id);
    }

    [Fact]
    public void Pick_PreferredNowReadOnly_FallsBackToPrimary()
    {
        var calendars = new[] { Cal("a1", "main", "owner", primary: true), Cal("a1", "family", "reader") };

        var pick = DefaultCalendar.Pick(calendars, Connected, new CalendarRef("a1", "family"));

        Assert.Equal("main", pick?.Id);
    }

    [Theory]
    [InlineData("a1", "deleted")]
    [InlineData("gone", "family")]
    public void Pick_PreferredGoneOrAccountDisconnected_FallsBackToPrimary(string account, string calendar)
    {
        var calendars = new[] { Cal("a1", "main", "owner", primary: true), Cal("gone", "family", "writer") };

        var pick = DefaultCalendar.Pick(calendars, Connected, new CalendarRef(account, calendar));

        Assert.Equal("main", pick?.Id);
    }

    [Fact]
    public void Pick_ForAnotherAccount_IgnoresThePreferredOne()
    {
        var calendars = new[] { Cal("a1", "family", "writer"), Cal("a2", "work", "owner", primary: true) };

        var pick = DefaultCalendar.Pick(calendars, Connected, new CalendarRef("a1", "family"), "a2");

        Assert.Equal("work", pick?.Id);
    }

    [Fact]
    public void Pick_NoPreference_KeepsTheOldRule()
    {
        var calendars = new[]
        {
            Cal("a1", "readonly", "reader", primary: true),
            Cal("a1", "hiddenWriter", "writer", hidden: true),
            Cal("a1", "shownWriter", "writer"),
        };

        Assert.Equal("shownWriter", DefaultCalendar.Pick(calendars, Connected, null)?.Id);
        Assert.Equal("main", DefaultCalendar.Pick([.. calendars, Cal("a1", "main", "owner", primary: true)], Connected, null)?.Id);
    }
}
