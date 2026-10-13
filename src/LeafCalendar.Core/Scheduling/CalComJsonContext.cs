using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Scheduling;

/// <summary>Source-generated JSON for Cal.com's v2 API (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CalComEnvelope<List<CalComEventType>>))]
[JsonSerializable(typeof(CalComEnvelope<Dictionary<string, List<CalComSlot>>>))]
[JsonSerializable(typeof(CalComEnvelope<List<CalComBooking>>))]
[JsonSerializable(typeof(CalComEnvelope<CalComBooking>))]
[JsonSerializable(typeof(CalComBookingRequest))]
internal sealed partial class CalComJsonContext : JsonSerializerContext;
