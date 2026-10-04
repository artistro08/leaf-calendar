using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using Microsoft.Data.Sqlite;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]

namespace LeafCalendar.LiveTests.Support;

/// <summary>
/// The throwaway Google account used by live tests.
/// </summary>
/// <remarks>
/// Secrets live in Credential Locker under profile <c>live-tests</c>, and are set once by
/// <c>LiveSignInTests</c>. Each instance has its own temp database and log.
/// </remarks>
public sealed class LiveAccount : IAsyncDisposable
{
    /// <summary>Credential Locker profile for live tests.</summary>
    public const string Profile = "live-tests";

    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "leaf-live", Guid.NewGuid().ToString("N"))).FullName;

    private LiveAccount(OAuthClientCredentials credentials, string accountId)
    {
        AccountId = accountId;
        Database = new LeafDatabase(Path.Combine(_folder, "leaf.db"));
        Database.Migrate();
        Log = new AppLog(Path.Combine(_folder, "Logs"), TimeProvider.System);
        Services = new GoogleServices(Http, credentials, Tokens, Database, Log, TimeProvider.System);

        using var conn = Database.Open();
        AccountStore.Upsert(conn, new Account(accountId, "live-test", null, null, AccountStatus.Ok));
    }

    /// <summary>Shared HTTP client.</summary>
    public static HttpClient Http { get; } = new();

    /// <summary>Live secrets.</summary>
    public static CredentialLockerTokenStore Tokens { get; } = new(Profile);

    /// <summary>Signed-in account ID.</summary>
    public string AccountId { get; }

    /// <summary>Temp database.</summary>
    public LeafDatabase Database { get; }

    /// <summary>Temp log.</summary>
    public AppLog Log { get; }

    /// <summary>Wired Google services.</summary>
    public GoogleServices Services { get; }

    /// <summary>Loads the account, or returns null when live tests aren't set up.</summary>
    public static LiveAccount? TryLoad() =>
        Tokens.GetClientCredentials() is { } credentials && Tokens.GetAccountIds() is [var accountId, ..]
            ? new LiveAccount(credentials, accountId)
            : null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleaner.
        }
    }
}
