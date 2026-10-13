using System.Text.Json;
using LeafCalendar.Core.Alerts;

namespace LeafCalendar.Core.Api;

/// <summary>Methods that make Leaf do something on screen or in the browser: join a meeting, show a day, show an event (local API spec 4).</summary>
internal static class ApiActions
{
    // The join shortcut's pick (the next meeting in the upcoming lookahead), or one event's own link; Leaf opens it itself
    public static async Task<JsonElement> JoinAsync(ApiContext context, IdParams p)
    {
        JoinTarget? target;
        Uri link;
        using (var conn = context.Database.Open())
        {
            if (p.Id is null)
            {
                var now = context.Time.GetUtcNow();
                var ahead = TimeSpan.FromHours(context.Host.Settings.UpcomingHours);
                target = JoinPicker.PickNext(JoinPicker.Candidates(conn, now, context.Host.Zone, ahead), now, ahead);
            }
            else
            {
                var o = ApiEvents.Find(conn, context, p.Id);
                target = JoinPicker.MeetingLink(conn, o) is { } meeting ? new JoinTarget(o, meeting) : null;
            }

            if (target is null)
            {
                throw new ApiException(ApiErrors.NoMeeting, "No meeting to join.");
            }

            link = JoinPicker.JoinLink(conn, target);
        }

        var opened = await context.Host.OpenLinkAsync(link);
        return ApiParams.Write(new JoinResult(ApiIds.Event(target.Occurrence), opened), ApiJsonContext.Default.JoinResult);
    }

    public static JsonElement ShowDay(ApiContext context, DayParams p)
    {
        context.Host.ShowDay(ApiTime.ReadDate(p.Date) ?? throw ApiTime.Invalid("date must be yyyy-MM-dd."));
        return ApiParams.Write(new EmptyResult(), ApiJsonContext.Default.EmptyResult);
    }

    public static JsonElement ShowEvent(ApiContext context, IdParams p)
    {
        using (var conn = context.Database.Open())
        {
            context.Host.ShowEvent(ApiEvents.Find(conn, context, p.Id));
        }

        return ApiParams.Write(new EmptyResult(), ApiJsonContext.Default.EmptyResult);
    }
}
