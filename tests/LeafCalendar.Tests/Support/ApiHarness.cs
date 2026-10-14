using System.Text.Json;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests.Support;

/// <summary>An API call's error reply.</summary>
public sealed class ApiCallException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiCallException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ApiCallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ApiCallException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception from a reply's error.</summary>
    public ApiCallException(int code, string? dataCode, string message)
        : base(message)
    {
        Code = code;
        DataCode = dataCode;
    }

    /// <summary>The JSON-RPC code.</summary>
    public int Code { get; }

    /// <summary>Leaf's short code, or null.</summary>
    public string? DataCode { get; }
}

/// <summary>What the API asked the app to do.</summary>
public sealed class FakeApiHost : IApiHost
{
    /// <inheritdoc />
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <inheritdoc />
    public LeafSettings Settings { get; set; } = new LeafSettings().Normalize();

    /// <summary>Links opened, in order.</summary>
    public List<Uri> Opened { get; } = [];

    /// <summary>Days shown, in order.</summary>
    public List<DateOnly> Days { get; } = [];

    /// <summary>Events shown, in order.</summary>
    public List<CalendarOccurrence> Events { get; } = [];

    /// <summary>What <see cref="OpenLinkAsync"/> answers.</summary>
    public bool OpenSucceeds { get; set; } = true;

    /// <inheritdoc />
    public Task<bool> OpenLinkAsync(Uri link)
    {
        Opened.Add(link);
        return Task.FromResult(OpenSucceeds);
    }

    /// <inheritdoc />
    public void ShowDay(DateOnly day) => Days.Add(day);

    /// <inheritdoc />
    public void ShowEvent(CalendarOccurrence occurrence) => Events.Add(occurrence);

    /// <summary>How many times saved times changed.</summary>
    public int SharesChangedCount { get; private set; }

    /// <inheritdoc />
    public void SharesChanged() => SharesChangedCount++;
}

/// <summary>A dispatcher over a seeded test database (the calendar-list.json calendars, New York time, Oct 1 2026 8 AM).</summary>
public sealed class ApiHarness : IDisposable
{
    /// <summary>The primary calendar.</summary>
    public const string Primary = "leaf.tester@gmail.com";

    /// <summary>The writable Family calendar.</summary>
    public const string Family = "family123@group.calendar.google.com";

    private readonly TestDatabase _db = new();
    private readonly TempFolder _logs = new();
    private int _nextId;

    /// <summary>Creates the harness.</summary>
    public ApiHarness()
    {
        using (var conn = _db.Database.Open())
        {
            AccountStore.Upsert(conn, TestDatabase.SampleAccount);
            CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        }

        var log = new AppLog(_logs.Path, Time) { Detailed = true };
        LogPath = log.FilePath;
        Editor = new EventEditor(_db.Database, Time) { LocalZoneId = "America/New_York" };
        Scheduling = new SchedulingHub(new HttpClient(Http), Tokens, log, Time);
        Dispatcher = new ApiDispatcher(new ApiContext(_db.Database, Editor, Scheduling, Host, Time, "0.1.400", "default"), log);
    }

    /// <summary>The fixtures' account.</summary>
    public static string Account => TestDatabase.SampleAccount.Id;

    /// <summary>The clock.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The fake app.</summary>
    public FakeApiHost Host { get; } = new();

    /// <summary>Cal.com's fake server.</summary>
    public FakeHttpHandler Http { get; } = new();

    /// <summary>Secrets (Cal.com's sign-in goes here).</summary>
    public InMemoryTokenStore Tokens { get; } = new();

    /// <summary>The editor the API writes through.</summary>
    public EventEditor Editor { get; }

    /// <summary>The scheduling providers.</summary>
    public SchedulingHub Scheduling { get; }

    /// <summary>The dispatcher under test.</summary>
    public ApiDispatcher Dispatcher { get; }

    /// <summary>The log file (detailed logging is on, so breadcrumbs are written too).</summary>
    public string LogPath { get; }

    /// <summary>The database.</summary>
    public LeafDatabase Database => _db.Database;

    /// <summary>Stores a Google event on a calendar of the fixtures' account.</summary>
    public void Store(string calendar, string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, calendar, json);
    }

    /// <summary>The outbox's pending writes.</summary>
    public IReadOnlyList<OutboxEntry> Outbox()
    {
        using var conn = _db.Database.Open();
        return OutboxStore.Pending(conn, Account);
    }

    /// <summary>Sends one request and returns its result.</summary>
    /// <exception cref="ApiCallException">The reply is an error.</exception>
    public async Task<JsonElement> CallAsync(string method, string paramsJson = "{}")
    {
        var id = ++_nextId;
        var line = $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""";
        var reply = await Dispatcher.HandleAsync(line, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("No reply.");
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        Assert.Equal(id, root.GetProperty("id").GetInt32());
        if (root.TryGetProperty("error", out var error))
        {
            var dataCode = error.TryGetProperty("data", out var data) ? data.GetProperty("code").GetString() : null;
            throw new ApiCallException(error.GetProperty("code").GetInt32(), dataCode, error.GetProperty("message").GetString()!);
        }

        return root.GetProperty("result").Clone();
    }

    /// <summary>Sends one request and returns its error.</summary>
    public async Task<ApiCallException> FailAsync(string method, string paramsJson = "{}") =>
        await Assert.ThrowsAsync<ApiCallException>(() => CallAsync(method, paramsJson));

    /// <inheritdoc />
    public void Dispose()
    {
        Http.Dispose();
        _db.Dispose();
        _logs.Dispose();
    }
}
