using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
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
/// <para>
/// Results are untrusted text: control and format characters are removed, names are capped, and only addresses that
/// parse exactly are kept. Nothing is stored; logs carry the account ID and HTTP status only.
/// </para>
/// <para>
/// Both sources are searched at once, each limited to <see cref="Timeout"/>. A source that fails is left out and the
/// other still counts. Google's search cache needs an empty-query "warmup" request before it returns results, so
/// the first search of each source for each account sends one. The warmup runs on its own (the caller canceling
/// doesn't stop it), its answer and any failure are ignored, and nothing about it is logged. It is kept per source,
/// so a source added by a later re-sign-in is warmed the first time it's searched.
/// </para>
/// </remarks>
/// <seealso href="https://developers.google.com/people/api/rest/v1/people/searchContacts"/>
/// <seealso href="https://developers.google.com/people/api/rest/v1/otherContacts/search"/>
public sealed class ContactSearch(HttpClient http, AccessTokenProvider tokens, AppLog log, GoogleEndpoints? endpoints = null)
{
    /// <summary>Most suggestions returned.</summary>
    public const int MaxResults = 8;

    const int MaxQuery     = 100;
    const int MaxName      = 100;
    const int MaxEmail     = 254;
    const int MaxPeople    = 50;
    const int MaxPerPerson = 5;
    const int MaxPerSource = 25;

    // Relative paths start with "./" so the colon isn't read as a URI scheme
    const string ContactsPath = "./people:searchContacts";
    const string OthersPath   = "./otherContacts:search";

    readonly Uri _root = (endpoints ?? GoogleEndpoints.Default).PeopleApi;

    // "account source" -> the warmup request (completed after the first search)
    readonly ConcurrentDictionary<string, Task> _warmups = new(StringComparer.Ordinal);

    /// <summary>How long one source may take before it counts as failed, so a slow Google can't stall typing.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Searches both sources. Google, network, timeout, malformed-answer, and sign-in problems never throw: a failed
    /// source is left out, and the access state says when the user can fix something.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled (the user kept typing).</exception>
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

        // Warm Up Google's Search Cache Once Per Source
        await Task.WhenAll(paths.Select(path => _warmups.GetOrAdd($"{accountId} {path}", _ => WarmUpAsync(accountId, path)))).WaitAsync(ct);

        // Search Both Sources At Once
        var pages = await Task.WhenAll(paths.Select(path => GetAsync(accountId, Url(path, query), ct)));

        // A Problem The User Can Fix Wins
        if (pages.Select(p => p.Access).FirstOrDefault(a => a != ContactAccess.Allowed) is var access and not ContactAccess.Allowed)
        {
            return new([], access);
        }

        // Merge, Contacts First
        var found = pages.Where(p => p.Page is not null).SelectMany(p => Clean(p.Page!).Take(MaxPerSource));
        return new([.. found.DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase).Take(MaxResults)], ContactAccess.Allowed);
    }

    // =========================================================================
    // HTTP
    // =========================================================================

    // Full URL for one source and query
    Uri Url(string path, string query) =>
        new(_root, $"{path}?query={Uri.EscapeDataString(query)}&readMask=names,emailAddresses&pageSize=10");

    // Empty-query request with its own timeout; never faults, and nothing is logged
    async Task WarmUpAsync(string accountId, string path)
    {
        try
        {
            using var timeout  = new CancellationTokenSource(Timeout);
            using var response = await SendAsync(accountId, Url(path, ""), timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed warmup only means the next search may come back short; the real search reports errors.
        }
    }

    // One search, limited to Timeout. A null page with Allowed means it failed (logged by status only).
    // Anything but the caller's own cancellation is a failure of this source, never an exception.
    async Task<(PeopleSearchResponse? Page, ContactAccess Access)> GetAsync(string accountId, Uri uri, CancellationToken ct)
    {
        var status = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);

        try
        {
            using var response = await SendAsync(accountId, uri, limit.Token);
            status = (int)response.StatusCode;

            // Success
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.PeopleSearchResponse, limit.Token) ?? new(), ContactAccess.Allowed);
            }

            // Refusals The User Can Fix
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var error   = GoogleJson.TryParse(await response.Content.ReadAsStringAsync(limit.Token), GoogleJsonContext.Default.ApiErrorEnvelope)?.Error;
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
        }
        catch (AccountNeedsSignInException)
        {
            return (null, ContactAccess.NeedsConsent);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            status = ex is HttpRequestException { StatusCode: { } code } ? (int)code : status;
        }

        Failed(accountId, status);
        return (null, ContactAccess.Allowed);
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

    // Up to five valid addresses per person, each with the person's first name; people without one are dropped
    static IEnumerable<Contact> Clean(PeopleSearchResponse page)
    {
        foreach (var person in (page.Results ?? []).Take(MaxPeople).Select(r => r.Person).OfType<PeoplePerson>())
        {
            var name = Plain(person.Names?.FirstOrDefault()?.DisplayName, MaxName);

            foreach (var email in (person.EmailAddresses ?? []).Select(e => ValidEmail(e.Value)).OfType<string>().Take(MaxPerPerson))
            {
                yield return new Contact(name, email);
            }
        }
    }

    // Kept only when it has no hidden or separator characters and parses back to exactly the same text
    // (the guest field's rule); never altered
    static string? ValidEmail(string? value)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > MaxEmail || email.Any(c => IsHidden(c) || char.IsSeparator(c)))
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
