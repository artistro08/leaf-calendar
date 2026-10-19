namespace LeafCalendar.Core.Scheduling;

// Cal.com v2 answers: { "status": "...", "data": ... }. Every field is nullable, since nothing from Cal.com is trusted
internal sealed record CalComEnvelope<T>(string? Status, T? Data);

internal sealed record CalComEventType(long? Id, string? Title, int? LengthInMinutes, string? BookingUrl, bool? DisableGuests);

internal sealed record CalComSlot(DateTimeOffset? Start, DateTimeOffset? End);

internal sealed record CalComAttendee(string? Name, string? Email);

internal sealed record CalComBooking(string? Uid, DateTimeOffset? Start, DateTimeOffset? CreatedAt, long? EventTypeId, List<CalComAttendee>? Attendees);

internal sealed record CalComBookingRequest(string Start, long EventTypeId, CalComBookingAttendee Attendee, List<string>? Guests);

internal sealed record CalComBookingAttendee(string Name, string Email, string TimeZone);
