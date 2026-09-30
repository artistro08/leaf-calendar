using LeafCalendar.Core.Auth;
using LeafCalendar.Core.People;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LivePeopleTests
{
    [Fact]
    public async Task Search_RunsAgainstGoogleWithoutError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip("Live account not set up. Run LiveSignInTests once (see its comment).");
            return;
        }

        // Skip Accounts Signed In Before Contacts Were Granted
        if (!await live.Services.AccessTokens.HasScopeAsync(live.AccountId, GoogleOAuthClient.ContactsScope, ct))
        {
            Assert.Skip("The live account hasn't granted the contacts scope. Run LiveSignInTests again.");
            return;
        }

        // Results are never printed: they're real people's names and addresses
        var results = await live.Services.Contacts.SearchAsync(live.AccountId, "a", ct);

        Assert.Equal(ContactAccess.Allowed, results.Access);
        Assert.InRange(results.Contacts.Count, 0, ContactSearch.MaxResults);
    }
}
