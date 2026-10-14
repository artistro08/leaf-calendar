using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Api;

/// <summary><c>people.search</c> (local API spec 4): the people the editor's guest box suggests, for an app's guest field.</summary>
internal static class ApiPeople
{
    private const int MaxQuery = 100;

    // People from your events first (your main account's, then the others'), then Google contacts; each address once
    public static async Task<JsonElement> SearchAsync(ApiContext context, PeopleParams p, CancellationToken ct)
    {
        var query = (p.Query ?? "").Trim();
        if (query.Length > MaxQuery)
        {
            throw ApiTime.Invalid("query is at most 100 characters.");
        }

        List<Account> accounts;
        var found = new List<Contact>();
        using (var conn = context.Database.Open())
        {
            var main = context.Host.Settings.MainAccountId;
            accounts = [.. AccountStore.GetAll(conn).OrderBy(a => a.Id == main ? 0 : 1)];
            var now = context.Time.GetUtcNow();
            foreach (var account in accounts)
            {
                var people = FrequentPeople.Load(conn, account.Id, now);
                found.AddRange(query.Length == 0 ? people : FrequentPeople.Match(people, query));
            }
        }

        // Google Contacts Only For A Query (asked of every account at once)
        if (query.Length > 0 && context.Host.Contacts is { } contacts)
        {
            var answers = await Task.WhenAll(accounts.Select(a => contacts.SearchAsync(a.Id, query, workspace: a.HostedDomain is not null, ct)));
            found.AddRange(answers.SelectMany(a => a.Contacts));
        }

        List<PersonResult> result = [.. found
            .DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase)
            .Take(ContactSearch.MaxResults)
            .Select(c => new PersonResult(c.Name.Length > 0 ? c.Name : null, c.Email))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListPersonResult);
    }
}
