using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public class GoogleJsonTests
{
    [Fact]
    public void Deserialize_EventsPage_ReadsItemsAndTokens()
    {
        var page = JsonSerializer.Deserialize(Fixture.Read("events-page1.json"), GoogleJsonContext.Default.EventsPage)!;

        Assert.Equal("page-2", page.NextPageToken);
        Assert.Null(page.NextSyncToken);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public void Deserialize_TimedAndAllDayEvents_ReadsDates()
    {
        var page = JsonSerializer.Deserialize(Fixture.Read("events-page1.json"), GoogleJsonContext.Default.EventsPage)!;
        var timed = JsonSerializer.Deserialize(page.Items[0], GoogleJsonContext.Default.GoogleEvent)!;
        var allDay = JsonSerializer.Deserialize(page.Items[1], GoogleJsonContext.Default.GoogleEvent)!;

        Assert.Equal("evt-single", timed.Id);
        Assert.Equal("evt-single@google.com", timed.ICalUid);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), timed.Start!.DateTime!.Value.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 10, 12), allDay.Start!.Date);
        Assert.Null(allDay.Start.DateTime);
    }

    [Fact]
    public void Deserialize_CanceledException_ReadsRecurringFields()
    {
        var page = JsonSerializer.Deserialize(Fixture.Read("events-page2.json"), GoogleJsonContext.Default.EventsPage)!;
        var master = JsonSerializer.Deserialize(page.Items[0], GoogleJsonContext.Default.GoogleEvent)!;
        var exception = JsonSerializer.Deserialize(page.Items[1], GoogleJsonContext.Default.GoogleEvent)!;

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR"], master.Recurrence);
        Assert.Equal("cancelled", exception.Status);
        Assert.Equal("evt-weekly", exception.RecurringEventId);
        Assert.NotNull(exception.OriginalStartTime?.DateTime);
    }

    [Fact]
    public void Deserialize_CalendarList_ReadsFlagsAndReminders()
    {
        var list = JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!;

        Assert.True(list.Items[0].Primary);
        Assert.Equal("owner", list.Items[0].AccessRole);
        Assert.Equal(10, list.Items[0].DefaultReminders![0].Minutes);
        Assert.False(list.Items[1].Primary);
    }

    [Fact]
    public void Deserialize_TokenResponse_ReadsSnakeCase()
    {
        var token = JsonSerializer.Deserialize(Fixture.Read("token-response.json"), GoogleJsonContext.Default.TokenResponse)!;

        Assert.Equal("ya29.test-access-token", token.AccessToken);
        Assert.Equal("1//test-refresh-token", token.RefreshToken);
        Assert.Equal(3599, token.ExpiresIn);
    }

    [Fact]
    public async Task ToExceptionAsync_ApiError_ReadsReason()
    {
        using var response = FakeHttpHandler.Json(HttpStatusCode.Forbidden, Fixture.Read("error-rate-limit.json"));

        var exception = await GoogleJson.ToExceptionAsync(response, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, exception.Status);
        Assert.Equal("rateLimitExceeded", exception.Reason);
    }

    [Fact]
    public void TryParse_NotJson_ReturnsNull()
    {
        Assert.Null(GoogleJson.TryParse("<html>oops</html>", GoogleJsonContext.Default.ApiErrorEnvelope));
    }
}
