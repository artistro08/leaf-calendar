using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636) helpers for the Google sign-in flow.
/// </summary>
/// <remarks>
/// A fresh verifier and state are created for every sign-in. The verifier proves to Google that the
/// app redeeming the code is the one that started sign-in; the state rejects replies Leaf never asked for.
/// </remarks>
public static class Pkce
{
    /// <summary>Creates a 43-character, URL-safe, high-entropy code verifier.</summary>
    public static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Creates the S256 code challenge for <paramref name="verifier"/>.</summary>
    public static string CreateChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Creates a random state value used to reject replies Leaf did not start.</summary>
    public static string CreateState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Compares the expected and returned state in constant time.</summary>
    public static bool StateMatches(string expected, string? actual) =>
        actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
}
