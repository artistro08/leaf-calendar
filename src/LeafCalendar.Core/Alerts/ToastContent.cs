using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Alerts;

/// <summary>
/// A notification ready for Windows: its tag and group (for replacing and withdrawing it) and its XML. Windows limits
/// both to 64 characters, so the tag must be a hash or ID, never event content.
/// </summary>
public sealed record ToastMessage(string Tag, string Group, string Xml);

/// <summary>
/// Builds every notification Leaf shows (spec 8.4) as Windows toast XML.
/// </summary>
/// <remarks>
/// <para>
/// Event text comes from anyone who can send an invite, so it only ever enters as <see cref="XElement"/> text (always
/// escaped), after <see cref="DisplayText.Clean"/> removed control, bidi, zero-width, and non-XML characters and
/// clipped it to 200 characters. Nothing from an event can add an element, a button, or an attribute, and nothing from
/// an event goes into a tag or into activation arguments (those carry IDs only).
/// </para>
/// <para>
/// Reminders and "Join now" that <see cref="StaysOnScreen"/> picks use <c>scenario="reminder"</c>, like other calendar
/// apps, so they stay on screen until acted on and show through Do Not Disturb when Windows allows reminders; the others
/// are plain notifications that go to Notification Center after a few seconds. Reminders carry Join (when there's a link),
/// Windows' own Snooze with a 5/10/15/30-minute choice, and Dismiss; "Join now" a background-activated Join button
/// (Windows requires a button for that scenario) and Dismiss, and no Snooze. Invites carry Yes / No / Maybe. Clicking a
/// notification's body opens what it's about. With sound off, the toast is silent.
/// </para>
/// </remarks>
public static class ToastContent
{
    /// <summary>Group of reminder notifications.</summary>
    public const string ReminderGroup = "reminders";

    /// <summary>Group of "Join now" notifications.</summary>
    public const string JoinGroup = "join";

    /// <summary>Group of invitations.</summary>
    public const string InviteGroup = "invites";

    /// <summary>Group of the conflict notification.</summary>
    public const string ConflictGroup = "conflicts";

    /// <summary>Group of "Sign in again" notifications.</summary>
    public const string SignInGroup = "signin";

    /// <summary>Group of short notices ("No meeting to join").</summary>
    public const string NoticeGroup = "notices";

    /// <summary>The single conflict notification's tag (a newer count replaces it).</summary>
    public const string ConflictTag = "conflicts";

    /// <summary>ID of the snooze-time choice that Windows' Snooze button reads.</summary>
    public const string SnoozeInputId = "snoozeTime";

    private const int MaxText = 200;

    // Separator Between Inline Facts
    private const string Dot = " \u00B7 ";

