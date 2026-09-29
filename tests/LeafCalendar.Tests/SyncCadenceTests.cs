using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SyncCadenceTests : IDisposable
{
    readonly SyncHarness _h = new();

    public void Dispose() => _h.Dispose();

    int CalendarListCalls() => _h.Google.Requests.Count(r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.ListUrl, StringComparison.Ordinal));

    [Fact]
    public async Task SyncAllAsync_WithinFifteenMinutes_SkipsCalendarList()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(ct);
        _h.Time.Advance(TimeSpan.FromMinutes(14));
        await _h.Engine.SyncAllAsync(ct);

        Assert.Equal(1, CalendarListCalls());
    }

    [Fact]
    public async Task SyncAllAsync_AfterFifteenMinutes_RefreshesCalendarList()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(ct);
        _h.Time.Advance(SyncEngine.CalendarListInterval);
        await _h.Engine.SyncAllAsync(ct);

        Assert.Equal(2, CalendarListCalls());
    }

    [Fact]
    public async Task SyncAllAsync_Forced_RefreshesCalendarList()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(ct);
        await _h.Engine.SyncAllAsync(refreshCalendarLists: true, ct);

        Assert.Equal(2, CalendarListCalls());
    }

    [Fact]
    public async Task DataChanged_RaisedOnlyWhenSomethingWasWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-2",
            _ => FakeHttpHandler.Json(System.Net.HttpStatusCode.OK, """{"items":[],"nextSyncToken":"sync-token-2"}"""));
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.FamilyEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-empty",
            _ => FakeHttpHandler.Json(System.Net.HttpStatusCode.OK, """{"items":[],"nextSyncToken":"sync-token-empty"}"""));
        _h.RouteStandardGoogle();
        var raised = 0;
        _h.Engine.DataChanged += (_, _) => raised++;

        await _h.Engine.SyncAllAsync(ct);   // full sync: list + events written
        await _h.Engine.SyncAllAsync(ct);   // incremental: 3 changes written
        await _h.Engine.SyncAllAsync(ct);   // nothing new, list not due

        Assert.Equal(2, raised);
    }
}
