using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Creates a profile that's already signed in to the fake Google, so view tests start on the
/// calendar. The app downloads calendars and events from <see cref="FakeGoogleServer"/> on launch.
/// </summary>
public static class SeededProfile
{
    /// <summary>The fixture account (<c>userinfo.json</c>).</summary>
    public const string AccountId = "109876543210";

    /// <summary>Creates the profile and returns its name. Clean it up with <see cref="LeafApp.DeleteProfile"/>.</summary>
    public static string Create()
    {
        var profile = LeafApp.NewProfile();

        // Secrets
        var store = new CredentialLockerTokenStore(profile);
        store.SetClientCredentials(new OAuthClientCredentials("123-uitest.apps.googleusercontent.com", "GOCSPX-uitest"));
        store.SetRefreshToken(AccountId, "1//test-refresh-token");

        // Account Row
        var database = new LeafDatabase(Path.Combine(LeafApp.ProfileFolder(profile), "leaf.db"));
        database.Migrate();
        using (var conn = database.Open())
        {
            AccountStore.Upsert(conn, new Account(AccountId, "leaf.tester@gmail.com", "Leaf Tester", null, AccountStatus.Ok));
        }

        SqliteConnection.ClearAllPools();
        return profile;
    }
}
