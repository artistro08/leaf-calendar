using System.Diagnostics;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveSignInTests
{
    // One-time interactive setup. Run with:
    //   $env:LEAF_LIVE_SIGNIN = '1'; $env:LEAF_LIVE_CLIENT_ID = '...'; $env:LEAF_LIVE_CLIENT_SECRET = '...'
    //   dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --filter-class "*LiveSignInTests"
    // then sign in with the THROWAWAY Google account in the browser that opens.
    [Fact]
    public async Task SignIn_Interactive_StoresThrowawayAccount()
    {
        if (Environment.GetEnvironmentVariable("LEAF_LIVE_SIGNIN") != "1")
        {
            Assert.Skip("Set LEAF_LIVE_SIGNIN=1, LEAF_LIVE_CLIENT_ID, and LEAF_LIVE_CLIENT_SECRET to sign in the throwaway account once.");
        }

        var credentials = new OAuthClientCredentials(
            Environment.GetEnvironmentVariable("LEAF_LIVE_CLIENT_ID") ?? "",
            Environment.GetEnvironmentVariable("LEAF_LIVE_CLIENT_SECRET") ?? "");
        Assert.Null(OAuthClientCredentials.Validate(credentials.ClientId, credentials.ClientSecret));

        LiveAccount.Tokens.DeleteAll();
        LiveAccount.Tokens.SetClientCredentials(credentials);

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "leaf-live", Guid.NewGuid().ToString("N"))).FullName;
        var database = new LeafDatabase(Path.Combine(folder, "leaf.db"));
        database.Migrate();
        await using var services = new GoogleServices(LiveAccount.Http, credentials, LiveAccount.Tokens, database, new AppLog(folder, TimeProvider.System), TimeProvider.System);

        var account = await services.CreateSignIn(uri =>
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal([account.Id], LiveAccount.Tokens.GetAccountIds());
    }
}
