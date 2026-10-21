using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaTextTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Theory]
    [InlineData("1 hours and 24 minutes", 84)]
    [InlineData("30 minutes", 30)]
    [InlineData("2 hours", 120)]
    [InlineData("1 hour", 60)]
    [InlineData("1 minute", 1)]
    [InlineData("0 minutes", 0)]
    public void Duration_SunsamaText_ReadsMinutes(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SunsamaText.Duration(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5 minutes")]
    [InlineData("1 hours and")]
    public void Duration_Unreadable_ReturnsNull(string? text) => Assert.Null(SunsamaText.Duration(text));

    [Fact]
    public void LocalTime_OrdinaryDay_ConvertsWithTheZone() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 15, 50, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-10-16 10:50 AM", Chicago));

    [Fact]
    public void LocalTime_Afternoon_ReadsPm() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 18, 50, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-10-16 1:50 PM", Chicago));

    [Fact]
    public void LocalTime_AmbiguousFallBack_UsesFirst() =>
        // 1:30 AM happens twice on 2026-11-01; the first is CDT (UTC-5)
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-11-01 1:30 AM", Chicago));

    [Fact]
    public void LocalTime_SkippedSpringForward_MovesForward() =>
        // 2:30 AM doesn't exist on 2026-03-08; it becomes 3:30 AM CDT
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-03-08 2:30 AM", Chicago));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10:50 AM")]
    [InlineData("2026-13-01 10:50 AM")]
    public void LocalTime_Unreadable_ReturnsNull(string? text) => Assert.Null(SunsamaText.LocalTime(text, Chicago));

    [Fact]
    public void IsTaskId_SunsamaId_IsTrue() => Assert.True(SunsamaText.IsTaskId("6ac7a44534029300011d076e"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("6ac7a44534029300011d076")]
    [InlineData("6ac7a44534029300011d076eX")]
    [InlineData("6AC7A44534029300011D076E")]
    [InlineData("../../etc/passwd/aaaaaaaa")]
    public void IsTaskId_RejectsOtherShapes(string? id) => Assert.False(SunsamaText.IsTaskId(id));

    [Theory]
    [InlineData(84, "1 h 24 m")]
    [InlineData(30, "30 m")]
    [InlineData(120, "2 h")]
    [InlineData(0, "0 m")]
    public void Format_Minutes_ReadsLikeADuration(int minutes, string text) => Assert.Equal(text, SunsamaText.Format(minutes));

    [Theory]
    [InlineData(115, "1:55")]
    [InlineData(55, "0:55")]
    [InlineData(5, "0:05")]
    [InlineData(600, "10:00")]
    public void Clock_Minutes_ReadsLikeAClock(int minutes, string text) => Assert.Equal(text, SunsamaText.Clock(minutes));

    [Fact]
    public void Clock_Negative_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => SunsamaText.Clock(-5));
}
