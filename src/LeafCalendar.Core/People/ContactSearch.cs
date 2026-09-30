using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.People;

/// <summary>A suggestion for the guest field: a display name (may be empty) and a checked email address.</summary>
public sealed record Contact(string Name, string Email);

/// <summary>Whether contacts can be searched for an account.</summary>
public enum ContactAccess
{
    /// <summary>Searched (the list may be empty).</summary>
    Allowed,

    /// <summary>The account's grant lacks the contacts scopes (or the account needs to sign in again); one re-sign-in fixes it.</summary>
    NeedsConsent,

    /// <summary>The People API is turned off in the user's Google Cloud project.</summary>
    ApiDisabled,
}

/// <summary>What a search found.</summary>
public sealed record ContactResults(IReadOnlyList<Contact> Contacts, ContactAccess Access);

/// <summary>
/// Guest autocomplete from the People API: your contacts, then "other contacts" (people you've emailed), read-only.
/// </summary>
/// <remarks>
/// Results are untrusted text: control and format characters are removed, names are capped, and only addresses that
/// parse exactly are kept. Nothing is stored; logs carry the account ID and HTTP status only.
/// Google's search cache needs an empty-query "warmup" request before it returns results, so the first search for
/// each account in a session sends one quietly (its answer and any failure are ignored).
/// </remarks>
/// <seealso href="https://developers.google.com/people/api/rest/v1/people/searchContacts"/>
/// <seealso href="https://developers.google.com/people/api/rest/v1/otherContacts/search"/>
public sealed class ContactSearch(HttpClient http, AccessTokenProvider tokens, AppLog log, GoogleEndpoints? endpoints = null)
{
    /// <summary>Most suggestions returned.</summary>
    public const int MaxResults = 8;

    const int MaxQuery   = 100;
    const int MaxName    = 100;
    const int MaxEmail   = 254;
    const int MaxPerPage = 50;

    // Relative paths start with "./" so the colon isn't read as a URI scheme
    const string ContactsPath = "./people:searchContacts";
    const string OthersPath   = "./otherContacts:search";

    readonly Uri _root = (endpoints ?? GoogleEndpoints.Default).PeopleApi;

    readonly ConcurrentDictionary<string, byte> _warmed = new(StringComparer.Ordinal);

