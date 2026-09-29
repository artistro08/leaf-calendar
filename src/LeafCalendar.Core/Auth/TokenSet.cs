namespace LeafCalendar.Core.Auth;

/// <summary>
/// Tokens returned by Google's token endpoint. <see cref="RefreshToken"/> is null on most refreshes.
/// </summary>
public sealed record TokenSet(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string Scope)
{
    /// <summary>True when Google granted <paramref name="scope"/>.</summary>
    public bool HasScope(string scope) => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    /// <summary>Omits the tokens so logging a record can't leak them.</summary>
    public override string ToString() => $"TokenSet(expires {ExpiresAt:O})";
}
