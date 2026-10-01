using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>Whether an account can sync.</summary>
public enum AccountStatus
{
    /// <summary>Tokens work.</summary>
    Ok,

    /// <summary>Refresh token missing or rejected; user must sign in again.</summary>
    NeedsSignIn,
}

/// <summary>
/// A connected Google account. <see cref="Id"/> is Google's stable <c>sub</c>. <see cref="HostedDomain"/> is Google's
/// <c>hd</c> sign-in claim: null while Leaf doesn't know it yet, empty for a personal account, else the Workspace domain.
/// </summary>
public sealed record Account(string Id, string Email, string? DisplayName, string? Picture, AccountStatus Status, string? HostedDomain = null);

/// <summary>Reads and writes the <c>accounts</c> table.</summary>
public static class AccountStore
{
    /// <summary>Inserts or updates an account (same Google account never duplicates). A null domain keeps the one already known.</summary>
    public static void Upsert(SqliteConnection conn, Account account) =>
        conn.Execute(
            null,
            """
            INSERT INTO accounts (id, email, display_name, picture, status, hosted_domain)
            VALUES ($id, $email, $name, $picture, $status, $domain)
            ON CONFLICT (id) DO UPDATE SET
                email         = excluded.email,
                display_name  = excluded.display_name,
                picture       = excluded.picture,
                status        = excluded.status,
                hosted_domain = COALESCE(excluded.hosted_domain, accounts.hosted_domain);
            """,
            ("$id", account.Id),
            ("$email", account.Email),
            ("$name", account.DisplayName),
            ("$picture", account.Picture),
            ("$status", ToText(account.Status)),
            ("$domain", account.HostedDomain));

    /// <summary>All accounts, ordered by email.</summary>
    public static IReadOnlyList<Account> GetAll(SqliteConnection conn) =>
        conn.Query(
            null,
            "SELECT id, email, display_name, picture, status, hosted_domain FROM accounts ORDER BY email;",
            r => new Account(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), FromText(r.GetString(4)), r.GetStringOrNull(5)));

    /// <summary>Saves an account's Workspace domain (<c>""</c> for a personal account).</summary>
    public static void SetHostedDomain(SqliteConnection conn, string accountId, string hostedDomain) =>
        conn.Execute(null, "UPDATE accounts SET hosted_domain = $domain WHERE id = $id;", ("$domain", hostedDomain), ("$id", accountId));

    /// <summary>True for a Google Workspace account (rooms and event types are offered only there).</summary>
    public static bool IsWorkspace(Account a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return !string.IsNullOrEmpty(a.HostedDomain);
    }

    /// <summary>Updates an account's status.</summary>
    public static void SetStatus(SqliteConnection conn, string id, AccountStatus status) =>
        conn.Execute(null, "UPDATE accounts SET status = $status WHERE id = $id;", ("$status", ToText(status)), ("$id", id));

    /// <summary>Deletes an account and (by cascade) its calendars and events.</summary>
    public static void Delete(SqliteConnection conn, string id, SqliteTransaction? tx = null) =>
        conn.Execute(tx, "DELETE FROM accounts WHERE id = $id;", ("$id", id));

    static string ToText(AccountStatus status) => status == AccountStatus.NeedsSignIn ? "needs-sign-in" : "ok";

    static AccountStatus FromText(string text) => text == "needs-sign-in" ? AccountStatus.NeedsSignIn : AccountStatus.Ok;
}