    /// <summary>"Today &#x00B7; 2 PM &#x2013; 3 PM", "Tomorrow &#x00B7; All day", or "Saturday, October 3 &#x00B7; 9 AM &#x2013; 10 AM".</summary>
    public static string When(CalendarOccurrence o, TimeZoneInfo zone, bool use24Hour, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day = o.IsAllDay ? o.AllDayStart : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start, zone).DateTime);
        var date = TrayAgenda.DayHeader(day, today);
        return o.IsAllDay ? date + Dot + "All day" : date + Dot + TimeLabels.Range(o.Start, o.End, zone, use24Hour);
    }

    /// <summary>
    /// True when an event's reminder or "Join now" stays on screen: the persistent notification setting is on, and the
    /// event has a meeting link (<paramref name="meetingLink"/>) with <see cref="LeafSettings.PersistForMeetings"/>, or
    /// nobody but you on it with <see cref="LeafSettings.PersistWhenAlone"/>.
    /// </summary>
    public static bool StaysOnScreen(LeafSettings settings, EventDetails details, Uri? meetingLink)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(details);

        return settings.JoinNowNotifications
            && ((settings.PersistForMeetings && meetingLink is not null) || (settings.PersistWhenAlone && !details.HasOtherGuests));
    }

    /// <summary>A reminder: title, time, location; Join (with a link), Snooze, Dismiss. On screen until acted on when <paramref name="persistent"/>.</summary>
    public static ToastMessage Reminder(Alert alert, EventDetails details, string when, string profile, bool sound, bool persistent = true)
    {
        var o = alert.Occurrence;
        var actions = new List<XElement> { SnoozeInput() };
        if (alert.MeetingLink is not null)
        {
            actions.Add(Button("Join", ToastArgs.For(ToastAction.Join, profile, o)));
        }

        actions.Add(SystemButton("snooze", SnoozeInputId));
        actions.Add(SystemButton("dismiss"));

        return Build(alert.Tag, ReminderGroup, ToastArgs.For(ToastAction.Open, profile, o), persistent ? "reminder" : null, [details.Title, when, details.Location], actions, sound);
    }

    /// <summary>"Join now": title and "Starting now &#x00B7; time"; Join and Dismiss. On screen until acted on when <paramref name="persistent"/>.</summary>
    public static ToastMessage JoinNow(Alert alert, EventDetails details, string when, string profile, bool sound, bool persistent = true)
    {
        var o = alert.Occurrence;
        return Build(
            alert.Tag,
            JoinGroup,
            ToastArgs.For(ToastAction.Open, profile, o),
            persistent ? "reminder" : null,
            [details.Title, "Starting now" + Dot + when],
            [Button("Join", ToastArgs.For(ToastAction.Join, profile, o)), SystemButton("dismiss")],
            sound);
    }

    /// <summary>A new or updated invitation: title, time, who it's from; Yes, No, Maybe.</summary>
    public static ToastMessage Invite(CalendarOccurrence occurrence, EventDetails details, bool isUpdate, string tag, string when, string profile, bool sound)
    {
        var kind = isUpdate ? "Updated invitation" : "New invitation";
        var from = details.OrganizerEmail is { Length: > 0 } organizer ? $"{kind} from {organizer}" : kind;
        return Build(
            tag,
            InviteGroup,
            ToastArgs.For(ToastAction.Open, profile, occurrence),
            null,
            [details.Title, when, from],
            [
                Button("Yes", ToastArgs.For(ToastAction.Accept, profile, occurrence)),
                Button("No", ToastArgs.For(ToastAction.Decline, profile, occurrence)),
                Button("Maybe", ToastArgs.For(ToastAction.Maybe, profile, occurrence)),
            ],
            sound);
    }

    /// <summary>"1 change needs your review" (spec 5.5); clicking opens the conflict dialog.</summary>
    public static ToastMessage Conflicts(int count, string profile, bool sound)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var title = count == 1 ? "1 change needs your review" : string.Create(CultureInfo.InvariantCulture, $"{count} changes need your review");
        return Build(
            ConflictTag,
            ConflictGroup,
            new ToastArgs(ToastAction.ReviewConflicts, profile),
            null,
            [title, "Google's copy changed while yours waited to sync. Choose which one to keep."],
            [],
            sound);
    }

    /// <summary>"Sign in again" for an account whose sign-in stopped working; clicking opens Settings &#x203A; Accounts.</summary>
    public static ToastMessage SignIn(string accountId, string email, string profile, bool sound) =>
        Build(
            "signin-" + Alert.TagFor(accountId),
            SignInGroup,
            new ToastArgs(ToastAction.SignIn, profile, accountId),
            null,
            ["Sign in again", $"Leaf can't sync {email} anymore. Sign in again in Settings to keep it up to date."],
            [],
            sound);

    /// <summary>The join shortcut found nothing to join (spec 8.5).</summary>
    public static ToastMessage NoMeeting(bool sound) =>
        Build("no-meeting", NoticeGroup, null, null, ["No meeting to join", "Nothing with a meeting link starts in the next 10 minutes."], [], sound);

    // Every Line Goes Through DisplayText.Clean And Enters As Escaped Element Text; The First Line Is The Headline, So It Never Drops Out
    private static ToastMessage Build(string tag, string group, ToastArgs? launch, string? scenario, IEnumerable<string?> lines, List<XElement> actions, bool sound)
    {
        var toast = new XElement(
            "toast",
            launch is null ? null : new XAttribute("launch", launch.Encode()),
            scenario is null ? null : new XAttribute("scenario", scenario),
            new XElement(
                "visual",
                new XElement(
                    "binding",
                    new XAttribute("template", "ToastGeneric"),
                    lines.Select((l, i) => (Text: DisplayText.Clean(l, MaxText), Index: i))
                        .Where(l => l.Text.Length > 0 || l.Index == 0)
                        .Select(l => new XElement("text", l.Text.Length > 0 ? l.Text : EventDetailsParser.NoTitle)))));

        if (actions.Count > 0)
        {
            toast.Add(new XElement("actions", actions));
        }

        if (!sound)
        {
            toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        }

        // Tag And Group Are Limited To 64 Characters By Windows
        Debug.Assert(tag.Length <= 64 && group.Length <= 64, "Toast tag and group must be 64 characters or fewer.");

        return new ToastMessage(tag, group, toast.ToString(SaveOptions.DisableFormatting));
    }

    // Windows' Own Snooze Choices; The Selection IDs Are Minutes
    private static XElement SnoozeInput() => new(
        "input",
        new XAttribute("id", SnoozeInputId),
        new XAttribute("type", "selection"),
        new XAttribute("defaultInput", "5"),
        Choice("5", "5 minutes"),
        Choice("10", "10 minutes"),
        Choice("15", "15 minutes"),
        Choice("30", "30 minutes"));

    private static XElement Choice(string minutes, string content) =>
        new("selection", new XAttribute("id", minutes), new XAttribute("content", content));

    // Background Activation: Leaf Acts Without Coming To The Front
    private static XElement Button(string content, ToastArgs args) => new(
        "action",
        new XAttribute("content", content),
        new XAttribute("arguments", args.Encode()),
        new XAttribute("activationType", "background"));

    // Windows' Snooze And Dismiss (Empty Content = Windows' Own Localized Label)
    private static XElement SystemButton(string arguments, string? inputId = null) => new(
        "action",
        new XAttribute("content", ""),
        new XAttribute("arguments", arguments),
        new XAttribute("activationType", "system"),
        inputId is null ? null : new XAttribute("hint-inputId", inputId));
}
