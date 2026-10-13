using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class FitMessagesTests
{
    [Fact]
    public void For_SplitOnly_OneMessage()
    {
        Assert.Equal(["Split into 4 times of 30 minutes."], FitMessages.For(new FitSummary(4, 30, 0, 0, 0), "Cal.com"));
    }

    [Fact]
    public void For_Everything_SplitCombinesStretchedAndRemoved_OnNewLines()
    {
        var messages = FitMessages.For(new FitSummary(2, 30, 1, 1, 90), "Cal.com");

        Assert.Equal(
            [
                "Split into 2 times of 30 minutes.\nLast time block stretched to match the available time slot.\nLast time block removed to match the available time slots.",
                "1 hour 30 minutes wasn't open on Cal.com.",
            ],
            messages);
    }

    [Fact]
    public void For_OneKept_UsesSingularTime()
    {
        Assert.Equal(["Split into 1 time of 30 minutes."], FitMessages.For(new FitSummary(1, 30, 0, 0, 0), "Cal.com"));
    }

    [Fact]
    public void For_TwoOfEach_KeepsPlural()
    {
        var messages = FitMessages.For(new FitSummary(2, 30, 2, 2, 0), "Cal.com");

        Assert.Equal(
            [
                "Split into 2 times of 30 minutes.\nStretched 2 times to their full 30 minutes, since you picked more than half of each.\nRemoved 2 times, since you picked half or less of each.",
            ],
            messages);
    }

    [Fact]
    public void For_NothingKept_SaysNothingOpen()
    {
        Assert.Equal(["That time is not open per your availability. Pick a different time."], FitMessages.For(new FitSummary(0, 0, 0, 0, 60), "Cal.com"));
    }

    [Fact]
    public void For_NothingKeptButSomeRemoved_SaysRemoved()
    {
        Assert.Equal(["Last time block removed to match the available time slots."], FitMessages.For(new FitSummary(0, 0, 0, 1, 0), "Cal.com"));
    }

    [Theory]
    [InlineData(45, "45 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(150, "2 hours 30 minutes")]
    public void Duration_Reads(int minutes, string expected) => Assert.Equal(expected, FitMessages.Duration(minutes));
}
