using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;
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

    /// <summary>The fixture account's email.</summary>
    public const string Email = "leaf.tester@gmail.com";

    /// <summary>Creates the profile (with <paramref name="settings"/> saved, when given) and returns its name. Clean it up with <see cref="LeafApp.DeleteProfile"/>.</summary>
    public static string Create(LeafSettings? settings = null)
    {
        var profile = LeafApp.NewProfile();

        // Secrets
        var store = new ProtectedFileTokenStore(LeafApp.ProfileFolder(profile));
        store.SetClientCredentials(new OAuthClientCredentials("123-uitest.apps.googleusercontent.com", "GOCSPX-uitest"));
        store.SetRefreshToken(AccountId, "1//test-refresh-token");

        // Account Row
        var database = new LeafDatabase(Path.Combine(LeafApp.ProfileFolder(profile), "leaf.db"));
        database.Migrate();
        using (var conn = database.Open())
        {
            AccountStore.Upsert(conn, new Account(AccountId, Email, "Leaf Tester", null, AccountStatus.Ok));
            if (settings is not null)
            {
                SettingsStore.Save(conn, settings);
            }
        }

        SqliteConnection.ClearAllPools();
        return profile;
    }
}
