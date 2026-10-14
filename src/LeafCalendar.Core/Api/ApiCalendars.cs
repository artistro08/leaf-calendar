using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Api;

/// <summary><c>leaf.hello</c> and <c>calendars.list</c> (local API spec 4).</summary>
internal static class ApiCalendars
{
    public static JsonElement Hello(ApiContext context, HelloParams _) =>
        ApiParams.Write(new HelloResult(context.LeafVersion, ApiDispatcher.ApiVersion, context.Profile), ApiJsonContext.Default.HelloResult);

    // The calendars shown in Leaf Calendar, in its order, with where new events go marked
    public static JsonElement List(ApiContext context, NoParams _)
    {
        using var conn = context.Database.Open();
        var emails = AccountStore.GetAll(conn).ToDictionary(a => a.Id, a => a.Email);
        var calendars = CalendarStore.GetAll(conn);
        var settings = context.Host.Settings;
        var home = DefaultCalendar.Pick(calendars, emails.Keys.ToHashSet(), settings.DefaultCalendar, mainAccountId: settings.MainAccountId);

        List<CalendarResult> result = [.. calendars
            .Where(c => c.IsVisible && emails.ContainsKey(c.AccountId))
            .Select(c => new CalendarResult(
                ApiIds.Calendar(c.AccountId, c.Id),
                emails[c.AccountId],
                c.Summary,
                c.DisplayColor,
                c.AccessRole is "owner" or "writer",
                home is not null && home.AccountId == c.AccountId && home.Id == c.Id))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListCalendarResult);
    }
}
