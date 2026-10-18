using LeafCalendar.Core.Events;
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaMatchTests
{
    private static readonly DateOnly Day = new(2026, 10, 16);
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 17, 0, 0, TimeSpan.Zero);

    private static CalendarOccurrence Google(string calendar, string id, string title, DateTimeOffset start, bool allDay = false) =>
        new("acct", calendar, id, null, null, start, start.AddHours(1), allDay, title, EventKind.Default, ResponseStatus.Accepted, "#000000", null, false, false);

    private static SunsamaTask Task(string id, IReadOnlyList<(DateTimeOffset, DateTimeOffset)> projected, params SunsamaEventRef[] events) =>
        new(id, "Title", "Channel", 60, 30, false, projected, events);

    [Fact]
    public void Blocks_Projected_OneBlockPerEntry()
    {
        var blocks = SunsamaMatch.Blocks(Day, [Task("t1", [(Noon, Noon.AddMinutes(40)), (Noon.AddHours(2), Noon.AddHours(3))])], []);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(SunsamaBlockKind.Projected, b.Kind));
        Assert.All(blocks, b => Assert.Equal(("t1", "Title", "Channel", 60, 30, Day), (b.TaskId, b.Title, b.Channel, b.EstimateMinutes, b.TrackedMinutes, b.Day)));
        Assert.Equal(Noon.AddMinutes(40), blocks[0].End);
    }

    [Fact]
    public void Blocks_TimeboxOnSunsamaCalendar_IsATimeboxBlock()
    {
        var box = new SunsamaEventRef("e1", "sunsama-cal", "Title", Noon, Noon.AddHours(1), OnGoogle: false);

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [Task("t1", [], box)], []));

        Assert.Equal(SunsamaBlockKind.Timebox, block.Kind);
        Assert.Equal(Noon, block.Start);
        Assert.Null(block.MatchedEventId);
    }

    [Fact]
    public void Blocks_GoogleEventSameStartAndTitle_IsMatchedNotDrawn()
    {
        var lunch = new SunsamaEventRef("s-lunch", "work@example.com", "Lunch Break", Noon, Noon.AddMinutes(45), OnGoogle: true);
        var cached = new[] { Google("work@example.com", "g-other", "Other", Noon), Google("work@example.com", "g-lunch", "Lunch Break", Noon) };

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [Task("t1", [], lunch)], cached));

        Assert.Equal(SunsamaBlockKind.Matched, block.Kind);
        Assert.Equal(("acct", "work@example.com", "g-lunch"), (block.MatchedAccountId, block.MatchedCalendarId, block.MatchedEventId));
        Assert.Equal(Noon.AddHours(1), block.End);
    }

    [Fact]
    public void Blocks_TaskAlsoAGoogleEvent_DrawsNoProjectedOrTimeboxBlock()
    {
        var google = new SunsamaEventRef("s-lunch", "work@example.com", "Lunch Break", Noon, Noon.AddHours(1), OnGoogle: true);
        var box = new SunsamaEventRef("e1", "sunsama-cal", "Lunch Break", Noon.AddHours(2), Noon.AddHours(3), OnGoogle: false);
        var hidden = new SunsamaEventRef("s-hidden", "hidden@example.com", "Title", Noon, Noon.AddHours(1), OnGoogle: true);
        var cached = new[] { Google("work@example.com", "g-lunch", "Lunch Break", Noon) };

        var blocks = SunsamaMatch.Blocks(
            Day,
            [Task("t1", [(Noon.AddHours(4), Noon.AddHours(5))], google, box), Task("t2", [(Noon.AddHours(4), Noon.AddHours(5))], hidden)],
            cached);

        var block = Assert.Single(blocks);
        Assert.Equal(("t1", SunsamaBlockKind.Matched), (block.TaskId, block.Kind));
    }

    [Fact]
    public void Blocks_GoogleEventTitleDiffers_FallsBackToSameStart()
    {
        var ev = new SunsamaEventRef("s", "work@example.com", "Renamed in Sunsama", Noon, Noon.AddHours(1), OnGoogle: true);

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [Task("t1", [], ev)], [Google("work@example.com", "g1", "Original", Noon)]));

        Assert.Equal("g1", block.MatchedEventId);
    }

    [Fact]
    public void Blocks_GoogleEventNotCached_DrawsNothing()
    {
        var ev = new SunsamaEventRef("s", "hidden@example.com", "Title", Noon, Noon.AddHours(1), OnGoogle: true);

        Assert.Empty(SunsamaMatch.Blocks(Day, [Task("t1", [], ev)], [Google("work@example.com", "g1", "Title", Noon)]));
    }

    // A Done Task Sunsama No Longer Places: it ends when it was done, as long as it was tracked (else planned, else 15 m)
    [Theory]
    [InlineData(30, 60, 30)]
    [InlineData(null, 60, 60)]
    [InlineData(null, null, 15)]
    public void Blocks_CompletedWithNoTimes_EndsWhenItWasDone(int? tracked, int? estimate, int minutes)
    {
        var task = new SunsamaTask("t1", "Title", "Channel", estimate, tracked, true, [], [], "<p>Notes</p>", [new("Step", true)], Noon, Day);

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [task], []));

        Assert.Equal(SunsamaBlockKind.Completed, block.Kind);
        Assert.Equal((Noon.AddMinutes(-minutes), Noon), (block.Start, block.End));
        Assert.Equal(("<p>Notes</p>", "1Step"), (block.Notes, block.Subtasks));
        Assert.True(block.Completed);
    }

    [Fact]
    public void Blocks_CompletedOtherDayOrTiedToAnEvent_IsNotPlaced()
    {
        SunsamaTask Done(DateOnly on, bool hasEventIds) =>
            new("t1", "Title", "Channel", 60, 30, true, [], [], CompletedAt: Noon, CompletedOn: on, HasEventIds: hasEventIds);

        Assert.Empty(SunsamaMatch.Blocks(Day, [Done(Day.AddDays(1), false), Done(Day, true)], []));
    }

    [Fact]
    public void Blocks_CarryTheTasksNotesAndSubtasks()
    {
        var task = Task("t1", [(Noon, Noon.AddHours(1))]) with { Notes = "<p>Hi</p>", Subtasks = [new("A", true), new("B", false)] };

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [task], []));

        Assert.Equal(("<p>Hi</p>", "1A\n0B"), (block.Notes, block.Subtasks));
        Assert.Equal([new SunsamaSubtask("A", true), new SunsamaSubtask("B", false)], SunsamaSubtasks.Decode(block.Subtasks));
    }

    [Theory]
    [InlineData("Line\r\nbreak", "Line  break")]
    [InlineData("", "")]
    public void Subtasks_EncodeKeepsOneLineEach(string title, string expected) =>
        Assert.Equal([new SunsamaSubtask(expected, false)], SunsamaSubtasks.Decode(SunsamaSubtasks.Encode([new(title, false)])));

    [Fact]
    public void Subtasks_DecodeEmpty_IsNone() => Assert.Empty(SunsamaSubtasks.Decode(""));

    [Fact]
    public void Blocks_AllDayCachedEvent_IsNotAMatch()
    {
        var ev = new SunsamaEventRef("s", "work@example.com", "Title", Noon, Noon.AddHours(1), OnGoogle: true);

        Assert.Empty(SunsamaMatch.Blocks(Day, [Task("t1", [], ev)], [Google("work@example.com", "g1", "Title", Noon, allDay: true)]));
    }
}
