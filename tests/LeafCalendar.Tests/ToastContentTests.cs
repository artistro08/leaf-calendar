using System.Xml.Linq;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class ToastContentTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
    static readonly Uri Meet = new("https://meet.google.com/abc-defg-hij");

    static CalendarOccurrence Occurrence(string title = "Design review", bool allDay = false) =>
        new("109876543210", "leaf.tester@gmail.com", "evt-meet", null, null, Start, Start.AddHours(1), allDay, title, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, true);

    static EventDetails Details(string title = "Design review", string? location = "Room 4", string? organizer = "boss@example.com") =>
        new(title, location, "", EventKind.Default, ResponseStatus.NeedsAction, null, Meet, false, 3, organizer);

    static Alert Reminder(Uri? link = null, string title = "Design review") =>
        new(AlertKind.Reminder, Occurrence(title), Start.AddMinutes(-10), 10, link);

    static XElement Parse(ToastMessage message) => XDocument.Parse(message.Xml).Root!;

    static List<string> Texts(XElement toast) => [.. toast.Descendants("text").Select(t => t.Value)];

    static List<XElement> Actions(XElement toast) => [.. toast.Descendants("action")];

    // Every element and attribute name, in order: what event text must never change
    static List<string> Shape(XElement toast) =>
        [.. toast.DescendantsAndSelf().Select(e => e.Name + "(" + string.Join(",", e.Attributes().Select(a => a.Name)) + ")")];

    [Fact]
    public void Reminder_HostileTitle_StaysPlainText()
    {
        var hostile = "Standup</text><action content=\"Pwn\" arguments=\"x\"/><text>&\"'\r\n\u202E\u200B\u2066" + new string('x', 5000);

        var message = ToastContent.Reminder(Reminder(Meet, hostile), Details(hostile, location: "<b>Room</b>\u202E"), "Today \u00B7 2 PM \u2013 3 PM", "default", sound: true);
        var toast   = Parse(message);
        var benign  = Parse(ToastContent.Reminder(Reminder(Meet), Details(), "Today \u00B7 2 PM \u2013 3 PM", "default", sound: true));

        var texts = Texts(toast);
        Assert.StartsWith("Standup</text><action", texts[0], StringComparison.Ordinal);
        Assert.True(texts[0].Length <= 200);
        Assert.DoesNotContain('\n', texts[0]);
        Assert.DoesNotContain('\u202E', texts[0]);
        Assert.DoesNotContain('\u200B', texts[0]);
        Assert.Equal("<b>Room</b>", texts[2]);
        Assert.Equal(3, Actions(toast).Count);
        Assert.DoesNotContain(Actions(toast), a => (string?)a.Attribute("content") == "Pwn");
        Assert.Equal(Shape(benign), Shape(toast));
        Assert.DoesNotContain("Standup", message.Tag, StringComparison.Ordinal);
        Assert.All(Actions(toast), a => Assert.DoesNotContain("Standup", (string?)a.Attribute("arguments") ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public void Reminder_BrokenCharacters_StillValidXml()
    {
        var broken = "A\uD800B\uDC00C\uFFFE\uFFFFD\u0000E\U0001F600";

        var toast = Parse(ToastContent.Reminder(Reminder(Meet, broken), Details(broken, location: broken), "Today", "default", sound: true));

        Assert.Equal("ABCD E\U0001F600", Texts(toast)[0]);
    }

    [Fact]
    public void Reminder_WithLink_HasJoinSnoozeAndDismiss()
    {
        var toast   = Parse(ToastContent.Reminder(Reminder(Meet), Details(), "Today \u00B7 2 PM \u2013 3 PM", "default", sound: true));
        var actions = Actions(toast);

        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal(["Design review", "Today \u00B7 2 PM \u2013 3 PM", "Room 4"], Texts(toast));
        Assert.Equal("Join", (string?)actions[0].Attribute("content"));
        Assert.Equal("background", (string?)actions[0].Attribute("activationType"));
        Assert.Equal(ToastAction.Join, ToastArgs.Parse((string?)actions[0].Attribute("arguments"))!.Action);
        Assert.Equal(["snooze", "dismiss"], actions.Skip(1).Select(a => (string?)a.Attribute("arguments")));
        Assert.All(actions.Skip(1), a => Assert.Equal("system", (string?)a.Attribute("activationType")));
        Assert.Equal(ToastContent.SnoozeInputId, (string?)actions[1].Attribute("hint-inputId"));
        Assert.Equal(["5", "10", "15", "30"], toast.Descendants("selection").Select(s => (string?)s.Attribute("id")));
        Assert.Empty(toast.Descendants("audio"));
    }

    [Fact]
    public void Reminder_WithoutLink_NoJoin()
    {
        var toast = Parse(ToastContent.Reminder(Reminder(), Details(), "Today", "default", sound: true));

        Assert.DoesNotContain(Actions(toast), a => (string?)a.Attribute("content") == "Join");
    }

    [Fact]
    public void Reminder_Click_OpensTheEvent()
    {
        var message = ToastContent.Reminder(Reminder(Meet), Details(), "Today", "work", sound: true);
        var launch  = ToastArgs.Parse((string?)Parse(message).Attribute("launch"))!;

        Assert.Equal(new ToastArgs(ToastAction.Open, "work", "109876543210", "leaf.tester@gmail.com", "evt-meet", Start), launch);
        Assert.Equal(ToastContent.ReminderGroup, message.Group);
        Assert.Equal(Reminder(Meet).Tag, message.Tag);
    }

    [Fact]
    public void Reminder_SoundOff_IsSilent()
    {
        var toast = Parse(ToastContent.Reminder(Reminder(), Details(), "Today", "default", sound: false));

        Assert.Equal("true", (string?)toast.Element("audio")!.Attribute("silent"));
    }

    [Fact]
    public void JoinNow_ReminderScenario_JoinAndDismissOnly()
    {
        var alert   = new Alert(AlertKind.JoinNow, Occurrence(), Start, 0, Meet);
        var message = ToastContent.JoinNow(alert, Details(), "Today \u00B7 2 PM \u2013 3 PM", "default", sound: true);
        var toast   = Parse(message);
        var actions = Actions(toast);

        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
        Assert.Equal(["Design review", "Starting now \u00B7 Today \u00B7 2 PM \u2013 3 PM"], Texts(toast));
        Assert.Equal(2, actions.Count);
        Assert.Equal("Join", (string?)actions[0].Attribute("content"));
        Assert.Equal("background", (string?)actions[0].Attribute("activationType"));
        Assert.Equal("dismiss", (string?)actions[1].Attribute("arguments"));
        Assert.Empty(toast.Descendants("input"));
        Assert.Equal(ToastContent.JoinGroup, message.Group);
    }

    [Fact]
    public void Invite_Update_HasYesNoMaybe()
    {
        var toast   = Parse(ToastContent.Invite(Occurrence(), Details(), isUpdate: true, "TAG", "Today \u00B7 2 PM \u2013 3 PM", "default", sound: true));
        var actions = Actions(toast);

        Assert.Equal("Updated invitation from boss@example.com", Texts(toast)[2]);
        Assert.Equal(["Yes", "No", "Maybe"], actions.Select(a => (string?)a.Attribute("content")));
        Assert.Equal([ToastAction.Accept, ToastAction.Decline, ToastAction.Maybe], actions.Select(a => ToastArgs.Parse((string?)a.Attribute("arguments"))!.Action));
        Assert.All(actions, a => Assert.Equal("background", (string?)a.Attribute("activationType")));
    }

    [Fact]
    public void Invite_New_NoOrganizer()
    {
        var toast = Parse(ToastContent.Invite(Occurrence(), Details(organizer: null), isUpdate: false, "TAG", "Today", "default", sound: true));

        Assert.Equal("New invitation", Texts(toast)[2]);
    }

    [Theory]
    [InlineData(1, "1 change needs your review")]
    [InlineData(3, "3 changes need your review")]
    public void Conflicts_CountAndReviewClick(int count, string title)
    {
        var message = ToastContent.Conflicts(count, "default", sound: true);
        var toast   = Parse(message);

        Assert.Equal(title, Texts(toast)[0]);
        Assert.Equal(ToastAction.ReviewConflicts, ToastArgs.Parse((string?)toast.Attribute("launch"))!.Action);
        Assert.Equal(ToastContent.ConflictTag, message.Tag);
    }

    [Fact]
    public void SignIn_NamesTheAccountAndOpensSettings()
    {
        var message = ToastContent.SignIn("109876543210", "leaf.tester@gmail.com", "default", sound: true);
        var toast   = Parse(message);

        Assert.Equal("Sign in again", Texts(toast)[0]);
        Assert.Contains("leaf.tester@gmail.com", Texts(toast)[1], StringComparison.Ordinal);
        Assert.Equal(new ToastArgs(ToastAction.SignIn, "default", "109876543210"), ToastArgs.Parse((string?)toast.Attribute("launch")));
        Assert.DoesNotContain("leaf.tester", (string?)toast.Attribute("launch") ?? "", StringComparison.Ordinal);
        Assert.Equal("signin-" + Alert.TagFor("109876543210"), message.Tag);
    }

    [Fact]
    public void NoMeeting_JustSaysSo()
    {
        var toast = Parse(ToastContent.NoMeeting(sound: true));

        Assert.Equal("No meeting to join", Texts(toast)[0]);
        Assert.Null(toast.Attribute("launch"));
        Assert.Empty(Actions(toast));
    }

    [Fact]
    public void When_TodayTomorrowAndAllDay()
    {
        var now = Start.AddHours(-6);

        Assert.Equal("Today \u00B7 6 PM \u2013 7 PM", ToastContent.When(Occurrence(), TimeZoneInfo.Utc, false, now));
        Assert.Equal("Tomorrow \u00B7 6 PM \u2013 7 PM", ToastContent.When(Occurrence(), TimeZoneInfo.Utc, false, now.AddDays(-1)));
        var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Today \u00B7 All day", ToastContent.When(Occurrence(allDay: true) with { Start = day, End = day.AddDays(1) }, TimeZoneInfo.Utc, false, now));
    }

    [Fact]
    public void Args_RoundTripAwkwardCalendarIds()
    {
        var args = new ToastArgs(ToastAction.Accept, "uitest-1", "1", "a;b=c%d@group.calendar.google.com", "evt_1", Start);

        Assert.Equal(args, ToastArgs.Parse(args.Encode()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("action=Explode;profile=default")]
    [InlineData("action=3;profile=default")]
    [InlineData("action=Open,Join;profile=default")]
    [InlineData("action=ReviewConflicts")]
    [InlineData("action=Join;profile=default;account=1;calendar=c")]
    [InlineData("action=Join;profile=default;account=1;calendar=c;event=e;start=99999999999999999999")]
    [InlineData("action=Join;profile=default;account=1;calendar=c;event=e;start=-5")]
    [InlineData("action=SignIn;profile=default")]
    [InlineData("=x;action=ReviewConflicts;profile=default")]
    [InlineData("snooze")]
    [InlineData("dismiss")]
    public void Args_Malformed_ReadAsNothing(string? text)
    {
        Assert.Null(ToastArgs.Parse(text));
    }

    [Fact]
    public void Args_TooLong_ReadAsNothing()
    {
        Assert.Null(ToastArgs.Parse("action=ReviewConflicts;profile=default;x=" + new string('x', 5000)));
    }

    [Theory]
    [InlineData("action=Open;profile=..%5C..%5CWindows;account=1;calendar=c;event=e;start=0")]
    [InlineData("action=ReviewConflicts;profile=a%00b")]
    [InlineData("action=ReviewConflicts;profile=a%20b")]
    [InlineData("action=ReviewConflicts;profile=a%E2%80%AEb")]
    [InlineData("action=ReviewConflicts;profile=")]
    [InlineData("action=Open;action=Join;profile=default;account=1;calendar=c;event=e;start=0")]
    [InlineData("action=ReviewConflicts;profile=default;profile=other")]
    [InlineData("action=Open;profile=default;account=;calendar=;event=;start=0")]
    [InlineData("action=SignIn;profile=default;account=")]
    public void Args_UnsafeProfileDuplicateKeysOrEmptyIds_ReadAsNothing(string text)
    {
        Assert.Null(ToastArgs.Parse(text));
    }

    [Fact]
    public void Conflicts_ZeroOrNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToastContent.Conflicts(0, "default", sound: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToastContent.Conflicts(-2, "default", sound: true));
    }

    [Fact]
    public void SignIn_HostileEmail_StaysPlainText()
    {
        var hostile = "x</text><action content=\"Pwn\" arguments=\"x\"/><text>‮​@evil.test";
        var toast   = Parse(ToastContent.SignIn("1", hostile, "default", sound: true));
        var benign  = Parse(ToastContent.SignIn("1", "a@b.test", "default", sound: true));

        Assert.Equal(Shape(benign), Shape(toast));
        Assert.DoesNotContain('‮', Texts(toast)[1]);
        Assert.DoesNotContain('​', Texts(toast)[1]);
    }

    [Fact]
    public void Invite_HostileOrganizer_StaysPlainText()
    {
        var hostile = "x</text><action content=\"Pwn\" arguments=\"x\"/><text>‮​@evil.test";
        var toast   = Parse(ToastContent.Invite(Occurrence(), Details(organizer: hostile), false, "tag", "Today", "default", sound: true));
        var benign  = Parse(ToastContent.Invite(Occurrence(), Details(), false, "tag", "Today", "default", sound: true));

        Assert.Equal(Shape(benign), Shape(toast));
        Assert.DoesNotContain('‮', Texts(toast)[2]);
        Assert.DoesNotContain('​', Texts(toast)[2]);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("​‮")]
    public void Reminder_BlankTitle_ShowsNoTitleAsHeadline(string title)
    {
        var texts = Texts(Parse(ToastContent.Reminder(Reminder(Meet, title), Details(title), "Today", "default", sound: true)));

        Assert.Equal(EventDetailsParser.NoTitle, texts[0]);
        Assert.Equal("Today", texts[1]);
        Assert.Equal("Room 4", texts[2]);
    }
}
