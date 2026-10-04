using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Auth;

/// <summary>Moves secrets from the Credential Locker (earlier builds) to <c>secrets.bin</c> in the profile folder.</summary>
public static class TokenStoreMigration
{
    /// <summary>
    /// Picks the profile's secret store, moving the Credential Locker's secrets into <paramref name="file"/> once.
    /// </summary>
    /// <remarks>
    /// <para>The Locker's secrets that <c>secrets.bin</c> lacks (an account it has no refresh token for, and the OAuth client
    /// when it has none) are written to it in one write, and only then is the Locker emptied. <c>secrets.bin</c>'s own
    /// secrets are never replaced: they're newer (a sign-in since the move). So a first move, a move a passing Locker
    /// failure put off while the user set Leaf up again, and a rebuild of a <c>secrets.bin</c> that's bad data (it reads as
    /// empty) are one path. When <c>secrets.bin</c> can't be read right now, nothing is touched until it can.</para>
    /// <para>When that write fails, this run uses the Locker (untouched) if <c>secrets.bin</c> holds nothing, or
    /// <c>secrets.bin</c> otherwise (the Locker keeps its leftovers); the next start tries again. When the Locker can't be
    /// opened or read, the file store is used and the Locker is left as it is. Emptying the Locker is best effort: a failure
    /// is logged and tried again on the next start.</para>
    /// <para>Never throws, so startup can't fail here. Logs only event names and exception type names, never a secret.</para>
    /// </remarks>
    /// <param name="file">The profile's <c>secrets.bin</c> store.</param>
    /// <param name="openLocker">Opens the profile's Credential Locker store (it can throw, so it's opened here).</param>
    /// <param name="log">The app log.</param>
    /// <returns><paramref name="file"/>, or the Locker store when the move couldn't be written and the file holds nothing.</returns>
    public static ITokenStore Open(ProtectedFileTokenStore file, Func<ITokenStore> openLocker, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(openLocker);
        ArgumentNullException.ThrowIfNull(log);

        // What secrets.bin Holds (nothing before the move; bad data reads as empty)
        var moved = file.Exists;
        HashSet<string> saved;
        bool hasClient;
        try
        {
            saved = [.. file.GetAccountIds()];
            hasClient = file.GetClientCredentials() is not null;
        }
        catch (InvalidDataException)
        {
            // Can't Be Read Right Now (it may be fine): the leftovers stay until it can
            return file;
        }

        // Read What The File Lacks From The Locker
        ITokenStore locker;
        OAuthClientCredentials? client;
        Dictionary<string, string> tokens;
        try
        {
            locker = openLocker();
            client = hasClient ? null : locker.GetClientCredentials();
            tokens = new(StringComparer.Ordinal);
            foreach (var accountId in locker.GetAccountIds())
            {
                if (!saved.Contains(accountId) && locker.GetRefreshToken(accountId) is { } token)
                {
                    tokens[accountId] = token;
                }
            }
        }
        catch (Exception ex)
        {
            log.Info("auth.secrets.locker.unreadable", $"error={ex.GetType().Name}");
            return file;
        }

        // One Write, Then The Locker Copy Goes
        if (client is not null || tokens.Count > 0)
        {
            try
            {
                file.Import(client, tokens);
            }
            catch (Exception ex)
            {
                log.Info("auth.secrets.migrate.failed", $"error={ex.GetType().Name}");
                return saved.Count == 0 && !hasClient ? locker : file;
            }

            log.Info(moved ? "auth.secrets.rebuilt" : "auth.secrets.migrated");
        }

        CleanUp(locker, log);
        return file;
    }

    // Best effort: whatever is left is deleted on the next start. The client is always cleared, so half of one (the ID
    // gone, the secret left, which reads as no client) goes too.
    private static void CleanUp(ITokenStore locker, AppLog log)
    {
        try
        {
            foreach (var accountId in locker.GetAccountIds())
            {
                locker.RemoveRefreshToken(accountId);
            }

            locker.ClearClientCredentials();
        }
        catch (Exception ex)
        {
            log.Info("auth.secrets.cleanup.failed", $"error={ex.GetType().Name}");
        }
    }
}
