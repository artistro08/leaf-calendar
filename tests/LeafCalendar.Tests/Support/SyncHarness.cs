using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Sync;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests.Support;

/// <summary>A sync engine wired to fake Google, a temp database, and a temp log.</summary>
public sealed class SyncHarness : IDisposable
{
    /// <summary>Fixture account ID (userinfo.json <c>sub</c>).</summary>
    public const string AccountId = "109876543210";

    /// <summary>Token endpoint.</summary>
    public const string TokenUrl = "https://oauth2.googleapis.com/token";

    /// <summary>Calendar list endpoint.</summary>
    public const string ListUrl = "https://www.googleapis.com/calendar/v3/users/me/calendarList";

    /// <summary>Primary calendar events endpoint.</summary>
    public const string PrimaryEventsUrl = "https://www.googleapis.com/calendar/v3/calendars/leaf.tester%40gmail.com/events";

    /// <summary>Family calendar events endpoint.</summary>
    public const string FamilyEventsUrl = "https://www.googleapis.com/calendar/v3/calendars/family123%40group.calendar.google.com/events";

    readonly TempFolder _logs = new();
    readonly List<IDisposable> _disposables = [];

    /// <summary>Creates the harness with one signed-in account.</summary>
    public SyncHarness()
    {
        Log = new AppLog(_logs.Path, Time);
        Tokens.SetRefreshToken(AccountId, "1//test-refresh-token");
        using (var conn = Db.Database.Open())
        {
            AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        }

        Engine = NewEngine();
    }

    /// <summary>Fake Google.</summary>
    public FakeHttpHandler Google { get; } = new();

    /// <summary>Fake clock.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Secret store.</summary>
    public InMemoryTokenStore Tokens { get; } = new();

    /// <summary>Database.</summary>
    public TestDatabase Db { get; } = new();

    /// <summary>Log.</summary>
    public AppLog Log { get; }

    /// <summary>Log file path.</summary>
    public string LogPath => Log.FilePath;

    /// <summary>Engine under test.</summary>
    public SyncEngine Engine { get; private set; }

    /// <summary>Builds a new engine with an empty access-token cache.</summary>
    public SyncEngine NewEngine()
    {
        var http  = new HttpClient(Google);
        var oauth = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "GOCSPX-test"), Time);
        var provider = new AccessTokenProvider(oauth, Tokens, Time);
        Engine = new SyncEngine(new GoogleCalendarClient(http, provider), Db.Database, Log, Time);
        _disposables.Add(provider);
        _disposables.Add(Engine);
        return Engine;
    }

    /// <summary>
    /// Token refresh OK. Calendar list has primary and family. The primary full sync is two pages
    /// (sync-token-1), the primary incremental from sync-token-1 gives sync-token-2, and family is empty.
    /// </summary>
    public void RouteStandardGoogle()
    {
        Google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        Google.On(HttpMethod.Get, ListUrl, HttpStatusCode.OK, Fixture.Read("calendar-list.json"));
        RouteEvents(PrimaryEventsUrl, syncToken: null, pageToken: null, "events-page1.json");
        RouteEvents(PrimaryEventsUrl, syncToken: null, pageToken: "page-2", "events-page2.json");
        RouteEvents(PrimaryEventsUrl, syncToken: "sync-token-1", pageToken: null, "events-incremental.json");
        Google.On(HttpMethod.Get, FamilyEventsUrl, HttpStatusCode.OK, Fixture.Read("events-empty.json"));
    }

    /// <summary>Routes an events URL for an exact sync/page token pair.</summary>
    public void RouteEvents(string url, string? syncToken, string? pageToken, string fixture, HttpStatusCode status = HttpStatusCode.OK, bool once = false) =>
        Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(url, StringComparison.Ordinal) && r.Query("syncToken") == syncToken && r.Query("pageToken") == pageToken,
            _ => FakeHttpHandler.Json(status, Fixture.Read(fixture)),
            once);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Db.Dispose();
        _logs.Dispose();
    }
}
