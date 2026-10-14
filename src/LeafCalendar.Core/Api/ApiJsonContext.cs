using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LeafCalendar.Core.Api;

// =========================================================================
// PARAMS (unknown members are refused, so a typo is an error, not ignored)
// =========================================================================

/// <summary><c>leaf.hello</c>'s params: the app's name for the log, and (first line only) its icon and return link (<see cref="ApiHello"/>).</summary>
public sealed record HelloParams(string? Client = null, string? Icon = null, string? ReturnUrl = null);

/// <summary>Params of methods that take none (<c>calendars.list</c>, <c>scheduling.links</c>).</summary>
public sealed record NoParams;

/// <summary><c>events.list</c>'s params.</summary>
public sealed record RangeParams(string? Start = null, string? End = null, List<string>? CalendarIds = null);

/// <summary>Params naming one event (<c>events.get</c>, <c>app.showEvent</c>, <c>meetings.join</c>).</summary>
public sealed record IdParams(string? Id = null);

/// <summary><c>events.create</c>'s params.</summary>
public sealed record CreateParams(string? CalendarId = null, string? Title = null, string? Start = null, string? End = null, string? Location = null, string? Description = null, List<string>? Guests = null, bool? NotifyGuests = null, bool? Meet = null);

/// <summary><c>people.search</c>'s params.</summary>
public sealed record PeopleParams(string? Query = null);

/// <summary><c>events.move</c>'s params.</summary>
public sealed record MoveParams(string? Id = null, string? Start = null, string? End = null, string? Scope = null);

/// <summary><c>events.delete</c>'s params.</summary>
public sealed record DeleteParams(string? Id = null, string? Scope = null, bool? NotifyGuests = null);

/// <summary><c>app.showDay</c>'s params.</summary>
public sealed record DayParams(string? Date = null);

/// <summary><c>scheduling.openTimes</c>'s params.</summary>
public sealed record OpenTimesParams(string? LinkId = null, string? Start = null, string? End = null);

/// <summary>A guest for <c>scheduling.timeLink</c>.</summary>
public sealed record GuestParam(string? Name = null, string? Email = null);

/// <summary><c>scheduling.timeLink</c>'s params.</summary>
public sealed record TimeLinkParams(string? LinkId = null, string? Start = null, List<GuestParam>? Guests = null);

/// <summary><c>availability.free</c>'s params.</summary>
public sealed record FreeParams(string? Start = null, string? End = null, int? Minutes = null, string? LinkId = null);

/// <summary>One time in <c>shares.create</c>.</summary>
public sealed record TimeParam(string? Start = null, string? End = null);

/// <summary><c>shares.create</c>'s params.</summary>
public sealed record ShareCreateParams(List<TimeParam>? Times = null, string? Title = null, string? Message = null, List<GuestParam>? Guests = null, string? LinkId = null);

/// <summary><c>shares.book</c>'s params.</summary>
public sealed record ShareBookParams(string? Id = null, string? Start = null, string? End = null);

// =========================================================================
// RESULTS
// =========================================================================

/// <summary><c>leaf.hello</c>'s result.</summary>
public sealed record HelloResult(string LeafVersion, int ApiVersion, string Profile);

/// <summary>One calendar.</summary>
public sealed record CalendarResult(string Id, string Account, string Name, string Color, bool CanEdit, bool IsDefault);

/// <summary>One event instance.</summary>
public sealed record EventResult(string Id, string CalendarId, string Title, string Start, string End, bool AllDay, string? Location, string Response, string Color, string? MeetingLink);

/// <summary>A guest of an event.</summary>
public sealed record GuestResult(string? Name, string Email, string Response, bool Organizer);

/// <summary>One event instance with its description and guests (<c>events.get</c>).</summary>
public sealed record EventDetailResult(string Id, string CalendarId, string Title, string Start, string End, bool AllDay, string? Location, string Response, string Color, string? MeetingLink, string Description, List<GuestResult> Guests);

/// <summary>An event's ID after a write; null when Leaf Calendar can't name it (a series split by "following").</summary>
public sealed record IdResult(string? Id);

/// <summary><c>meetings.join</c>'s result.</summary>
public sealed record JoinResult(string Id, bool Opened);

/// <summary>An empty result (<c>{}</c>).</summary>
public sealed record EmptyResult;

/// <summary>One scheduling link.</summary>
public sealed record LinkResult(string Id, string Provider, string Name, int Minutes, bool AllowsGuests);

/// <summary>One bookable time.</summary>
public sealed record TimeRangeResult(string Start, string End);

/// <summary>A booking link.</summary>
public sealed record UrlResult(string Url);

/// <summary><c>shares.create</c>'s result: the saved times' ID and the text to paste (<see cref="Html"/> only with a link).</summary>
public sealed record ShareTextResult(string Id, string Text, string? Html);

/// <summary>A guest of saved times.</summary>
public sealed record ShareGuestResult(string? Name, string Email);

/// <summary>Someone <c>people.search</c> found.</summary>
public sealed record PersonResult(string? Name, string Email);

/// <summary>The link saved times are on.</summary>
public sealed record ShareLinkResult(string Id, int Minutes);

/// <summary>Saved times (<c>shares.list</c>).</summary>
public sealed record ShareResult(string Id, string Title, string Message, List<TimeRangeResult> Times, List<ShareGuestResult> Guests, ShareLinkResult? Link);

/// <summary>Source-generated JSON for the local API (Native AOT), camelCase, unknown members refused.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(NoParams))]
[JsonSerializable(typeof(RangeParams))]
[JsonSerializable(typeof(IdParams))]
[JsonSerializable(typeof(CreateParams))]
[JsonSerializable(typeof(MoveParams))]
[JsonSerializable(typeof(DayParams))]
[JsonSerializable(typeof(OpenTimesParams))]
[JsonSerializable(typeof(TimeLinkParams))]
[JsonSerializable(typeof(DeleteParams))]
[JsonSerializable(typeof(PeopleParams))]
[JsonSerializable(typeof(List<PersonResult>))]
[JsonSerializable(typeof(FreeParams))]
[JsonSerializable(typeof(ShareCreateParams))]
[JsonSerializable(typeof(ShareBookParams))]
[JsonSerializable(typeof(ShareTextResult))]
[JsonSerializable(typeof(List<ShareResult>))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(List<CalendarResult>))]
[JsonSerializable(typeof(List<EventResult>))]
[JsonSerializable(typeof(EventDetailResult))]
[JsonSerializable(typeof(IdResult))]
[JsonSerializable(typeof(JoinResult))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(List<LinkResult>))]
[JsonSerializable(typeof(List<TimeRangeResult>))]
[JsonSerializable(typeof(UrlResult))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>Reads params and writes results through <see cref="ApiJsonContext"/>.</summary>
internal static class ApiParams
{
    /// <summary>Reads params; any type mismatch or unknown member is <see cref="ApiErrors.InvalidParams"/>.</summary>
    public static T Read<T>(JsonElement element, JsonTypeInfo<T> info)
    {
        try
        {
            return element.Deserialize(info) ?? throw ApiTime.Invalid("Invalid params.");
        }
        catch (JsonException)
        {
            throw ApiTime.Invalid("Invalid params.");
        }
    }

    /// <summary>A result as a JSON element.</summary>
    public static JsonElement Write<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);
}
