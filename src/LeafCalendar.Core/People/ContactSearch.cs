using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json.Serialization.Metadata;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Tray;

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
/// Guest autocomplete from the People API: your contacts, then your Workspace directory (coworkers), then "other
/// contacts" (people you've emailed), read-only.
/// </summary>
/// <remarks>
/// <para>
/// Results are untrusted text: control and format characters are removed, names are capped, and only addresses that
/// parse exactly are kept. Nothing is stored; logs carry the account ID and HTTP status only.
/// </para>
/// <para>
/// The sources are searched at once, each limited to <see cref="Timeout"/>. A source that fails is left out and the
/// others still count. Google's contacts search cache needs an empty-query "warmup" request before it returns
/// results, so the first search of each contacts source for each account sends one. The warmup runs on its own (the
/// caller canceling doesn't stop it), its answer and any failure are ignored, and nothing about it is logged. It is
/// kept per source, so a source added by a later re-sign-in is warmed the first time it's searched.
/// </para>
/// <para>
/// The directory is searched only when the grant includes <see cref="GoogleOAuthClient.DirectoryScope"/>, with no
/// warmup. Google refuses it for personal accounts (<c>400 FAILED_PRECONDITION</c>), so any refusal there is just a
/// failed source, never something the user is asked to fix.
/// </para>
/// </remarks>
/// <seealso href="https://developers.google.com/people/api/rest/v1/people/searchDirectoryPeople"/>
/// <seealso href="https://developers.google.com/people/api/rest/v1/people/searchContacts"/>
/// <seealso href="https://developers.google.com/people/api/rest/v1/otherContacts/search"/>
public sealed class ContactSearch(HttpClient http, AccessTokenProvider tokens, AppLog log, GoogleEndpoints? endpoints = null)
{
    /// <summary>Most suggestions returned.</summary>
    public const int MaxResults = 8;

    /// <summary>Longest name kept (longer ones are cut).</summary>
    internal const int MaxName = 100;

    private const int MaxQuery = 100;
    private const int MaxEmail = 254;
    private const int MaxPeople = 50;
    private const int MaxPerPerson = 5;
    private const int MaxPerSource = 25;

    // Relative paths start with "./" so the colon isn't read as a URI scheme
    private const string ContactsPath = "./people:searchContacts";
    private const string OthersPath = "./otherContacts:search";
    private const string DirectoryPath = "./people:searchDirectoryPeople";

    private readonly Uri _root = (endpoints ?? GoogleEndpoints.Default).PeopleApi;

    // "account source" -> the warmup request (completed after the first search)
    private readonly ConcurrentDictionary<string, Task> _warmups = new(StringComparer.Ordinal);

    /// <summary>How long one source may take before it counts as failed, so a slow Google can't stall typing.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Searches your contacts, your Workspace directory (when the grant has <see cref="GoogleOAuthClient.DirectoryScope"/>),
    /// and other contacts, merged in that order. Google, network, timeout, malformed-answer, and sign-in problems never
    /// throw: a failed source is left out, and the access state says when the user can fix something.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled (the user kept typing).</exception>
    public Task<ContactResults> SearchAsync(string accountId, string query, CancellationToken ct) => SearchAsync(accountId, query, workspace: false, ct);

    /// <summary>
    /// <see cref="SearchAsync(string, string, CancellationToken)"/>, for an account Leaf knows is a Workspace one: when
    /// its grant lacks the directory permission, the results still come back, marked
    /// <see cref="ContactAccess.NeedsConsent"/>, so the editor offers the one re-sign-in that adds it.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled (the user kept typing).</exception>
    public async Task<ContactResults> SearchAsync(string accountId, string query, bool workspace, CancellationToken ct)
    {
        // Nothing To Search
        query = query.Trim();
        if (query.Length is 0 or > MaxQuery)
        {
            return new([], ContactAccess.Allowed);
        }

        // Which Sources The Grant Allows (checking may refresh the token, so it's limited to Timeout too)
        bool contacts, others, directory;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);

