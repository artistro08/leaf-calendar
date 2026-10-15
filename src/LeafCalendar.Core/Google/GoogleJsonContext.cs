using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Google;

/// <summary>Source-generated JSON metadata for every Google type Leaf Calendar reads or writes (AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(GoogleUserInfo))]
[JsonSerializable(typeof(ApiErrorEnvelope))]
[JsonSerializable(typeof(CalendarListPage))]
[JsonSerializable(typeof(EventsPage))]
[JsonSerializable(typeof(GoogleEvent))]
[JsonSerializable(typeof(List<ReminderOverride>))]
[JsonSerializable(typeof(PeopleSearchResponse))]
[JsonSerializable(typeof(DirectorySearchResponse))]
[JsonSerializable(typeof(FreeBusyRequest))]
[JsonSerializable(typeof(FreeBusyResponse))]
[JsonSerializable(typeof(PlacesAutocompleteRequest))]
[JsonSerializable(typeof(PlacesAutocompleteResponse))]
[JsonSerializable(typeof(PlaceTimeZoneResponse))]
internal sealed partial class GoogleJsonContext : JsonSerializerContext
{
}
