using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SelectionTests : IDisposable
{
    const string Dentist    = "Event_evt-single_202610011300";
    const string SchoolPlay = "Event_evt-family-play_202610022200";
    const string FamilyId   = "family123@group.calendar.google.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    static void CtrlClick(AutomationElement element)
    {
        Keyboard.Press(VirtualKeyShort.CONTROL);
        element.Click();
        Keyboard.Release(VirtualKeyShort.CONTROL);
    }

    [Fact]
    public void CtrlClickTwoEvents_DeleteRemovesBoth_UndoBringsBothBack()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var monday = leaf.WaitFor("Event_evt-weekly_202610051330");
        LeafApp.WaitUntilStill(monday);
        monday.Click();
        CtrlClick(leaf.WaitFor("Event_evt-weekly_202610091330"));
        Assert.Equal("2 events selected", leaf.WaitFor("SelectionSummary").Name);

        leaf.Press(VirtualKeyShort.DELETE);
        leaf.WaitForAnywhere("ScopeThis").Click();
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("Event_evt-weekly_202610051330") || leaf.Exists("Event_evt-weekly_202610091330"), TimeSpan.FromSeconds(5)).Success);
        leaf.WaitFor("UndoButton").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610091330"));
    }

    [Fact]
    public void CopyThenPasteAtClickedTime_CreatesCopyThere()
    {
        using var leaf = Launch();
        var dentist = leaf.WaitFor(Dentist);
        var hour    = dentist.BoundingRectangle.Height + 2;
        dentist.Click();
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);

        // Oct 3, four hours after the dentist's start
        var column = leaf.WaitFor("DayHeader_2026-10-03").BoundingRectangle;
        Mouse.Click(new Point(column.X + column.Width / 2, dentist.BoundingRectangle.Y + 4 * hour + hour / 10));
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);

        var write = _google.WaitForWrite(w => w.Method == "POST");
        using var body = JsonDocument.Parse(write.Body);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero), body.RootElement.GetProperty("start").GetProperty("dateTime").GetDateTimeOffset().ToUniversalTime());
        Assert.Equal("Dentist appointment", body.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public void CopyFromReadOnlyCalendar_PastesIntoYourCalendar()
    {
        _google.ReadOnlyCalendarId = FamilyId;
        using var leaf = Launch();
        leaf.WaitFor(SchoolPlay).Click();
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);

        var write = _google.WaitForWrite(w => w.Method == "POST");
        using var body = JsonDocument.Parse(write.Body);
        Assert.Equal("School play", body.RootElement.GetProperty("summary").GetString());
        Assert.DoesNotContain("family", write.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("leaf.tester", Uri.UnescapeDataString(write.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteMixedSelection_NoticeCountsTheSkippedEvent()
    {
        _google.ReadOnlyCalendarId = FamilyId;
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();
        CtrlClick(leaf.WaitFor(SchoolPlay));
        Assert.Equal("2 events selected", leaf.WaitFor("SelectionSummary").Name);

        leaf.Press(VirtualKeyShort.DELETE);

        var notice = leaf.WaitFor("NoticeBar");
        Assert.True(Retry.WhileFalse(() => notice.FindAllDescendants().Prepend(notice).Any(e => e.Properties.Name.ValueOrDefault == "Event deleted · 1 couldn't be changed"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("UndoButton"));
        Assert.True(Retry.WhileTrue(() => leaf.Exists(Dentist), TimeSpan.FromSeconds(5)).Success);
        Assert.True(leaf.Exists(SchoolPlay));
    }

    [Fact]
    public void ContextMenu_Tomato_RecolorsTheEvent()
    {
        using var leaf = Launch();

        leaf.WaitFor(Dentist).RightClick();
        leaf.WaitForAnywhere("MenuColor").Click();
        leaf.WaitForAnywhere("MenuColor_11").Click();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));
        Assert.Equal("""{"colorId":"11"}""", write.Body);
    }

    [Fact]
    public void CtrlA_SelectsEverythingVisible()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);

        var summary = leaf.WaitFor("SelectionSummary").Name;
        Assert.EndsWith("events selected", summary, StringComparison.Ordinal);
        Assert.True(int.Parse(summary.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) >= 3);
    }

    [Fact]
    public void X_OnHoveredEvent_AddsItToTheSelection()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();
        var meeting = leaf.WaitFor("Event_evt-meeting_202610011800").BoundingRectangle;

        LeafApp.MoveMouse(new Point(meeting.X + meeting.Width / 2, meeting.Y + meeting.Height / 2));
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.KEY_X);

        Assert.Equal("2 events selected", leaf.WaitFor("SelectionSummary").Name);
    }
}
