using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class BookingWatcherTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly TempFolder _logs = new();
    private readonly FakeHttpHandler _cal = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero));
    private readonly SchedulingHub _hub;
    private readonly BookingWatcher _watcher;

    public BookingWatcherTests()
    {
        var log = new AppLog(_logs.Path, _time);
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        _hub = new SchedulingHub(new HttpClient(_cal), _tokens, log, _time);
        _hub.Load([]);
        _watcher = new BookingWatcher(_hub, _db.Database, log, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _logs.Dispose();
    }

    private long SaveGroup(string provider = "calcom")
    {
        using var conn = _db.Database.Open();
        var nine = new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
        var link = provider.Length > 0 ? new ShareLink(provider, "101", "https://cal.com/leaf-tester/30min", 30) : ShareLink.None;
        return ShareGroupStore.Insert(conn, "", "", "UTC", [new BusyRange(nine, nine.AddHours(2))], _time.GetUtcNow(), [], link);
    }

    [Fact]
    public async Task CheckAsync_NoProviderGroups_SendsNothing()
    {
        SaveGroup(provider: "");

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_cal.Requests);
    }

    [Fact]
    public async Task CheckAsync_MatchingBooking_DeletesGroupAndRaises()
    {
        SaveGroup();
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/bookings", HttpStatusCode.OK, Fixture.Read("calcom-bookings.json"));
        var found = new List<BookingFoundEventArgs>();
        _watcher.BookingFound += (_, e) => found.Add(e);

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jane Doe", Assert.Single(found).Booking.AttendeeName);
        using var conn = _db.Database.Open();
        Assert.Empty(ShareGroupStore.GetAll(conn, _time.GetUtcNow()));
    }

    [Fact]
    public async Task CheckAsync_KeyRevoked_MarksNeedsReconnectAndKeepsGroup()
    {
        SaveGroup();
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/bookings", HttpStatusCode.Unauthorized, "{}");

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.NeedsReconnect, _hub.StateOf("calcom"));
        using var conn = _db.Database.Open();
        Assert.Single(ShareGroupStore.GetAll(conn, _time.GetUtcNow()));
    }

    [Fact]
    public async Task CheckAsync_Offline_DoesNotThrowAndKeepsGroup()
    {
        SaveGroup();
        _cal.Throw(new HttpRequestException("offline"));

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        using var conn = _db.Database.Open();
        Assert.Single(ShareGroupStore.GetAll(conn, _time.GetUtcNow()));
    }

    [Fact]
    public async Task CheckAsync_WhileWaitingOn429_SendsNothing()
    {
        SaveGroup();
        _hub.WaitFor("calcom", TimeSpan.FromMinutes(1));

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_cal.Requests);
    }
}
