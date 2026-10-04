namespace LeafCalendar.Core.Auth;

/// <summary>Moves secrets from the old store (the Credential Locker) to the new one (the profile folder).</summary>
public static class TokenStoreMigration
{
    /// <summary>
    /// Moves secrets from the old store to the new one, once. Everything is written to the new store before anything is
    /// removed from the old one, so a failure leaves the user signed in through the old copy, and the next start tries again.
    /// </summary>
    /// <returns>True when it moved something; false when the old store was empty.</returns>
    public static bool Migrate(ITokenStore from, ITokenStore to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var client = from.GetClientCredentials();
        var accounts = from.GetAccountIds();
        if (client is null && accounts.Count == 0)
        {
            return false;
        }

        // Copy Everything First
        if (client is not null)
        {
            to.SetClientCredentials(client);
        }

        foreach (var account in accounts)
        {
            if (from.GetRefreshToken(account) is { } token)
            {
                to.SetRefreshToken(account, token);
            }
        }

        // Then Remove The Old Copy
        foreach (var account in accounts)
        {
            from.RemoveRefreshToken(account);
        }

        from.ClearClientCredentials();
        return true;
    }
}
