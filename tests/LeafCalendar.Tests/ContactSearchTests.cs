using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.People;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class ContactSearchTests : IDisposable
{
    const string Account     = "109876543210";
    const string TokenUrl    = "https://oauth2.googleapis.com/token";
    const string ContactsUrl = "https://people.googleapis.com/v1/people:searchContacts";
    const string OthersUrl   = "https://people.googleapis.com/v1/otherContacts:search";
    const string AllScopes   = "openid https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/contacts.readonly https://www.googleapis.com/auth/contacts.other.readonly";

    // Right-to-left override, a format character that can disguise text
    static readonly string Rlo = char.ConvertFromUtf32(0x202E);

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly TempFolder _logs = new();
    readonly AccessTokenProvider _tokens;
    readonly AppLog _log;

    public ContactSearchTests()
    {
        var store = new InMemoryTokenStore();
        store.SetRefreshToken(Account, "1//test-refresh-token");
        _tokens = new AccessTokenProvider(new GoogleOAuthClient(new HttpClient(_google), new("id.apps.googleusercontent.com", "secret"), _time), store, _time);
        _log    = new AppLog(_logs.Path, _time);
    }

    public void Dispose()
    {
        _tokens.Dispose();
        _google.Dispose();
        _logs.Dispose();
    }

    ContactSearch CreateSearch() => new(new HttpClient(_google), _tokens, _log);

    void RouteToken(string scope) =>
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, $$"""{"access_token":"ya29.people","expires_in":3599,"scope":"{{scope}}","token_type":"Bearer"}""");

    // Only real searches (the one-time warmup sends an empty query)
    IEnumerable<RecordedRequest> Searches() =>
        _google.Requests.Where(r => r.Method == HttpMethod.Get && !string.IsNullOrEmpty(r.Query("query")));

    [Fact]
    public async Task Search_MergesContactsAndOtherContacts_ContactsFirst_DistinctByEmail()
    {
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, """
            {"results":[{"person":{"names":[{"displayName":"Alice"}],"emailAddresses":[{"value":"alice@example.com"}]}}]}
            """);
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, """
            {"results":[
              {"person":{"emailAddresses":[{"value":"alice@EXAMPLE.com"}]}},
              {"person":{"names":[{"displayName":"Bob"}],"emailAddresses":[{"value":"bob@example.com"}]}}
            ]}
            """);

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Equal([new Contact("Alice", "alice@example.com"), new Contact("Bob", "bob@example.com")], results.Contacts);
    }

    [Fact]
    public async Task Search_DropsInvalidEmailsAndStripsControlCharacters()
    {
        RouteToken(AllScopes);
        var longName = new string('L', 5_000);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$$"""
            {"results":[
              {"person":{"names":[{"displayName":"{{{Rlo}}}Evil<b>x</b>"}],"emailAddresses":[{"value":"evil@example.com"}]}},
              {"person":{"names":[{"displayName":"No email"}]}},
              {"person":{"names":[{"displayName":"Bad"}],"emailAddresses":[{"value":"not an address"}]}},
              {"person":{"names":[{"displayName":"{{{longName}}}"}],"emailAddresses":[{"value":"long@example.com"}]}}
            ]}
            """);
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");

        var results = await CreateSearch().SearchAsync(Account, "e", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Contacts.Count);
        Assert.Equal(new Contact("Evil<b>x</b>", "evil@example.com"), results.Contacts[0]);
        Assert.Equal("long@example.com", results.Contacts[1].Email);
        Assert.Equal(100, results.Contacts[1].Name.Length);
    }

    [Fact]
    public async Task Search_SendsReadMaskAndQuery_AndCapsResults()
    {
        RouteToken(AllScopes);
        var many = string.Join(',', Enumerable.Range(1, 10).Select(i => $$$"""{"person":{"emailAddresses":[{"value":"p{{{i}}}@example.com"}]}}"""));
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$"""{"results":[{{many}}]}""");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, """{"results":[{"person":{"emailAddresses":[{"value":"other@example.com"}]}}]}""");

        var results = await CreateSearch().SearchAsync(Account, "al b&c", TestContext.Current.CancellationToken);

        Assert.Equal(ContactSearch.MaxResults, results.Contacts.Count);
        Assert.Equal(
            [
                "https://people.googleapis.com/v1/people:searchContacts?query=al%20b%26c&readMask=names,emailAddresses&pageSize=10",
                "https://people.googleapis.com/v1/otherContacts:search?query=al%20b%26c&readMask=names,emailAddresses&pageSize=10",
            ],
            Searches().Select(r => r.Uri.AbsoluteUri));
        Assert.All(Searches(), r => Assert.Equal("ya29.people", r.BearerToken));
    }

    [Fact]
    public async Task Search_FirstSearch_WarmsUpEachSourceOnceWithoutLogging()
    {
        var ct = TestContext.Current.CancellationToken;
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, "{}");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");
        var search = CreateSearch();

        await search.SearchAsync(Account, "a", ct);
        await search.SearchAsync(Account, "b", ct);

        var warmups = _google.Requests.Where(r => r.Method == HttpMethod.Get && r.Query("query") == "").Select(r => r.Uri.AbsolutePath);
        Assert.Equal(["/v1/people:searchContacts", "/v1/otherContacts:search"], warmups);
        Assert.Equal(4, Searches().Count());
        Assert.False(File.Exists(_log.FilePath));
    }

    [Fact]
    public async Task Search_GrantWithoutContactsScopes_NeedsConsentAndSendsNothing()
    {
        RouteToken("openid https://www.googleapis.com/auth/calendar");

        var results = await CreateSearch().SearchAsync(Account, "alice", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.NeedsConsent, results.Access);
        Assert.Empty(results.Contacts);
        Assert.DoesNotContain(_google.Requests, r => r.Uri.Host == "people.googleapis.com");
    }

    [Fact]
    public async Task Search_AccountNeedsSignIn_NeedsConsentWithoutThrowing()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json"));

        var results = await CreateSearch().SearchAsync(Account, "alice", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.NeedsConsent, results.Access);
        Assert.Empty(results.Contacts);
    }

    [Fact]
    public async Task Search_PeopleApiDisabled_ReportsApiDisabledNotConsent()
    {
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.Forbidden, """
            {"error":{"code":403,"status":"PERMISSION_DENIED","details":[{"reason":"SERVICE_DISABLED"}],"errors":[{"reason":"accessNotConfigured"}]}}
            """);

        var results = await CreateSearch().SearchAsync(Account, "alice", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.ApiDisabled, results.Access);
        Assert.Empty(results.Contacts);
    }

    [Fact]
    public async Task Search_ScopeInsufficientOnlyInDetails_ReportsNeedsConsent()
    {
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.Forbidden, """
            {"error":{"code":403,"status":"PERMISSION_DENIED","details":[{"reason":"ACCESS_TOKEN_SCOPE_INSUFFICIENT"}]}}
            """);

        var results = await CreateSearch().SearchAsync(Account, "alice", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.NeedsConsent, results.Access);
    }

    [Fact]
    public async Task Search_Error_ReturnsEmptyAndLogsNoQueryOrContactData()
    {
        RouteToken(AllScopes);
        _google.On(r => r.Uri.AbsoluteUri.StartsWith(ContactsUrl, StringComparison.Ordinal) && r.Query("query") == "alice",
            _ => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":{"code":500,"message":"alice@example.com"}}"""));
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, "{}");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");

        var results = await CreateSearch().SearchAsync(Account, "alice", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Empty(results.Contacts);
        var log = File.ReadAllText(_log.FilePath);
        Assert.Contains("contacts.search.failed", log, StringComparison.Ordinal);
        Assert.Contains($"account={Account} status=500", log, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_BlankOrHugeQuery_SendsNothing()
    {
        var ct     = TestContext.Current.CancellationToken;
        var search = CreateSearch();

        var blank = await search.SearchAsync(Account, "   ", ct);
        var huge  = await search.SearchAsync(Account, new string('a', 101), ct);

        Assert.Empty(blank.Contacts);
        Assert.Empty(huge.Contacts);
        Assert.Equal(ContactAccess.Allowed, blank.Access);
        Assert.Empty(_google.Requests);
    }
}
