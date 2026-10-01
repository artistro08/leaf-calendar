using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class TrayAgendaTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Oct 1, 2026, 8:00 AM in New York
    static readonly DateTimeOffset Morning = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public TrayAgendaTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var item in JsonSerializer.Deserialize(Fixture.Read("events-page1.json"), GoogleJsonContext.Default.EventsPage)!.Items)
        {
            EventStore.Apply(conn, null, Account, Primary, item);
        }

        // Dentist 9-10 AM (fixture), Design review 2-3 PM with Meet, and a Friday event
        Store("""{"id":"evt-meet","status":"confirmed","summary":"Design review","hangoutLink":"https://meet.google.com/abc-defg-hij","start":{"dateTime":"2026-10-01T14:00:00-04:00"},"end":{"dateTime":"2026-10-01T15:00:00-04:00"}}""");
        Store("""{"id":"evt-fri","status":"confirmed","summary":"Lunch","start":{"dateTime":"2026-10-02T12:00:00-04:00"},"end":{"dateTime":"2026-10-02T13:00:00-04:00"}}""");
    }

    public void Dispose() => _db.Dispose();

    void Store(string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, Primary, json);
    }

    IReadOnlyList<AgendaDay> Load(DateTimeOffset now, int days = 3, bool includeAllDay = true)
    {
        using var conn = _db.Database.Open();
        return TrayAgenda.Load(conn, now, NewYork, days, includeAllDay, use24Hour: false);
    }

    [Fact]
    public void Load_GroupsByDay_TodayThenTomorrow_SkippingEmptyDays()
    {
        var days = Load(Morning);

        Assert.Equal(["Today", "Tomorrow"], days.Select(d => d.Header));
        Assert.Equal(["Dentist appointment", "Design review"], days[0].Items.Select(i => i.Title));
        Assert.Equal("9 AM – 10 AM", days[0].Items[0].When);
        Assert.Equal(["Lunch"], days[1].Items.Select(i => i.Title));
    }

    [Fact]
    public void Load_EventsThatEnded_LeaveToday()
    {
        var days = Load(Morning.AddHours(2).AddMinutes(30));

        Assert.Equal(["Design review"], days[0].Items.Select(i => i.Title));
    }

    [Fact]
    public void Load_MeetingLink_OnTheItem()
    {
        var review = Load(Morning)[0].Items.Single(i => i.Title == "Design review");

        Assert.Equal("https://meet.google.com/abc-defg-hij", review.Link!.AbsoluteUri);
    }

    [Fact]
    public void Load_AllDay_ListedFirstUnlessTurnedOff()
    {
        Store("""{"id":"evt-day","status":"confirmed","summary":"Offsite","start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}""");

        var today = Load(Morning)[0].Items;
        Assert.Equal("Offsite", today[0].Title);
        Assert.Equal("All day", today[0].When);

        Assert.DoesNotContain(Load(Morning, includeAllDay: false)[0].Items, i => i.Title == "Offsite");
    }

    [Fact]
    public void Load_RunningSinceYesterday_ShowsToday()
    {
        Store("""{"id":"evt-night","status":"confirmed","summary":"Night shift","start":{"dateTime":"2026-09-30T22:00:00-04:00"},"end":{"dateTime":"2026-10-01T09:00:00-04:00"}}""");

        Assert.Equal("Night shift", Load(Morning)[0].Items[0].Title);
    }

    [Fact]
    public void Load_TitleWithLineBreaks_ShownOnOneLine()
    {
        Store("""{"id":"evt-lines","status":"confirmed","summary":"Line one\nLine two","start":{"dateTime":"2026-10-01T11:00:00-04:00"},"end":{"dateTime":"2026-10-01T11:30:00-04:00"}}""");

        Assert.Contains(Load(Morning)[0].Items, i => i.Title == "Line one Line two");
    }

    [Fact]
    public void Next_MeetingSoon_BeatsTheOneRunning()
    {
        Store("""{"id":"evt-run","status":"confirmed","summary":"Running","start":{"dateTime":"2026-10-01T13:30:00-04:00"},"end":{"dateTime":"2026-10-01T14:30:00-04:00"}}""");
        var now = new DateTimeOffset(2026, 10, 1, 13, 55, 0, TimeSpan.FromHours(-4));

        var next = TrayAgenda.Next(Load(now), now, TimeSpan.FromHours(1));

        Assert.Equal("Design review", next!.Item.Title);
        Assert.Equal("in 5 min", next.Countdown);
    }

    [Fact]
    public void Next_NextOneIsLater_ShowsTheRunningOne()
    {
        Store("""{"id":"evt-run","status":"confirmed","summary":"Running","start":{"dateTime":"2026-10-01T13:00:00-04:00"},"end":{"dateTime":"2026-10-01T14:30:00-04:00"}}""");
        var now = new DateTimeOffset(2026, 10, 1, 13, 30, 0, TimeSpan.FromHours(-4));

        var next = TrayAgenda.Next(Load(now), now, TimeSpan.FromHours(1));

        Assert.Equal("Running", next!.Item.Title);
        Assert.Equal("Now", next.Countdown);
    }

    [Fact]
    public void Next_BeyondTheLookahead_None()
    {
        Assert.Null(TrayAgenda.Next(Load(Morning), Morning, TimeSpan.FromMinutes(30)));
        Assert.Equal("Dentist appointment", TrayAgenda.Next(Load(Morning), Morning, TimeSpan.FromHours(1))!.Item.Title);
    }

    [Fact]
    public void Next_LookaheadCrossesMidnight_FindsTomorrowsMeeting()
    {
        Store("""{"id":"evt-late","status":"confirmed","summary":"Late call","start":{"dateTime":"2026-10-02T01:00:00-04:00"},"end":{"dateTime":"2026-10-02T02:00:00-04:00"}}""");
        var now = new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(-4));

        var next = TrayAgenda.Next(Load(now, TrayAgenda.NextDays), now, TimeSpan.FromMinutes(LeafSettings.LookaheadChoices.Max()));

        Assert.Equal("Late call", next!.Item.Title);
    }

    [Fact]
    public void Tooltip_SaysWhatAndWhen()
    {
        var now  = new DateTimeOffset(2026, 10, 1, 8, 48, 0, TimeSpan.FromHours(-4));
        var next = TrayAgenda.Next(Load(now), now, TimeSpan.FromHours(1));

        Assert.Equal("Dentist appointment in 12 min", TrayAgenda.Tooltip(next));
        Assert.Equal("Leaf Calendar", TrayAgenda.Tooltip(null));
    }

    [Fact]
    public void Tooltip_Running_SaysNow()
    {
        var now  = new DateTimeOffset(2026, 10, 1, 9, 10, 0, TimeSpan.FromHours(-4));
        var next = TrayAgenda.Next(Load(now), now, TimeSpan.FromHours(1));

        Assert.Equal("Dentist appointment now", TrayAgenda.Tooltip(next));
    }

    [Fact]
    public void Tooltip_LongTitle_FitsTheShellsLimitWithoutSplittingAnEmoji()
    {
        var title = new string('x', 113) + "😀😀😀" + new string('y', 50);
        Store($$$"""{"id":"evt-long","status":"confirmed","summary":"{{{title}}}","start":{"dateTime":"2026-10-01T08:10:00-04:00"},"end":{"dateTime":"2026-10-01T08:20:00-04:00"}}""");

        var tooltip = TrayAgenda.Tooltip(TrayAgenda.Next(Load(Morning), Morning, TimeSpan.FromHours(1)));

        Assert.True(tooltip.Length <= TrayAgenda.MaxTooltip);
        Assert.EndsWith("… in 10 min", tooltip, StringComparison.Ordinal);
        for (var i = 0; i < tooltip.Length; i++)
        {
            Assert.False(char.IsHighSurrogate(tooltip[i]) && (i + 1 == tooltip.Length || !char.IsLowSurrogate(tooltip[i + 1])));
        }
    }

    [Theory]
    [InlineData(15, "Nothing in the next 15 minutes.")]
    [InlineData(60, "Nothing in the next hour.")]
    [InlineData(480, "Nothing in the next 8 hours.")]
    public void NothingNext_NamesTheLookahead(int minutes, string expected)
    {
        Assert.Equal(expected, TrayAgenda.NothingNext(minutes));
    }

    [Fact]
    public void DayHeader_TodayTomorrowThenTheDate()
    {
        var today = new DateOnly(2026, 10, 1);

        Assert.Equal("Today", TrayAgenda.DayHeader(today, today));
        Assert.Equal("Tomorrow", TrayAgenda.DayHeader(today.AddDays(1), today));
        Assert.Equal("Saturday, October 3", TrayAgenda.DayHeader(today.AddDays(2), today));
    }

    [Fact]
    public void Clean_ControlCharactersAndLength()
    {
        Assert.Equal("a b c", DisplayText.Clean("a\r\nb\tc", 50));
        Assert.Equal("abcd…", DisplayText.Clean("abcdefgh", 5));
        Assert.Equal("", DisplayText.Clean(null, 5));
    }

    [Theory]
    [InlineData("­")]
    [InlineData("͏")]
    [InlineData("᠎")]
    [InlineData("⁡")]
    [InlineData("⁤")]
    [InlineData("⁪")]
    [InlineData("⁯")]
    [InlineData("￹")]
    [InlineData("￻")]
    [InlineData("󠁁")]
    [InlineData("󠁿")]
    public void Clean_OtherFormatCharactersRemoved(string invisible)
    {
        Assert.Equal("ab", DisplayText.Clean("a" + invisible + "b", 50));
    }

    [Fact]
    public void Clean_KeepsZeroWidthJoinerInEmojiSequences()
    {
        var woman_technologist = "👩‍💻";

        Assert.Equal(woman_technologist, DisplayText.Clean(woman_technologist, 50));
    }

    [Fact]
    public void Clean_BidiAndZeroWidthCharactersRemoved()
    {
        Assert.Equal("abcdefgh", DisplayText.Clean("a\u202Eb\u2066c\u200Fd\u200Be\u2060f\uFEFFg\u2028h\u2029", 50));
    }

    [Fact]
    public void Load_TitleOfOnlyInvisibleCharacters_FallsBackToNoTitle()
    {
        Store("""{"id":"evt-blank","status":"confirmed","summary":"\u200B\u202E","start":{"dateTime":"2026-10-01T11:00:00-04:00"},"end":{"dateTime":"2026-10-01T11:30:00-04:00"}}""");

        Assert.Contains(Load(Morning)[0].Items, i => i.Title == "(No title)");
    }

    [Fact]
    public void Load_ExcludedCalendar_IsLeftOut()
    {
        const string Family = "family123@group.calendar.google.com";
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, Family, """{"id":"evt-play","status":"confirmed","summary":"School play","start":{"dateTime":"2026-10-01T15:00:00-04:00"},"end":{"dateTime":"2026-10-01T16:00:00-04:00"}}""");

        var all  = TrayAgenda.Load(conn, Morning, NewYork, 14, includeAllDay: true, use24Hour: false);
        var some = TrayAgenda.Load(conn, Morning, NewYork, 14, includeAllDay: true, use24Hour: false, excluded: [new CalendarRef(Account, Family)]);

        Assert.Contains(all.SelectMany(d => d.Items), i => i.Occurrence.CalendarId == Family);
        Assert.DoesNotContain(some.SelectMany(d => d.Items), i => i.Occurrence.CalendarId == Family);
        Assert.Contains(some.SelectMany(d => d.Items), i => i.Occurrence.CalendarId == Primary);
    }
}
