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
public class SignInException(string message) : Exception(message);

/// <summary>
/// A sign-in for one account came back as another Google user, so nothing was saved. Both addresses are plain text
/// (control and format characters removed), ready to show.
/// </summary>
public sealed class WrongAccountException(string signedInEmail, string? expectedEmail)
    : SignInException("You signed in with a different Google account.")
{
    /// <summary>The address Google signed in.</summary>
    public string SignedInEmail { get; } = Plain(signedInEmail);

    /// <summary>The address that was asked for (empty when unknown).</summary>
    public string ExpectedEmail { get; } = Plain(expectedEmail ?? "");

    static string Plain(string text) =>
        new([.. text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)]);
}
