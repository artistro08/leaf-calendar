using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class SharedEventsTests
{
    static readonly DateTimeOffset Nine = new(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);

    static CalendarOccurrence On(string account, string calendar, string? uid, string color = "#4285F4", DateTimeOffset? start = null, string id = "evt", string? colorId = null, bool allDay = false)
    {
        var s = start ?? Nine;
        return new(account, calendar, id, uid, null, s, s.AddHours(1), allDay, "Standup", EventKind.Default, ResponseStatus.Accepted, color, colorId, false, false);
    }

    [Fact]
    public void Merge_SameEventOnTwoAccounts_ShowsItOnceWithBothColors()
    {
        var work     = On("work", "work@example.com", "uid-1", "#039BE5");
        var personal = On("home", "home@gmail.com", "uid-1", "#D50000");

        var result = SharedEvents.Merge([work, personal]);

        Assert.Equal([work], result.Shown);
        Assert.Equal(["#039BE5", "#D50000"], result.Stripes[work.Key]);
    }

    [Fact]
    public void Merge_ThreeCalendars_ThreeStripesInOrder()
    {
        var a = On("a", "a", "uid", "#111111");
        var b = On("b", "b", "uid", "#222222");
        var c = On("a", "family", "uid", "#333333");

        var result = SharedEvents.Merge([a, b, c]);

        Assert.Single(result.Shown);
        Assert.Equal(["#111111", "#222222", "#333333"], result.Stripes[a.Key]);
    }

    [Fact]
    public void Merge_EventColor_WinsOverTheCalendarColor()
    {
        var a = On("a", "a", "uid", "#111111");
        var b = On("b", "b", "uid", "#222222", colorId: "11");

        Assert.Equal(["#111111", "#D50000"], SharedEvents.Merge([a, b]).Stripes[a.Key]);
    }

    [Fact]
    public void Merge_DifferentTimes_AreDifferentEvents()
    {
        // Two instances of one series share a UID; only the same instance merges
        var monday  = On("a", "a", "series");
        var tuesday = On("b", "b", "series", start: Nine.AddDays(1));

        var result = SharedEvents.Merge([monday, tuesday]);

        Assert.Equal([monday, tuesday], result.Shown);
        Assert.Empty(result.Stripes);
    }

    [Fact]
    public void Merge_AllDayAndTimedWithOneUid_StaySeparate()
    {
        var timed  = On("a", "a", "uid");
        var allDay = On("b", "b", "uid", allDay: true);

        Assert.Equal(2, SharedEvents.Merge([timed, allDay]).Shown.Count);
    }

    [Fact]
    public void Merge_NoUid_NeverMerges()
    {
        var a = On("a", "a", null);
        var b = On("b", "b", null);

        var result = SharedEvents.Merge([a, b]);

        Assert.Equal([a, b], result.Shown);
        Assert.Empty(result.Stripes);
    }

    [Fact]
    public void Merge_TwoEventsOnOneCalendar_StaySeparate()
    {
        var first  = On("a", "a", "uid", id: "one");
        var second = On("a", "a", "uid", id: "two");

        var result = SharedEvents.Merge([first, second]);

        Assert.Equal([first, second], result.Shown);
        Assert.Empty(result.Stripes);
    }

    [Fact]
    public void Merge_KeepsTheInputOrder()
    {
        var early  = On("a", "a", "u1", start: Nine.AddHours(-2));
        var shared = On("a", "a", "u2");
        var copy   = On("b", "b", "u2");
        var late   = On("b", "b", "u3", start: Nine.AddHours(2));

        Assert.Equal([early, shared, late], SharedEvents.Merge([early, shared, copy, late]).Shown);
    }

    [Fact]
    public void Merge_Null_Throws() => Assert.Throws<ArgumentNullException>(() => SharedEvents.Merge(null!));
}
