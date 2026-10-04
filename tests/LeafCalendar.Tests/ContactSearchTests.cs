using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.People;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class ContactSearchTests : IDisposable
{
    const string Account       = "109876543210";
    const string TokenUrl      = "https://oauth2.googleapis.com/token";
    const string ContactsUrl   = "https://people.googleapis.com/v1/people:searchContacts";
    const string OthersUrl     = "https://people.googleapis.com/v1/otherContacts:search";
    const string DirectoryUrl  = "https://people.googleapis.com/v1/people:searchDirectoryPeople";
    const string AllScopes     = "openid https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/contacts.readonly https://www.googleapis.com/auth/contacts.other.readonly";
    const string WithDirectory = AllScopes + " https://www.googleapis.com/auth/directory.readonly";

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

    ContactSearch CreateSearch(HttpMessageHandler? handler = null) => new(new HttpClient(handler ?? _google, disposeHandler: false), _tokens, _log);

    // Holds matching requests until the release task completes (or the request is canceled)
    sealed class GateHandler(Func<HttpRequestMessage, bool> hold, Task release) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (hold(request))
            {
                await release.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    static bool IsSearch(HttpRequestMessage r, string url) =>
        r.RequestUri!.AbsoluteUri.StartsWith(url + "?query=a", StringComparison.Ordinal);

    static string Person(string email) => $$$"""{"person":{"emailAddresses":[{"value":"{{{email}}}"}]}}""";

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
                "https://people.googleapis.com/v1/otherContacts:search?query=al%20b%26c&readMask=names,emailAddresses&pageSize=10",
                "https://people.googleapis.com/v1/people:searchContacts?query=al%20b%26c&readMask=names,emailAddresses&pageSize=10",
            ],
            Searches().Select(r => r.Uri.AbsoluteUri).Order(StringComparer.Ordinal));
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
    public async Task Search_TokenRefreshUnreachable_IsAFailedSearchWithoutThrowing()
    {
        _google.Throw(new HttpRequestException("offline"));

        var results = await CreateSearch().SearchAsync(Account, "alice", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Empty(results.Contacts);
        Assert.Contains($"contacts.search.failed account={Account} status=0", File.ReadAllText(_log.FilePath), StringComparison.Ordinal);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_OneSourceFails_KeepsTheOther(bool contactsFail)
    {
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, contactsFail ? ContactsUrl : OthersUrl, HttpStatusCode.InternalServerError, "{}");
        _google.On(HttpMethod.Get, contactsFail ? OthersUrl : ContactsUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("kept@example.com")}}]}""");

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Equal([new Contact("", "kept@example.com")], results.Contacts);
        Assert.Contains("status=500", File.ReadAllText(_log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_LoneSurrogateInResponse_ReturnsOtherSourceAndLogsStatusOnly()
    {
        RouteToken(AllScopes);
        var lone = @"\" + "uD800";
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$$"""{"results":[{"person":{"names":[{"displayName":"x{{{lone}}}"}],"emailAddresses":[{"value":"lone@example.com"}]}}]}""");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("ok@example.com")}}]}""");

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal([new Contact("", "ok@example.com")], results.Contacts);
        var log = File.ReadAllText(_log.FilePath);
        Assert.Contains($"contacts.search.failed account={Account} status=200", log, StringComparison.Ordinal);
        Assert.DoesNotContain("lone", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_BogusCharset_ReturnsEmptyAndLogsStatus()
    {
        RouteToken(AllScopes);
        _google.On(r => r.Method == HttpMethod.Get, _ =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"results":[{{Person("x@example.com")}}]}""");
            response.Content.Headers.ContentType!.CharSet = "bogus";
            return response;
        });

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Empty(results.Contacts);
        Assert.Contains("status=200", File.ReadAllText(_log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_CanceledFirstSearch_StillWarmsUpBothSources()
    {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        RouteToken(AllScopes);
        _google.On(r => r.Method == HttpMethod.Get, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        // The contacts warmup is still in flight when the user types the next letter
        var typed = Task.Delay(Timeout.Infinite, typing.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        using var gate = new GateHandler(r => r.RequestUri!.AbsoluteUri.StartsWith(ContactsUrl + "?query=&", StringComparison.Ordinal), typed) { InnerHandler = _google };
        var search = CreateSearch(gate);

        var first = search.SearchAsync(Account, "a", typing.Token);
        typing.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await search.SearchAsync(Account, "ab", TestContext.Current.CancellationToken);

        var warmups = _google.Requests.Where(r => r.Method == HttpMethod.Get && r.Query("query") == "").Select(r => r.Uri.AbsolutePath);
        Assert.Equal(["/v1/otherContacts:search", "/v1/people:searchContacts"], warmups.Order(StringComparer.Ordinal));
        Assert.Contains(Searches(), r => r.Query("query") == "ab");
    }

    [Fact]
    public async Task Search_CallerCancels_ThrowsOperationCanceled()
    {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        RouteToken(AllScopes);
        _google.On(r => r.Method == HttpMethod.Get, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        using var gate = new GateHandler(r => IsSearch(r, ContactsUrl), Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken)) { InnerHandler = _google };

        var pending = CreateSearch(gate).SearchAsync(Account, "a", typing.Token);
        typing.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(_log.FilePath));
    }

    [Fact]
    public async Task Search_SlowSource_TimesOutAndKeepsTheOther()
    {
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("fast@example.com")}}]}""");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");
        using var gate = new GateHandler(r => IsSearch(r, OthersUrl), Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken)) { InnerHandler = _google };

        var results = await new ContactSearch(new HttpClient(gate, disposeHandler: false), _tokens, _log) { Timeout = TimeSpan.FromMilliseconds(200) }
            .SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal([new Contact("", "fast@example.com")], results.Contacts);
        Assert.Contains($"account={Account} status=0", File.ReadAllText(_log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_QueriesBothSourcesAtOnce()
    {
        var othersSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("c@example.com")}}]}""");
        _google.On(r => r.Uri.AbsoluteUri.StartsWith(OthersUrl, StringComparison.Ordinal), r =>
        {
            if (r.Query("query") == "a")
            {
                othersSeen.TrySetResult();
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"results":[{{Person("o@example.com")}}]}""");
        });

        // Contacts waits for the other-contacts request, so a one-after-the-other search times out on contacts
        using var gate = new GateHandler(r => IsSearch(r, ContactsUrl), othersSeen.Task) { InnerHandler = _google };
        var results = await new ContactSearch(new HttpClient(gate, disposeHandler: false), _tokens, _log) { Timeout = TimeSpan.FromSeconds(2) }
            .SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal([new Contact("", "c@example.com"), new Contact("", "o@example.com")], results.Contacts);
    }

    [Fact]
    public async Task Search_EmailsWithHiddenOrSeparatorCharactersOrTooLong_Dropped()
    {
        RouteToken(AllScopes);
        var lineSeparator = char.ConvertFromUtf32(0x2028);
        var noBreakSpace  = char.ConvertFromUtf32(0x00A0);
        var tooLong       = new string('a', 250) + "@example.com";
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$"""
            {"results":[
              {{Person("ls" + lineSeparator + "x@example.com")}},
              {{Person("nb" + noBreakSpace + "x@example.com")}},
              {{Person("tab\\tx@example.com")}},
              {{Person(tooLong)}},
              {{Person("good@example.com")}}
            ]}
            """);
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal([new Contact("", "good@example.com")], results.Contacts);
    }

    [Fact]
    public async Task Search_PersonWithManyAddresses_CappedPerPerson()
    {
        RouteToken(AllScopes);
        var addresses = string.Join(',', Enumerable.Range(1, 30).Select(i => $$"""{"value":"m{{i}}@example.com"}"""));
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$$"""
            {"results":[{"person":{"emailAddresses":[{{{addresses}}}]}},{{{Person("second@example.com")}}}]}
            """);
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["m1@example.com", "m2@example.com", "m3@example.com", "m4@example.com", "m5@example.com", "second@example.com"],
            results.Contacts.Select(c => c.Email));
    }

    // =========================================================================
    // WORKSPACE DIRECTORY
    // =========================================================================

    [Fact]
    public async Task Search_WithDirectoryScope_ListsDirectoryBetweenContactsAndOthers()
    {
        RouteToken(WithDirectory);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, """{"results":[{"person":{"names":[{"displayName":"Alice"}],"emailAddresses":[{"value":"alice@example.com"}]}}]}""");
        _google.On(HttpMethod.Get, DirectoryUrl, HttpStatusCode.OK, """{"people":[{"names":[{"displayName":"Dana Director"}],"emailAddresses":[{"value":"dana@example.com"}]}]}""");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("bob@example.com")}}]}""");

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Equal(["alice@example.com", "dana@example.com", "bob@example.com"], results.Contacts.Select(c => c.Email));
        Assert.Equal("Dana Director", results.Contacts[1].Name);
        var directory = Assert.Single(_google.Requests, r => r.Uri.AbsolutePath.Contains("searchDirectoryPeople", StringComparison.Ordinal));
        Assert.Equal(DirectoryUrl + "?query=a&readMask=names,emailAddresses&sources=DIRECTORY_SOURCE_TYPE_DOMAIN_PROFILE&pageSize=10", directory.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Search_DirectoryRefused_KeepsTheOtherSources()
    {
        RouteToken(WithDirectory);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("alice@example.com")}}]}""");
        _google.On(HttpMethod.Get, DirectoryUrl, HttpStatusCode.BadRequest, """{"error":{"status":"FAILED_PRECONDITION","message":"Must be a G Suite domain user."}}""");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("bob@example.com")}}]}""");

        var results = await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.Equal(["alice@example.com", "bob@example.com"], results.Contacts.Select(c => c.Email));
        Assert.Contains($"contacts.search.failed account={Account} status=400", File.ReadAllText(_log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_WithoutDirectoryScope_NeverAsksTheDirectory()
    {
        RouteToken(AllScopes);
        _google.On(r => r.Method == HttpMethod.Get, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        await CreateSearch().SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_google.Requests, r => r.Uri.AbsoluteUri.Contains("searchDirectoryPeople", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_WorkspaceWithoutDirectoryScope_OffersConsentAndStillLists()
    {
        RouteToken(AllScopes);
        _google.On(HttpMethod.Get, ContactsUrl, HttpStatusCode.OK, $$"""{"results":[{{Person("alice@example.com")}}]}""");
        _google.On(HttpMethod.Get, OthersUrl, HttpStatusCode.OK, "{}");

        var search    = CreateSearch();
        var workspace = await search.SearchAsync(Account, "a", workspace: true, TestContext.Current.CancellationToken);
        var personal  = await search.SearchAsync(Account, "a", TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.NeedsConsent, workspace.Access);
        Assert.Equal(["alice@example.com"], workspace.Contacts.Select(c => c.Email));
        Assert.Equal(ContactAccess.Allowed, personal.Access);
    }

    [Fact]
    public async Task Search_WorkspaceWithDirectoryScope_IsAllowed()
    {
        RouteToken(WithDirectory);
        _google.On(r => r.Method == HttpMethod.Get, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var results = await CreateSearch().SearchAsync(Account, "a", workspace: true, TestContext.Current.CancellationToken);

        Assert.Equal(ContactAccess.Allowed, results.Access);
    }
}