            contacts = await tokens.HasScopeAsync(accountId, GoogleOAuthClient.ContactsScope, limit.Token);
            others = await tokens.HasScopeAsync(accountId, GoogleOAuthClient.OtherContactsScope, limit.Token);
            directory = await tokens.HasScopeAsync(accountId, GoogleOAuthClient.DirectoryScope, limit.Token);
        }
        catch (AccountNeedsSignInException)
        {
            return new([], ContactAccess.NeedsConsent);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            // A token refresh that failed or hung is a failed search, never an exception
            Failed(accountId, 0);
            return new([], ContactAccess.Allowed);
        }

        if (!contacts && !others)
        {
            return new([], ContactAccess.NeedsConsent);
        }

        string[] paths = [.. new[] { contacts ? ContactsPath : null, others ? OthersPath : null }.OfType<string>()];

        // Warm Up Google's Search Cache Once Per Source (the directory needs none)
        await Task.WhenAll(paths.Select(path => _warmups.GetOrAdd($"{accountId} {path}", _ => WarmUpAsync(accountId, path)))).WaitAsync(ct);

        // Search Every Source At Once (the directory between contacts and other contacts)
        var searches = paths.Select(path => SearchSourceAsync(accountId, Url(path, query), ct)).ToList();
        if (directory)
        {
            searches.Insert(contacts ? 1 : 0, SearchDirectoryAsync(accountId, query, ct));
        }

        var pages = await Task.WhenAll(searches);

        // A Problem The User Can Fix Wins
        if (pages.Select(p => p.Access).FirstOrDefault(a => a != ContactAccess.Allowed) is var access and not ContactAccess.Allowed)
        {
            return new([], access);
        }

        // Merge In Source Order; A Workspace Grant Without The Directory Still Lists, With The Offer To Add It
        var found = pages.SelectMany(p => p.People.Take(MaxPerSource)).DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase).Take(MaxResults);
        return new([.. found], workspace && !directory ? ContactAccess.NeedsConsent : ContactAccess.Allowed);
    }

    // Contacts or other contacts
    private async Task<(IEnumerable<Contact> People, ContactAccess Access)> SearchSourceAsync(string accountId, Uri uri, CancellationToken ct)
    {
        var (page, access) = await GetAsync(accountId, uri, GoogleJsonContext.Default.PeopleSearchResponse, ct);
        return (Clean((page?.Results ?? []).Select(r => r.Person)), access);
    }

    // The Workspace directory: any refusal (personal accounts always get 400) is only a failed source, logged by status,
    // never something to fix
    private async Task<(IEnumerable<Contact> People, ContactAccess Access)> SearchDirectoryAsync(string accountId, string query, CancellationToken ct)
    {
        var uri = new Uri(_root, $"{DirectoryPath}?query={Uri.EscapeDataString(query)}&readMask=names,emailAddresses&sources=DIRECTORY_SOURCE_TYPE_DOMAIN_PROFILE&pageSize=10");
        var (page, _) = await GetAsync(accountId, uri, GoogleJsonContext.Default.DirectorySearchResponse, ct, canFix: false);
        return (Clean(page?.People ?? []), ContactAccess.Allowed);
    }

    // =========================================================================
    // HTTP
    // =========================================================================

    // Full URL for one source and query
    private Uri Url(string path, string query) =>
        new(_root, $"{path}?query={Uri.EscapeDataString(query)}&readMask=names,emailAddresses&pageSize=10");

    // Empty-query request with its own timeout; never faults, and nothing is logged
    private async Task WarmUpAsync(string accountId, string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            using var response = await SendAsync(accountId, Url(path, ""), timeout.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed warmup only means the next search may come back short; the real search reports errors.
        }
    }

    // One search, limited to Timeout. A null page with Allowed means it failed (logged by status only).
    // Anything but the caller's own cancellation is a failure of this source, never an exception. Without canFix,
    // every refusal is just a failure (the directory's).
    private async Task<(T? Page, ContactAccess Access)> GetAsync<T>(string accountId, Uri uri, JsonTypeInfo<T> type, CancellationToken ct, bool canFix = true)
        where T : class
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
                return (await response.Content.ReadFromJsonAsync(type, limit.Token), ContactAccess.Allowed);
            }

            // Refusals The User Can Fix
            if (canFix && response.StatusCode == HttpStatusCode.Forbidden)
            {
                var error = GoogleJson.TryParse(await response.Content.ReadAsStringAsync(limit.Token), GoogleJsonContext.Default.ApiErrorEnvelope)?.Error;
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
            return (null, canFix ? ContactAccess.NeedsConsent : ContactAccess.Allowed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            status = ex is HttpRequestException { StatusCode: { } code } ? (int)code : status;
        }

        Failed(accountId, status);
        return (null, ContactAccess.Allowed);
    }

    // Account ID and status only: never the query, names, or addresses
    private void Failed(string accountId, int status) =>
        log.Info("contacts.search.failed", string.Create(CultureInfo.InvariantCulture, $"account={accountId} status={status}"));

    // One call with a fresh access token; a 401 drops the cached token and retries once
    private async Task<HttpResponseMessage> SendAsync(string accountId, Uri uri, CancellationToken ct)
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
    private static IEnumerable<Contact> Clean(IEnumerable<PeoplePerson?> people)
    {
        foreach (var person in people.Take(MaxPeople).OfType<PeoplePerson>())
        {
            var name = Plain(person.Names?.FirstOrDefault()?.DisplayName, MaxName);

            foreach (var email in (person.EmailAddresses ?? []).Select(e => ValidEmail(e.Value)).OfType<string>().Take(MaxPerPerson))
            {
                yield return new Contact(name, email);
            }
        }
    }

    /// <summary>
    /// An untrusted address, kept only when it has no hidden or separator characters and parses back to exactly the
    /// same text (the guest field's rule); never altered. Null when it doesn't pass.
    /// </summary>
    internal static string? ValidEmail(string? value)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > MaxEmail || email.Any(c => IsHidden(c) || char.IsSeparator(c)))
        {
            return null;
        }

        return MailAddress.TryCreate(email, out var address) && address.Address == email ? email : null;
    }

    /// <summary>
    /// Untrusted text as one plain line: <see cref="DisplayText.Clean"/>'s rules (control characters become spaces;
    /// bidi, zero-width, and other format characters and lone surrogate halves go), trimmed, and cut at
    /// <paramref name="max"/> without an ellipsis, never inside an emoji.
    /// </summary>
    internal static string Plain(string? text, int max)
    {
        var plain = DisplayText.Clean(text, int.MaxValue);
        if (plain.Length <= max)
        {
            return plain;
        }

        // Don't split a surrogate pair at the cut
        return plain[..(char.IsHighSurrogate(plain[max - 1]) ? max - 1 : max)];
    }

    private static bool IsHidden(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format;
}
