namespace LeafCalendar.Core.Auth;

/// <summary>Google rejected a refresh token (revoked, expired, or password changed).</summary>
public sealed class InvalidGrantException() : Exception("Google rejected the refresh token.");

/// <summary>
/// Tells "the saved secrets (<c>secrets.bin</c>) can't be read right now" (a locked file, or DPAPI failing for a passing
/// reason) apart from other <see cref="InvalidDataException"/>s, such as Google's empty answers.
/// </summary>
/// <remarks>
/// <see cref="InvalidDataException"/> is sealed, so instead of a subclass the store throws a marked one: every catch of
/// <see cref="InvalidDataException"/> ("try later", never "signed out") keeps working, and <see cref="Is"/> picks it out.
/// </remarks>
public static class SecretsUnavailable
{
    private const string Marker = "LeafCalendar.SecretsUnavailable";

    /// <summary>Creates the marked exception the secret store throws.</summary>
    /// <param name="message">What couldn't be done (never a secret).</param>
    /// <param name="innerException">The failure underneath (a file or DPAPI error).</param>
    /// <returns>An <see cref="InvalidDataException"/> that <see cref="Is"/> recognizes.</returns>
    public static InvalidDataException Create(string message, Exception innerException)
    {
        var ex = new InvalidDataException(message, innerException);
        ex.Data[Marker] = true;
        return ex;
    }

    /// <summary>True when <paramref name="ex"/> says the saved secrets can't be read right now.</summary>
    public static bool Is(Exception ex) => ex is InvalidDataException && ex.Data.Contains(Marker);
}

/// <summary>An account can't get an access token until the user signs in again.</summary>
public sealed class AccountNeedsSignInException(string accountId) : Exception("The account needs to sign in again.")
{
    /// <summary>The Google account ID (<c>sub</c>).</summary>
    public string AccountId { get; } = accountId;
}

/// <summary>Sign-in failed. <see cref="Exception.Message"/> is written for the user.</summary>
public class SignInException(string message) : Exception(message);

/// <summary>The browser never came back from sign-in in time (<see cref="SignInFlow.Timeout"/>), so nothing was saved.</summary>
public sealed class SignInTimeoutException(string message) : SignInException(message);

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

    private static string Plain(string text) =>
        new([.. text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)]);
}
