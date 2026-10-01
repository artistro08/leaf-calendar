using System.Drawing;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TimeGridTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // Where the time grid came to rest (published on the grid's automation ItemStatus)
    static (string First, double Offset, double Column, double Top) Rest(LeafApp leaf)
    {
        var parts = (leaf.WaitFor("TimeGrid").Properties.ItemStatus.ValueOrDefault ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('='))
            .ToDictionary(p => p[0], p => p[1]);

        double Number(string key) => parts.TryGetValue(key, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
        return (parts.GetValueOrDefault("first", ""), Number("offset"), Number("column"), Number("top"));
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void WeekView_OnStartDate_ShowsSyncedEvent()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor("TimeGrid"));
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));
        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
    }

    [Fact]
    public void Next_ShowsRepeatingSeriesWithoutCanceledInstance()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("NextButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610091330"));
        Assert.False(leaf.Exists("Event_evt-weekly_202610071330"));
    }

    [Fact]
    public void AllDayEvent_ShownInAllDayRow()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("NextButton").AsButton().Invoke();
        leaf.WaitFor("NextButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("AllDay_evt-allday_20261012"));
    }

    [Fact]
    public void Next_LandsExactlyOnTheNextWeek()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("NextButton").AsButton().Invoke();

        // One scroll that comes to rest on a day edge: the first day is Oct 4, the offset a whole number of columns
        Assert.True(Retry.WhileFalse(() => Rest(leaf).First == "2026-10-04", TimeSpan.FromSeconds(10)).Success);
        var rest = Rest(leaf);
        var days = rest.Offset / rest.Column;
        Assert.True(Math.Abs(days - Math.Round(days)) * rest.Column < 0.05, $"offset {rest.Offset} isn't a whole number of {rest.Column} columns");
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-04"));
        Assert.Equal("October 2026", leaf.WaitFor("PeriodTitle").Name);
    }

    [Fact]
    public void Wheel_OverGridAfterSidebar_ScrollsGrid()
    {
        _google.ManyCalendars = true;
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.WaitFor("CalendarToggle_church@group.calendar.google.com");
        leaf.Resize(1300, 200);
        var sidebar = leaf.WaitFor("SidebarScroll");
        Assert.True(Retry.WhileFalse(() => Rest(leaf).Top > 0, TimeSpan.FromSeconds(5)).Success);

        // Sidebar First
        LeafApp.WheelOver(sidebar, -2);
        Assert.True(Retry.WhileFalse(() => sidebar.Patterns.Scroll.Pattern.VerticalScrollPercent.ValueOrDefault > 0, TimeSpan.FromSeconds(5)).Success);

        // Then The Grid (and the hour gutter on its left)
        var top = Rest(leaf).Top;
        LeafApp.WheelOver(leaf.WaitFor("TimeGrid"), -2);
        Assert.True(Retry.WhileFalse(() => Rest(leaf).Top > top, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void HorizontalScroll_HeaderMovesWithBodyEveryFrame()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.Resize(1600, 900);

        // One strip of the screen from the header's day dividers down to the bottom of the body, Monday to
        // Thursday (one capture is one frame)
        var monday   = leaf.WaitFor("DayHeader_2026-09-28").BoundingRectangle;
        var thursday = leaf.WaitFor("DayHeader_2026-10-01").BoundingRectangle;
        var grid     = leaf.WaitFor("TimeGrid").BoundingRectangle;
        var top      = monday.Bottom - 4;
        var strip    = new Rectangle(monday.X - 4, top, thursday.X - monday.X + 8, grid.Bottom - 8 - top);
        var rest = DividerEdges(strip).Header;

        // Tilt-Wheel Scroll, Captured While It Glides
        Mouse.MoveTo(new Point(grid.X + grid.Width / 2, grid.Bottom - 60));
        Thread.Sleep(200);
        Mouse.HorizontalScroll(-3);
        var frames = Enumerable.Range(0, 25).Select(_ => DividerEdges(strip)).ToList();
        Assert.True(Retry.WhileFalse(() => Rest(leaf).First is var first && first.Length > 0 && first != "2026-09-27", TimeSpan.FromSeconds(5)).Success);
        Thread.Sleep(300);
        var landed = DividerEdges(strip).Header;

        // Each header divider sits on a body column line, in every frame, including those caught mid-glide
        var moving = frames.Where(f => !f.Header.SequenceEqual(rest) && !f.Header.SequenceEqual(landed)).ToList();
        Assert.NotEmpty(moving);
        foreach (var (headerEdges, bodyEdges) in frames)
        {
            Assert.NotEmpty(headerEdges);
            Assert.All(headerEdges, x => Assert.Contains(bodyEdges, b => Math.Abs(b - x) <= 1));
        }
    }

    // The x of every thin vertical line crossing the top row of the strip (the header's day dividers) and its
    // bottom rows (the body's column lines). A line is a pixel that stands out from its neighbors two pixels either side,
    // which match each other (so the edges of the today circle or an event don't count)
    static (List<int> Header, List<int> Body) DividerEdges(Rectangle strip)
    {
        using var shot = Capture.Rectangle(strip).Bitmap;

        List<int> Lines(int y) =>
        [
            .. Enumerable.Range(2, shot.Width - 4).Where(x =>
            {
                int left = Brightness(shot.GetPixel(x - 2, y)), right = Brightness(shot.GetPixel(x + 2, y));
                return Math.Abs(left - right) <= 12 && Math.Abs(Brightness(shot.GetPixel(x, y)) - (left + right) / 2) > 12;
            }),
        ];

        // The body also counts plain edges: a line between a shaded weekend and a weekday has different neighbors
        List<int> Edges(int y) => [.. Enumerable.Range(1, shot.Width - 1).Where(x => Math.Abs(Brightness(shot.GetPixel(x, y)) - Brightness(shot.GetPixel(x - 1, y))) > 12)];
        var rows = new[] { shot.Height - 1, shot.Height - 30, shot.Height - 60 };
        return (Lines(0), [.. rows.SelectMany(y => Lines(y).Concat(Edges(y))).Distinct().Order()]);
    }

    static int Brightness(Color c) => c.R + c.G + c.B;

    [Fact]
    public void DayView_FromMenu_ShowsOneColumn()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewDay").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Day", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));
    }

    // A timed event at a local time on the PC's zone, as the JSON Google stores
    static JsonObject Seed(string id, DateTime localStart, DateTime localEnd) => new()
    {
        ["id"]      = id,
        ["summary"] = id,
        ["start"]   = new JsonObject { ["dateTime"] = Utc(localStart) },
        ["end"]     = new JsonObject { ["dateTime"] = Utc(localEnd) },
    };

    static string Utc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    static string EventId(string id, DateTime localStart) =>
        $"Event_{id}_{TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local).ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)}";

    static void SwitchToDayView(LeafApp leaf)
    {
        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewDay").AsMenuItem().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Day", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);
    }

    // The owner saw the next day through the see-through details pane in Day view. Checked while the pane slides
    // open, at rest, and while Day view scrolls to the next day and back (one capture is one frame)
    [Fact]
    public void DayView_DetailsPanel_DoesNotShowTheNextDay()
    {
        _google.AddEvent("leaf.tester@gmail.com", new JsonObject
        {
            ["id"]      = "evt-tomorrow",
            ["summary"] = "Tomorrow",
            ["colorId"] = "11",
            ["start"]   = new JsonObject { ["dateTime"] = "2026-10-02T13:00:00Z" },
            ["end"]     = new JsonObject { ["dateTime"] = "2026-10-02T14:00:00Z" },
        });
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        SwitchToDayView(leaf);

        // Close The Pane (it opens by default), So Its Slide Can Be Watched
        leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("UpcomingHeader"), TimeSpan.FromSeconds(5)).Success);

        // Sample The Event's Fill On Its Own Day
        leaf.WaitFor("NextButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => Rest(leaf).First == "2026-10-02", TimeSpan.FromSeconds(10)).Success);
        var card = leaf.WaitFor("Event_evt-tomorrow_202610021300");
        LeafApp.WaitUntilStill(card);
        var box = card.BoundingRectangle;
        Color fill;
        using (var shot = Capture.Rectangle(new Rectangle(box.X + box.Width / 2, box.Y + box.Height / 2, 1, 1)))
        {
            fill = shot.Bitmap.GetPixel(0, 0);
        }

        leaf.WaitFor("PreviousButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => Rest(leaf).First == "2026-10-01", TimeSpan.FromSeconds(10)).Success);

        // Where The Pane Will Be (320 wide at the window's right), At The Event's Height
        var root   = leaf.WaitFor("CalendarRoot").BoundingRectangle;
        var scale  = leaf.WaitFor("DetailsToggleButton").BoundingRectangle.Width / 32.0;
        var width  = (int)Math.Round(320 * scale);
        var region = new Rectangle(root.Right - width, box.Y, width, box.Height);

        // Mid-Slide: the pane opening
        leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
        AssertNoFill(Frames(region), fill, "while the pane opens");

        // At Rest
        leaf.WaitFor("UpcomingHeader");
        Thread.Sleep(300);
        AssertNoFill(Frames(region, 1), fill, "at rest");

        // Mid-Scroll: to the next day and back
        leaf.WaitFor("NextButton").AsButton().Invoke();
        AssertNoFill(Frames(region), fill, "while scrolling to Oct 2");
        Assert.True(Retry.WhileFalse(() => Rest(leaf).First == "2026-10-02", TimeSpan.FromSeconds(10)).Success);
        leaf.WaitFor("PreviousButton").AsButton().Invoke();
        AssertNoFill(Frames(region), fill, "while scrolling back to Oct 1");
    }

    // Back-to-back captures of the region (scanned afterward, so the frames stay close together)
    static List<Bitmap> Frames(Rectangle region, int count = 20) =>
        [.. Enumerable.Range(0, count).Select(_ =>
        {
            using var shot = Capture.Rectangle(region);
            return new Bitmap(shot.Bitmap);
        })];

    // No pixel within 12 (per RGB channel) of the event's fill, in any frame
    static void AssertNoFill(List<Bitmap> frames, Color fill, string when)
    {
        try
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var frame    = frames[i];
                var bleeding = 0;
                for (var x = 0; x < frame.Width; x++)
                {
                    for (var y = 0; y < frame.Height; y++)
                    {
                        var c = frame.GetPixel(x, y);
                        if (Math.Abs(c.R - fill.R) <= 12 && Math.Abs(c.G - fill.G) <= 12 && Math.Abs(c.B - fill.B) <= 12)
                        {
                            bleeding++;
                        }
                    }
                }

                Assert.True(bleeding == 0, $"{bleeding} pixels of the next day's event ({fill}) show in the details pane {when} (frame {i + 1} of {frames.Count}).");
            }
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void AllDayChevron_DoesNotOverlapTheZoneLabel()
    {
        for (var i = 1; i <= 4; i++)
        {
            _google.AddEvent("leaf.tester@gmail.com", new JsonObject
            {
                ["id"]      = $"evt-allday-{i}",
                ["summary"] = $"All day {i}",
                ["start"]   = new JsonObject { ["date"] = "2026-10-01" },
                ["end"]     = new JsonObject { ["date"] = "2026-10-02" },
            });
        }

        using var leaf = Launch();
        var chevron = leaf.WaitFor("AllDayExpand").BoundingRectangle;
        var labels  = leaf.WaitFor("TimeGrid")
            .FindAllDescendants()
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("ZoneLabel_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.False(label.BoundingRectangle.IntersectsWith(chevron), $"The all-day chevron {chevron} overlaps {label.AutomationId} {label.BoundingRectangle}."));
    }

    [Fact]
    public void CtrlWheel_OverTheGrid_ZoomsAndDoesNotScroll()
    {
        using var leaf = Launch();
        var card   = leaf.WaitFor("Event_evt-single_202610011300");
        var before = card.BoundingRectangle.Height;
        Assert.True(Retry.WhileFalse(() => Rest(leaf).Top > 0, TimeSpan.FromSeconds(5)).Success);
        var top = Rest(leaf).Top;

        Keyboard.Press(VirtualKeyShort.CONTROL);
        try { LeafApp.WheelOver(leaf.WaitFor("TimeGrid"), 2); }
        finally { Keyboard.Release(VirtualKeyShort.CONTROL); }

        Assert.True(Retry.WhileFalse(() => card.BoundingRectangle.Height > before + 8, TimeSpan.FromSeconds(3)).Success, "Ctrl+wheel didn't zoom in.");

        // The Same Scroll Offset (scrolling up two notches would have moved it about 96 px)
        Thread.Sleep(500);
        Assert.True(Math.Abs(Rest(leaf).Top - top) < 0.5, $"Ctrl+wheel scrolled the grid from {top} to {Rest(leaf).Top}.");
    }

    [Fact]
    public void PastEvents_AreFaded_FutureOnesAreNot()
    {
        // The test clock is 8:00 on the PC's clock on Oct 1, so both events are seeded in the PC's zone
        var past  = new DateTime(2026, 10, 1, 6, 0, 0);
        var later = new DateTime(2026, 10, 1, 10, 0, 0);
        _google.AddEvent("leaf.tester@gmail.com", Seed("evt-past", past, past.AddHours(1)));
        _google.AddEvent("leaf.tester@gmail.com", Seed("evt-later", later, later.AddHours(1)));
        using var leaf = Launch();

        Assert.Equal("Past", leaf.WaitFor(EventId("evt-past", past)).Properties.ItemStatus.ValueOrDefault);
        Assert.Equal("", leaf.WaitFor(EventId("evt-later", later)).Properties.ItemStatus.ValueOrDefault ?? "");
    }
}