    /// <summary>Searches both sources; never throws for Google, network, or sign-in errors (returns empty).</summary>
    public async Task<ContactResults> SearchAsync(string accountId, string query, CancellationToken ct)
    {
        // Nothing To Search
        query = query.Trim();
        if (query.Length is 0 or > MaxQuery)
        {
            return new([], ContactAccess.Allowed);
        }

        // Which Sources The Grant Allows
        bool contacts, others;
        try
        {
            contacts = await tokens.HasScopeAsync(accountId, GoogleOAuthClient.ContactsScope, ct);
            others   = await tokens.HasScopeAsync(accountId, GoogleOAuthClient.OtherContactsScope, ct);
        }
        catch (AccountNeedsSignInException)
        {
            return new([], ContactAccess.NeedsConsent);
        }

        if (!contacts && !others)
        {
            return new([], ContactAccess.NeedsConsent);
        }

        string[] paths = [.. new[] { contacts ? ContactsPath : null, others ? OthersPath : null }.OfType<string>()];

        // Warm Up Google's Search Cache Once Per Account
        if (_warmed.TryAdd(accountId, 0))
        {
            foreach (var path in paths)
            {
                await WarmUpAsync(accountId, path, ct);
            }
        }

        // Search Each Source
        var found = new List<Contact>();
        foreach (var path in paths)
        {
            var (page, access) = await GetAsync(accountId, Url(path, query), ct);
            if (access != ContactAccess.Allowed || page is null)
            {
                return new([], access);
            }

            found.AddRange(Clean(page));
        }

        return new([.. found.DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase).Take(MaxResults)], ContactAccess.Allowed);
    }

    // =========================================================================
    // HTTP
    // =========================================================================

    // Full URL for one source and query
    Uri Url(string path, string query) =>
        new(_root, $"{path}?query={Uri.EscapeDataString(query)}&readMask=names,emailAddresses&pageSize=10");

    // Empty-query request; the answer and any failure are ignored, and nothing is logged
    async Task WarmUpAsync(string accountId, string path, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(accountId, Url(path, ""), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AccountNeedsSignInException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // A failed warmup only means the next search may come back short; the real search reports errors.
        }
    }

    // One search; a null page with Allowed means it failed (logged by status only)
    async Task<(PeopleSearchResponse? Page, ContactAccess Access)> GetAsync(string accountId, Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(accountId, uri, ct);

            // Success
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.PeopleSearchResponse, ct) ?? new(), ContactAccess.Allowed);
            }

            // Refusals The User Can Fix
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var error   = GoogleJson.TryParse(await response.Content.ReadAsStringAsync(ct), GoogleJsonContext.Default.ApiErrorEnvelope)?.Error;
                var reasons = (error?.Errors ?? []).Concat(error?.Details ?? []).Select(e => e.Reason).ToList();

                if (reasons.Any(r => r is "accessNotConfigured" or "SERVICE_DISABLED"))
                {
                    return (null, ContactAccess.ApiDisabled);
                }

                if (reasons.Any(r => r is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT"))
                {
                    return (null, ContactAccess.NeedsConsent);
                }
            }

            Failed(accountId, (int)response.StatusCode);
            return (null, ContactAccess.Allowed);
        }
        catch (AccountNeedsSignInException)
        {
            return (null, ContactAccess.NeedsConsent);
        }
        catch (HttpRequestException ex)
        {
            Failed(accountId, (int?)ex.StatusCode ?? 0);
            return (null, ContactAccess.Allowed);
        }
        catch (JsonException)
        {
            Failed(accountId, 200);
            return (null, ContactAccess.Allowed);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Failed(accountId, 0);
            return (null, ContactAccess.Allowed);
        }
    }

    // Account ID and status only: never the query, names, or addresses
    void Failed(string accountId, int status) =>
        log.Info("contacts.search.failed", string.Create(CultureInfo.InvariantCulture, $"account={accountId} status={status}"));

    // One call with a fresh access token; a 401 drops the cached token and retries once
    async Task<HttpResponseMessage> SendAsync(string accountId, Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(accountId, ct));

            var response = await http.SendAsync(request, ct);

            // Expired Access Token: Refresh Once
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                tokens.Forget(accountId);
                continue;
            }

            return response;
        }
    }

    // =========================================================================
    // CLEANING
    // =========================================================================

    // One contact per valid address, with the person's first name; people without a valid address are dropped
    static IEnumerable<Contact> Clean(PeopleSearchResponse page)
    {
        foreach (var person in (page.Results ?? []).Take(MaxPerPage).Select(r => r.Person).OfType<PeoplePerson>())
        {
            var name = Plain(person.Names?.FirstOrDefault()?.DisplayName, MaxName);

            foreach (var email in (person.EmailAddresses ?? []).Select(e => ValidEmail(e.Value)).OfType<string>())
            {
                yield return new Contact(name, email);
            }
        }
    }

    // Kept only when it parses back to exactly the same text (the guest field's rule), unaltered
    static string? ValidEmail(string? value)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > MaxEmail || email.Any(IsHidden))
        {
            return null;
        }

        return MailAddress.TryCreate(email, out var address) && address.Address == email ? email : null;
    }

    // Drops control and format characters (e.g. right-to-left overrides), trims, and caps the length
    static string Plain(string? text, int max)
    {
        var plain = new string((text ?? "").Where(c => !IsHidden(c)).ToArray()).Trim();
        if (plain.Length <= max)
        {
            return plain;
        }

        // Don't split a surrogate pair at the cut
        return plain[..(char.IsHighSurrogate(plain[max - 1]) ? max - 1 : max)];
    }

    static bool IsHidden(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format;
}
