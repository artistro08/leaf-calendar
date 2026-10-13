using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// A scheduling provider (spec 3.1). Every provider implements this; each action it supports is one more interface
/// (<see cref="ILinkSource"/>, <see cref="IOpenTimesSource"/>, <see cref="ITimeLinkBuilder"/>, <see cref="IBlockLinkBuilder"/>,
/// <see cref="IBookingSource"/>, <see cref="IBookingCreator"/>), and Leaf shows only the UI for the actions it finds.
/// </summary>
public interface ISchedulingProvider
{
    /// <summary>A short lowercase ID, stable forever (stored in settings, secrets and saved groups), such as <c>calcom</c>.</summary>
    string Id { get; }

    /// <summary>The name shown in the UI, such as "Cal.com".</summary>
    string Name { get; }
}

/// <summary>Lists your scheduling links.</summary>
public interface ILinkSource
{
    /// <summary>Your links, in the provider's order, unsafe or incomplete ones left out.</summary>
    /// <exception cref="SchedulingAuthException">The provider refused the sign-in (its access token or the token refresh).</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed.</exception>
    Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct);
}

/// <summary>Reads a link's bookable times.</summary>
public interface IOpenTimesSource
{
    /// <summary>Each bookable start and end for the link between two instants, in order.</summary>
    /// <exception cref="SchedulingAuthException">The provider refused the sign-in (its access token or the token refresh).</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed.</exception>
    Task<IReadOnlyList<BusyRange>> GetOpenTimesAsync(string linkId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct);
}

/// <summary>Builds a link that opens one exact time, ready to book.</summary>
public interface ITimeLinkBuilder
{
    /// <summary>
    /// The booking page on <paramref name="startUtc"/>, with the first guest as the booker and, when the link allows them,
    /// the rest as guests. <paramref name="zone"/> is the share's zone (it picks the day the page opens on).
    /// </summary>
    Uri TimeLink(SchedulingLink link, DateTimeOffset startUtc, TimeZoneInfo zone, IReadOnlyList<Contact> guests);
}

/// <summary>Builds a link that opens a day, for the guest to pick a time in it.</summary>
public interface IBlockLinkBuilder
{
    /// <summary>The booking page on <paramref name="day"/>, with the guests filled in as for <see cref="ITimeLinkBuilder"/>.</summary>
    Uri BlockLink(SchedulingLink link, DateOnly day, IReadOnlyList<Contact> guests);
}

/// <summary>Reads the bookings people made on a link.</summary>
public interface IBookingSource
{
    /// <summary>Upcoming bookings on the link made after <paramref name="createdAfterUtc"/>.</summary>
    /// <exception cref="SchedulingAuthException">The provider refused the sign-in (its access token or the token refresh).</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed.</exception>
    Task<IReadOnlyList<SchedulingBooking>> GetBookingsAsync(string linkId, DateTimeOffset createdAfterUtc, CancellationToken ct);
}

/// <summary>Books a time on a link for your guests (Approve…).</summary>
public interface IBookingCreator
{
    /// <summary>
    /// Books <paramref name="startUtc"/> with the first guest as the booker and, when the link allows them, the rest as
    /// guests. The provider sends its usual confirmation emails.
    /// </summary>
    /// <exception cref="SchedulingSlotTakenException">The time isn't open anymore.</exception>
    /// <exception cref="SchedulingAuthException">The provider refused the sign-in (its access token or the token refresh).</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed; nothing was booked as far as Leaf knows.</exception>
    Task<SchedulingBooking> BookAsync(SchedulingLink link, DateTimeOffset startUtc, IReadOnlyList<Contact> guests, TimeZoneInfo zone, CancellationToken ct);
}
