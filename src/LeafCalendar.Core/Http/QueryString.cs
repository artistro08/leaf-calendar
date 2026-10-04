namespace LeafCalendar.Core.Http;

/// <summary>
/// Parses URL query strings such as the OAuth redirect <c>?code=...&amp;state=...</c>.
/// </summary>
public static class QueryString
{
    /// <summary>
    /// Parses <paramref name="query"/> into a dictionary.
    /// </summary>
    /// <remarks>
    /// The leading <c>?</c> is optional, <c>+</c> decodes to a space, keys without a value map to an
    /// empty string, and when a key repeats the first value wins so a later duplicate can't override it.
    /// </remarks>
    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? "" : Decode(pair[(separator + 1)..]);

            result.TryAdd(key, value);
        }

        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
