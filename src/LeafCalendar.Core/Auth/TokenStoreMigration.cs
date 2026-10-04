using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Auth;

/// <summary>Moves secrets from the Credential Locker (earlier builds) to <c>secrets.bin</c> in the profile folder.</summary>
public static class TokenStoreMigration
{
    /// <summary>
    /// Picks the profile's secret store, moving the Credential Locker's secrets into <paramref name="file"/> once.
    /// </summary>
    /// <remarks>
    /// <para>Once <c>secrets.bin</c> exists, it's the store, whatever the Locker holds. Deleting the Locker's leftovers is only
    /// tidying up: a failure is logged and tried again on the next start.</para>
    /// <para>Before that, the Locker's secrets are written to <c>secrets.bin</c> in one write, and only then deleted from the
    /// Locker. When that write fails, this run uses the Locker (untouched), and the next start tries again. When the Locker
    /// can't be opened or read, there's nothing to lose, so the file store is used.</para>
    /// <para>Never throws, so startup can't fail here. Logs only event names and exception type names, never a secret.</para>
    /// </remarks>
    /// <param name="file">The profile's <c>secrets.bin</c> store.</param>
    /// <param name="openLocker">Opens the profile's Credential Locker store (it can throw, so it's opened here).</param>
    /// <param name="log">The app log.</param>
    /// <returns><paramref name="file"/>, or the Locker store when the move couldn't be written.</returns>
    public static ITokenStore Open(ProtectedFileTokenStore file, Func<ITokenStore> openLocker, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(openLocker);
        ArgumentNullException.ThrowIfNull(log);

        // Already Moved: the Locker's leftovers are only tidied up
        if (file.Exists)
        {
            CleanUp(openLocker, log);
            return file;
        }

        // Read The Locker
        ITokenStore locker;
        OAuthClientCredentials? client;
        Dictionary<string, string> tokens;
        try
        {
            locker = openLocker();
            client = locker.GetClientCredentials();
            tokens = new(StringComparer.Ordinal);
            foreach (var accountId in locker.GetAccountIds())
            {
                if (locker.GetRefreshToken(accountId) is { } token)
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

        if (client is null && tokens.Count == 0)
        {
            return file;
        }

        // One Write, Then The Locker Copy Goes
        try
        {
            file.Import(client, tokens);
        }
        catch (Exception ex)
        {
            log.Info("auth.secrets.migrate.failed", $"error={ex.GetType().Name}");
            return locker;
        }

        log.Info("auth.secrets.migrated");
        CleanUp(() => locker, log);
        return file;
    }

    // Best effort: whatever is left is deleted on the next start
    private static void CleanUp(Func<ITokenStore> openLocker, AppLog log)
    {
        try
        {
            var locker = openLocker();
            foreach (var accountId in locker.GetAccountIds())
            {
                locker.RemoveRefreshToken(accountId);
            }

            if (locker.GetClientCredentials() is not null)
            {
                locker.ClearClientCredentials();
            }
        }
        catch (Exception ex)
        {
            log.Info("auth.secrets.cleanup.failed", $"error={ex.GetType().Name}");
        }
    }
}
