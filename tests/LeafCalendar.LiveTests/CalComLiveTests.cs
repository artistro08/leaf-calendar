using System.Diagnostics;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

/// <summary>
/// Live checks against a real Cal.com account. Read-only: they never book, because a booking sends real emails.
/// </summary>
public class CalComLiveTests
{
    private const string SkipMessage = "Cal.com live tests not set up. Run SignIn_Interactive_StoresTestClient once (see tests/LeafCalendar.LiveTests/README.md).";

    // One-time interactive setup. Set the user environment variables (see the README), then run:
    //   $env:LEAF_LIVE_CALCOM_SIGNIN = '1'
    //   dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --filter-class "*CalComLiveTests"
    // and approve the sign-in in the browser that opens.
    [Fact]
    public async Task SignIn_Interactive_StoresTestClient()
    {
        if (Environment.GetEnvironmentVariable("LEAF_LIVE_CALCOM_SIGNIN") != "1")
        {
            Assert.Skip("Set LEAF_LIVE_CALCOM_SIGNIN=1 (and the client ID and secret variables) to sign in to Cal.com once.");
        }

        // Name any missing variable (never its value)
        foreach (var name in new[] { "LEAF_LIVE_CALCOM_CLIENT_ID", "LEAF_LIVE_CALCOM_CLIENT_SECRET" })
        {
            Assert.True(ReadVariable(name).Length > 0, $"{name} is not set (process or user environment variable).");
        }

        var client = new ProviderClient(ReadVariable("LEAF_LIVE_CALCOM_CLIENT_ID"), ReadVariable("LEAF_LIVE_CALCOM_CLIENT_SECRET"));
        var ct = TestContext.Current.CancellationToken;

        var hub = NewHub();

        hub.SaveClient(CalComProvider.ProviderId, client);
        var links = await hub.SignInAsync(CalComProvider.ProviderId, uri =>
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }, ct);

        Assert.Equal(ProviderState.SignedIn, hub.StateOf(CalComProvider.ProviderId));
        Assert.NotEmpty(links);
    }

    [Fact]
    public async Task Links_ListsAtLeastOne()
    {
        var (_, provider) = Load();
        var links = await provider.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(links);
    }

    [Fact]
    public async Task OpenTimes_BuildTimeLinkUnderTheLinksBookingUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, provider) = Load();
        var link = (await provider.GetLinksAsync(ct))[0];

        var now = DateTimeOffset.UtcNow;
        var times = await provider.GetOpenTimesAsync(link.Id, now, now.AddDays(7), ct);
        if (times.Count == 0)
        {
            Assert.Skip("The first Cal.com link has no open times in the next 7 days.");
            return;
        }

        var timeLink = provider.TimeLink(link, times[0].Start, TimeZoneInfo.Utc, []);

        Assert.StartsWith(link.BookingUrl.AbsoluteUri, timeLink.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bookings_ReadsTheFirstLinksBookings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, provider) = Load();
        var link = (await provider.GetLinksAsync(ct))[0];

        // GET only: the call succeeding is the check; the bookings are never printed
        var bookings = await provider.GetBookingsAsync(link.Id, DateTimeOffset.UtcNow.AddDays(-30), ct);

        Assert.NotNull(bookings);
    }

    // Reads a variable from the process, then from the user-level (registry) values, since the shell may be older than the variables
    private static string ReadVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromProcess
            ? fromProcess
            : Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? "";

    // The hub over the stable live-tests secrets file; the folder must be named "live-tests" because its name seeds the DPAPI entropy. Only the log goes to temp.
    private static SchedulingHub NewHub()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafCalendar.LiveTests", "profiles", LiveAccount.Profile);
        var logs = Path.Combine(Path.GetTempPath(), "leaf-live", Guid.NewGuid().ToString("N"), "Logs");
        return new SchedulingHub(LiveAccount.Http, new ProtectedFileTokenStore(profile), new AppLog(logs, TimeProvider.System), TimeProvider.System);
    }

    // Loads the signed-in provider from the live-tests profile, or skips
    private static (SchedulingHub Hub, CalComProvider Provider) Load()
    {
        var hub = NewHub();
        hub.Load();

        if (hub.Get(CalComProvider.ProviderId) is not CalComProvider provider)
        {
            Assert.Skip(SkipMessage);
            throw new InvalidOperationException(SkipMessage);
        }

        return (hub, provider);
    }
}
