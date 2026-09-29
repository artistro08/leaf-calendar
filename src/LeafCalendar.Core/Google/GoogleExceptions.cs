using System.Net;

namespace LeafCalendar.Core.Google;

/// <summary>
/// A Google API call failed. The message never contains response bodies, tokens, or event data.
/// </summary>
public sealed class GoogleApiException(HttpStatusCode status, string? reason, string message) : Exception(message)
{
    /// <summary>HTTP status Google returned.</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>Google's machine-readable reason, e.g. <c>forbidden</c>.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>Google returned 410: the stored sync token is too old and a full sync is required.</summary>
public sealed class SyncTokenExpiredException() : Exception("Google reported the sync token is no longer valid.");
