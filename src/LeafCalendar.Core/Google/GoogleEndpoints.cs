namespace LeafCalendar.Core.Google;

/// <summary>
/// Where Leaf sends OAuth, Calendar, and People requests: Google in normal use, or a loopback fake Google
/// in UI tests (see <c>--fake-google</c> in <see cref="Hosting.LaunchOptions"/>).
/// </summary>
public sealed record GoogleEndpoints(Uri Authorization, Uri Token, Uri Revoke, Uri UserInfo, Uri CalendarApi, Uri PeopleApi)
{
    /// <summary>Real Google.</summary>
    public static GoogleEndpoints Default { get; } = new(
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        new Uri("https://oauth2.googleapis.com/revoke"),
        new Uri("https://openidconnect.googleapis.com/v1/userinfo"),
        new Uri("https://www.googleapis.com/calendar/v3/"),
        new Uri("https://people.googleapis.com/v1/"));

    /// <summary>A fake Google rooted at <paramref name="root"/> (which ends with <c>/</c>).</summary>
    public static GoogleEndpoints ForFake(Uri root) => new(
        new Uri(root, "auth"),
        new Uri(root, "token"),
        new Uri(root, "revoke"),
        new Uri(root, "userinfo"),
        new Uri(root, "calendar/v3/"),
        new Uri(root, "people/v1/"));
}
