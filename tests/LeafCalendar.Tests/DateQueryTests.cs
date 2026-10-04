using LeafCalendar.Core.Search;

namespace LeafCalendar.Tests;

public sealed class DateQueryTests
{
    private static readonly DateOnly Today = new(2026, 10, 1); // a Thursday

    [Theory]
    [InlineData("today", 2026, 10, 1)]
    [InlineData("Tomorrow", 2026, 10, 2)]
    [InlineData("yesterday", 2026, 9, 30)]
    [InlineData("fri", 2026, 10, 2)]
    [InlineData("friday", 2026, 10, 2)]
    [InlineData("thu", 2026, 10, 1)]
    [InlineData("next thu", 2026, 10, 8)]
    [InlineData("next friday", 2026, 10, 9)]
    [InlineData("2026-10-12", 2026, 10, 12)]
    [InlineData("10/12", 2026, 10, 12)]
    [InlineData("10/12/2027", 2027, 10, 12)]
    [InlineData("oct 12", 2026, 10, 12)]
    [InlineData("October 12", 2026, 10, 12)]
    [InlineData("12 oct", 2026, 10, 12)]
    [InlineData("oct 12 2027", 2027, 10, 12)]
    [InlineData("jan 5", 2027, 1, 5)]   // more than 2 months back this year means next year
    [InlineData("sep 15", 2026, 9, 15)] // recent past stays this year
    [InlineData("in 3 days", 2026, 10, 4)]
    [InlineData("in 2 weeks", 2026, 10, 15)]
    [InlineData("nov 5th", 2026, 11, 5)]
    [InlineData("Nov 5th, 2027", 2027, 11, 5)]
    [InlineData("1st nov", 2026, 11, 1)]
    [InlineData("3 days", 2026, 10, 4)]
    [InlineData("10 weeks", 2026, 12, 10)]
    [InlineData("10 weeks from now", 2026, 12, 10)]
    [InlineData("2 months from today", 2026, 12, 1)]
    [InlineData("a week", 2026, 10, 8)]
    [InlineData("1 year", 2027, 10, 1)]
    [InlineData("3 days ago", 2026, 9, 28)]
    [InlineData("next week", 2026, 10, 8)]
    [InlineData("last week", 2026, 9, 24)]
    [InlineData("next month", 2026, 11, 1)]
    [InlineData("last year", 2025, 10, 1)]
    [InlineData("  in   3   days ", 2026, 10, 4)]
    [InlineData("sept 8", 2026, 9, 8)]
    [InlineData("Sept. 8th", 2026, 9, 8)]
    [InlineData("8 sept", 2026, 9, 8)]
    [InlineData("tues", 2026, 10, 6)]
    [InlineData("next thurs", 2026, 10, 8)]
    [InlineData("two weeks from now", 2026, 10, 15)]
    [InlineData("in three days", 2026, 10, 4)]
    [InlineData("thirty days", 2026, 10, 31)]
    [InlineData("twelve weeks ago", 2026, 7, 9)]
    [InlineData("two sundays from now", 2026, 10, 11)]
    [InlineData("a thursday from now", 2026, 10, 8)] // today is a Thursday: the next one
    [InlineData("3 fridays", 2026, 10, 16)]
    [InlineData("friday after next", 2026, 10, 16)]
    [InlineData("Thursday after next", 2026, 10, 15)] // today is a Thursday
    [InlineData("week after next", 2026, 10, 15)]
    [InlineData("month after next", 2026, 12, 1)]
    public void TryParse_Understands(string text, int y, int m, int d)
    {
        Assert.True(DateQuery.TryParse(text, Today, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("feb 30")]
    [InlineData("13/45")]
    [InlineData("in 99999 days")]
    [InlineData("standup")]
    [InlineData("in ٣ days")]          // Arabic-Indic digits aren't ASCII digits
    [InlineData("in ٣٣٣٣ weeks")]
    [InlineData("0 days")]
    [InlineData("5 lightyears")]
    [InlineData("next decade")]
    [InlineData("3 days ago from now")]
    public void TryParse_Rejects(string text) => Assert.False(DateQuery.TryParse(text, Today, out _));

    [Fact]
    public void TryParse_Feb29_IsTheNextLeapDayOrRefused()
    {
        // Next Year Is The Leap Year
        Assert.True(DateQuery.TryParse("feb 29", new DateOnly(2027, 12, 15), out var date));
        Assert.Equal(new DateOnly(2028, 2, 29), date);

        // This Year's Was More Than 2 Months Ago, And Next Year Has None
        Assert.False(DateQuery.TryParse("feb 29", new DateOnly(2028, 6, 1), out _));
    }

    [Fact]
    public void Label_AddsTheYearOnlyWhenItDiffers()
    {
        Assert.Equal("Mon, Oct 12", DateQuery.Label(new DateOnly(2026, 10, 12), Today));
        Assert.Equal("Tue, Jan 5, 2027", DateQuery.Label(new DateOnly(2027, 1, 5), Today));
    }

    [Fact]
    public void Label_NamesTodayTomorrowAndYesterday()
    {
        Assert.Equal("Today", DateQuery.Label(Today, Today));
        Assert.Equal("Tomorrow", DateQuery.Label(Today.AddDays(1), Today));
        Assert.Equal("Yesterday", DateQuery.Label(Today.AddDays(-1), Today));
    }

    [Fact]
    public void GoTo_ReadsAsASentence()
    {
        Assert.Equal("Go to today", DateQuery.GoTo(Today, Today));
        Assert.Equal("Go to tomorrow", DateQuery.GoTo(Today.AddDays(1), Today));
        Assert.Equal("Go to Mon, Oct 12", DateQuery.GoTo(new DateOnly(2026, 10, 12), Today));
    }
}
