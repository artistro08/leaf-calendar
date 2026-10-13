using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Scheduling;

/// <summary>Raised when a booking lands inside a saved time's group.</summary>
/// <param name="booking">The booking that was found.</param>
/// <param name="providerName">The provider's display name, such as "Cal.com".</param>
public sealed class BookingFoundEventArgs(SchedulingBooking booking, string providerName) : EventArgs
{
    /// <summary>The booking that was found.</summary>
    public SchedulingBooking Booking { get; } = booking;

    /// <summary>The provider's display name.</summary>
    public string ProviderName { get; } = providerName;
}

/// <summary>
/// After each Google sync pass, asks the scheduling provider whether anyone booked a saved time, and closes the groups
/// the bookings landed in.
/// </summary>
/// <remarks>
/// With no saved group that has a provider it makes no network call at all. A refused key marks the provider as needing
/// reconnecting; a rate limit starts a back-off; any other failure is logged (type name only) and tried again next pass.
/// </remarks>
/// <param name="hub">The connected providers.</param>
/// <param name="database">The database holding the saved groups.</param>
/// <param name="log">The app log.</param>
/// <param name="time">The clock for reading the groups.</param>
public sealed class BookingWatcher(SchedulingHub hub, LeafDatabase database, AppLog log, TimeProvider time)
{
    /// <summary>Raised once per found booking, on the sync thread.</summary>
    public event EventHandler<BookingFoundEventArgs>? BookingFound;

    /// <summary>Raised once per check, after matched groups were deleted.</summary>
    public event EventHandler? GroupsChanged;

    /// <summary>
    /// Checks every provider link that has saved groups and closes the groups bookings landed in. Never throws, except
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is canceled.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            List<ShareGroup> groups;
            using (var conn = database.Open())
            {
                groups = [.. ShareGroupStore.GetAll(conn, time.GetUtcNow()).Where(g => g.ProviderId.Length > 0)];
            }

            var changed = false;
            foreach (var set in groups.GroupBy(g => (g.ProviderId, g.LinkId)))
            {
                var (providerId, linkId) = set.Key;
                if (hub.Get(providerId) is not ISchedulingProvider provider || provider is not IBookingSource source || hub.IsWaiting(providerId))
                {
                    continue;
                }

                IReadOnlyList<SchedulingBooking> bookings;
                try
                {
                    bookings = await source.GetBookingsAsync(linkId, set.Min(g => g.CreatedUtc), ct);
                }
                catch (SchedulingAuthException)
                {
                    hub.MarkNeedsReconnect(providerId);
                    continue;
                }
                catch (SchedulingUnavailableException ex)
                {
                    log.Info("scheduling.booking.unavailable", $"provider={providerId} error={ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
                    if (ex.RetryAfter is { } retryAfter)
                    {
                        hub.WaitFor(providerId, retryAfter);
                    }

                    continue;
                }

                foreach (var result in BookingMatch.Find([.. set], providerId, linkId, bookings))
                {
                    using (var conn = database.Open())
                    {
                        foreach (var id in result.GroupIds)
                        {
                            ShareGroupStore.Delete(conn, id);
                        }
                    }

                    changed = true;
                    log.Info("scheduling.booking.found", $"provider={providerId} groups={result.GroupIds.Count}");
                    BookingFound?.Invoke(this, new BookingFoundEventArgs(result.Booking, provider.Name));
                }
            }

            if (changed)
            {
                GroupsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Info("scheduling.booking.failed", $"error={ex.GetType().Name}");
        }
    }
}
