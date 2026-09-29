namespace LeafCalendar.Core.Auth;

/// <summary>Google rejected a refresh token (revoked, expired, or password changed).</summary>
public sealed class InvalidGrantException() : Exception("Google rejected the refresh token.");

/// <summary>An account can't get an access token until the user signs in again.</summary>
public sealed class AccountNeedsSignInException(string accountId) : Exception("The account needs to sign in again.")
{
    /// <summary>The Google account ID (<c>sub</c>).</summary>
    public string AccountId { get; } = accountId;
}

/// <summary>Sign-in failed. <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class SignInException(string message) : Exception(message);
