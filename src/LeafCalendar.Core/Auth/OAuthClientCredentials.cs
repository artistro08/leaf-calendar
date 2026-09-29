namespace LeafCalendar.Core.Auth;

/// <summary>
/// The user's own Google OAuth "Desktop app" client. Stored only in Credential Locker.
/// </summary>
public sealed record OAuthClientCredentials(string ClientId, string ClientSecret)
{
    /// <summary>
    /// Checks pasted values before saving. Returns a message for the user, or null when valid.
    /// </summary>
    public static string? Validate(string clientId, string clientSecret)
    {
        if (!clientId.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
        {
            return "Client ID should end with .apps.googleusercontent.com.";
        }

        if (clientSecret.Trim().Length == 0)
        {
            return "Enter the client secret.";
        }

        return null;
    }

    /// <summary>Omits the secret so logging a record can't leak it.</summary>
    public override string ToString() => $"OAuthClientCredentials({ClientId})";
}
