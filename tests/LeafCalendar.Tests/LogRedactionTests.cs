using System.Net;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class LogRedactionTests : IDisposable
{
    private static readonly HttpClient Browser = new();

    private readonly SyncHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task FullFlow_SignInSyncFailDisconnect_LogHasNoSecretsOrEventContent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Sign-in, sync with one failing calendar, and disconnect, all logged to one file
        _h.Google.On(HttpMethod.Get, SyncHarness.FamilyEventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.Google.On(HttpMethod.Post, "https://oauth2.googleapis.com/token", HttpStatusCode.OK, Fixture.Read("token-response.json"), once: true);
        _h.Google.On(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo", HttpStatusCode.OK, Fixture.Read("userinfo.json"));
        _h.Google.On(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", HttpStatusCode.ServiceUnavailable, "{}");
        _h.RouteStandardGoogle();

        await using var services = new GoogleServices(new HttpClient(_h.Google), new("id.apps.googleusercontent.com", "GOCSPX-test-secret"), _h.Tokens, _h.Db.Database, _h.Log, _h.Time);
        var account = await services.CreateSignIn(url =>
        {
            var q = QueryString.Parse(url.Query);
            _ = Task.Run(() => Browser.GetAsync(new Uri(new Uri(q["redirect_uri"]), $"?code=4%2Fauth-code&state={Uri.EscapeDataString(q["state"])}")));
            return Task.CompletedTask;
        }).RunAsync(null, ct);
        await services.Sync.SyncAccountAsync(account.Id, ct);
        await services.Sync.SyncAccountAsync(account.Id, ct);
        await services.DisconnectAsync(account.Id, ct);

        var log = await File.ReadAllTextAsync(_h.LogPath, ct);

        // Every secret and every piece of event content from the fixtures
        string[] forbidden =
        [
            "ya29.", "1//", "4/auth-code", "eyJ", "GOCSPX-",
            "leaf.tester@gmail.com", "family123@group.calendar.google.com",
            "Dentist", "Company holiday", "Team standup", "Lunch with Sam", "Leaf Tester",
        ];
        Assert.All(forbidden, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
        Assert.Contains("signin.completed", log, StringComparison.Ordinal);
        Assert.Contains("sync.calendar.failed", log, StringComparison.Ordinal);
        Assert.Contains("account.revoke.failed", log, StringComparison.Ordinal);
    }
}
