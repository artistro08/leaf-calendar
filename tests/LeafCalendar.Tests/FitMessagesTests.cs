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
    public void For_Everything_OneMessageEach_InOrder()
    {
        var messages = FitMessages.For(new FitSummary(2, 30, 1, 1, 90), "Cal.com");

        Assert.Equal(
            [
                "Split into 2 times of 30 minutes.",
                "Stretched 1 times to their full 30 minutes, since you picked more than half of each.",
                "Removed 1 times, since you picked half or less of each.",
                "1 hour 30 minutes wasn't open on Cal.com.",
            ],
            messages);
    }

    [Fact]
    public void For_NothingKept_SaysNothingOpen()
    {
        Assert.Equal(["None of that time is open on Cal.com."], FitMessages.For(new FitSummary(0, 0, 0, 0, 60), "Cal.com"));
    }

    [Fact]
    public void For_NothingKeptButSomeRemoved_SaysRemoved()
    {
        Assert.Equal(["Removed 1 times, since you picked half or less of each."], FitMessages.For(new FitSummary(0, 0, 0, 1, 0), "Cal.com"));
    }

    [Theory]
    [InlineData(45, "45 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(150, "2 hours 30 minutes")]
    public void Duration_Reads(int minutes, string expected) => Assert.Equal(expected, FitMessages.Duration(minutes));
}
