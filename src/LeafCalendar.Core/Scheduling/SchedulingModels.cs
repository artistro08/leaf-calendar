namespace LeafCalendar.Core.Scheduling;

/// <summary>A scheduling link (Cal.com calls these event types): what it's called, how long a booking is, and where it books.</summary>
/// <param name="ProviderId">The provider it belongs to (<see cref="ISchedulingProvider.Id"/>).</param>
/// <param name="Id">The provider's ID for it.</param>
/// <param name="Name">Its name, cleaned and capped.</param>
/// <param name="Minutes">Length of a booking.</param>
/// <param name="BookingUrl">Its booking page (always https and approved by <see cref="Events.LinkSafety.CanLaunch"/>).</param>
/// <param name="AllowsGuests">Whether people besides the booker can be added (Cal.com: <c>disableGuests</c> is false).</param>
public sealed record SchedulingLink(string ProviderId, string Id, string Name, int Minutes, Uri BookingUrl, bool AllowsGuests);

/// <summary>A booking someone made on a link.</summary>
/// <param name="Id">The provider's ID for it.</param>
/// <param name="Start">When the booked time starts.</param>
/// <param name="Created">When it was booked.</param>
/// <param name="AttendeeName">The booker's name, cleaned and capped; empty when the provider gave none.</param>
public sealed record SchedulingBooking(string Id, DateTimeOffset Start, DateTimeOffset Created, string AttendeeName);

/// <summary>The provider refused the saved key (it was revoked or is wrong). The provider needs reconnecting.</summary>
public sealed class SchedulingAuthException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingAuthException()
    {
    }

    /// <summary>Creates the exception with a message (never the key).</summary>
    public SchedulingAuthException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingAuthException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The provider couldn't be reached or answered with an error that may pass (offline, 5xx, 429, a malformed answer).</summary>
public sealed class SchedulingUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SchedulingUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception for a rate limit, with how long the provider asked to wait.</summary>
    public SchedulingUnavailableException(string message, TimeSpan? retryAfter)
        : base(message) => RetryAfter = retryAfter;

    /// <summary>How long the provider asked Leaf to wait (a 429's <c>Retry-After</c>), or null.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The time asked for was booked or closed in the meantime.</summary>
public sealed class SchedulingSlotTakenException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingSlotTakenException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SchedulingSlotTakenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingSlotTakenException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
