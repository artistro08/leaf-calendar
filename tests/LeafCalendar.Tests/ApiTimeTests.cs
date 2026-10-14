using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiTimeTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Theory]
    [InlineData("2026-10-14T09:00:00-04:00")]
    [InlineData("2026-10-14T13:00:00Z")]
    [InlineData("2026-10-14T13:00Z")]
    [InlineData("2026-10-14T13:00:00.123+00:00")]
    public void ReadInstant_WithOffset_Reads(string text)
    {
        Assert.NotNull(ApiTime.ReadInstant(text));
    }

    [Theory]
    [InlineData("2026-10-14T13:00:00")]
    [InlineData("2026-10-14")]
    [InlineData("Oct 14 2026 1 PM")]
    [InlineData(null)]
    public void ReadInstant_WithoutOffset_IsNull(string? text)
    {
        Assert.Null(ApiTime.ReadInstant(text));
    }

    [Fact]
    public void ReadSpan_Dates_AreAllDayUtcMidnights()
    {
        var (start, end, allDay) = ApiTime.ReadSpan("2026-10-14", "2026-10-16");

        Assert.True(allDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void ReadSpan_Instants_AreTimed()
    {
        var (_, _, allDay) = ApiTime.ReadSpan("2026-10-14T09:00:00-04:00", "2026-10-14T10:00:00-04:00");

        Assert.False(allDay);
    }

    [Theory]
    [InlineData("2026-10-14", "2026-10-14T10:00:00-04:00")]
    [InlineData("2026-10-14T10:00:00-04:00", "2026-10-14T09:00:00-04:00")]
    [InlineData("2026-10-14", "2026-10-14")]
    [InlineData("2026-10-14T10:00:00", "2026-10-14T11:00:00")]
    [InlineData(null, "2026-10-14")]
    public void ReadSpan_Bad_IsInvalidParams(string? start, string? end)
    {
        var ex = Assert.Throws<ApiProtocolException>(() => ApiTime.ReadSpan(start, end));

        Assert.Equal(ApiErrors.InvalidParams, ex.Code);
    }

    [Fact]
    public void Write_UsesTheZonesOffset()
    {
        Assert.Equal("2026-10-14T09:00:00-04:00", ApiTime.Write(new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero), NewYork));
        Assert.Equal("2026-10-14", ApiTime.Write(new DateOnly(2026, 10, 14)));
    }
}
