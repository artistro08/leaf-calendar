namespace LeafCalendar.Core.Api;

/// <summary>
/// The local API's error codes: JSON-RPC 2.0's numbers for protocol problems, and Leaf's own short codes (sent as
/// <c>data.code</c> under <see cref="ServerError"/>) for everything else (local API spec 3.4).
/// </summary>
public static class ApiErrors
{
    /// <summary>The line isn't JSON.</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON isn't a JSON-RPC 2.0 request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>No method has that name.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>The params are missing, of the wrong type, or out of range.</summary>
    public const int InvalidParams = -32602;

    /// <summary>Leaf failed unexpectedly.</summary>
    public const int InternalError = -32603;

    /// <summary>A Leaf error; <c>data.code</c> says which.</summary>
    public const int ServerError = -32000;

    /// <summary>You chose Don't allow, the approval timed out, or the app was removed.</summary>
    public const string NotApproved = "not_approved";

    /// <summary>No event, calendar or link with that ID.</summary>
    public const string NotFound = "not_found";

    /// <summary>You can't change that event or calendar.</summary>
    public const string ReadOnly = "read_only";

    /// <summary>No scheduling provider is signed in.</summary>
    public const string NotConnected = "not_connected";

    /// <summary>Nothing to join.</summary>
    public const string NoMeeting = "no_meeting";

    /// <summary>No Google account is signed in.</summary>
    public const string NotSignedIn = "not_signed_in";

    /// <summary>The scheduling provider couldn't be reached.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>A protocol problem, answered with a JSON-RPC error code (<see cref="ApiErrors"/>); its message is sent to the client.</summary>
public sealed class ApiProtocolException : Exception
{
    /// <summary>Creates the exception (an invalid request).</summary>
    public ApiProtocolException()
    {
    }

    /// <summary>Creates the exception (an invalid request) with a message for the client.</summary>
    public ApiProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception (an invalid request) with a message and its cause.</summary>
    public ApiProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception with a JSON-RPC code and a message for the client.</summary>
    public ApiProtocolException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; } = ApiErrors.InvalidRequest;
}

/// <summary>A Leaf error, answered as <see cref="ApiErrors.ServerError"/> with <see cref="Code"/> as <c>data.code</c>; its message is sent to the client.</summary>
public sealed class ApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApiException()
    {
    }

    /// <summary>Creates the exception with a message for the client.</summary>
    public ApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception with a short code, such as <see cref="ApiErrors.NotFound"/>, and a message for the client.</summary>
    public ApiException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The short error code.</summary>
    public string Code { get; } = "";
}
