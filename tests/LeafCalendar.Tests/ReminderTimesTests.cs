using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public class ReminderTimesTests
{
    [Theory]
    [InlineData(0, "At start")]
    [InlineData(5, "5 min")]
    [InlineData(30, "30 min")]
    [InlineData(60, "1 hr")]
    [InlineData(120, "2 hr")]
    [InlineData(90, "90 min")]
    [InlineData(1440, "1 day")]
    [InlineData(2880, "2 days")]
    public void Label_Minutes_ReadsLikeGoogle(int minutes, string expected)
    {
        Assert.Equal(expected, ReminderTimes.Label(minutes));
    }

    [Fact]
    public void Choices_NothingLoaded_AreThePresets()
    {
        Assert.Equal([0, 5, 10, 15, 30, 60, 1440], ReminderTimes.Choices([]));
    }

    [Fact]
    public void Choices_LoadedOddTimes_AreAddedInOrderOnce()
    {
        Assert.Equal([0, 5, 10, 15, 30, 45, 60, 1440, 2880], ReminderTimes.Choices([2880, 45, 30]));
    }
}
