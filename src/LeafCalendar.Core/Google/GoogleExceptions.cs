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

/// <summary>Google returned 412: its copy of the event changed since the ETag Leaf sent (a conflict).</summary>
public sealed class PreconditionFailedException() : Exception("Google's copy of the event changed since Leaf last saw it.");

/// <summary>Google returned 404 or 410 for an event: it no longer exists there.</summary>
public sealed class EventGoneException() : Exception("The event no longer exists on Google.");

/// <summary>Google returned 409 on insert: an event with Leaf's client-generated ID already exists (an earlier try got through).</summary>
public sealed class DuplicateEventException() : Exception("An event with this ID already exists on Google.");
