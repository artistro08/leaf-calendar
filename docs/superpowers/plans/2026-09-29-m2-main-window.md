# Leaf Calendar Milestone 2 (Main Window) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn Leaf from a sign-in/sync shell into a real calendar. It gets:
- a main window with a toolbar in the title bar, a sidebar, a details panel, and day, week, N-day, and month views over the synced Google data
- time-zone columns
- keyboard navigation
- a fake Google server so UI tests cover the real flows end to end

**Architecture:** `LeafCalendar.Core` does all the logic, with no UI and full unit tests:
- settings storage
- turning stored events into the event instances shown on screen (including repeating events)
- a sliding event cache covering 3 months back and 3 months ahead
- view navigation math
- overlap and multi-day layout
- colors, time labels, time-zone search, and the shortcut table

`LeafCalendar.App` renders with code-built WinUI controls on virtualized `ItemsRepeater`s, so only on-screen days plus one screen each side are built and reused. A fake Google HTTP server in the UI test project drives the packaged app through a loopback-only `--fake-google` launch option.

**Tech Stack:** .NET 10 / C# 14, Windows App SDK 2.5.1 (WinUI 3, packaged MSIX, Release Native AOT), CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.12, Meziantou.Framework.Scheduling [4.1.3], xUnit v3 on Microsoft.Testing.Platform, FlaUI.UIA3 5.0.0.

**Spec:** `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`. Read Sections 3, 5.6, 6, 7.4, 8.7, 9, 10, 12. Milestone 1's plan (`docs/superpowers/plans/2026-09-29-m1-foundation.md`) shows the established patterns.

## Global Constraints

- Every Milestone 1 constraint still holds:
  - `net10.0-windows10.0.22621.0`, min `10.0.22000.0`
  - Windows App SDK 2.5.1, packaged MSIX
  - Release-only Native AOT with **0 IL warnings**
  - `TreatWarningsAsErrors` with `AnalysisLevel` `latest-recommended` and NuGet audit on
  - No Google client libraries, no Newtonsoft, no Entity Framework, no WebView2, no Ical.Net/EWSoftware.PDI
  - `Meziantou.Framework.Scheduling` pinned `[4.1.3]`
- JSON: `System.Text.Json` source generation only (`GoogleJsonContext` for Google types, the new `LeafJsonContext` for Leaf's own types). Never call a serializer overload without `JsonTypeInfo`.
- XAML: prefer `x:Bind` and x:Bind function bindings. **No `{Binding}`, no `DisplayMemberPath`** (reflection, not AOT safe).
  - WinUI classes that implement WinRT interfaces (e.g. `IElementFactory`) must be `partial`, or CsWinRT warns CsWinRT1028.
- **Secrets:** OAuth client secret and refresh tokens live only in Credential Locker; access tokens only in memory. Never in SQLite, logs, settings, or exception messages.
- **Logs:** internal IDs only, and `AppLog.Redact` is the safety net. Never log event titles, descriptions, locations, guest emails, or calendar names.
- **Untrusted event content:** titles, descriptions, and locations are rendered as plain text only.
  - No clickable links in Milestone 2. The link allowlist arrives in Milestone 3 with the Join button.
  - Descriptions are HTML reduced to plain text.
- **US English** everywhere ("canceled", "color", "behavior"), with English-only formatting via `CultureInfo.GetCultureInfo("en-US")` (localization is deferred per spec 1.5). Google's own API value `"cancelled"` stays as Google spells it.
- **Tests:**
  - Test runner is Microsoft.Testing.Platform: `dotnet test --project <csproj> [--filter-class "*Name"] [--filter-method "*Name*"] [--output Detailed]`.
  - Async test calls pass `TestContext.Current.CancellationToken` (xUnit1051).
  - Test classes are `public`.
  - A test class holding a disposable field implements `IDisposable` (CA1001).
  - UI test assemblies use `[assembly: Parallelization(Mode = ParallelMode.None)]`, which is already present in `Support/LeafApp.cs`.
- **UI test prerequisite:** run `pwsh tools/dev-register.ps1` first. UI tests type into the app, so the desktop must be unlocked (a locked desktop gives `Access is denied`).
- **AOT publish:** `pwsh tools/publish-aot.ps1` handles the vswhere PATH fix.
- **Code style:**
  - 4-space indent
  - A Title Case block comment above each logical block
  - XML doc `<summary>` on public types and members
  - Guard clauses first
  - Aligned `=` in related assignment groups
- **Commits:** a commit at the end of each task is authorized. Never push without the owner saying so.
- **Look:** Windows 11 native.
  - Mica on the main window.
  - Content surfaces use `LayerFillColorDefaultBrush` with a `CardStrokeColorDefaultBrush` edge and an 8 px top-left corner radius, like the Settings app.
  - Fluent default controls.
  - Light and dark both supported.

## Review Focus

1. **A repeating event edited or canceled for one day.** The moved or canceled instance must replace the original on that exact day, never show twice or vanish. Test: `OccurrenceQueryTests.Load_MovedException_ReplacesOriginalInstance` and `Load_CancelledException_HidesInstance` (Task 6).
2. **All-day events across time zones.** A company holiday on Oct 12 is Oct 12 in Tokyo and in New York. It must not slide a day in either. Test: `OccurrenceQueryTests.Load_AllDayEvent_SameDateInEveryZone` (Task 6).
3. **Crowded days.** Three-plus overlapping meetings must sit side by side with none hidden, and back-to-back 5-minute events must not overlap visually. Test: `DayLayoutTests.Layout_ChainedOverlaps_SharesColumns` and `Layout_TinyAdjacentEvents_GetSeparateColumns` (Task 9).
4. **Hidden weekends with a weekend date requested.** Paging, "go to date", or "today" landing on a Saturday while weekends are hidden must show the next weekday, not crash or show a blank. Test: `DayStripTests.IndexOf_WeekendWhileHidden_SnapsToNextWeekday` (Task 8).
5. **Corrupt or old settings.** A settings row from an older or broken version (unknown enum, bad zone ID, huge day count) must load as safe defaults, not crash startup. Test: `SettingsStoreTests.Load_CorruptOrOutOfRange_FallsBackToSafeValues` (Task 1).

---

## File Structure

```
src/LeafCalendar.Core/
    Data/Schema.cs                      + V2 (settings table, calendar display columns)
    Data/LeafDatabase.cs                + migration to v2
    Data/CalendarStore.cs               + local visibility/color/order, GetAll
    Google/GoogleModels.cs              + CalendarListEntry.Selected
    Google/GoogleEndpoints.cs           NEW  real vs fake Google URLs
    Auth/GoogleOAuthClient.cs           endpoints injectable
    Google/GoogleCalendarClient.cs      endpoints injectable
    Hosting/LaunchOptions.cs            + --fake-google, --start-date
    Hosting/GoogleServices.cs           + endpoints parameter
    Sync/SyncEngine.cs                  + 15-min calendar-list cadence, DataChanged event
    Settings/LeafSettings.cs            NEW  user preferences + enums
    Settings/SettingsStore.cs           NEW  load/save (one JSON row)
    Settings/LeafJsonContext.cs         NEW  source-gen JSON for Leaf types
    Events/EventDetails.cs              NEW  EventKind, ResponseStatus, EventDetails
    Events/EventDetailsParser.cs        NEW  raw Google JSON -> display details (HTML -> text)
    Events/CalendarOccurrence.cs        NEW  one on-screen event instance
    Events/OccurrenceQuery.cs           NEW  DB -> expanded, filtered, de-duplicated instances
    Events/EventWindowCache.cs          NEW  +-3 month sliding cache, bucketed by local day
    Views/ViewNavigator.cs              NEW  periods, stepping, titles, column counts
    Views/DayStrip.cs                   NEW  index <-> date (optionally skipping weekends)
    Views/ShortcutMap.cs                NEW  key chord -> command
    Views/DayLayout.cs                  NEW  overlapping timed events -> columns
    Views/SpanLayout.cs                 NEW  all-day / multi-day / month chips -> lanes
    Views/EventColors.cs                NEW  Google palettes, fill/text contrast
    Views/TimeLabels.cs                 NEW  en-US time strings
    Views/TimeZoneCatalog.cs            NEW  zone search, offset and short labels

src/LeafCalendar.App/
    App.xaml                            + merge Styles/LeafTheme.xaml
    Styles/LeafTheme.xaml               NEW  XAML styles
    Controls/LeafBrushes.cs             NEW  hex -> brush cache, light/dark grid colors
    Controls/EventBlock.cs              NEW  timed event card
    Controls/DayColumn.cs               NEW  one day's canvas (hour lines, events, now line)
    Controls/DayHeaderCell.cs           NEW  weekday + date header
    Controls/AllDayCanvas.cs            NEW  all-day/multi-day lane chips
    Controls/TimeZoneGutter.cs          NEW  hour labels per zone
    Controls/TimeGridView.cs            NEW  day/week/N-day view (virtualized, synced scrolling)
    Controls/MonthGridView.cs           NEW  month view (virtualized week rows)
    Controls/WeekRow.cs                 NEW  one month-view week row
    ViewModels/CalendarViewModel.cs     NEW  view state, commands, cache, selection, upcoming
    Views/CalendarPage.xaml(.cs)        NEW  sidebar | view | details
    Views/SidebarView.xaml(.cs)         NEW  mini month, calendars, footer
    Views/DetailsPanel.xaml(.cs)        NEW  upcoming list / read-only event details
    Views/TimeZonePanel.xaml(.cs)       NEW  add/rename/reorder/remove zones
    MainWindow.xaml(.cs)                toolbar in title bar, pane toggle, theme, navigation
    LeafServices.cs                     + endpoints, fake browser, GoogleChanged event
    ViewModels/AccountsViewModel.cs     uses LeafServices.OpenSignInPageAsync, forced list refresh

tests/LeafCalendar.Tests/               new *Tests.cs per Core unit; fixtures gain "selected": true
tests/LeafCalendar.UITests/
    Support/FakeGoogleServer.cs         NEW  loopback fake Google
    Support/SeededProfile.cs            NEW  pre-signed-in profile for view tests
    Support/LeafApp.cs                  + ProfileFolder, WaitForName, WaitForAnywhere, Press
    AccountFlowTests.cs                 NEW  add / sync now / disconnect through fake Google
    CalendarShellTests.cs, SidebarTests.cs, TimeGridTests.cs, MonthViewTests.cs,
    TimeZoneTests.cs, DetailsPanelTests.cs, KeyboardTests.cs   NEW
    MemoryTests.cs                      measures tray mode with sync running
```

---

### Task 1: Settings Storage and Calendar Display Preferences

**Files:**
- Create: `src/LeafCalendar.Core/Settings/LeafSettings.cs`, `SettingsStore.cs`, `LeafJsonContext.cs`
- Modify: `src/LeafCalendar.Core/Data/Schema.cs` (add `V2`), `Data/LeafDatabase.cs` (`Migrate`), `Data/CalendarStore.cs` (full replacement below), `Google/GoogleModels.cs` (`CalendarListEntry.Selected`)
- Modify fixtures: `tests/LeafCalendar.Tests/Fixtures/calendar-list.json`, `calendar-list-primary-only.json` (add `"selected": true`)
- Modify: `tests/LeafCalendar.Tests/LeafDatabaseTests.cs` (expects `user_version` 2)
- Test: `tests/LeafCalendar.Tests/SettingsStoreTests.cs`, `tests/LeafCalendar.Tests/CalendarPreferencesTests.cs`

**Interfaces:**
- Consumes: `LeafDatabase`, `SqliteExtensions` (`Execute`, `Query`, `GetStringOrNull`), `AccountStore`, `TestDatabase`, and `Fixture` from Milestone 1.
- Produces (namespace `LeafCalendar.Core.Settings`):
  - `enum CalendarViewMode { Day, Week, Month, Days }` and `enum AppTheme { System, Light, Dark }`
  - `sealed record ExtraTimeZone(string Id, string? Label)`
  - `sealed record LeafSettings`, with init properties: `WeekStart`, `ShowWeekends`, `ShowDeclined`, `ShowWeekNumbers`, `Use24HourTime`, `ViewMode`, `CustomDayCount`, `HourHeight`, `Theme`, `SidebarOpen`, `DetailsPanelOpen`, `IReadOnlyList<ExtraTimeZone> TimeZones`
  - Constants on `LeafSettings`: `MinHourHeight = 24`, `MaxHourHeight = 120`, `DefaultHourHeight = 48`, `MaxTimeZones = 4`
  - `LeafSettings Normalize()`
  - `static class SettingsStore` with `LeafSettings Load(SqliteConnection)` and `void Save(SqliteConnection, LeafSettings)`
  - `CalendarInfo` gains trailing positional members `bool LeafHidden, string? LeafColor, int SortOrder`, plus computed `string DisplayColor` and `bool IsVisible`
  - `CalendarStore` gains:
    - `IReadOnlyList<CalendarInfo> GetAll(SqliteConnection)`
    - `void SetHidden(SqliteConnection, string accountId, string calendarId, bool hidden)`
    - `void SetColor(SqliteConnection, string accountId, string calendarId, string? color)` (throws `ArgumentException` for anything but `#RRGGBB` or null)
    - `void Reorder(SqliteConnection, string accountId, IReadOnlyList<string> calendarIds)`
  - Schema v2: `settings(key, value)` and `calendars.leaf_hidden` (nullable; null = "follow Google's `selected`"), `calendars.leaf_color`

- [ ] **Step 1: Update fixtures and the version test**

In both `calendar-list.json` and `calendar-list-primary-only.json`, add `"selected": true,` after `"accessRole": ...` in **every** calendar item.

In `tests/LeafCalendar.Tests/LeafDatabaseTests.cs`, change `Assert.Equal(1L, (long)version.ExecuteScalar()!);` to `Assert.Equal(2L, (long)version.ExecuteScalar()!);`.

- [ ] **Step 2: Write the failing tests**

`tests/LeafCalendar.Tests/SettingsStoreTests.cs`:
```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Load_NothingSaved_ReturnsDefaults()
    {
        using var conn = _db.Database.Open();

        var settings = SettingsStore.Load(conn);

        Assert.Equal(CalendarViewMode.Week, settings.ViewMode);
        Assert.True(settings.ShowWeekends);
        Assert.False(settings.ShowDeclined);
        Assert.Equal(LeafSettings.DefaultHourHeight, settings.HourHeight);
        Assert.Empty(settings.TimeZones);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var conn = _db.Database.Open();
        var saved = new LeafSettings
        {
            WeekStart      = DayOfWeek.Monday,
            ShowWeekends   = false,
            ViewMode       = CalendarViewMode.Days,
            CustomDayCount = 4,
            HourHeight     = 64,
            Theme          = AppTheme.Dark,
            TimeZones      = [new ExtraTimeZone("Asia/Tokyo", "Tokyo office")],
        };

        SettingsStore.Save(conn, saved);
        var loaded = SettingsStore.Load(conn);

        Assert.Equal(DayOfWeek.Monday, loaded.WeekStart);
        Assert.False(loaded.ShowWeekends);
        Assert.Equal(CalendarViewMode.Days, loaded.ViewMode);
        Assert.Equal(4, loaded.CustomDayCount);
        Assert.Equal(64, loaded.HourHeight);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(new ExtraTimeZone("Asia/Tokyo", "Tokyo office"), Assert.Single(loaded.TimeZones));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"viewMode":"Hologram","customDayCount":500,"hourHeight":2,"weekStart":"Funday","timeZones":[{"id":"Mars/Olympus","label":null}]}""")]
    [InlineData("""{"timeZones":null}""")]
    public void Load_CorruptOrOutOfRange_FallsBackToSafeValues(string storedJson)
    {
        using var conn = _db.Database.Open();
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO settings (key, value) VALUES ('app', $value);";
            insert.Parameters.AddWithValue("$value", storedJson);
            insert.ExecuteNonQuery();
        }

        var settings = SettingsStore.Load(conn);

        Assert.InRange(settings.CustomDayCount, 1, 31);
        Assert.InRange(settings.HourHeight, LeafSettings.MinHourHeight, LeafSettings.MaxHourHeight);
        Assert.True(Enum.IsDefined(settings.ViewMode));
        Assert.True(Enum.IsDefined(settings.WeekStart));
        Assert.Empty(settings.TimeZones);
    }

    [Fact]
    public void Normalize_TooManyAndDuplicateZones_KeepsFirstFourDistinctAndTrimsLabels()
    {
        var settings = new LeafSettings
        {
            TimeZones =
            [
                new("Asia/Tokyo", "  Tokyo  "),
                new("Asia/Tokyo", "dup"),
                new("Europe/London", ""),
                new("America/New_York", null),
                new("Australia/Sydney", null),
                new("Europe/Paris", null),
            ],
        }.Normalize();

        Assert.Equal(["Asia/Tokyo", "Europe/London", "America/New_York", "Australia/Sydney"], settings.TimeZones.Select(z => z.Id));
        Assert.Equal("Tokyo", settings.TimeZones[0].Label);
        Assert.Null(settings.TimeZones[1].Label);
    }
}
```

`tests/LeafCalendar.Tests/CalendarPreferencesTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class CalendarPreferencesTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Family  = "family123@group.calendar.google.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    readonly TestDatabase _db = new();

    public CalendarPreferencesTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, Entries("calendar-list.json"));
    }

    public void Dispose() => _db.Dispose();

    static List<CalendarListEntry> Entries(string fixture) =>
        JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.CalendarListPage)!.Items;

    CalendarInfo Get(string id)
    {
        using var conn = _db.Database.Open();
        return CalendarStore.GetAll(conn).Single(c => c.Id == id);
    }

    [Fact]
    public void ReplaceForAccount_UnselectedInGoogle_StartsHidden()
    {
        using var conn = _db.Database.Open();
        var entries = Entries("calendar-list.json");
        entries.Add(new CalendarListEntry { Id = "holidays@group.v.calendar.google.com", Summary = "Holidays", AccessRole = "reader", Selected = false });

        CalendarStore.ReplaceForAccount(conn, Account, entries);

        Assert.False(CalendarStore.GetAll(conn).Single(c => c.Id.StartsWith("holidays", StringComparison.Ordinal)).IsVisible);
        Assert.True(Get(Primary).IsVisible);
    }

    [Fact]
    public void SetHidden_ThenGoogleListRefresh_KeepsLeafChoice()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Family, hidden: true);
            CalendarStore.ReplaceForAccount(conn, Account, Entries("calendar-list.json"));
        }

        Assert.False(Get(Family).IsVisible);
    }

    [Fact]
    public void SetColor_OverridesGoogleColor_AndNullResets()
    {
        using var conn = _db.Database.Open();

        CalendarStore.SetColor(conn, Account, Family, "#16A765");
        Assert.Equal("#16A765", CalendarStore.GetAll(conn).Single(c => c.Id == Family).DisplayColor);

        CalendarStore.SetColor(conn, Account, Family, null);
        Assert.Equal("#f83a22", CalendarStore.GetAll(conn).Single(c => c.Id == Family).DisplayColor);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void SetColor_NotHex_Throws(string color)
    {
        using var conn = _db.Database.Open();

        Assert.Throws<ArgumentException>(() => CalendarStore.SetColor(conn, Account, Family, color));
    }

    [Fact]
    public void Reorder_ThenGoogleListRefresh_KeepsOrder()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.Reorder(conn, Account, [Family, Primary]);
            CalendarStore.ReplaceForAccount(conn, Account, Entries("calendar-list.json"));
        }

        using var check = _db.Database.Open();
        Assert.Equal([Family, Primary], CalendarStore.GetAll(check).Select(c => c.Id));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*SettingsStoreTests" --filter-class "*CalendarPreferencesTests" --filter-class "*LeafDatabaseTests"`
Expected: build FAILS (`LeafCalendar.Core.Settings` and `CalendarStore.GetAll` not found).

- [ ] **Step 4: Implement**

Add to `src/LeafCalendar.Core/Google/GoogleModels.cs`, inside `CalendarListEntry` after `Hidden`:
```csharp
    /// <summary>True when the calendar is ticked in Google Calendar's list (Google omits it when false).</summary>
    public bool Selected { get; set; }
```

Add to `src/LeafCalendar.Core/Data/Schema.cs`, inside the class after `V1`:
```csharp
    /// <summary>
    /// Version 2: app settings, plus Leaf's own calendar display choices. <c>leaf_hidden</c> is null
    /// until Leaf decides (it then follows Google's "selected"), so later Google list refreshes never
    /// override a choice the user made in Leaf.
    /// </summary>
    public const string V2 = """
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        ALTER TABLE calendars ADD COLUMN leaf_hidden INTEGER;
        ALTER TABLE calendars ADD COLUMN leaf_color  TEXT;
        """;
```

In `src/LeafCalendar.Core/Data/LeafDatabase.cs`, in `Migrate()`, add after the `// Version 1` block:
```csharp
        // Version 2
        if (version < 2)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V2);
            conn.Execute(tx, "PRAGMA user_version = 2;");
            tx.Commit();
        }
```

`src/LeafCalendar.Core/Settings/LeafSettings.cs`:
```csharp
using System.Globalization;

namespace LeafCalendar.Core.Settings;

/// <summary>Which calendar layout is shown.</summary>
public enum CalendarViewMode
{
    /// <summary>One day.</summary>
    Day,

    /// <summary>One week (5 days when weekends are hidden).</summary>
    Week,

    /// <summary>One month.</summary>
    Month,

    /// <summary>A custom number of days (1-31).</summary>
    Days,
}

/// <summary>App color theme.</summary>
public enum AppTheme
{
    /// <summary>Follow Windows.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>An extra time-zone column. <see cref="Id"/> is an IANA ID such as <c>Asia/Tokyo</c>.</summary>
public sealed record ExtraTimeZone(string Id, string? Label);

/// <summary>
/// The user's preferences. Stored as one JSON row by <see cref="SettingsStore"/> and always passed
/// through <see cref="Normalize"/>, so an old or damaged row can never produce unusable values.
/// </summary>
public sealed record LeafSettings
{
    /// <summary>Smallest hour height in the time grid (px).</summary>
    public const double MinHourHeight = 24;

    /// <summary>Largest hour height in the time grid (px).</summary>
    public const double MaxHourHeight = 120;

    /// <summary>Default hour height (px).</summary>
    public const double DefaultHourHeight = 48;

    /// <summary>Most extra time-zone columns.</summary>
    public const int MaxTimeZones = 4;

    const int MaxLabelLength = 24;

    /// <summary>First day of the week. Defaults to the Windows culture's choice.</summary>
    public DayOfWeek WeekStart { get; init; } = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>Show Saturday and Sunday.</summary>
    public bool ShowWeekends { get; init; } = true;

    /// <summary>Show events you declined.</summary>
    public bool ShowDeclined { get; init; }

    /// <summary>Show ISO week numbers.</summary>
    public bool ShowWeekNumbers { get; init; }

    /// <summary>24-hour clock instead of AM/PM.</summary>
    public bool Use24HourTime { get; init; }

    /// <summary>Current view.</summary>
    public CalendarViewMode ViewMode { get; init; } = CalendarViewMode.Week;

    /// <summary>Day count for <see cref="CalendarViewMode.Days"/> (1-31).</summary>
    public int CustomDayCount { get; init; } = 3;

    /// <summary>Time-grid hour height in px.</summary>
    public double HourHeight { get; init; } = DefaultHourHeight;

    /// <summary>Color theme.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Sidebar shown.</summary>
    public bool SidebarOpen { get; init; } = true;

    /// <summary>Right details panel shown.</summary>
    public bool DetailsPanelOpen { get; init; } = true;

    /// <summary>Extra time-zone columns, left to right after the local zone.</summary>
    public IReadOnlyList<ExtraTimeZone> TimeZones { get; init; } = [];

    /// <summary>
    /// Returns a copy with every value made safe.
    /// </summary>
    /// <remarks>
    /// Day count is clamped to 1-31 and hour height to its range. Unknown enum values reset to defaults.
    /// Time zones are limited to distinct IDs this PC knows, capped at <see cref="MaxTimeZones"/>, with
    /// labels trimmed (blank becomes null) to at most 24 characters.
    /// </remarks>
    public LeafSettings Normalize()
    {
        var zones = (TimeZones ?? [])
            .Where(z => z is not null && !string.IsNullOrWhiteSpace(z.Id) && TimeZoneInfo.TryFindSystemTimeZoneById(z.Id, out _))
            .DistinctBy(z => z.Id, StringComparer.Ordinal)
            .Take(MaxTimeZones)
            .Select(z => z with { Label = CleanLabel(z.Label) })
            .ToList();

        return this with
        {
            WeekStart      = Enum.IsDefined(WeekStart) ? WeekStart : DayOfWeek.Sunday,
            ViewMode       = Enum.IsDefined(ViewMode) ? ViewMode : CalendarViewMode.Week,
            Theme          = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
            CustomDayCount = Math.Clamp(CustomDayCount, 1, 31),
            HourHeight     = double.IsFinite(HourHeight) ? Math.Clamp(HourHeight, MinHourHeight, MaxHourHeight) : DefaultHourHeight,
            TimeZones      = zones,
        };
    }

    static string? CleanLabel(string? label)
    {
        var trimmed = label?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxLabelLength)];
    }
}
```

`src/LeafCalendar.Core/Settings/LeafJsonContext.cs`:
```csharp
using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Settings;

/// <summary>Source-generated JSON metadata for Leaf's own stored types (AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LeafSettings))]
internal sealed partial class LeafJsonContext : JsonSerializerContext
{
}
```

`src/LeafCalendar.Core/Settings/SettingsStore.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Settings;

/// <summary>Reads and writes <see cref="LeafSettings"/> as one JSON row in the <c>settings</c> table.</summary>
public static class SettingsStore
{
    const string Key = "app";

    /// <summary>Loads settings; a missing, unreadable, or out-of-range row yields safe defaults.</summary>
    public static LeafSettings Load(SqliteConnection conn)
    {
        var json = conn.Query(null, "SELECT value FROM settings WHERE key = $key;", r => r.GetString(0), ("$key", Key)).SingleOrDefault();
        if (json is null)
        {
            return new LeafSettings().Normalize();
        }

        try
        {
            return (JsonSerializer.Deserialize(json, LeafJsonContext.Default.LeafSettings) ?? new LeafSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new LeafSettings().Normalize();
        }
    }

    /// <summary>Saves (replaces) settings after normalizing them.</summary>
    public static void Save(SqliteConnection conn, LeafSettings settings) =>
        conn.Execute(
            null,
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            ("$key", Key),
            ("$value", JsonSerializer.Serialize(settings.Normalize(), LeafJsonContext.Default.LeafSettings)));
}
```

Replace the whole of `src/LeafCalendar.Core/Data/CalendarStore.cs` with:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// A stored calendar. <see cref="SyncToken"/> is null until the first full sync finishes.
/// <see cref="Hidden"/> is Google's flag; <see cref="LeafHidden"/> and <see cref="LeafColor"/> are
/// Leaf's own display choices, kept locally and never sent to Google.
/// </summary>
public sealed record CalendarInfo(
    string AccountId,
    string Id,
    string Summary,
    string? BackgroundColor,
    string AccessRole,
    bool IsPrimary,
    bool Hidden,
    string? SyncToken,
    bool LeafHidden,
    string? LeafColor,
    int SortOrder)
{
    /// <summary>Fallback when Google gives no color.</summary>
    public const string DefaultColor = "#4285F4";

    /// <summary>Color to draw with: Leaf's override, then Google's, then the default.</summary>
    public string DisplayColor => LeafColor ?? BackgroundColor ?? DefaultColor;

    /// <summary>True when the calendar's events are shown.</summary>
    public bool IsVisible => !LeafHidden;
}

/// <summary>Reads and writes the <c>calendars</c> table.</summary>
public static partial class CalendarStore
{
    const string SelectColumns = """
        SELECT c.account_id, c.id, COALESCE(c.summary_override, c.summary), c.background_color, c.access_role,
               c.is_primary, c.hidden, c.sync_token, COALESCE(c.leaf_hidden, c.hidden), c.leaf_color, c.sort_order
        FROM calendars c
        """;

    /// <summary>
    /// Makes the account's calendars match Google's list.
    /// </summary>
    /// <remarks>
    /// Calendars missing from the list (or marked deleted) are removed along with their events.
    /// Existing calendars keep their sync token, local order, color, and visibility. A newly seen
    /// calendar starts visible only when it is ticked ("selected") in Google Calendar.
    /// </remarks>
    public static void ReplaceForAccount(SqliteConnection conn, string accountId, IReadOnlyList<CalendarListEntry> entries)
    {
        var incoming    = entries.Where(e => !e.Deleted).ToList();
        var incomingIds = incoming.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        using var tx = conn.BeginTransaction();

        // Remove Calendars No Longer Listed
        var existingIds = conn.Query(tx, "SELECT id FROM calendars WHERE account_id = $account;", r => r.GetString(0), ("$account", accountId));
        foreach (var id in existingIds.Where(id => !incomingIds.Contains(id)))
        {
            conn.Execute(tx, "DELETE FROM calendars WHERE account_id = $account AND id = $id;", ("$account", accountId), ("$id", id));
        }

        // Upsert Listed Calendars
        var nextOrder = conn.Query(tx, "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM calendars WHERE account_id = $account;", r => r.GetInt32(0), ("$account", accountId)).Single();
        foreach (var entry in incoming)
        {
            conn.Execute(
                tx,
                """
                INSERT INTO calendars (account_id, id, summary, summary_override, time_zone, background_color, foreground_color,
                                       access_role, is_primary, hidden, sort_order, default_reminders, leaf_hidden)
                VALUES ($account, $id, $summary, $override, $zone, $background, $foreground, $role, $primary, $hidden, $order, $reminders, $leafHidden)
                ON CONFLICT (account_id, id) DO UPDATE SET
                    summary           = excluded.summary,
                    summary_override  = excluded.summary_override,
                    time_zone         = excluded.time_zone,
                    background_color  = excluded.background_color,
                    foreground_color  = excluded.foreground_color,
                    access_role       = excluded.access_role,
                    is_primary        = excluded.is_primary,
                    hidden            = excluded.hidden,
                    default_reminders = excluded.default_reminders,
                    leaf_hidden       = COALESCE(calendars.leaf_hidden, excluded.leaf_hidden);
                """,
                ("$account", accountId),
                ("$id", entry.Id),
                ("$summary", entry.Summary),
                ("$override", entry.SummaryOverride),
                ("$zone", entry.TimeZone),
                ("$background", entry.BackgroundColor),
                ("$foreground", entry.ForegroundColor),
                ("$role", entry.AccessRole),
                ("$primary", entry.Primary),
                ("$hidden", entry.Hidden),
                ("$order", nextOrder++),
                ("$reminders", JsonSerializer.Serialize(entry.DefaultReminders ?? [], GoogleJsonContext.Default.ListReminderOverride)),
                ("$leafHidden", !entry.Selected));
        }

        tx.Commit();
    }

    /// <summary>An account's calendars in Leaf's order.</summary>
    public static IReadOnlyList<CalendarInfo> GetForAccount(SqliteConnection conn, string accountId) =>
        conn.Query(null, SelectColumns + " WHERE c.account_id = $account ORDER BY c.sort_order;", Map, ("$account", accountId));

    /// <summary>Every calendar, grouped by account email, in Leaf's order.</summary>
    public static IReadOnlyList<CalendarInfo> GetAll(SqliteConnection conn) =>
        conn.Query(null, SelectColumns + " JOIN accounts a ON a.id = c.account_id ORDER BY a.email, c.sort_order;", Map);

    /// <summary>Saves the token for the calendar's next incremental sync.</summary>
    public static void SetSyncToken(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string? syncToken) =>
        conn.Execute(
            tx,
            "UPDATE calendars SET sync_token = $token WHERE account_id = $account AND id = $id;",
            ("$token", syncToken),
            ("$account", accountId),
            ("$id", calendarId));

    /// <summary>Shows or hides a calendar in Leaf (Google is not changed).</summary>
    public static void SetHidden(SqliteConnection conn, string accountId, string calendarId, bool hidden) =>
        conn.Execute(
            null,
            "UPDATE calendars SET leaf_hidden = $hidden WHERE account_id = $account AND id = $id;",
            ("$hidden", hidden),
            ("$account", accountId),
            ("$id", calendarId));

    /// <summary>Sets Leaf's color for a calendar (<c>#RRGGBB</c>), or null to use Google's color.</summary>
    /// <exception cref="ArgumentException">The color is not <c>#RRGGBB</c>.</exception>
    public static void SetColor(SqliteConnection conn, string accountId, string calendarId, string? color)
    {
        if (color is not null && !HexColor().IsMatch(color))
        {
            throw new ArgumentException("Color must be #RRGGBB.", nameof(color));
        }

        conn.Execute(
            null,
            "UPDATE calendars SET leaf_color = $color WHERE account_id = $account AND id = $id;",
            ("$color", color),
            ("$account", accountId),
            ("$id", calendarId));
    }

    /// <summary>Stores the account's calendar order (IDs not listed keep their place after the listed ones).</summary>
    public static void Reorder(SqliteConnection conn, string accountId, IReadOnlyList<string> calendarIds)
    {
        using var tx = conn.BeginTransaction();

        var others = conn.Query(tx, "SELECT id FROM calendars WHERE account_id = $account ORDER BY sort_order;", r => r.GetString(0), ("$account", accountId))
            .Where(id => !calendarIds.Contains(id, StringComparer.Ordinal));

        var order = 0;
        foreach (var id in calendarIds.Concat(others))
        {
            conn.Execute(tx, "UPDATE calendars SET sort_order = $order WHERE account_id = $account AND id = $id;", ("$order", order++), ("$account", accountId), ("$id", id));
        }

        tx.Commit();
    }

    static CalendarInfo Map(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetStringOrNull(3),
        r.GetString(4),
        r.GetBoolean(5),
        r.GetBoolean(6),
        r.GetStringOrNull(7),
        r.GetBoolean(8),
        r.GetStringOrNull(9),
        r.GetInt32(10));

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
```

Behavior change to note: Milestone 1 wrote `sort_order` as the Google list index on every refresh. New calendars now append after the existing ones, and existing ones keep their order. `CalendarStoreTests.ReplaceForAccount_NewList_StoresInOrder` still passes because a fresh account appends in Google's order.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: all PASS, including the new ones and the existing `CalendarStoreTests`, `SyncEngineTests`, and `EventStoreTests`.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core tests/LeafCalendar.Tests
git commit -m "feat(core): add settings storage and local calendar display preferences"
```

---

### Task 2: Calendar-List Cadence and Data-Changed Signal

**Files:**
- Modify: `src/LeafCalendar.Core/Sync/SyncEngine.cs`, `src/LeafCalendar.Core/Hosting/GoogleServices.cs`, `tests/LeafCalendar.Tests/Support/SyncHarness.cs`, `tests/LeafCalendar.Tests/SyncEngineTests.cs`, `src/LeafCalendar.App/ViewModels/AccountsViewModel.cs`
- Test: `tests/LeafCalendar.Tests/SyncCadenceTests.cs`

**Interfaces:**
- Consumes: `SyncEngine`, `SyncHarness` (`Google`, `Time`, `RouteStandardGoogle()`, `NewEngine()`).
- Produces:
  - `SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log, TimeProvider time)`, with a new trailing `time` parameter.
  - `static readonly TimeSpan SyncEngine.CalendarListInterval` (15 minutes).
  - `Task SyncAllAsync(bool refreshCalendarLists, CancellationToken ct)`; the existing `SyncAllAsync(CancellationToken)` means `false`.
  - `event EventHandler? DataChanged`, raised on the calling thread after a sync that wrote anything (calendar list refreshed or at least one event item). It's raised after the gate is released.

- [ ] **Step 1: Update the harness and the one test that assumed a list fetch every sync**

In `tests/LeafCalendar.Tests/Support/SyncHarness.cs`, in `NewEngine()`, change `new SyncEngine(new GoogleCalendarClient(http, provider), Db.Database, Log)` to pass `Time` as the last argument. The exact local name for the provider may differ; keep it.

In `tests/LeafCalendar.Tests/SyncEngineTests.cs`, in `SyncAccountAsync_CalendarRemovedFromList_DeletesItsEvents`, insert this before the second `SyncAccountAsync` call:
```csharp
        _h.Time.Advance(SyncEngine.CalendarListInterval);
```
Also add `using LeafCalendar.Core.Sync;` if it's missing.

- [ ] **Step 2: Write the failing tests**

`tests/LeafCalendar.Tests/SyncCadenceTests.cs`:
```csharp
using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SyncCadenceTests : IDisposable
{
    readonly SyncHarness _h = new();

    public void Dispose() => _h.Dispose();

    int CalendarListCalls() => _h.Google.Requests.Count(r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.ListUrl, StringComparison.Ordinal));

    [Fact]
    public async Task SyncAllAsync_WithinFifteenMinutes_SkipsCalendarList()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(ct);
        _h.Time.Advance(TimeSpan.FromMinutes(14));
        await _h.Engine.SyncAllAsync(ct);

        Assert.Equal(1, CalendarListCalls());
    }

    [Fact]
    public async Task SyncAllAsync_AfterFifteenMinutes_RefreshesCalendarList()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(ct);
        _h.Time.Advance(SyncEngine.CalendarListInterval);
        await _h.Engine.SyncAllAsync(ct);

        Assert.Equal(2, CalendarListCalls());
    }

    [Fact]
    public async Task SyncAllAsync_Forced_RefreshesCalendarList()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAllAsync(ct);
        await _h.Engine.SyncAllAsync(refreshCalendarLists: true, ct);

        Assert.Equal(2, CalendarListCalls());
    }

    [Fact]
    public async Task DataChanged_RaisedOnlyWhenSomethingWasWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-2",
            _ => FakeHttpHandler.Json(System.Net.HttpStatusCode.OK, """{"items":[],"nextSyncToken":"sync-token-2"}"""));
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.FamilyEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-empty",
            _ => FakeHttpHandler.Json(System.Net.HttpStatusCode.OK, """{"items":[],"nextSyncToken":"sync-token-empty"}"""));
        _h.RouteStandardGoogle();
        var raised = 0;
        _h.Engine.DataChanged += (_, _) => raised++;

        await _h.Engine.SyncAllAsync(ct);   // full sync: list + events written
        await _h.Engine.SyncAllAsync(ct);   // incremental: 3 changes written
        await _h.Engine.SyncAllAsync(ct);   // nothing new, list not due

        Assert.Equal(2, raised);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*SyncCadenceTests"`
Expected: build FAILS (`CalendarListInterval`, `DataChanged`, and the 4-argument constructor are missing).

- [ ] **Step 4: Implement**

Edit `src/LeafCalendar.Core/Sync/SyncEngine.cs`:

1. Change the class declaration to add the clock:
```csharp
public sealed class SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log, TimeProvider time) : IDisposable
```

2. Add below the `_gate` field:
```csharp
    /// <summary>How often each account's calendar list is refreshed (event changes are checked every poll).</summary>
    public static readonly TimeSpan CalendarListInterval = TimeSpan.FromMinutes(15);

    // Guarded by _gate
    readonly Dictionary<string, DateTimeOffset> _calendarListSyncedAt = new(StringComparer.Ordinal);
    bool _changed;

    /// <summary>Raised after a sync that wrote anything. Raised on the syncing thread, after the sync lock is released.</summary>
    public event EventHandler? DataChanged;
```

3. Replace `SyncAllAsync(CancellationToken ct)` and `SyncAccountAsync(...)` with:
```csharp
    /// <summary>Syncs every account that can sync; the calendar list only when it's due.</summary>
    public Task SyncAllAsync(CancellationToken ct) => SyncAllAsync(false, ct);

    /// <summary>Syncs every account that can sync. <paramref name="refreshCalendarLists"/> forces a calendar-list refresh ("Sync now").</summary>
    public async Task SyncAllAsync(bool refreshCalendarLists, CancellationToken ct)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            _changed = false;

            IReadOnlyList<Account> accounts;
            using (var conn = database.Open())
            {
                accounts = AccountStore.GetAll(conn);
            }

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Ok))
            {
                await SyncAccountCoreAsync(account.Id, refreshCalendarLists, ct);
            }

            changed = _changed;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Syncs one account now, including its calendar list (used right after sign-in).</summary>
    public async Task SyncAccountAsync(string accountId, CancellationToken ct)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            _changed = false;
            await SyncAccountCoreAsync(accountId, refreshCalendarList: true, ct);
            changed = _changed;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }
```

4. Change `SyncAccountCoreAsync` so the list is refreshed only when forced, never done, or due:
```csharp
    async Task SyncAccountCoreAsync(string accountId, bool refreshCalendarList, CancellationToken ct)
    {
        try
        {
            // Calendar List (When Due)
            var now = time.GetUtcNow();
            var due = refreshCalendarList
                || !_calendarListSyncedAt.TryGetValue(accountId, out var last)
                || now - last >= CalendarListInterval;

            if (due)
            {
                var entries = await google.ListCalendarsAsync(accountId, ct);
                using var conn = database.Open();
                CalendarStore.ReplaceForAccount(conn, accountId, entries);
                _calendarListSyncedAt[accountId] = now;
                _changed = true;
            }

            IReadOnlyList<CalendarInfo> calendars;
            using (var conn = database.Open())
            {
                calendars = CalendarStore.GetForAccount(conn, accountId);
            }

            // Events Per Calendar
            foreach (var calendar in calendars)
            {
                await SyncCalendarAsync(calendar, ct);
            }
        }
        catch (AccountNeedsSignInException)
        {
            using var conn = database.Open();
            AccountStore.SetStatus(conn, accountId, AccountStatus.NeedsSignIn);
            log.Info("sync.account.needs-sign-in", $"account={accountId}");
        }
        catch (Exception ex) when (IsSyncFailure(ex, ct))
        {
            log.Error("sync.account.failed", ex);
        }
    }
```

5. In `PullAsync`, after `tx.Commit();`, add:
```csharp
        if (items.Count > 0 || syncToken is null)
        {
            _changed = true;
        }
```

In `src/LeafCalendar.Core/Hosting/GoogleServices.cs`, change `Sync = new SyncEngine(Calendar, database, log);` to `Sync = new SyncEngine(Calendar, database, log, time);`.

In `src/LeafCalendar.App/ViewModels/AccountsViewModel.cs`, in the "Sync now" command, change the `SyncAllAsync(CancellationToken.None)` call to `SyncAllAsync(refreshCalendarLists: true, CancellationToken.None)`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: all PASS. Then run `dotnet build LeafCalendar.slnx -c Debug`: 0 warnings (the live tests compile against `GoogleServices` unchanged).

- [ ] **Step 6: Commit**

```bash
git add src tests/LeafCalendar.Tests
git commit -m "feat(core): refresh calendar lists every 15 minutes and signal data changes"
```

---

### Task 3: Fake-Google Test Mode Plumbing

**Files:**
- Create: `src/LeafCalendar.Core/Google/GoogleEndpoints.cs`
- Modify:
  - `src/LeafCalendar.Core/Auth/GoogleOAuthClient.cs`, `src/LeafCalendar.Core/Google/GoogleCalendarClient.cs`
  - `src/LeafCalendar.Core/Hosting/GoogleServices.cs`, `src/LeafCalendar.Core/Hosting/LaunchOptions.cs`
  - `src/LeafCalendar.App/LeafServices.cs`, `src/LeafCalendar.App/ViewModels/AccountsViewModel.cs`
  - `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (threat model row)
- Test: `tests/LeafCalendar.Tests/GoogleEndpointsTests.cs`, and `tests/LeafCalendar.Tests/LaunchOptionsTests.cs` (add cases)

**Interfaces:**
- Consumes: Milestone 1 OAuth and calendar clients.
- Produces:
  - `sealed record GoogleEndpoints(Uri Authorization, Uri Token, Uri Revoke, Uri UserInfo, Uri CalendarApi)` with:
    - `static GoogleEndpoints Default`
    - `static GoogleEndpoints ForFake(Uri root)`: paths `auth`, `token`, `revoke`, `userinfo`, `calendar/v3/`
  - `GoogleOAuthClient(HttpClient, OAuthClientCredentials, TimeProvider, GoogleEndpoints? endpoints = null)`
  - `GoogleCalendarClient(HttpClient, AccessTokenProvider, GoogleEndpoints? endpoints = null)`
  - `GoogleServices(..., TimeProvider time, GoogleEndpoints? endpoints = null)`
  - `LaunchOptions(string Profile, bool TrayProbe, Uri? FakeGoogle = null, DateOnly? StartDate = null)`
    - `--fake-google <uri>`: accepted only for an absolute `http` loopback URI, and normalized to end with `/`.
    - `--start-date yyyy-MM-dd`: kept only when `FakeGoogle` is set.
  - `LeafServices.Endpoints` and `Task LeafServices.OpenSignInPageAsync(Uri uri)`. In fake mode it GETs the URL itself, fire-and-forget; otherwise it launches the browser and throws `SignInException` if the browser won't open.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/GoogleEndpointsTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class GoogleEndpointsTests : IDisposable
{
    static readonly Uri Root = new("http://127.0.0.1:4567/");

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new();

    public void Dispose() => _google.Dispose();

    [Fact]
    public void ForFake_Root_BuildsAllPathsUnderRoot()
    {
        var endpoints = GoogleEndpoints.ForFake(Root);

        Assert.Equal("http://127.0.0.1:4567/auth", endpoints.Authorization.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/token", endpoints.Token.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/revoke", endpoints.Revoke.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/userinfo", endpoints.UserInfo.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4567/calendar/v3/", endpoints.CalendarApi.AbsoluteUri);
    }

    [Fact]
    public void Default_UsesGoogle()
    {
        Assert.Equal("https://oauth2.googleapis.com/token", GoogleEndpoints.Default.Token.AbsoluteUri);
        Assert.Equal("https://www.googleapis.com/calendar/v3/", GoogleEndpoints.Default.CalendarApi.AbsoluteUri);
    }

    [Fact]
    public async Task Clients_WithFakeEndpoints_CallFakeUrls()
    {
        var ct       = TestContext.Current.CancellationToken;
        var fake     = GoogleEndpoints.ForFake(Root);
        var http     = new HttpClient(_google);
        var store    = new InMemoryTokenStore();
        store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, "http://127.0.0.1:4567/token", HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, "http://127.0.0.1:4567/calendar/v3/users/me/calendarList", HttpStatusCode.OK, Fixture.Read("calendar-list.json"));
        var oauth    = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "secret"), _time, fake);
        using var tokens = new AccessTokenProvider(oauth, store, _time);
        var calendar = new GoogleCalendarClient(http, tokens, fake);

        var url       = oauth.BuildAuthorizationUrl(new Uri("http://127.0.0.1:5000/"), "s", "c");
        var calendars = await calendar.ListCalendarsAsync("acct", ct);

        Assert.StartsWith("http://127.0.0.1:4567/auth?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(2, calendars.Count);
        Assert.Equal("s", QueryString.Parse(url.Query)["state"]);
    }
}
```

Add to `tests/LeafCalendar.Tests/LaunchOptionsTests.cs`:
```csharp
    [Fact]
    public void Parse_FakeGoogleLoopback_AcceptsAndNormalizes()
    {
        var options = LaunchOptions.Parse(["--fake-google", "http://127.0.0.1:4567", "--start-date", "2026-10-01"]);

        Assert.Equal(new Uri("http://127.0.0.1:4567/"), options.FakeGoogle);
        Assert.Equal(new DateOnly(2026, 10, 1), options.StartDate);
    }

    [Theory]
    [InlineData("https://127.0.0.1:4567/")]
    [InlineData("http://example.com/")]
    [InlineData("http://192.168.1.5:80/")]
    [InlineData("not a url")]
    [InlineData("file:///C:/fake")]
    public void Parse_FakeGoogleNotLoopbackHttp_Ignored(string value)
    {
        var options = LaunchOptions.Parse(["--fake-google", value, "--start-date", "2026-10-01"]);

        Assert.Null(options.FakeGoogle);
        Assert.Null(options.StartDate);
    }

    [Fact]
    public void Parse_StartDateWithoutFakeGoogle_Ignored()
    {
        Assert.Null(LaunchOptions.Parse(["--start-date", "2026-10-01"]).StartDate);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*GoogleEndpointsTests" --filter-class "*LaunchOptionsTests"`
Expected: build FAILS (`GoogleEndpoints` and `LaunchOptions.FakeGoogle` not found).

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Google/GoogleEndpoints.cs`:
```csharp
namespace LeafCalendar.Core.Google;

/// <summary>
/// Where Leaf sends OAuth and Calendar requests: Google in normal use, or a loopback fake Google
/// in UI tests (see <c>--fake-google</c> in <see cref="Hosting.LaunchOptions"/>).
/// </summary>
public sealed record GoogleEndpoints(Uri Authorization, Uri Token, Uri Revoke, Uri UserInfo, Uri CalendarApi)
{
    /// <summary>Real Google.</summary>
    public static GoogleEndpoints Default { get; } = new(
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        new Uri("https://oauth2.googleapis.com/revoke"),
        new Uri("https://openidconnect.googleapis.com/v1/userinfo"),
        new Uri("https://www.googleapis.com/calendar/v3/"));

    /// <summary>A fake Google rooted at <paramref name="root"/> (which ends with <c>/</c>).</summary>
    public static GoogleEndpoints ForFake(Uri root) => new(
        new Uri(root, "auth"),
        new Uri(root, "token"),
        new Uri(root, "revoke"),
        new Uri(root, "userinfo"),
        new Uri(root, "calendar/v3/"));
}
```

`src/LeafCalendar.Core/Auth/GoogleOAuthClient.cs`:
1. Change the declaration to:
```csharp
public sealed class GoogleOAuthClient(HttpClient http, OAuthClientCredentials credentials, TimeProvider time, GoogleEndpoints? endpoints = null)
```
2. Delete the four `static readonly Uri ...Endpoint` fields and add:
```csharp
    readonly GoogleEndpoints _endpoints = endpoints ?? GoogleEndpoints.Default;
```
3. Replace the uses:

   | Old | New |
   |---|---|
   | `AuthorizationEndpoint.AbsoluteUri` | `_endpoints.Authorization.AbsoluteUri` |
   | `TokenEndpoint` | `_endpoints.Token` |
   | `RevokeEndpoint` | `_endpoints.Revoke` |
   | `UserInfoEndpoint` | `_endpoints.UserInfo` |

   Add `using LeafCalendar.Core.Google;` if it's missing.

`src/LeafCalendar.Core/Google/GoogleCalendarClient.cs`:
1. Change the declaration to:
```csharp
public sealed class GoogleCalendarClient(HttpClient http, AccessTokenProvider tokens, GoogleEndpoints? endpoints = null)
```
2. Replace `static readonly Uri BaseUri = new("https://www.googleapis.com/calendar/v3/");` with:
```csharp
    readonly Uri _baseUri = (endpoints ?? GoogleEndpoints.Default).CalendarApi;
```
3. Replace `new Uri(BaseUri, relativePath)` with `new Uri(_baseUri, relativePath)`.

`src/LeafCalendar.Core/Hosting/GoogleServices.cs`: add a trailing constructor parameter `GoogleEndpoints? endpoints = null`, and pass it through:
```csharp
        OAuth        = new GoogleOAuthClient(http, credentials, time, endpoints);
        AccessTokens = new AccessTokenProvider(OAuth, tokenStore, time);
        Calendar     = new GoogleCalendarClient(http, AccessTokens, endpoints);
```

Replace `src/LeafCalendar.Core/Hosting/LaunchOptions.cs` with:
```csharp
using System.Globalization;

namespace LeafCalendar.Core.Hosting;

/// <summary>
/// Command-line options.
/// </summary>
/// <remarks>
/// Recognized options:
/// <list type="bullet">
/// <item><c>--profile &lt;name&gt;</c> isolates data. Only <c>[A-Za-z0-9_-]{1,64}</c> is accepted; anything else means <c>default</c>.</item>
/// <item><c>--tray-probe</c> closes the window after it renders and trims memory (memory budget test).</item>
/// <item><c>--fake-google &lt;uri&gt;</c> sends every Google request to a fake server (UI tests). Only an absolute
/// <c>http</c> loopback address is accepted, so it can never point at the network. It exposes nothing new: anyone who
/// can launch Leaf with arguments already runs as this Windows user, and that user can read the Credential Locker.</item>
/// <item><c>--start-date yyyy-MM-dd</c> opens on that date and treats it as "today". It's honored only with <c>--fake-google</c>.</item>
/// </list>
/// </remarks>
public sealed record LaunchOptions(string Profile, bool TrayProbe, Uri? FakeGoogle = null, DateOnly? StartDate = null)
{
    /// <summary>Parses arguments (without the executable path).</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var profile    = "default";
        var trayProbe  = false;
        Uri? fake      = null;
        DateOnly? date = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Count:
                    profile = args[++i];
                    break;

                case "--tray-probe":
                    trayProbe = true;
                    break;

                case "--fake-google" when i + 1 < args.Count:
                    fake = ParseLoopback(args[++i]);
                    break;

                case "--start-date" when i + 1 < args.Count:
                    date = DateOnly.TryParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    break;
            }
        }

        return new LaunchOptions(IsSafeProfile(profile) ? profile : "default", trayProbe, fake, fake is null ? null : date);
    }

    static Uri? ParseLoopback(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
        {
            return null;
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    static bool IsSafeProfile(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
```

`src/LeafCalendar.App/LeafServices.cs`:
1. Add `using LeafCalendar.Core.Auth;` (already present), `using Windows.System;`.
2. Add a property and static client:
```csharp
    // Test Mode "Browser": follows the fake Google's sign-in redirect itself
    static readonly HttpClient FakeBrowser = new();

    /// <summary>Real Google, or the fake Google from <c>--fake-google</c>.</summary>
    public GoogleEndpoints Endpoints { get; }
```
3. In the constructor, before `Google = CreateGoogle();`:
```csharp
        Endpoints = options.FakeGoogle is { } fake ? GoogleEndpoints.ForFake(fake) : GoogleEndpoints.Default;
```
4. In `CreateGoogle()`, pass it: `new GoogleServices(_http, credentials, Tokens, Database, Log, Time, Endpoints);`
5. Add:
```csharp
    /// <summary>
    /// Opens Google's sign-in page. In fake-Google mode Leaf plays the browser itself. It doesn't wait,
    /// because the redirect only completes once sign-in is listening for it.
    /// </summary>
    /// <exception cref="SignInException">The browser couldn't be opened.</exception>
    public async Task OpenSignInPageAsync(Uri uri)
    {
        if (Options.FakeGoogle is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var response = await FakeBrowser.GetAsync(uri);
                }
                catch (HttpRequestException ex)
                {
                    Log.Error("signin.fake-browser.failed", ex);
                }
            });
            return;
        }

        if (!await Launcher.LaunchUriAsync(uri))
        {
            throw new SignInException("Couldn't open your browser. Try again.");
        }
    }
```

`src/LeafCalendar.App/ViewModels/AccountsViewModel.cs`: pass `_services.OpenSignInPageAsync` to `google.CreateSignIn(...)` in place of the current opener. Then delete the now-unused static `OpenBrowserAsync` method and any `using Windows.System;` that no longer has users.

Spec edit: in `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` Section 4.8, add this row to the threat table:
```markdown
| Test-mode switch pointed at a hostile server | `--fake-google` accepts only an absolute `http` loopback address. Anyone able to pass launch arguments already runs as this Windows user, who can read the Credential Locker anyway, so it exposes nothing new. |
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → all PASS. Run `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src tests/LeafCalendar.Tests docs/superpowers/specs/2026-09-29-leaf-calendar-design.md
git commit -m "feat: add loopback-only fake-Google test mode"
```

---

### Task 4: Fake Google Server, Account-Flow UI Tests, and Real Tray Memory Measurement

**Files:**
- Create: `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`, `Support/SeededProfile.cs`, `AccountFlowTests.cs`
- Modify: `tests/LeafCalendar.UITests/Support/LeafApp.cs`, `LeafCalendar.UITests.csproj`, `SetupTests.cs`, `MemoryTests.cs`, `memory-budget.json`, and the spec (Section 3.4 item 7)

**Interfaces:**
- Consumes: `--fake-google` and `--start-date` (Task 3); `QueryString`, `CredentialLockerTokenStore`, `LeafDatabase`, `AccountStore`, and `Account` from Core. The fixtures live in `tests/LeafCalendar.Tests/Fixtures`.
- Produces (namespace `LeafCalendar.UITests.Support`):
  - `sealed class FakeGoogleServer : IDisposable` with:
    - `Uri BaseUri`
    - `ConcurrentQueue<string> Requests` (`"METHOD /path?query"`)
    - `int RevokeCount`
  - `static class SeededProfile`:
    - `const string AccountId = "109876543210"`
    - `string Create()`: a profile with the fake OAuth client, a refresh token, and the account row (no calendars; the app syncs them from the fake)
  - `LeafApp` additions:
    - `static string ProfileFolder(string profile)`
    - `AutomationElement WaitForName(string name)`
    - `AutomationElement WaitForAnywhere(string automationId)` (searches every top-level window of the app, for flyouts and dialogs)
    - `bool Exists(string automationId)`
    - `void Press(params VirtualKeyShort[] keys)` (focuses the main window, then sends a chord)
  - `SetupTests.EnterCredentials` becomes `internal static` so other test classes reuse it.

- [ ] **Step 1: Link fixtures into the UI test output**

Add to `tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`:
```xml
    <ItemGroup>
        <None Include="..\LeafCalendar.Tests\Fixtures\*.json" Link="Fixtures\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>
```

- [ ] **Step 2: Write the fake server**

`tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`:
```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// A tiny loopback HTTP server that plays Google for UI tests. It serves the recorded fixtures for
/// sign-in, user info, calendar list, and events (full sync in two pages; incremental syncs return
/// no changes). Start the app with <c>--fake-google {BaseUri}</c>.
/// </summary>
public sealed class FakeGoogleServer : IDisposable
{
    const string PrimaryId = "leaf.tester@gmail.com";

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    int _revokes;

    /// <summary>Starts listening on a random loopback port.</summary>
    public FakeGoogleServer()
    {
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}/");
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Root address to pass to <c>--fake-google</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>Every request seen, as <c>"GET /path?query"</c>.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>How many token revocations were requested.</summary>
    public int RevokeCount => Volatile.Read(ref _revokes);

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, target, body) = await ReadRequestAsync(stream);
                Requests.Enqueue($"{method} {target}");

                var (status, content, location) = Route(method, target, body);
                var bytes  = Encoding.UTF8.GetBytes(content);
                var header = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {(status == 302 ? "Found" : status == 200 ? "OK" : "Not Found")}\r\n")
                    .Append("Content-Type: application/json\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Length: {bytes.Length}\r\n")
                    .Append(location is null ? "" : $"Location: {location}\r\n")
                    .Append("Connection: close\r\n\r\n")
                    .ToString();

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(bytes);
            }
            catch (IOException)
            {
                // Client went away; nothing to do in a test fake.
            }
        }
    }

    (int Status, string Content, string? Location) Route(string method, string target, string body)
    {
        var uri   = new Uri(BaseUri, target);
        var path  = uri.AbsolutePath;
        var query = QueryString.Parse(uri.Query);

        // Sign-In
        if (method == "GET" && path == "/auth")
        {
            var redirect = $"{query["redirect_uri"]}?code=fake-code&state={Uri.EscapeDataString(query["state"])}";
            return (302, "", redirect);
        }

        if (method == "POST" && path == "/token")
        {
            var grant = QueryString.Parse(body).GetValueOrDefault("grant_type");
            return (200, Read(grant == "authorization_code" ? "token-response.json" : "token-refresh.json"), null);
        }

        if (method == "POST" && path == "/revoke")
        {
            Interlocked.Increment(ref _revokes);
            return (200, "{}", null);
        }

        if (method == "GET" && path == "/userinfo")
        {
            return (200, Read("userinfo.json"), null);
        }

        // Calendar
        if (method == "GET" && path == "/calendar/v3/users/me/calendarList")
        {
            return (200, Read("calendar-list.json"), null);
        }

        const string eventsPrefix = "/calendar/v3/calendars/";
        if (method == "GET" && path.StartsWith(eventsPrefix, StringComparison.Ordinal) && path.EndsWith("/events", StringComparison.Ordinal))
        {
            var calendarId = Uri.UnescapeDataString(path[eventsPrefix.Length..^"/events".Length]);
            if (calendarId != PrimaryId)
            {
                return (200, Read("events-empty.json"), null);
            }

            if (query.ContainsKey("syncToken"))
            {
                return (200, """{"items":[],"nextSyncToken":"sync-token-1"}""", null);
            }

            return (200, Read(query.GetValueOrDefault("pageToken") == "page-2" ? "events-page2.json" : "events-page1.json"), null);
        }

        return (404, """{"error":{"code":404,"message":"No fake route"}}""", null);
    }

    string Read(string name) => File.ReadAllText(Path.Combine(_fixtures, name));

    static async Task<(string Method, string Target, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[65536];
        var length = 0;
        int headerEnd;

        // Headers
        while ((headerEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0 || (length += read) == buffer.Length)
            {
                throw new IOException("Bad request.");
            }
        }

        var head  = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        var contentLength = lines
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture))
            .FirstOrDefault();

        // Body
        var bodyStart = headerEnd + 4;
        while (length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return (parts[0], parts[1], Encoding.UTF8.GetString(buffer, bodyStart, Math.Max(0, length - bodyStart)));
    }
}
```

`tests/LeafCalendar.UITests/Support/SeededProfile.cs`:
```csharp
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Creates a profile that's already signed in to the fake Google, so view tests start on the
/// calendar. The app downloads calendars and events from <see cref="FakeGoogleServer"/> on launch.
/// </summary>
public static class SeededProfile
{
    /// <summary>The fixture account (<c>userinfo.json</c>).</summary>
    public const string AccountId = "109876543210";

    /// <summary>Creates the profile and returns its name. Clean it up with <see cref="LeafApp.DeleteProfile"/>.</summary>
    public static string Create()
    {
        var profile = LeafApp.NewProfile();

        // Secrets
        var store = new CredentialLockerTokenStore(profile);
        store.SetClientCredentials(new OAuthClientCredentials("123-uitest.apps.googleusercontent.com", "GOCSPX-uitest"));
        store.SetRefreshToken(AccountId, "1//test-refresh-token");

        // Account Row
        var database = new LeafDatabase(Path.Combine(LeafApp.ProfileFolder(profile), "leaf.db"));
        database.Migrate();
        using (var conn = database.Open())
        {
            AccountStore.Upsert(conn, new Account(AccountId, "leaf.tester@gmail.com", "Leaf Tester", null, AccountStatus.Ok));
        }

        SqliteConnection.ClearAllPools();
        return profile;
    }
}
```

- [ ] **Step 3: Extend `LeafApp`**

In `tests/LeafCalendar.UITests/Support/LeafApp.cs`:
1. Extract the `LocalState\profiles\<profile>` path used by `DeleteProfile` into:
```csharp
    /// <summary>The package's local folder for <paramref name="profile"/>.</summary>
    public static string ProfileFolder(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        Package.Id.FamilyName,
        "LocalState",
        "profiles",
        profile);
```
and make `DeleteProfile` use it.

2. Add (with `using FlaUI.Core.Input;` and `using FlaUI.Core.WindowsAPI;`):
```csharp
    /// <summary>Waits up to 15 s for an element by its accessible name.</summary>
    public AutomationElement WaitForName(string name) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element named '{name}' didn't appear.");

    /// <summary>Waits up to 15 s for an element in any of the app's windows (flyouts and dialogs can be separate).</summary>
    public AutomationElement WaitForAnywhere(string automationId) =>
        Retry.WhileNull(
            () => App.GetAllTopLevelWindows(_automation)
                .Select(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)))
                .FirstOrDefault(e => e is not null),
            TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in any window.");

    /// <summary>True when an element with this ID is currently in the main window.</summary>
    public bool Exists(string automationId) => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null;

    /// <summary>Focuses the main window and presses a key chord (e.g. Control + Shift + E).</summary>
    public void Press(params VirtualKeyShort[] keys)
    {
        MainWindow.Focus();
        Keyboard.TypeSimultaneously(keys);
    }
```

In `tests/LeafCalendar.UITests/SetupTests.cs`, change `static void EnterCredentials(...)` to `internal static void EnterCredentials(...)`.

- [ ] **Step 4: Write the account-flow tests**

`tests/LeafCalendar.UITests/AccountFlowTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class AccountFlowTests : IDisposable
{
    readonly FakeGoogleServer _google = new();

    public void Dispose() => _google.Dispose();

    LeafApp LaunchAndAddAccount(string profile)
    {
        var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri}");
        SetupTests.EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");
        leaf.WaitFor("AddAccountButton").AsButton().Invoke();
        leaf.WaitForName("2 calendars · 4 events");
        return leaf;
    }

    [Fact]
    public void AddAccount_FakeGoogle_ShowsAccountWithCounts()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            Assert.NotNull(leaf.WaitForName("leaf.tester@gmail.com"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void SyncNow_AfterAdd_RunsIncrementalSync()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            leaf.WaitFor("SyncNowButton").AsButton().Invoke();

            Assert.True(Retry.WhileFalse(() => _google.Requests.Any(r => r.Contains("syncToken=sync-token-1", StringComparison.Ordinal)), TimeSpan.FromSeconds(15)).Success);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Disconnect_Confirmed_RevokesAndRemovesAccount()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LaunchAndAddAccount(profile);

            leaf.WaitForName("Disconnect").AsButton().Invoke();
            leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

            Assert.True(Retry.WhileTrue(() => leaf.MainWindow.FindFirstDescendant(cf => cf.ByName("leaf.tester@gmail.com")) is not null, TimeSpan.FromSeconds(15)).Success);
            Assert.Equal(1, _google.RevokeCount);
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
```

- [ ] **Step 5: Run the account-flow tests**

Run: `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*AccountFlowTests" --filter-class "*SetupTests"`
Expected: 6 PASS.

- [ ] **Step 6: Measure tray memory with sync running**

Replace the body of `TrayProbe_Measured_StaysWithinBudget` in `tests/LeafCalendar.UITests/MemoryTests.cs`. Launch a seeded profile against the fake Google, so the sync loop, HTTP stack, and database are all live:
```csharp
        using var google  = new FakeGoogleServer();
        var profile       = SeededProfile.Create();
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {google.BaseUri} --tray-probe");

            // Probe closes the window at ~3 s, keeps sync running in tray mode, and trims; let it settle
            await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Contains(google.Requests, r => r.Contains("/events", StringComparison.Ordinal));

            // (existing measurement + budget assertions unchanged)
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
```
Keep the `IsNativeAot` skip at the top of the test.

Then:
1. Run `pwsh tools/publish-aot.ps1 -Register`.
2. Run `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*MemoryTests" --output Detailed` **3 times** and record the six numbers.
3. Set `trayPrivateBytesMb` to `ceil(max private × 1.2)`. Keep `trayWorkingSetMb` at 25 unless a reading exceeds 20 MB; if one does, use `ceil(max × 1.25)`.
4. Update `measuredOn`, `measuredPrivateBytesMb`, and `measuredWorkingSetMb`.
5. Update spec Section 3.4 item 7: replace the "measured with no OAuth client…" sentence with the new numbers and "measured with sync running against the fake Google".
6. Re-register Debug with `pwsh tools/dev-register.ps1`.

- [ ] **Step 7: Commit**

```bash
git add tests/LeafCalendar.UITests docs/superpowers/specs/2026-09-29-leaf-calendar-design.md
git commit -m "test(ui): drive sign-in, sync, and disconnect through a fake Google; re-measure tray memory"
```

---

---

### Task 5: Event Details Parser

**Files:**
- Create: `src/LeafCalendar.Core/Events/EventDetails.cs`, `src/LeafCalendar.Core/Events/EventDetailsParser.cs`
- Test: `tests/LeafCalendar.Tests/EventDetailsParserTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (namespace `LeafCalendar.Core.Events`):
  - `enum EventKind { Default, FocusTime, OutOfOffice, Birthday, WorkingLocation }`
  - `enum ResponseStatus { Accepted, Tentative, Declined, NeedsAction }`
  - `sealed record EventDetails(string Title, string? Location, string Description, EventKind Kind, ResponseStatus SelfResponse, string? ColorId, Uri? ConferenceUri, bool IsFree, int GuestCount, string? OrganizerEmail)`
  - `static class EventDetailsParser`:
    - `EventDetails Parse(string rawJson)`
    - `string HtmlToText(string html)` (≤ 10,000 chars)
    - `const string NoTitle = "(No title)"`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/EventDetailsParserTests.cs`:
```csharp
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class EventDetailsParserTests
{
    [Fact]
    public void Parse_MinimalEvent_UsesDefaults()
    {
        var details = EventDetailsParser.Parse("""{"id":"a","status":"confirmed"}""");

        Assert.Equal(EventDetailsParser.NoTitle, details.Title);
        Assert.Equal(EventKind.Default, details.Kind);
        Assert.Equal(ResponseStatus.Accepted, details.SelfResponse);
        Assert.Equal("", details.Description);
        Assert.Null(details.ConferenceUri);
        Assert.False(details.IsFree);
        Assert.Equal(0, details.GuestCount);
    }

    [Fact]
    public void Parse_FullEvent_ReadsFields()
    {
        var details = EventDetailsParser.Parse("""
            {
              "id": "a", "summary": "Design review", "location": "Room 4", "colorId": "5",
              "eventType": "focusTime", "transparency": "transparent",
              "organizer": { "email": "boss@example.com" },
              "attendees": [
                { "email": "me@example.com", "self": true, "responseStatus": "declined" },
                { "email": "you@example.com", "responseStatus": "accepted" }
              ],
              "hangoutLink": "https://meet.google.com/abc-defg-hij"
            }
            """);

        Assert.Equal("Design review", details.Title);
        Assert.Equal("Room 4", details.Location);
        Assert.Equal("5", details.ColorId);
        Assert.Equal(EventKind.FocusTime, details.Kind);
        Assert.True(details.IsFree);
        Assert.Equal("boss@example.com", details.OrganizerEmail);
        Assert.Equal(ResponseStatus.Declined, details.SelfResponse);
        Assert.Equal(2, details.GuestCount);
        Assert.Equal(new Uri("https://meet.google.com/abc-defg-hij"), details.ConferenceUri);
    }

    [Theory]
    [InlineData("outOfOffice", EventKind.OutOfOffice)]
    [InlineData("birthday", EventKind.Birthday)]
    [InlineData("workingLocation", EventKind.WorkingLocation)]
    [InlineData("fromGmail", EventKind.Default)]
    [InlineData("somethingNew", EventKind.Default)]
    public void Parse_EventType_MapsKind(string eventType, EventKind kind)
    {
        Assert.Equal(kind, EventDetailsParser.Parse($$"""{"id":"a","eventType":"{{eventType}}"}""").Kind);
    }

    [Fact]
    public void Parse_ConferenceDataVideoEntry_PreferredOverHangoutLink()
    {
        var details = EventDetailsParser.Parse("""
            {"id":"a","hangoutLink":"https://meet.google.com/old",
             "conferenceData":{"entryPoints":[{"entryPointType":"phone","uri":"tel:+1-555"},{"entryPointType":"video","uri":"https://zoom.us/j/123"}]}}
            """);

        Assert.Equal(new Uri("https://zoom.us/j/123"), details.ConferenceUri);
    }

    [Theory]
    [InlineData("""{"id":"a","hangoutLink":"javascript:alert(1)"}""")]
    [InlineData("""{"id":"a","hangoutLink":"http://meet.example.com/x"}""")]
    [InlineData("""{"id":"a","conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"file:///C:/evil.exe"}]}}""")]
    public void Parse_NonHttpsConference_Ignored(string json)
    {
        Assert.Null(EventDetailsParser.Parse(json).ConferenceUri);
    }

    [Fact]
    public void HtmlToText_GoogleDescription_KeepsStructureDropsTags()
    {
        var text = EventDetailsParser.HtmlToText(
            "<p>Agenda:</p><ul><li>Budget &amp; timeline</li><li><b>Hiring</b></li></ul>Join <a href=\"https://evil.example\">here</a><br>Thanks<script>alert(1)</script>");

        Assert.Equal("Agenda:\n• Budget & timeline\n• Hiring\nJoin here\nThanksalert(1)", text);
    }

    [Fact]
    public void HtmlToText_Huge_IsCapped()
    {
        Assert.Equal(10_001, EventDetailsParser.HtmlToText(new string('x', 50_000)).Length);
    }
}
```

The `HtmlToText` expectation keeps the *text* inside `<script>` (`alert(1)`) as inert plain text. Nothing is ever executed, since there's no browser. That's intended: dropping tag contents would need an HTML parser, and plain text is harmless.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EventDetailsParserTests"`
Expected: build FAILS (`LeafCalendar.Core.Events` not found).

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Events/EventDetails.cs`:
```csharp
namespace LeafCalendar.Core.Events;

/// <summary>Google's event type, as Leaf draws it.</summary>
public enum EventKind
{
    /// <summary>Ordinary event.</summary>
    Default,

    /// <summary>Focus time (Workspace).</summary>
    FocusTime,

    /// <summary>Out of office (Workspace).</summary>
    OutOfOffice,

    /// <summary>Birthday.</summary>
    Birthday,

    /// <summary>Working location (Workspace).</summary>
    WorkingLocation,
}

/// <summary>The signed-in user's response to an event.</summary>
public enum ResponseStatus
{
    /// <summary>Going (also used when you are the only attendee or the organizer).</summary>
    Accepted,

    /// <summary>Maybe.</summary>
    Tentative,

    /// <summary>Not going.</summary>
    Declined,

    /// <summary>Hasn't answered.</summary>
    NeedsAction,
}

/// <summary>
/// What Leaf shows about one event, read from Google's stored JSON. Everything here comes from people
/// who can send you invites, so it is displayed as plain text only.
/// </summary>
public sealed record EventDetails(
    string Title,
    string? Location,
    string Description,
    EventKind Kind,
    ResponseStatus SelfResponse,
    string? ColorId,
    Uri? ConferenceUri,
    bool IsFree,
    int GuestCount,
    string? OrganizerEmail);
```

`src/LeafCalendar.Core/Events/EventDetailsParser.cs`:
```csharp
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Events;

/// <summary>Reads display details from an event's raw Google JSON (AOT safe: <see cref="JsonDocument"/> only).</summary>
public static partial class EventDetailsParser
{
    /// <summary>Title shown when Google has none.</summary>
    public const string NoTitle = "(No title)";

    const int MaxDescriptionLength = 10_000;

    /// <summary>Parses one event.</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static EventDetails Parse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var title = String(root, "summary") is { Length: > 0 } summary ? summary : NoTitle;

        return new EventDetails(
            title,
            String(root, "location"),
            String(root, "description") is { } html ? HtmlToText(html) : "",
            KindOf(String(root, "eventType")),
            SelfResponse(root),
            String(root, "colorId"),
            ConferenceUri(root),
            String(root, "transparency") == "transparent",
            root.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array ? attendees.GetArrayLength() : 0,
            root.TryGetProperty("organizer", out var organizer) ? String(organizer, "email") : null);
    }

    /// <summary>
    /// Turns Google's description HTML into plain text. Line-breaking tags become newlines, list items
    /// become bullets, every other tag is dropped, entities are decoded, and consecutive newlines collapse to one.
    /// The result is capped at 10,000 characters plus an ellipsis.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var text = ListItemOpen().Replace(html, "• ");
        text = LineBreak().Replace(text, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = RepeatedNewlines().Replace(text, "\n").Trim();

        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] + "…" : text;
    }

    static EventKind KindOf(string? eventType) => eventType switch
    {
        "focusTime"       => EventKind.FocusTime,
        "outOfOffice"     => EventKind.OutOfOffice,
        "birthday"        => EventKind.Birthday,
        "workingLocation" => EventKind.WorkingLocation,
        _                 => EventKind.Default,
    };

    static ResponseStatus SelfResponse(JsonElement root)
    {
        if (!root.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
        {
            return ResponseStatus.Accepted;
        }

        foreach (var attendee in attendees.EnumerateArray())
        {
            if (attendee.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
            {
                return String(attendee, "responseStatus") switch
                {
                    "declined"    => ResponseStatus.Declined,
                    "tentative"   => ResponseStatus.Tentative,
                    "needsAction" => ResponseStatus.NeedsAction,
                    _             => ResponseStatus.Accepted,
                };
            }
        }

        return ResponseStatus.Accepted;
    }

    // Only https links count; anything else from an invite is ignored
    static Uri? ConferenceUri(JsonElement root)
    {
        if (root.TryGetProperty("conferenceData", out var conference)
            && conference.TryGetProperty("entryPoints", out var entryPoints)
            && entryPoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entryPoints.EnumerateArray())
            {
                if (String(entry, "entryPointType") == "video" && Https(String(entry, "uri")) is { } video)
                {
                    return video;
                }
            }
        }

        return Https(String(root, "hangoutLink"));
    }

    static Uri? Https(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemOpen();

    [GeneratedRegex(@"<\s*br\s*/?\s*>|<\s*/\s*(p|div|li|ul|ol|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex RepeatedNewlines();
}
```

Consecutive newlines collapse to one. `</li></ul>` would otherwise leave a blank line, and the plain-text panel reads better without it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EventDetailsParserTests"`
Expected: PASS, 13 tests. If a `HtmlToText` expectation still differs only in whitespace, fix the regex, not the test.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Events tests/LeafCalendar.Tests/EventDetailsParserTests.cs
git commit -m "feat(core): parse event display details from Google JSON as plain text"
```

---

### Task 6: Occurrence Query

**Files:**
- Create: `src/LeafCalendar.Core/Events/CalendarOccurrence.cs`, `src/LeafCalendar.Core/Events/OccurrenceQuery.cs`
- Test: `tests/LeafCalendar.Tests/OccurrenceQueryTests.cs`

**Interfaces:**
- Consumes:
  - `EventStore`, `CalendarStore` (Task 1 display columns), `AccountStore`
  - `RecurrenceExpander` (`ExpandTimed`, `ExpandAllDay`), `GoogleJsonContext.Default.GoogleEvent`
  - `EventDetailsParser`, `TestDatabase`, `Fixture`
- Produces (namespace `LeafCalendar.Core.Events`):
  - `sealed record CalendarOccurrence`, with positional members `(string AccountId, string CalendarId, string EventId, string? ICalUid, string? RecurringEventId, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string Title, EventKind Kind, ResponseStatus SelfResponse, string CalendarColor, string? ColorId, bool IsFree, bool HasConference)`, and computed:
    - `DateOnly AllDayStart`
    - `DateOnly AllDayEnd` (exclusive; meaningful when `IsAllDay`)
    - `string Key` (unique per instance)
  - All-day `Start`/`End` are UTC midnights of the dates.
  - `static class OccurrenceQuery`:
    - `IReadOnlyList<CalendarOccurrence> Load(SqliteConnection conn, DateOnly fromDate, DateOnly toDate, TimeZoneInfo zone, bool includeDeclined)`: instances overlapping local days `[fromDate, toDate)` in visible calendars, sorted by start
    - `static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/OccurrenceQueryTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OccurrenceQueryTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();

    public OccurrenceQueryTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account,
            JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Primary, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    void Insert(string json, string account = "109876543210", string calendar = Primary)
    {
        using var conn = _db.Database.Open();
        using var doc  = JsonDocument.Parse(json);
        EventStore.Apply(conn, null, account, calendar, doc.RootElement);
    }

    IReadOnlyList<CalendarOccurrence> Load(DateOnly from, DateOnly to, TimeZoneInfo? zone = null, bool includeDeclined = false)
    {
        using var conn = _db.Database.Open();
        return OccurrenceQuery.Load(conn, from, to, zone ?? NewYork, includeDeclined);
    }

    static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void Load_SingleEvent_ReturnsInstanceWithCalendarColor()
    {
        var o = Assert.Single(Load(D(10, 1), D(10, 2)));

        Assert.Equal("evt-single", o.EventId);
        Assert.Equal("Dentist appointment", o.Title);
        Assert.Equal("#9fe1e7", o.CalendarColor);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), o.Start);
        Assert.False(o.IsAllDay);
    }

    [Fact]
    public void Load_CancelledException_HidesInstance()
    {
        var starts = Load(D(10, 5), D(10, 12)).Where(o => o.RecurringEventId == "evt-weekly").Select(o => o.Start.UtcDateTime.Day);

        Assert.Equal([5, 9], starts);
    }

    [Fact]
    public void Load_MovedException_ReplacesOriginalInstance()
    {
        Insert("""
            {"id":"evt-weekly_20261009T133000Z","status":"confirmed","recurringEventId":"evt-weekly","summary":"Standup (moved)",
             "originalStartTime":{"dateTime":"2026-10-09T09:30:00-04:00","timeZone":"America/New_York"},
             "start":{"dateTime":"2026-10-10T11:00:00-04:00"},"end":{"dateTime":"2026-10-10T11:30:00-04:00"}}
            """);

        var weekly = Load(D(10, 5), D(10, 12)).Where(o => o.RecurringEventId == "evt-weekly").ToList();

        Assert.Equal([5, 10], weekly.Select(o => o.Start.UtcDateTime.Day));
        Assert.Equal("Standup (moved)", weekly[1].Title);
    }

    [Fact]
    public void Load_AllDayEvent_SameDateInEveryZone()
    {
        foreach (var zone in new[] { "Asia/Tokyo", "America/Los_Angeles", "Pacific/Kiritimati", "Pacific/Pago_Pago" })
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);

            var o = Assert.Single(Load(D(10, 12), D(10, 13), tz));
            Assert.True(o.IsAllDay);
            Assert.Equal(D(10, 12), o.AllDayStart);
            Assert.Equal(D(10, 13), o.AllDayEnd);

            Assert.DoesNotContain(Load(D(10, 11), D(10, 12), tz), x => x.EventId == "evt-allday");
            Assert.DoesNotContain(Load(D(10, 13), D(10, 14), tz), x => x.EventId == "evt-allday");
        }
    }

    [Fact]
    public void Load_AllDayWeeklySeries_ExpandsByDate()
    {
        Insert("""
            {"id":"evt-yoga","status":"confirmed","summary":"Yoga","recurrence":["RRULE:FREQ=WEEKLY"],
             "start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}
            """);

        var o = Assert.Single(Load(D(10, 8), D(10, 9)), x => x.RecurringEventId == "evt-yoga");

        Assert.Equal(D(10, 8), o.AllDayStart);
    }

    [Fact]
    public void Load_EventStartingBeforeWindow_Included()
    {
        Insert("""
            {"id":"evt-late","status":"confirmed","summary":"Late shift",
             "start":{"dateTime":"2026-09-30T22:00:00-04:00"},"end":{"dateTime":"2026-10-01T02:00:00-04:00"}}
            """);

        Assert.Contains(Load(D(10, 1), D(10, 2)), o => o.EventId == "evt-late");
    }

    [Fact]
    public void Load_HiddenCalendar_Excluded()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Primary, hidden: true);
        }

        Assert.Empty(Load(D(10, 1), D(10, 31)));
    }

    [Fact]
    public void Load_Declined_ExcludedUnlessRequested()
    {
        Insert("""
            {"id":"evt-no","status":"confirmed","summary":"Optional sync","attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],
             "start":{"dateTime":"2026-10-02T09:00:00-04:00"},"end":{"dateTime":"2026-10-02T10:00:00-04:00"}}
            """);

        Assert.DoesNotContain(Load(D(10, 2), D(10, 3)), o => o.EventId == "evt-no");
        Assert.Equal(ResponseStatus.Declined, Assert.Single(Load(D(10, 2), D(10, 3), includeDeclined: true), o => o.EventId == "evt-no").SelfResponse);
    }

    [Fact]
    public void Load_SameEventInTwoAccounts_ShownOnce()
    {
        using (var conn = _db.Database.Open())
        {
            AccountStore.Upsert(conn, new Account("222", "zzz.second@gmail.com", null, null, AccountStatus.Ok));
            CalendarStore.ReplaceForAccount(conn, "222", [new CalendarListEntry { Id = "zzz.second@gmail.com", Summary = "Second", AccessRole = "owner", Primary = true, Selected = true }]);
        }

        Insert("""
            {"id":"copy-in-second","status":"confirmed","iCalUID":"evt-single@google.com","summary":"Dentist appointment",
             "start":{"dateTime":"2026-10-01T09:00:00-04:00"},"end":{"dateTime":"2026-10-01T10:00:00-04:00"}}
            """, account: "222", calendar: "zzz.second@gmail.com");

        var o = Assert.Single(Load(D(10, 1), D(10, 2)));
        Assert.Equal(Account, o.AccountId);
    }

    [Fact]
    public void Load_LeafColorOverride_Used()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetColor(conn, Account, Primary, "#16A765");
        }

        Assert.Equal("#16A765", Assert.Single(Load(D(10, 1), D(10, 2))).CalendarColor);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*OccurrenceQueryTests"`
Expected: build FAILS (`OccurrenceQuery` not found).

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Events/CalendarOccurrence.cs`:
```csharp
using System.Globalization;

namespace LeafCalendar.Core.Events;

/// <summary>
/// One event instance on screen: a single event, one instance of a repeating series, or a
/// moved or edited instance.
/// </summary>
/// <remarks>
/// For all-day events, <see cref="Start"/> and <see cref="End"/> are UTC midnights of the dates.
/// Use <see cref="AllDayStart"/> and <see cref="AllDayEnd"/>, because all-day dates float across time zones.
/// </remarks>
public sealed record CalendarOccurrence(
    string AccountId,
    string CalendarId,
    string EventId,
    string? ICalUid,
    string? RecurringEventId,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string Title,
    EventKind Kind,
    ResponseStatus SelfResponse,
    string CalendarColor,
    string? ColorId,
    bool IsFree,
    bool HasConference)
{
    /// <summary>First all-day date.</summary>
    public DateOnly AllDayStart => DateOnly.FromDateTime(Start.UtcDateTime);

    /// <summary>All-day end date (exclusive).</summary>
    public DateOnly AllDayEnd => DateOnly.FromDateTime(End.UtcDateTime);

    /// <summary>Unique per instance (a series' instances differ by start).</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{AccountId}|{CalendarId}|{EventId}|{Start.UtcTicks}");
}
```

`src/LeafCalendar.Core/Events/OccurrenceQuery.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Recurrence;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Events;

/// <summary>
/// Turns stored events into the instances visible in a date range.
/// </summary>
/// <remarks>
/// Repeating series are expanded with <see cref="RecurrenceExpander"/>. An exception row (a moved,
/// edited, or canceled single instance) replaces the series instance that started at its
/// <c>originalStartTime</c>. Only visible calendars are included. Declined events are dropped unless
/// asked for. The same event seen in two of your accounts (same iCalUID and start) is kept once.
/// </remarks>
public static class OccurrenceQuery
{
    // Exceptions can move an instance far; look this far around the window for them
    static readonly TimeSpan ExceptionPad = TimeSpan.FromDays(32);

    const string Sql = """
        SELECT e.account_id, e.calendar_id, e.id, e.ical_uid, e.status, e.start_utc, e.end_utc, e.is_all_day,
               e.start_time_zone, e.is_recurring_master, e.recurring_event_id, e.original_start_utc, e.raw_json,
               COALESCE(c.leaf_color, c.background_color, '#4285F4')
        FROM events e
        JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
        JOIN accounts a  ON a.id = e.account_id
        WHERE COALESCE(c.leaf_hidden, c.hidden) = 0
          AND (
                (e.is_recurring_master = 1 AND e.status <> 'cancelled' AND e.start_utc < $to)
             OR (e.is_recurring_master = 0 AND e.recurring_event_id IS NULL AND e.status <> 'cancelled'
                 AND e.start_utc < $to AND e.end_utc > $from)
             OR (e.recurring_event_id IS NOT NULL
                 AND ((e.original_start_utc >= $padFrom AND e.original_start_utc < $padTo)
                   OR (e.start_utc < $to AND e.end_utc > $from)))
          )
        ORDER BY a.email, c.sort_order;
        """;

    /// <summary>Loads instances overlapping local days <c>[fromDate, toDate)</c>, sorted by start (longer first on ties).</summary>
    public static IReadOnlyList<CalendarOccurrence> Load(SqliteConnection conn, DateOnly fromDate, DateOnly toDate, TimeZoneInfo zone, bool includeDeclined)
    {
        var from = LocalMidnight(fromDate, zone);
        var to   = LocalMidnight(toDate, zone);

        // Broad SQL window (a day of slack for all-day dates), refined below
        var rows = conn.Query(
            null,
            Sql,
            Row.Read,
            ("$from", (from - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()),
            ("$to", (to + TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()),
            ("$padFrom", (from - ExceptionPad).ToUnixTimeMilliseconds()),
            ("$padTo", (to + ExceptionPad).ToUnixTimeMilliseconds()));

        // Series Instances Replaced By An Exception
        var replaced = rows
            .Where(r => r.RecurringEventId is not null && r.OriginalStartMs is not null)
            .Select(r => (r.AccountId, r.CalendarId, r.RecurringEventId!, r.OriginalStartMs!.Value))
            .ToHashSet();

        var result = new List<CalendarOccurrence>();
        foreach (var row in rows)
        {
            if (row.IsMaster)
            {
                var details = EventDetailsParser.Parse(row.RawJson);
                foreach (var (start, end) in ExpandMaster(row, fromDate, toDate, from, to))
                {
                    if (!replaced.Contains((row.AccountId, row.CalendarId, row.Id, start.ToUnixTimeMilliseconds())))
                    {
                        result.Add(Create(row, details, start, end, recurringEventId: row.Id));
                    }
                }

                continue;
            }

            if (row.Status == "cancelled" || row.StartMs is not { } startMs || row.EndMs is not { } endMs)
            {
                continue;
            }

            var s = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
            var e = DateTimeOffset.FromUnixTimeMilliseconds(endMs);
            if (Overlaps(row.IsAllDay, s, e, fromDate, toDate, from, to))
            {
                result.Add(Create(row, EventDetailsParser.Parse(row.RawJson), s, e, row.RecurringEventId));
            }
        }

        // Declined, Duplicates, Order
        var seen = new HashSet<(string, long)>();
        return result
            .Where(o => includeDeclined || o.SelfResponse != ResponseStatus.Declined)
            .Where(o => o.ICalUid is null || seen.Add((o.ICalUid, o.Start.ToUnixTimeMilliseconds())))
            .OrderBy(o => o.Start)
            .ThenByDescending(o => o.End)
            .ToList();
    }

    /// <summary>The instant local midnight starts <paramref name="day"/> in <paramref name="zone"/> (skipping a DST gap).</summary>
    public static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ExpandMaster(Row row, DateOnly fromDate, DateOnly toDate, DateTimeOffset from, DateTimeOffset to)
    {
        if (row.StartMs is not { } startMs || row.EndMs is not { } endMs)
        {
            yield break;
        }

        var start      = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
        var duration   = DateTimeOffset.FromUnixTimeMilliseconds(endMs) - start;
        var ev         = JsonSerializer.Deserialize(row.RawJson, GoogleJsonContext.Default.GoogleEvent);
        var recurrence = ev?.Recurrence ?? [];

        // All-Day Series: expand by date
        if (row.IsAllDay)
        {
            var days = (int)Math.Max(1, Math.Round(duration.TotalDays));
            foreach (var date in RecurrenceExpander.ExpandAllDay(recurrence, DateOnly.FromDateTime(start.UtcDateTime), fromDate.AddDays(-days), toDate))
            {
                if (date.AddDays(days) > fromDate)
                {
                    var s = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    yield return (s, s + duration);
                }
            }

            yield break;
        }

        // Timed Series: expand in the event's own zone so wall-clock time survives DST
        var anchor = ev?.Start?.DateTime ?? start;
        foreach (var s in RecurrenceExpander.ExpandTimed(recurrence, anchor, row.TimeZone, from - duration, to))
        {
            if (s + duration > from)
            {
                yield return (s, s + duration);
            }
        }
    }

    static bool Overlaps(bool isAllDay, DateTimeOffset start, DateTimeOffset end, DateOnly fromDate, DateOnly toDate, DateTimeOffset from, DateTimeOffset to) =>
        isAllDay
            ? DateOnly.FromDateTime(start.UtcDateTime) < toDate && DateOnly.FromDateTime(end.UtcDateTime) > fromDate
            : start < to && end > from;

    static CalendarOccurrence Create(Row row, EventDetails details, DateTimeOffset start, DateTimeOffset end, string? recurringEventId) => new(
        row.AccountId,
        row.CalendarId,
        row.Id,
        row.ICalUid,
        recurringEventId,
        start,
        end,
        row.IsAllDay,
        details.Title,
        details.Kind,
        details.SelfResponse,
        row.CalendarColor,
        details.ColorId,
        details.IsFree,
        details.ConferenceUri is not null);

    sealed record Row(
        string AccountId,
        string CalendarId,
        string Id,
        string? ICalUid,
        string Status,
        long? StartMs,
        long? EndMs,
        bool IsAllDay,
        string? TimeZone,
        bool IsMaster,
        string? RecurringEventId,
        long? OriginalStartMs,
        string RawJson,
        string CalendarColor)
    {
        public static Row Read(SqliteDataReader r) => new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.GetStringOrNull(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetInt64(6),
            r.GetBoolean(7),
            r.GetStringOrNull(8),
            r.GetBoolean(9),
            r.GetStringOrNull(10),
            r.IsDBNull(11) ? null : r.GetInt64(11),
            r.GetString(12),
            r.GetString(13));
    }
}
```

The `InternalsVisibleTo`-free access above works because `SqliteExtensions`, `GoogleJsonContext`, and `Row` are all in the Core assembly.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*OccurrenceQueryTests"`
Expected: PASS, 10 tests.

If `Load_MovedException_ReplacesOriginalInstance` shows Oct 9 still present, the replaced key didn't match. Check that the exception's `original_start_utc` equals the expanded instant in milliseconds, and fix the matching. Don't change the test.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Events tests/LeafCalendar.Tests/OccurrenceQueryTests.cs
git commit -m "feat(core): load visible event instances with series expansion and exceptions"
```

---

### Task 7: Sliding Event Cache

**Files:**
- Create: `src/LeafCalendar.Core/Events/EventWindowCache.cs`
- Test: `tests/LeafCalendar.Tests/EventWindowCacheTests.cs`

**Interfaces:**
- Consumes: `CalendarOccurrence`, `OccurrenceQuery.LocalMidnight` (Task 6).
- Produces: `sealed class EventWindowCache(Func<DateOnly, DateOnly, CancellationToken, Task<IReadOnlyList<CalendarOccurrence>>> load, TimeZoneInfo zone) : IDisposable`, with:
  - `const int MonthsAround = 3`
  - `Task EnsureAsync(DateOnly visibleStart, DateOnly visibleEnd, CancellationToken ct)`: loads missing months in `[month(visibleStart) − 3, month(visibleEnd − 1) + 3]`, nearest first, and evicts the rest
  - `Task RefreshAsync(CancellationToken ct)`: reloads every loaded month and swaps atomically, with no blank flash
  - `IReadOnlyList<CalendarOccurrence> ForDay(DateOnly day)`: instances touching that local day
  - `IReadOnlyCollection<DateOnly> LoadedMonths`
  - `event EventHandler? Changed`
  - The loader is called once per month as `[firstOfMonth, firstOfNextMonth)`.
  - Calls are serialized: overlapping `EnsureAsync` calls wait their turn.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/EventWindowCacheTests.cs`:
```csharp
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public sealed class EventWindowCacheTests : IDisposable
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly List<DateOnly> _loads = [];
    readonly List<CalendarOccurrence> _data = [];
    readonly EventWindowCache _cache;

    public EventWindowCacheTests() =>
        _cache = new EventWindowCache(
            (from, to, _) =>
            {
                _loads.Add(from);
                var f = OccurrenceQuery.LocalMidnight(from, Zone);
                var t = OccurrenceQuery.LocalMidnight(to, Zone);
                return Task.FromResult<IReadOnlyList<CalendarOccurrence>>(_data.Where(o => o.Start < t && o.End > f).ToList());
            },
            Zone);

    public void Dispose() => _cache.Dispose();

    static DateOnly D(int year, int month, int day) => new(year, month, day);

    static CalendarOccurrence Timed(string id, DateTimeOffset start, DateTimeOffset end) =>
        new("a", "c", id, null, null, start, end, false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static CalendarOccurrence AllDay(string id, DateOnly start, int days)
    {
        var s = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new("a", "c", id, null, null, s, s.AddDays(days), true, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);
    }

    [Fact]
    public async Task EnsureAsync_OneWeek_LoadsSevenMonthsNearestFirst()
    {
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), TestContext.Current.CancellationToken);

        Assert.Equal(7, _loads.Count);
        Assert.Equal(D(2026, 10, 1), _loads[0]);
        Assert.Equal(Enumerable.Range(-3, 7).Select(m => D(2026, 10, 1).AddMonths(m)).Order(), _cache.LoadedMonths.Order());
    }

    [Fact]
    public async Task EnsureAsync_SameRangeAgain_LoadsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), ct);
        _loads.Clear();

        await _cache.EnsureAsync(D(2026, 10, 18), D(2026, 10, 25), ct);

        Assert.Empty(_loads);
    }

    [Fact]
    public async Task EnsureAsync_NextMonth_LoadsOneAndEvictsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), ct);
        _loads.Clear();

        await _cache.EnsureAsync(D(2026, 11, 1), D(2026, 11, 8), ct);

        Assert.Equal([D(2027, 2, 1)], _loads);
        Assert.DoesNotContain(D(2026, 7, 1), _cache.LoadedMonths);
    }

    [Fact]
    public async Task ForDay_OvernightTimedEvent_OnBothLocalDays()
    {
        _data.Add(Timed("night", new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 10, 2, 2, 0, 0, TimeSpan.FromHours(-4))));

        await _cache.EnsureAsync(D(2026, 10, 1), D(2026, 10, 8), TestContext.Current.CancellationToken);

        Assert.Single(_cache.ForDay(D(2026, 10, 1)));
        Assert.Single(_cache.ForDay(D(2026, 10, 2)));
        Assert.Empty(_cache.ForDay(D(2026, 10, 3)));
    }

    [Fact]
    public async Task ForDay_EventEndingAtMidnight_NotOnNextDay()
    {
        _data.Add(Timed("eve", new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.FromHours(-4))));

        await _cache.EnsureAsync(D(2026, 10, 1), D(2026, 10, 8), TestContext.Current.CancellationToken);

        Assert.Empty(_cache.ForDay(D(2026, 10, 2)));
    }

    [Fact]
    public async Task ForDay_MultiDayAllDayAcrossMonthBoundary_EachDayOnce()
    {
        _data.Add(AllDay("trip", D(2026, 10, 30), 4));

        await _cache.EnsureAsync(D(2026, 10, 25), D(2026, 11, 1), TestContext.Current.CancellationToken);

        foreach (var day in new[] { D(2026, 10, 30), D(2026, 10, 31), D(2026, 11, 1), D(2026, 11, 2) })
        {
            Assert.Single(_cache.ForDay(day));
        }

        Assert.Empty(_cache.ForDay(D(2026, 11, 3)));
    }

    [Fact]
    public async Task RefreshAsync_ReloadsEveryLoadedMonthAndRaisesChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cache.EnsureAsync(D(2026, 10, 4), D(2026, 10, 11), ct);
        _loads.Clear();
        var changed = 0;
        _cache.Changed += (_, _) => changed++;
        _data.Add(Timed("new", new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(-4))));

        await _cache.RefreshAsync(ct);

        Assert.Equal(7, _loads.Count);
        Assert.Equal(1, changed);
        Assert.Single(_cache.ForDay(D(2026, 10, 5)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EventWindowCacheTests"`
Expected: build FAILS (`EventWindowCache` not found).

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Events/EventWindowCache.cs`:
```csharp
namespace LeafCalendar.Core.Events;

/// <summary>
/// Keeps event instances in memory for 3 months before and 3 months after what's on screen.
/// </summary>
/// <remarks>
/// Data is loaded a month at a time through the loader (which should run the database work off
/// the UI thread), nearest month first. <see cref="Changed"/> is raised after each month, so the
/// visible month appears first. Months outside the window are dropped. Calls are serialized; call
/// from the UI thread. Instances are bucketed by local day, so a view asks
/// <see cref="ForDay"/> without touching the database.
/// </remarks>
public sealed class EventWindowCache(Func<DateOnly, DateOnly, CancellationToken, Task<IReadOnlyList<CalendarOccurrence>>> load, TimeZoneInfo zone) : IDisposable
{
    /// <summary>Months kept on each side of the visible range.</summary>
    public const int MonthsAround = 3;

    readonly SemaphoreSlim _gate = new(1, 1);
    Dictionary<DateOnly, IReadOnlyList<CalendarOccurrence>> _months = [];
    Dictionary<DateOnly, List<CalendarOccurrence>> _byDay = [];

    /// <summary>Raised on the calling thread whenever the cached data changes.</summary>
    public event EventHandler? Changed;

    /// <summary>First days of the months currently held.</summary>
    public IReadOnlyCollection<DateOnly> LoadedMonths => _months.Keys;

    /// <summary>Makes sure the months around <c>[visibleStart, visibleEnd)</c> are loaded.</summary>
    public async Task EnsureAsync(DateOnly visibleStart, DateOnly visibleEnd, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var first = MonthOf(visibleStart).AddMonths(-MonthsAround);
            var last  = MonthOf(visibleEnd.AddDays(-1)).AddMonths(MonthsAround);

            // Evict
            var evicted = false;
            foreach (var month in _months.Keys.Where(m => m < first || m > last).ToList())
            {
                _months.Remove(month);
                evicted = true;
            }

            // Load Missing, Nearest First
            var missing = Months(first, last)
                .Where(m => !_months.ContainsKey(m))
                .OrderBy(m => Math.Abs(m.DayNumber - MonthOf(visibleStart).DayNumber))
                .ToList();

            if (missing.Count == 0)
            {
                if (evicted)
                {
                    Rebuild();
                }

                return;
            }

            foreach (var month in missing)
            {
                _months[month] = await load(month, month.AddMonths(1), ct);
                Rebuild();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reloads every held month (after a sync or a filter change) and swaps them in at once.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var fresh = new Dictionary<DateOnly, IReadOnlyList<CalendarOccurrence>>();
            foreach (var month in _months.Keys.ToList())
            {
                fresh[month] = await load(month, month.AddMonths(1), ct);
            }

            _months = fresh;
            Rebuild();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Instances touching <paramref name="day"/> in local time (empty when not loaded).</summary>
    public IReadOnlyList<CalendarOccurrence> ForDay(DateOnly day) => _byDay.TryGetValue(day, out var list) ? list : [];

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    void Rebuild()
    {
        var byDay = new Dictionary<DateOnly, List<CalendarOccurrence>>();

        foreach (var occurrence in _months.Values.SelectMany(m => m).DistinctBy(o => o.Key))
        {
            var (first, last) = occurrence.IsAllDay
                ? (occurrence.AllDayStart, occurrence.AllDayEnd.AddDays(-1))
                : (LocalDate(occurrence.Start), LocalDate(occurrence.End.AddTicks(-1)));

            for (var day = first; day <= (last < first ? first : last); day = day.AddDays(1))
            {
                if (!byDay.TryGetValue(day, out var list))
                {
                    byDay[day] = list = [];
                }

                list.Add(occurrence);
            }
        }

        _byDay = byDay;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    static IEnumerable<DateOnly> Months(DateOnly first, DateOnly last)
    {
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            yield return month;
        }
    }
}
```

`RefreshAsync` raises `Changed` once through `Rebuild()`. `EnsureAsync` raises it once per loaded month, and once on eviction. The `RefreshAsync` test subscribes after the first `EnsureAsync`, so it counts exactly 1.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EventWindowCacheTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Events/EventWindowCache.cs tests/LeafCalendar.Tests/EventWindowCacheTests.cs
git commit -m "feat(core): add sliding three-month event cache bucketed by local day"
```

---

### Task 8: View Navigation, Day Strip, and Shortcut Map

**Files:**
- Create: `src/LeafCalendar.Core/Views/ViewNavigator.cs`, `Views/DayStrip.cs`, `Views/ShortcutMap.cs`
- Test: `tests/LeafCalendar.Tests/ViewNavigatorTests.cs`, `DayStripTests.cs`, `ShortcutMapTests.cs`

**Interfaces:**
- Consumes: `CalendarViewMode` (Task 1).
- Produces (namespace `LeafCalendar.Core.Views`):
  - `static class ViewNavigator`:
    - `int VisibleColumnCount(CalendarViewMode, int customDays, bool showWeekends)`
    - `DateOnly WeekStartOf(DateOnly, DayOfWeek)` and `DateOnly MonthStartOf(DateOnly)`
    - `DateOnly PeriodStart(CalendarViewMode, DateOnly anchor, DayOfWeek weekStart)`
    - `DateOnly Step(CalendarViewMode, DateOnly periodStart, int direction, int customDays)`
    - `string PeriodTitle(DateOnly first, DateOnly lastInclusive)` and `string MonthTitle(DateOnly)`
    - `int WeekNumber(DateOnly)` (ISO) and `bool IsWeekend(DateOnly)`
    - `(DateOnly GridStart, int Weeks) MonthGrid(DateOnly anyDayInMonth, DayOfWeek weekStart)`
  - `sealed class DayStrip(DateOnly origin, int daysBefore, int daysAfter, bool skipWeekends)`, with:
    - Members: `Count`, `this[int]` (clamped), `First`, `Last`, `SkipsWeekends`, `Contains(DateOnly)`
    - `int IndexOf(DateOnly)`: an exact match, otherwise the next later day in the strip
  - `enum CalendarCommand { None, Today, Previous, Next, DayView, WeekView, MonthView, Days, GoToDate, ToggleWeekends, ToggleDeclined, ZoomIn, ZoomOut, ZoomReset, ToggleTheme, NextEvent, PreviousEvent }`
  - `readonly record struct ShortcutResult(CalendarCommand Command, int Days = 0)`
  - `static class ShortcutMap` with `ShortcutResult Resolve(string key, bool ctrl, bool shift, bool alt)`. `key` is `Windows.System.VirtualKey.ToString()`, which is a number for OEM keys, e.g. `"190"` for period and `"187"` for `=`.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/ViewNavigatorTests.cs`:
```csharp
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ViewNavigatorTests
{
    static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(CalendarViewMode.Day, 3, true, 1)]
    [InlineData(CalendarViewMode.Week, 3, true, 7)]
    [InlineData(CalendarViewMode.Week, 3, false, 5)]
    [InlineData(CalendarViewMode.Days, 4, false, 4)]
    [InlineData(CalendarViewMode.Days, 99, true, 31)]
    [InlineData(CalendarViewMode.Month, 3, false, 5)]
    public void VisibleColumnCount_Mode_MatchesSpec(CalendarViewMode mode, int days, bool weekends, int expected)
    {
        Assert.Equal(expected, ViewNavigator.VisibleColumnCount(mode, days, weekends));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 2026, 9, 27)]
    [InlineData(DayOfWeek.Monday, 2026, 9, 28)]
    [InlineData(DayOfWeek.Saturday, 2026, 9, 26)]
    public void WeekStartOf_Thursday_UsesSetting(DayOfWeek start, int y, int m, int d)
    {
        Assert.Equal(D(y, m, d), ViewNavigator.WeekStartOf(D(2026, 10, 1), start));
    }

    [Fact]
    public void Step_EachMode_MovesOnePeriod()
    {
        Assert.Equal(D(2026, 10, 2), ViewNavigator.Step(CalendarViewMode.Day, D(2026, 10, 1), 1, 3));
        Assert.Equal(D(2026, 9, 20), ViewNavigator.Step(CalendarViewMode.Week, D(2026, 9, 27), -1, 3));
        Assert.Equal(D(2026, 11, 1), ViewNavigator.Step(CalendarViewMode.Month, D(2026, 10, 17), 1, 3));
        Assert.Equal(D(2026, 10, 5), ViewNavigator.Step(CalendarViewMode.Days, D(2026, 10, 1), 1, 4));
    }

    [Theory]
    [InlineData(2026, 10, 4, 2026, 10, 10, "October 2026")]
    [InlineData(2026, 9, 27, 2026, 10, 3, "Sep – Oct 2026")]
    [InlineData(2026, 12, 27, 2027, 1, 2, "Dec 2026 – Jan 2027")]
    public void PeriodTitle_Range_FormatsLikeSpec(int y1, int m1, int d1, int y2, int m2, int d2, string expected)
    {
        Assert.Equal(expected, ViewNavigator.PeriodTitle(D(y1, m1, d1), D(y2, m2, d2)));
    }

    [Fact]
    public void MonthGrid_October2026SundayStart_StartsSep27SixWeeksNotNeeded()
    {
        var (start, weeks) = ViewNavigator.MonthGrid(D(2026, 10, 15), DayOfWeek.Sunday);

        Assert.Equal(D(2026, 9, 27), start);
        Assert.Equal(5, weeks);
    }

    [Fact]
    public void WeekNumber_Iso_Correct()
    {
        Assert.Equal(40, ViewNavigator.WeekNumber(D(2026, 10, 1)));
        Assert.Equal(53, ViewNavigator.WeekNumber(D(2026, 12, 31)));
    }
}
```

`tests/LeafCalendar.Tests/DayStripTests.cs`:
```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class DayStripTests
{
    static readonly DateOnly Origin = new(2026, 10, 1); // Thursday

    [Fact]
    public void Strip_AllDays_IndexRoundTrips()
    {
        var strip = new DayStrip(Origin, 10, 10, skipWeekends: false);

        Assert.Equal(21, strip.Count);
        Assert.Equal(Origin, strip[strip.IndexOf(Origin)]);
        Assert.Equal(Origin.AddDays(-10), strip.First);
    }

    [Fact]
    public void Strip_SkipWeekends_HasNoWeekendDays()
    {
        var strip = new DayStrip(Origin, 14, 14, skipWeekends: true);

        Assert.All(Enumerable.Range(0, strip.Count), i => Assert.False(ViewNavigator.IsWeekend(strip[i])));
    }

    [Fact]
    public void IndexOf_WeekendWhileHidden_SnapsToNextWeekday()
    {
        var strip = new DayStrip(Origin, 14, 14, skipWeekends: true);

        Assert.Equal(new DateOnly(2026, 10, 5), strip[strip.IndexOf(new DateOnly(2026, 10, 3))]);
        Assert.Equal(new DateOnly(2026, 10, 5), strip[strip.IndexOf(new DateOnly(2026, 10, 4))]);
    }

    [Fact]
    public void IndexOf_OutsideStrip_Clamps()
    {
        var strip = new DayStrip(Origin, 5, 5, skipWeekends: false);

        Assert.Equal(0, strip.IndexOf(Origin.AddDays(-100)));
        Assert.Equal(strip.Count - 1, strip.IndexOf(Origin.AddDays(100)));
        Assert.Equal(strip.Last, strip[10_000]);
    }
}
```

`tests/LeafCalendar.Tests/ShortcutMapTests.cs`:
```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ShortcutMapTests
{
    [Theory]
    [InlineData("T", false, false, CalendarCommand.Today, 0)]
    [InlineData("Left", false, false, CalendarCommand.Previous, 0)]
    [InlineData("Right", false, false, CalendarCommand.Next, 0)]
    [InlineData("J", false, false, CalendarCommand.Next, 0)]
    [InlineData("K", false, false, CalendarCommand.Previous, 0)]
    [InlineData("D", false, false, CalendarCommand.DayView, 0)]
    [InlineData("Number1", false, false, CalendarCommand.DayView, 0)]
    [InlineData("W", false, false, CalendarCommand.WeekView, 0)]
    [InlineData("Number0", false, false, CalendarCommand.WeekView, 0)]
    [InlineData("M", false, false, CalendarCommand.MonthView, 0)]
    [InlineData("Number4", false, false, CalendarCommand.Days, 4)]
    [InlineData("NumberPad9", false, false, CalendarCommand.Days, 9)]
    [InlineData("190", false, false, CalendarCommand.GoToDate, 0)]
    [InlineData("Decimal", false, false, CalendarCommand.GoToDate, 0)]
    [InlineData("N", false, false, CalendarCommand.NextEvent, 0)]
    [InlineData("B", false, false, CalendarCommand.PreviousEvent, 0)]
    [InlineData("N", false, true, CalendarCommand.PreviousEvent, 0)]
    [InlineData("E", true, true, CalendarCommand.ToggleWeekends, 0)]
    [InlineData("D", true, true, CalendarCommand.ToggleDeclined, 0)]
    [InlineData("L", true, true, CalendarCommand.ToggleTheme, 0)]
    [InlineData("187", true, false, CalendarCommand.ZoomIn, 0)]
    [InlineData("Add", true, false, CalendarCommand.ZoomIn, 0)]
    [InlineData("189", true, false, CalendarCommand.ZoomOut, 0)]
    [InlineData("Number0", true, false, CalendarCommand.ZoomReset, 0)]
    public void Resolve_KnownChord_MapsCommand(string key, bool ctrl, bool shift, CalendarCommand command, int days)
    {
        Assert.Equal(new ShortcutResult(command, days), ShortcutMap.Resolve(key, ctrl, shift, alt: false));
    }

    [Theory]
    [InlineData("T", false, false, true)]
    [InlineData("T", true, false, false)]
    [InlineData("T", false, true, false)]
    [InlineData("Q", false, false, false)]
    [InlineData("E", true, false, false)]
    public void Resolve_Unmapped_ReturnsNone(string key, bool ctrl, bool shift, bool alt)
    {
        Assert.Equal(CalendarCommand.None, ShortcutMap.Resolve(key, ctrl, shift, alt).Command);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*ViewNavigatorTests" --filter-class "*DayStripTests" --filter-class "*ShortcutMapTests"`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Views/ViewNavigator.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>Date math for the calendar views. English-only formatting (localization is deferred).</summary>
public static class ViewNavigator
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>How many day columns the view shows.</summary>
    public static int VisibleColumnCount(CalendarViewMode mode, int customDays, bool showWeekends) => mode switch
    {
        CalendarViewMode.Day  => 1,
        CalendarViewMode.Days => Math.Clamp(customDays, 1, 31),
        _                     => showWeekends ? 7 : 5,
    };

    /// <summary>The first day of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStartOf(DateOnly date, DayOfWeek weekStart) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)weekStart + 7) % 7));

    /// <summary>The first day of the month.</summary>
    public static DateOnly MonthStartOf(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>Where the period containing <paramref name="anchor"/> starts.</summary>
    public static DateOnly PeriodStart(CalendarViewMode mode, DateOnly anchor, DayOfWeek weekStart) => mode switch
    {
        CalendarViewMode.Week  => WeekStartOf(anchor, weekStart),
        CalendarViewMode.Month => MonthStartOf(anchor),
        _                      => anchor,
    };

    /// <summary>The start of the next (<c>+1</c>) or previous (<c>-1</c>) period.</summary>
    public static DateOnly Step(CalendarViewMode mode, DateOnly periodStart, int direction, int customDays) => mode switch
    {
        CalendarViewMode.Day   => periodStart.AddDays(direction),
        CalendarViewMode.Week  => periodStart.AddDays(7 * direction),
        CalendarViewMode.Month => MonthStartOf(periodStart).AddMonths(direction),
        _                      => periodStart.AddDays(Math.Clamp(customDays, 1, 31) * direction),
    };

    /// <summary>Title for a visible range: "October 2026", "Sep – Oct 2026", or "Dec 2026 – Jan 2027".</summary>
    public static string PeriodTitle(DateOnly first, DateOnly lastInclusive)
    {
        if (first.Year == lastInclusive.Year && first.Month == lastInclusive.Month)
        {
            return MonthTitle(first);
        }

        return first.Year == lastInclusive.Year
            ? $"{first.ToString("MMM", English)} – {lastInclusive.ToString("MMM yyyy", English)}"
            : $"{first.ToString("MMM yyyy", English)} – {lastInclusive.ToString("MMM yyyy", English)}";
    }

    /// <summary>"October 2026".</summary>
    public static string MonthTitle(DateOnly anyDay) => anyDay.ToString("MMMM yyyy", English);

    /// <summary>ISO 8601 week number.</summary>
    public static int WeekNumber(DateOnly date) => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));

    /// <summary>Saturday or Sunday.</summary>
    public static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>The first grid day and number of week rows needed to show the whole month.</summary>
    public static (DateOnly GridStart, int Weeks) MonthGrid(DateOnly anyDayInMonth, DayOfWeek weekStart)
    {
        var first     = MonthStartOf(anyDayInMonth);
        var gridStart = WeekStartOf(first, weekStart);
        var last      = first.AddMonths(1).AddDays(-1);

        return (gridStart, (last.DayNumber - gridStart.DayNumber) / 7 + 1);
    }
}
```

`src/LeafCalendar.Core/Views/DayStrip.cs`:
```csharp
namespace LeafCalendar.Core.Views;

/// <summary>
/// The scrollable run of days behind the time grid: index to date and back, optionally without
/// weekends. Asking for a date that isn't in the strip (a weekend while hidden) gives the next later day.
/// </summary>
public sealed class DayStrip
{
    readonly DateOnly[] _days;

    /// <summary>Builds the strip around <paramref name="origin"/>.</summary>
    public DayStrip(DateOnly origin, int daysBefore, int daysAfter, bool skipWeekends)
    {
        SkipsWeekends = skipWeekends;

        var days = new List<DateOnly>(daysBefore + daysAfter + 1);
        for (var day = origin.AddDays(-daysBefore); day <= origin.AddDays(daysAfter); day = day.AddDays(1))
        {
            if (!skipWeekends || !ViewNavigator.IsWeekend(day))
            {
                days.Add(day);
            }
        }

        _days = [.. days];
    }

    /// <summary>Number of days.</summary>
    public int Count => _days.Length;

    /// <summary>True when weekends are left out.</summary>
    public bool SkipsWeekends { get; }

    /// <summary>First day.</summary>
    public DateOnly First => _days[0];

    /// <summary>Last day.</summary>
    public DateOnly Last => _days[^1];

    /// <summary>The day at <paramref name="index"/> (clamped to the strip).</summary>
    public DateOnly this[int index] => _days[Math.Clamp(index, 0, _days.Length - 1)];

    /// <summary>True when <paramref name="day"/> is within the strip's range.</summary>
    public bool Contains(DateOnly day) => day >= First && day <= Last;

    /// <summary>Index of <paramref name="day"/>, or of the next later day in the strip (clamped).</summary>
    public int IndexOf(DateOnly day)
    {
        var index = Array.BinarySearch(_days, day);
        return index >= 0 ? index : Math.Min(~index, _days.Length - 1);
    }
}
```

`src/LeafCalendar.Core/Views/ShortcutMap.cs`:
```csharp
namespace LeafCalendar.Core.Views;

/// <summary>Calendar keyboard commands (spec Section 8.7).</summary>
public enum CalendarCommand
{
    /// <summary>Not a shortcut.</summary>
    None,

    /// <summary>T.</summary>
    Today,

    /// <summary>Left arrow or K.</summary>
    Previous,

    /// <summary>Right arrow or J.</summary>
    Next,

    /// <summary>D or 1.</summary>
    DayView,

    /// <summary>W or 0.</summary>
    WeekView,

    /// <summary>M.</summary>
    MonthView,

    /// <summary>2-9: that many days.</summary>
    Days,

    /// <summary>Period.</summary>
    GoToDate,

    /// <summary>Ctrl+Shift+E.</summary>
    ToggleWeekends,

    /// <summary>Ctrl+Shift+D.</summary>
    ToggleDeclined,

    /// <summary>Ctrl+= (grid taller).</summary>
    ZoomIn,

    /// <summary>Ctrl+- (grid shorter).</summary>
    ZoomOut,

    /// <summary>Ctrl+0.</summary>
    ZoomReset,

    /// <summary>Ctrl+Shift+L.</summary>
    ToggleTheme,

    /// <summary>N.</summary>
    NextEvent,

    /// <summary>B or Shift+N.</summary>
    PreviousEvent,
}

/// <summary>A resolved shortcut. <see cref="Days"/> is set for <see cref="CalendarCommand.Days"/>.</summary>
public readonly record struct ShortcutResult(CalendarCommand Command, int Days = 0);

/// <summary>Maps a key chord to a calendar command. Keys are <c>VirtualKey.ToString()</c> values.</summary>
public static class ShortcutMap
{
    /// <summary>Resolves a chord; Alt chords are never calendar shortcuts.</summary>
    public static ShortcutResult Resolve(string key, bool ctrl, bool shift, bool alt)
    {
        if (alt)
        {
            return default;
        }

        // Ctrl+Shift
        if (ctrl && shift)
        {
            return key switch
            {
                "E" => new(CalendarCommand.ToggleWeekends),
                "D" => new(CalendarCommand.ToggleDeclined),
                "L" => new(CalendarCommand.ToggleTheme),
                _   => default,
            };
        }

        // Ctrl
        if (ctrl)
        {
            return key switch
            {
                "187" or "Add"             => new(CalendarCommand.ZoomIn),
                "189" or "Subtract"        => new(CalendarCommand.ZoomOut),
                "Number0" or "NumberPad0"  => new(CalendarCommand.ZoomReset),
                _                          => default,
            };
        }

        // Shift
        if (shift)
        {
            return key == "N" ? new(CalendarCommand.PreviousEvent) : default;
        }

        // Plain Keys
        if (DigitOf(key) is { } digit)
        {
            return digit switch
            {
                0 => new(CalendarCommand.WeekView),
                1 => new(CalendarCommand.DayView),
                _ => new(CalendarCommand.Days, digit),
            };
        }

        return key switch
        {
            "T"                 => new(CalendarCommand.Today),
            "Left" or "K"       => new(CalendarCommand.Previous),
            "Right" or "J"      => new(CalendarCommand.Next),
            "D"                 => new(CalendarCommand.DayView),
            "W"                 => new(CalendarCommand.WeekView),
            "M"                 => new(CalendarCommand.MonthView),
            "190" or "Decimal"  => new(CalendarCommand.GoToDate),
            "N"                 => new(CalendarCommand.NextEvent),
            "B"                 => new(CalendarCommand.PreviousEvent),
            _                   => default,
        };
    }

    static int? DigitOf(string key) =>
        key.Length == 7 && key.StartsWith("Number", StringComparison.Ordinal) && char.IsAsciiDigit(key[6]) ? key[6] - '0'
        : key.Length == 10 && key.StartsWith("NumberPad", StringComparison.Ordinal) && char.IsAsciiDigit(key[9]) ? key[9] - '0'
        : null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Views tests/LeafCalendar.Tests
git commit -m "feat(core): add view navigation math, day strip, and shortcut map"
```

---

### Task 9: Day and Span Layouts

**Files:**
- Create: `src/LeafCalendar.Core/Views/DayLayout.cs`, `src/LeafCalendar.Core/Views/SpanLayout.cs`
- Test: `tests/LeafCalendar.Tests/DayLayoutTests.cs`, `tests/LeafCalendar.Tests/SpanLayoutTests.cs`

**Interfaces:**
- Consumes: `CalendarOccurrence`, `OccurrenceQuery.LocalMidnight` (Task 6).
- Produces (namespace `LeafCalendar.Core.Views`):
  - `sealed record TimedBlock(CalendarOccurrence Occurrence, double StartMinute, double EndMinute, int Column, int ColumnCount)`
  - `static class DayLayout`:
    - `const double MinVisualMinutes = 20`
    - `IReadOnlyList<TimedBlock> Layout(DateOnly day, IEnumerable<CalendarOccurrence>, TimeZoneInfo zone)`: timed, non-spanning events only, minutes of local wall-clock time 0–1440
  - `sealed record SpanBlock(CalendarOccurrence Occurrence, int FirstColumn, int ColumnSpan, int Lane, bool ContinuesBefore, bool ContinuesAfter)`
  - `static class SpanLayout`:
    - `bool IsSpanning(CalendarOccurrence)`: all-day or ≥ 24 h
    - `(DateOnly First, DateOnly Last) CoveredDates(CalendarOccurrence, TimeZoneInfo)`: inclusive
    - `IReadOnlyList<SpanBlock> Layout(IReadOnlyList<DateOnly> columns, IEnumerable<CalendarOccurrence>, TimeZoneInfo, bool includeTimed)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/DayLayoutTests.cs`:
```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class DayLayoutTests
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateOnly Day = new(2026, 10, 1);

    static CalendarOccurrence At(string id, int startHour, int startMinute, int endHour, int endMinute, int endDayOffset = 0) =>
        new("a", "c", id, null, null,
            new DateTimeOffset(2026, 10, 1, startHour, startMinute, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 10, 1 + endDayOffset, endHour, endMinute, 0, TimeSpan.FromHours(-4)),
            false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static IReadOnlyList<TimedBlock> Lay(params CalendarOccurrence[] items) => DayLayout.Layout(Day, items, Zone);

    [Fact]
    public void Layout_Alone_OneFullWidthColumn()
    {
        var b = Assert.Single(Lay(At("a", 9, 0, 10, 0)));

        Assert.Equal(540, b.StartMinute);
        Assert.Equal(600, b.EndMinute);
        Assert.Equal((0, 1), (b.Column, b.ColumnCount));
    }

    [Fact]
    public void Layout_BackToBack_SeparateClusters()
    {
        var blocks = Lay(At("a", 9, 0, 10, 0), At("b", 10, 0, 11, 0));

        Assert.All(blocks, b => Assert.Equal((0, 1), (b.Column, b.ColumnCount)));
    }

    [Fact]
    public void Layout_ChainedOverlaps_SharesColumns()
    {
        var blocks = Lay(At("a", 9, 0, 10, 0), At("b", 9, 30, 11, 0), At("c", 10, 0, 10, 30)).ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal(0, blocks["a"].Column);
        Assert.Equal(1, blocks["b"].Column);
        Assert.Equal(0, blocks["c"].Column);
        Assert.All(blocks.Values, b => Assert.Equal(2, b.ColumnCount));
    }

    [Fact]
    public void Layout_ThreeAtOnce_ThreeColumns()
    {
        var blocks = Lay(At("a", 9, 0, 10, 0), At("b", 9, 0, 10, 0), At("c", 9, 15, 9, 45));

        Assert.Equal([0, 1, 2], blocks.Select(b => b.Column).Order());
        Assert.All(blocks, b => Assert.Equal(3, b.ColumnCount));
    }

    [Fact]
    public void Layout_TinyAdjacentEvents_GetSeparateColumns()
    {
        var blocks = Lay(At("a", 9, 0, 9, 5), At("b", 9, 10, 9, 15));

        Assert.Equal([0, 1], blocks.Select(b => b.Column).Order());
    }

    [Fact]
    public void Layout_CrossesMidnight_ClippedToDay()
    {
        var b = Assert.Single(Lay(At("late", 22, 0, 2, 0, endDayOffset: 1)));

        Assert.Equal(1320, b.StartMinute);
        Assert.Equal(1440, b.EndMinute);
    }

    [Fact]
    public void Layout_TwentyFourHoursOrMore_LeftToSpanRow()
    {
        Assert.Empty(Lay(At("long", 9, 0, 9, 0, endDayOffset: 1)));
    }
}
```

`tests/LeafCalendar.Tests/SpanLayoutTests.cs`:
```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class SpanLayoutTests
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    static DateOnly D(int day) => new(2026, 10, day);

    static CalendarOccurrence AllDay(string id, int startDay, int days)
    {
        var s = new DateTimeOffset(D(startDay).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new("a", "c", id, null, null, s, s.AddDays(days), true, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);
    }

    static CalendarOccurrence Timed(string id, int day, int hour) =>
        new("a", "c", id, null, null,
            new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 10, day, hour + 1, 0, 0, TimeSpan.FromHours(-4)),
            false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static List<DateOnly> Week(int firstDay, bool skipWeekends = false) =>
        [.. Enumerable.Range(0, 7).Select(i => D(firstDay + i)).Where(d => !skipWeekends || !ViewNavigator.IsWeekend(d))];

    [Fact]
    public void Layout_ThreeDayEvent_SpansColumns()
    {
        var b = Assert.Single(SpanLayout.Layout(Week(4), [AllDay("trip", 6, 3)], Zone, includeTimed: false));

        Assert.Equal((2, 3, 0), (b.FirstColumn, b.ColumnSpan, b.Lane));
        Assert.False(b.ContinuesBefore);
        Assert.False(b.ContinuesAfter);
    }

    [Fact]
    public void Layout_EventRunningPastBothEdges_FlagsContinuation()
    {
        var b = Assert.Single(SpanLayout.Layout(Week(4), [AllDay("long", 1, 14)], Zone, includeTimed: false));

        Assert.Equal((0, 7), (b.FirstColumn, b.ColumnSpan));
        Assert.True(b.ContinuesBefore);
        Assert.True(b.ContinuesAfter);
    }

    [Fact]
    public void Layout_OverlappingSpans_StackInLanes()
    {
        var blocks = SpanLayout.Layout(Week(4), [AllDay("a", 4, 3), AllDay("b", 5, 3), AllDay("c", 8, 2)], Zone, includeTimed: false)
            .ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal(0, blocks["a"].Lane);
        Assert.Equal(1, blocks["b"].Lane);
        Assert.Equal(0, blocks["c"].Lane);
    }

    [Fact]
    public void Layout_WeekendsHidden_WeekendOnlyEventDropped()
    {
        var columns = Week(4, skipWeekends: true);

        Assert.Empty(SpanLayout.Layout(columns, [AllDay("sat", 10, 1)], Zone, includeTimed: false));
        Assert.Equal(5, Assert.Single(SpanLayout.Layout(columns, [AllDay("wk", 4, 7)], Zone, includeTimed: false)).ColumnSpan);
    }

    [Fact]
    public void Layout_TimedEvents_OnlyWhenIncluded()
    {
        Assert.Empty(SpanLayout.Layout(Week(4), [Timed("t", 6, 9)], Zone, includeTimed: false));

        var b = Assert.Single(SpanLayout.Layout(Week(4), [Timed("t", 6, 9)], Zone, includeTimed: true));
        Assert.Equal((2, 1), (b.FirstColumn, b.ColumnSpan));
    }

    [Fact]
    public void Layout_SpanningPlacedAboveTimedOnSameDay()
    {
        var blocks = SpanLayout.Layout(Week(4), [Timed("t", 6, 9), AllDay("a", 6, 1)], Zone, includeTimed: true)
            .ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal(0, blocks["a"].Lane);
        Assert.Equal(1, blocks["t"].Lane);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*DayLayoutTests" --filter-class "*SpanLayoutTests"`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Views/DayLayout.cs`:
```csharp
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>A timed event's position in one day: minutes from local midnight, and its side-by-side column.</summary>
public sealed record TimedBlock(CalendarOccurrence Occurrence, double StartMinute, double EndMinute, int Column, int ColumnCount);

/// <summary>
/// Lays out one day's timed events so overlapping ones sit side by side.
/// </summary>
/// <remarks>
/// Events are sorted by start (longer first) and packed into the leftmost free column. A cluster is
/// a run of events that overlap in a chain. Every event in a cluster gets the cluster's column count,
/// so widths line up. Each event occupies at least <see cref="MinVisualMinutes"/> for overlap purposes,
/// so tiny back-to-back events never draw on top of each other. Events of 24 hours or more belong to
/// the span row (<see cref="SpanLayout"/>).
/// </remarks>
public static class DayLayout
{
    /// <summary>Smallest height an event takes when deciding overlaps (minutes).</summary>
    public const double MinVisualMinutes = 20;

    /// <summary>Lays out <paramref name="day"/>.</summary>
    public static IReadOnlyList<TimedBlock> Layout(DateOnly day, IEnumerable<CalendarOccurrence> occurrences, TimeZoneInfo zone)
    {
        var dayStart = OccurrenceQuery.LocalMidnight(day, zone);
        var dayEnd   = OccurrenceQuery.LocalMidnight(day.AddDays(1), zone);

        var items = occurrences
            .Where(o => !o.IsAllDay && !SpanLayout.IsSpanning(o) && o.Start < dayEnd && o.End > dayStart)
            .Select(o => (Occurrence: o, Start: MinuteOfDay(o.Start < dayStart ? dayStart : o.Start, day, zone), End: o.End >= dayEnd ? 1440 : MinuteOfDay(o.End, day, zone)))
            .OrderBy(i => i.Start)
            .ThenByDescending(i => i.End - i.Start)
            .ThenBy(i => i.Occurrence.Key, StringComparer.Ordinal)
            .ToList();

        var result     = new List<TimedBlock>(items.Count);
        var cluster    = new List<(int Index, int Column)>();
        var columnEnds = new List<double>();
        var clusterEnd = double.MinValue;

        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            // Close The Cluster When Nothing Overlaps Anymore
            if (cluster.Count > 0 && item.Start >= clusterEnd)
            {
                Flush();
            }

            var visualEnd = Math.Max(item.End, item.Start + MinVisualMinutes);
            var column    = columnEnds.FindIndex(end => end <= item.Start);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(visualEnd);
            }
            else
            {
                columnEnds[column] = visualEnd;
            }

            clusterEnd = cluster.Count == 0 ? visualEnd : Math.Max(clusterEnd, visualEnd);
            cluster.Add((index, column));
        }

        Flush();
        return result;

        void Flush()
        {
            foreach (var (index, column) in cluster)
            {
                var item = items[index];
                result.Add(new TimedBlock(item.Occurrence, item.Start, Math.Max(item.End, item.Start), column, columnEnds.Count));
            }

            cluster.Clear();
            columnEnds.Clear();
        }
    }

    static double MinuteOfDay(DateTimeOffset instant, DateOnly day, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var date  = DateOnly.FromDateTime(local.DateTime);

        return date < day ? 0 : date > day ? 1440 : local.TimeOfDay.TotalMinutes;
    }
}
```

`src/LeafCalendar.Core/Views/SpanLayout.cs`:
```csharp
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>A horizontal bar across visible day columns, in a lane (row) of the all-day area or month cell.</summary>
public sealed record SpanBlock(CalendarOccurrence Occurrence, int FirstColumn, int ColumnSpan, int Lane, bool ContinuesBefore, bool ContinuesAfter);

/// <summary>
/// Lays out events that run across day columns: all-day and 24-hour-plus events in the time grid's
/// all-day row, and every event in the month view.
/// </summary>
/// <remarks>
/// Columns can skip days (hidden weekends). An event is clipped to the columns it touches and flags
/// when it continues beyond them. Lanes are packed greedily: spanning events first, then wider ones,
/// then earlier ones.
/// </remarks>
public static class SpanLayout
{
    /// <summary>True for all-day events and events lasting 24 hours or more.</summary>
    public static bool IsSpanning(CalendarOccurrence occurrence) =>
        occurrence.IsAllDay || occurrence.End - occurrence.Start >= TimeSpan.FromHours(24);

    /// <summary>The local dates an event touches (inclusive).</summary>
    public static (DateOnly First, DateOnly Last) CoveredDates(CalendarOccurrence occurrence, TimeZoneInfo zone)
    {
        var (first, last) = occurrence.IsAllDay
            ? (occurrence.AllDayStart, occurrence.AllDayEnd.AddDays(-1))
            : (LocalDate(occurrence.Start, zone), LocalDate(occurrence.End.AddTicks(-1), zone));

        return (first, last < first ? first : last);
    }

    /// <summary>Lays out events over <paramref name="columns"/> (ascending dates).</summary>
    public static IReadOnlyList<SpanBlock> Layout(IReadOnlyList<DateOnly> columns, IEnumerable<CalendarOccurrence> occurrences, TimeZoneInfo zone, bool includeTimed)
    {
        if (columns.Count == 0)
        {
            return [];
        }

        // Clip To Columns
        var candidates = new List<(CalendarOccurrence Occurrence, int First, int Last, bool Before, bool After)>();
        foreach (var occurrence in occurrences.DistinctBy(o => o.Key))
        {
            if (!includeTimed && !IsSpanning(occurrence))
            {
                continue;
            }

            var (first, last) = CoveredDates(occurrence, zone);
            var firstColumn   = FirstIndex(columns, d => d >= first && d <= last);
            var lastColumn    = LastIndex(columns, d => d >= first && d <= last);
            if (firstColumn < 0)
            {
                continue;
            }

            candidates.Add((occurrence, firstColumn, lastColumn, first < columns[firstColumn], last > columns[lastColumn]));
        }

        // Pack Lanes
        var laneEnds = new List<int>();
        var result   = new List<SpanBlock>(candidates.Count);
        foreach (var c in candidates
            .OrderBy(c => c.First)
            .ThenBy(c => IsSpanning(c.Occurrence) ? 0 : 1)
            .ThenByDescending(c => c.Last - c.First)
            .ThenBy(c => c.Occurrence.Start))
        {
            var lane = laneEnds.FindIndex(end => end < c.First);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(c.Last);
            }
            else
            {
                laneEnds[lane] = c.Last;
            }

            result.Add(new SpanBlock(c.Occurrence, c.First, c.Last - c.First + 1, lane, c.Before, c.After));
        }

        return result;
    }

    static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    static int FirstIndex(IReadOnlyList<DateOnly> columns, Func<DateOnly, bool> match)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (match(columns[i]))
            {
                return i;
            }
        }

        return -1;
    }

    static int LastIndex(IReadOnlyList<DateOnly> columns, Func<DateOnly, bool> match)
    {
        for (var i = columns.Count - 1; i >= 0; i--)
        {
            if (match(columns[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the Step 2 command. Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Views tests/LeafCalendar.Tests
git commit -m "feat(core): lay out overlapping timed events and multi-day spans"
```

---

### Task 10: Colors, Time Labels, and Time-Zone Catalog

**Files:**
- Create: `src/LeafCalendar.Core/Views/EventColors.cs`, `Views/TimeLabels.cs`, `Views/TimeZoneCatalog.cs`
- Test: `tests/LeafCalendar.Tests/EventColorsTests.cs`, `TimeLabelsTests.cs`, `TimeZoneCatalogTests.cs`

**Interfaces:**
- Consumes: `CalendarInfo.DefaultColor` (Task 1), `ExtraTimeZone` (Task 1).
- Produces (namespace `LeafCalendar.Core.Views`):
  - `sealed record EventPalette(string Accent, string Fill, string Text, string SecondaryText)`, in hex. `SecondaryText` is `#AARRGGBB`.
  - `static class EventColors`:
    - `IReadOnlyList<string> CalendarPalette` (24 Google calendar colors)
    - `string ResolveAccent(string? colorId, string calendarColor)`
    - `EventPalette Palette(string accentHex, bool dark)`
    - `string Blend(string foregroundHex, string backgroundHex, double backgroundAmount)`
    - `double ContrastRatio(string hexA, string hexB)`
  - `static class TimeLabels`:
    - `string HourLabel(int hour, bool use24h)`
    - `string TimeOfDay(DateTimeOffset, TimeZoneInfo, bool use24h)`
    - `string Range(DateTimeOffset, DateTimeOffset, TimeZoneInfo, bool use24h)`
    - `string Compact(DateTimeOffset, TimeZoneInfo, bool use24h)`
    - `string WeekdayShort(DateOnly)` and `string LongDate(DateOnly)`
    - `string Relative(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)`
  - `sealed record TimeZoneChoice(string Id, string City, string Detail)`, whose `ToString()` is `"City (Detail)"`. `AutoSuggestBox` shows it, which avoids `DisplayMemberPath`.
  - `static class TimeZoneCatalog`:
    - `bool IsKnown(string id)`
    - `IReadOnlyList<TimeZoneChoice> Search(string query, DateTimeOffset now, int max = 20)`
    - `string OffsetLabel(TimeSpan)`
    - `string CityFor(string id)`
    - `string ShortLabel(ExtraTimeZone zone)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/EventColorsTests.cs`:
```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class EventColorsTests
{
    [Theory]
    [InlineData("5", "#9fe1e7", "#F6BF26")]
    [InlineData("11", "#9fe1e7", "#D50000")]
    [InlineData(null, "#9fe1e7", "#9FE1E7")]
    [InlineData("99", "#9fe1e7", "#9FE1E7")]
    [InlineData(null, "not a color", "#4285F4")]
    public void ResolveAccent_ColorIdOrCalendar_Picks(string? colorId, string calendar, string expected)
    {
        Assert.Equal(expected, EventColors.ResolveAccent(colorId, calendar));
    }

    [Fact]
    public void Palette_EveryGoogleColorBothThemes_TextMeetsContrast()
    {
        var accents = EventColors.CalendarPalette.Concat(["#7986CB", "#33B679", "#8E24AA", "#E67C73", "#F6BF26", "#F4511E", "#039BE5", "#616161", "#3F51B5", "#0B8043", "#D50000"]);

        foreach (var accent in accents)
        {
            foreach (var dark in new[] { true, false })
            {
                var palette = EventColors.Palette(accent, dark);
                Assert.True(EventColors.ContrastRatio(palette.Text, palette.Fill) >= 4.5, $"{accent} dark={dark}");
            }
        }
    }

    [Fact]
    public void Blend_HalfWay_AveragesChannels()
    {
        Assert.Equal("#808080", EventColors.Blend("#FFFFFF", "#000000", 0.5));
    }

    [Fact]
    public void ContrastRatio_BlackOnWhite_Is21()
    {
        Assert.Equal(21, EventColors.ContrastRatio("#000000", "#FFFFFF"), 1);
    }
}
```

`tests/LeafCalendar.Tests/TimeLabelsTests.cs`:
```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class TimeLabelsTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, TimeSpan.FromHours(-4));

    [Theory]
    [InlineData(0, false, "12 AM")]
    [InlineData(9, false, "9 AM")]
    [InlineData(12, false, "12 PM")]
    [InlineData(13, false, "1 PM")]
    [InlineData(9, true, "09:00")]
    [InlineData(21, true, "21:00")]
    public void HourLabel_Formats(int hour, bool use24h, string expected) => Assert.Equal(expected, TimeLabels.HourLabel(hour, use24h));

    [Fact]
    public void TimeOfDay_OnTheHourAndNot_Formats()
    {
        Assert.Equal("9 AM", TimeLabels.TimeOfDay(At(9, 0), NewYork, false));
        Assert.Equal("9:30 AM", TimeLabels.TimeOfDay(At(9, 30), NewYork, false));
        Assert.Equal("13:05", TimeLabels.TimeOfDay(At(13, 5), NewYork, true));
    }

    [Fact]
    public void Range_Formats() => Assert.Equal("9 AM – 10:30 AM", TimeLabels.Range(At(9, 0), At(10, 30), NewYork, false));

    [Theory]
    [InlineData(9, 0, false, "9a")]
    [InlineData(13, 30, false, "1:30p")]
    [InlineData(0, 15, false, "12:15a")]
    [InlineData(13, 30, true, "13:30")]
    public void Compact_Formats(int hour, int minute, bool use24h, string expected) =>
        Assert.Equal(expected, TimeLabels.Compact(At(hour, minute), NewYork, use24h));

    [Fact]
    public void Relative_BeforeDuringAfter()
    {
        Assert.Equal("in 12 min", TimeLabels.Relative(At(9, 0), At(10, 0), At(8, 48)));
        Assert.Equal("in 2 h", TimeLabels.Relative(At(11, 0), At(12, 0), At(8, 50)));
        Assert.Equal("Now", TimeLabels.Relative(At(9, 0), At(10, 0), At(9, 30)));
        Assert.Equal("Ended", TimeLabels.Relative(At(9, 0), At(10, 0), At(10, 30)));
    }
}
```

`tests/LeafCalendar.Tests/TimeZoneCatalogTests.cs`:
```csharp
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class TimeZoneCatalogTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("NYC", "America/New_York")]
    [InlineData("sf", "America/Los_Angeles")]
    [InlineData("LON", "Europe/London")]
    [InlineData("tok", "Asia/Tokyo")]
    [InlineData("mumbai", "Asia/Kolkata")]
    public void Search_AliasOrCity_FindsZoneFirst(string query, string id)
    {
        Assert.Equal(id, TimeZoneCatalog.Search(query, Now)[0].Id);
    }

    [Fact]
    public void Search_Empty_ReturnsCuratedList()
    {
        var results = TimeZoneCatalog.Search("", Now);

        Assert.Equal(20, results.Count);
        Assert.All(results, r => Assert.True(TimeZoneCatalog.IsKnown(r.Id)));
    }

    [Fact]
    public void Search_Nonsense_Empty() => Assert.Empty(TimeZoneCatalog.Search("zzqqxx", Now));

    [Theory]
    [InlineData(0, 0, "UTC")]
    [InlineData(9, 0, "UTC+9")]
    [InlineData(-5, 0, "UTC−5")]
    [InlineData(5, 30, "UTC+5:30")]
    [InlineData(-3, -30, "UTC−3:30")]
    public void OffsetLabel_Formats(int hours, int minutes, string expected) =>
        Assert.Equal(expected, TimeZoneCatalog.OffsetLabel(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public void ShortLabel_CustomOrCity()
    {
        Assert.Equal("HQ", TimeZoneCatalog.ShortLabel(new ExtraTimeZone("Asia/Tokyo", "HQ")));
        Assert.Equal("Tokyo", TimeZoneCatalog.ShortLabel(new ExtraTimeZone("Asia/Tokyo", null)));
        Assert.Equal("Buenos Aires", TimeZoneCatalog.CityFor("America/Argentina/Buenos_Aires"));
    }

    [Fact]
    public void Choice_ToString_ShowsCityAndDetail()
    {
        var choice = TimeZoneCatalog.Search("tokyo", Now)[0];

        Assert.StartsWith("Tokyo (UTC+9", choice.ToString(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EventColorsTests" --filter-class "*TimeLabelsTests" --filter-class "*TimeZoneCatalogTests"`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/LeafCalendar.Core/Views/EventColors.cs`:
```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Data;

namespace LeafCalendar.Core.Views;

/// <summary>Colors for one event card: accent bar, fill, and readable text (hex; <see cref="SecondaryText"/> is #AARRGGBB).</summary>
public sealed record EventPalette(string Accent, string Fill, string Text, string SecondaryText);

/// <summary>
/// Google's color palettes and card color math. Fills are the accent mixed into the page surface,
/// and text is black or white, whichever contrasts more (always 4.5:1 or better for Google's colors).
/// </summary>
public static partial class EventColors
{
    const string DarkSurface  = "#202020";
    const string LightSurface = "#FFFFFF";
    const string DarkText     = "#1A1A1A";
    const string LightText    = "#FFFFFF";

    // Google Calendar event colors (colorId 1-11)
    static readonly Dictionary<string, string> EventColorIds = new(StringComparer.Ordinal)
    {
        ["1"]  = "#7986CB",
        ["2"]  = "#33B679",
        ["3"]  = "#8E24AA",
        ["4"]  = "#E67C73",
        ["5"]  = "#F6BF26",
        ["6"]  = "#F4511E",
        ["7"]  = "#039BE5",
        ["8"]  = "#616161",
        ["9"]  = "#3F51B5",
        ["10"] = "#0B8043",
        ["11"] = "#D50000",
    };

    /// <summary>Google Calendar's 24 calendar colors, offered in the sidebar color picker.</summary>
    public static IReadOnlyList<string> CalendarPalette { get; } =
    [
        "#AC725E", "#D06B64", "#F83A22", "#FA573C", "#FF7537", "#FFAD46",
        "#42D692", "#16A765", "#7BD148", "#B3DC6C", "#FBE983", "#FAD165",
        "#92E1C0", "#9FE1E7", "#9FC6E7", "#4986E7", "#9A9CFF", "#B99AFF",
        "#C2C2C2", "#CABDBF", "#CCA6AC", "#F691B2", "#CD74E6", "#A47AE2",
    ];

    /// <summary>The event's own color when it has one, else its calendar's, else Google blue (uppercase hex).</summary>
    public static string ResolveAccent(string? colorId, string calendarColor)
    {
        if (colorId is not null && EventColorIds.TryGetValue(colorId, out var eventColor))
        {
            return eventColor;
        }

        return Hex().IsMatch(calendarColor) ? calendarColor.ToUpperInvariant() : CalendarInfo.DefaultColor;
    }

    /// <summary>Card colors for an accent in the dark or light theme.</summary>
    public static EventPalette Palette(string accentHex, bool dark)
    {
        var accent = Hex().IsMatch(accentHex) ? accentHex.ToUpperInvariant() : CalendarInfo.DefaultColor;
        var fill   = Blend(accent, dark ? DarkSurface : LightSurface, dark ? 0.62 : 0.78);
        var text   = ContrastRatio(LightText, fill) >= ContrastRatio(DarkText, fill) ? LightText : DarkText;

        return new EventPalette(accent, fill, text, (text == LightText ? "#D9" : "#B3") + text[1..]);
    }

    /// <summary>Mixes two colors; <paramref name="backgroundAmount"/> 0 is all foreground, 1 all background.</summary>
    public static string Blend(string foregroundHex, string backgroundHex, double backgroundAmount)
    {
        var (fr, fg, fb) = Channels(foregroundHex);
        var (br, bg, bb) = Channels(backgroundHex);
        var t = Math.Clamp(backgroundAmount, 0, 1);

        static int Mix(int f, int b, double t) => (int)Math.Round(f * (1 - t) + b * t, MidpointRounding.AwayFromZero);

        return string.Create(CultureInfo.InvariantCulture, $"#{Mix(fr, br, t):X2}{Mix(fg, bg, t):X2}{Mix(fb, bb, t):X2}");
    }

    /// <summary>WCAG contrast ratio between two colors (1-21).</summary>
    public static double ContrastRatio(string hexA, string hexB)
    {
        var a = Luminance(hexA);
        var b = Luminance(hexB);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static double Luminance(string hex)
    {
        var (r, g, b) = Channels(hex);

        static double Linear(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    static (int R, int G, int B) Channels(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Hex();
}
```

`src/LeafCalendar.Core/Views/TimeLabels.cs`:
```csharp
using System.Globalization;

namespace LeafCalendar.Core.Views;

/// <summary>English time and date strings for the calendar.</summary>
public static class TimeLabels
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>"9 AM" or "09:00".</summary>
    public static string HourLabel(int hour, bool use24h)
    {
        if (use24h)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hour:00}:00");
        }

        return hour switch
        {
            0    => "12 AM",
            12   => "12 PM",
            < 12 => string.Create(CultureInfo.InvariantCulture, $"{hour} AM"),
            _    => string.Create(CultureInfo.InvariantCulture, $"{hour - 12} PM"),
        };
    }

    /// <summary>"9 AM", "9:30 AM", or "13:05".</summary>
    public static string TimeOfDay(DateTimeOffset instant, TimeZoneInfo zone, bool use24h)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        if (use24h)
        {
            return local.ToString("HH:mm", English);
        }

        return local.ToString(local.Minute == 0 ? "h tt" : "h:mm tt", English);
    }

    /// <summary>"9 AM – 10:30 AM".</summary>
    public static string Range(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone, bool use24h) =>
        $"{TimeOfDay(start, zone, use24h)} – {TimeOfDay(end, zone, use24h)}";

    /// <summary>Month-view time: "9a", "1:30p", or "13:30".</summary>
    public static string Compact(DateTimeOffset instant, TimeZoneInfo zone, bool use24h)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        if (use24h)
        {
            return local.ToString("HH:mm", English);
        }

        var hour   = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        var suffix = local.Hour < 12 ? "a" : "p";

        return local.Minute == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hour}{suffix}")
            : string.Create(CultureInfo.InvariantCulture, $"{hour}:{local.Minute:00}{suffix}");
    }

    /// <summary>"Thu".</summary>
    public static string WeekdayShort(DateOnly day) => day.ToString("ddd", English);

    /// <summary>"Thursday, October 1".</summary>
    public static string LongDate(DateOnly day) => day.ToString("dddd, MMMM d", English);

    /// <summary>"in 12 min", "in 2 h", "Now", or "Ended".</summary>
    public static string Relative(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        if (now >= end)
        {
            return "Ended";
        }

        if (now >= start)
        {
            return "Now";
        }

        var until = start - now;
        return until.TotalMinutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"in {(int)Math.Ceiling(until.TotalMinutes)} min")
            : string.Create(CultureInfo.InvariantCulture, $"in {(int)Math.Floor(until.TotalHours)} h");
    }
}
```

`src/LeafCalendar.Core/Views/TimeZoneCatalog.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>A time zone offered in search. <see cref="ToString"/> is what the search box shows.</summary>
public sealed record TimeZoneChoice(string Id, string City, string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{City} ({Detail})";
}

/// <summary>
/// Finds time zones by city, IANA ID, Windows name, or common abbreviation (NYC, SF, LON...).
/// </summary>
/// <remarks>
/// A curated list of major cities comes first. Every other zone Windows knows is added from its
/// primary IANA ID. Only zones this PC can resolve are offered.
/// </remarks>
public static class TimeZoneCatalog
{
    static readonly (string Id, string City, string[] Aliases)[] Cities =
    [
        ("America/New_York", "New York", ["NYC", "NY", "EST", "EDT", "ET", "Eastern", "Boston", "Miami"]),
        ("America/Chicago", "Chicago", ["CHI", "CST", "CDT", "CT", "Central", "Dallas", "Houston"]),
        ("America/Denver", "Denver", ["DEN", "MST", "MDT", "MT", "Mountain"]),
        ("America/Phoenix", "Phoenix", ["PHX", "Arizona"]),
        ("America/Los_Angeles", "Los Angeles", ["LA", "SF", "San Francisco", "Seattle", "PST", "PDT", "PT", "Pacific"]),
        ("America/Anchorage", "Anchorage", ["AKST", "Alaska"]),
        ("Pacific/Honolulu", "Honolulu", ["HST", "Hawaii"]),
        ("America/Toronto", "Toronto", ["YYZ"]),
        ("America/Vancouver", "Vancouver", ["YVR"]),
        ("America/Mexico_City", "Mexico City", ["CDMX"]),
        ("America/Sao_Paulo", "São Paulo", ["SAO", "Sao Paulo"]),
        ("Europe/London", "London", ["LON", "UK", "GMT", "BST"]),
        ("Europe/Dublin", "Dublin", ["DUB"]),
        ("Europe/Paris", "Paris", ["PAR", "CET", "CEST"]),
        ("Europe/Berlin", "Berlin", ["BER"]),
        ("Europe/Madrid", "Madrid", ["MAD"]),
        ("Europe/Amsterdam", "Amsterdam", ["AMS"]),
        ("Europe/Athens", "Athens", ["EET"]),
        ("Asia/Kolkata", "Mumbai", ["Delhi", "Bangalore", "IST", "India"]),
        ("Asia/Tokyo", "Tokyo", ["TYO", "JST"]),
        ("Asia/Singapore", "Singapore", ["SIN", "SGT"]),
        ("Asia/Hong_Kong", "Hong Kong", ["HK", "HKT"]),
        ("Asia/Shanghai", "Shanghai", ["Beijing", "China"]),
        ("Asia/Seoul", "Seoul", ["KST"]),
        ("Asia/Dubai", "Dubai", ["GST", "UAE"]),
        ("Australia/Sydney", "Sydney", ["SYD", "AEST", "AEDT", "Melbourne"]),
        ("Australia/Perth", "Perth", ["AWST"]),
        ("Pacific/Auckland", "Auckland", ["AKL", "NZST", "NZDT", "New Zealand"]),
        ("Africa/Johannesburg", "Johannesburg", ["SAST"]),
        ("Etc/UTC", "UTC", ["GMT", "Coordinated Universal Time", "Z"]),
    ];

    static readonly Lazy<IReadOnlyList<Entry>> AllEntries = new(BuildEntries);

    /// <summary>True when this PC can resolve <paramref name="id"/>.</summary>
    public static bool IsKnown(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    /// <summary>Best matches for <paramref name="query"/> (curated cities when empty).</summary>
    public static IReadOnlyList<TimeZoneChoice> Search(string query, DateTimeOffset now, int max = 20)
    {
        var q = query.Trim();

        return AllEntries.Value
            .Select((entry, order) => (entry, order, rank: Rank(entry, q)))
            .Where(x => x.rank < int.MaxValue)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => new TimeZoneChoice(x.entry.Id, x.entry.City, Detail(x.entry.Id, now)))
            .ToList();
    }

    /// <summary>"UTC", "UTC+9", "UTC−5", "UTC+5:30" (with a real minus sign).</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        var sign = offset < TimeSpan.Zero ? "−" : "+";
        var abs  = offset.Duration();

        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}:{abs.Minutes:00}");
    }

    /// <summary>A zone's city name (curated, else the last part of the IANA ID).</summary>
    public static string CityFor(string id)
    {
        var curated = Array.Find(Cities, c => c.Id == id);
        return curated.City ?? id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
    }

    /// <summary>The column label: the custom label, else the city.</summary>
    public static string ShortLabel(ExtraTimeZone zone) => zone.Label ?? CityFor(zone.Id);

    static string Detail(string id, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return $"{OffsetLabel(zone.GetUtcOffset(now))} · {zone.StandardName}";
    }

    static int Rank(Entry entry, string query)
    {
        if (query.Length == 0)
        {
            return entry.Curated ? 0 : int.MaxValue;
        }

        if (entry.Aliases.Any(a => a.Equals(query, StringComparison.OrdinalIgnoreCase)) || entry.City.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (entry.City.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return entry.Curated ? 1 : 2;
        }

        if (entry.City.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return entry.Curated ? 3 : 4;
        }

        return int.MaxValue;
    }

    static IReadOnlyList<Entry> BuildEntries()
    {
        var entries = Cities.Where(c => IsKnown(c.Id)).Select(c => new Entry(c.Id, c.City, c.Aliases, Curated: true)).ToList();
        var ids     = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && IsKnown(iana) && ids.Add(iana))
            {
                entries.Add(new Entry(iana, CityFor(iana), [zone.DisplayName, zone.StandardName], Curated: false));
            }
        }

        return entries;
    }

    sealed record Entry(string Id, string City, string[] Aliases, bool Curated);
}
```

If `Search_Empty_ReturnsCuratedList` finds fewer than 20 because a curated ID doesn't resolve on this PC, keep it that way. The catalog is right to hide unknown zones. Change the assertion to `Assert.InRange(results.Count, 20, 30)` only if at least 20 curated zones resolve.

- [ ] **Step 4: Run tests to verify they pass**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Views tests/LeafCalendar.Tests
git commit -m "feat(core): add event color palettes, English time labels, and time-zone search"
```

---

### Task 11: Calendar Shell (View Model, Page, Title-Bar Toolbar, Theme)

**Files:**
- Create:
  - `src/LeafCalendar.App/Styles/LeafTheme.xaml`, `src/LeafCalendar.App/Controls/LeafBrushes.cs`
  - `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`
  - `src/LeafCalendar.App/Views/CalendarPage.xaml` + `.xaml.cs`, `src/LeafCalendar.App/Views/SidebarView.xaml` + `.xaml.cs` (footer only; Task 12 fills the rest)
  - `tests/LeafCalendar.UITests/CalendarShellTests.cs`
- Modify: `src/LeafCalendar.App/App.xaml`, `MainWindow.xaml` + `.xaml.cs`, `LeafServices.cs` (add `GoogleChanged`), `tests/LeafCalendar.UITests/SetupTests.cs`, `tests/LeafCalendar.UITests/AccountFlowTests.cs`

**Interfaces:**
- Consumes:
  - Core: `SettingsStore`, `LeafSettings`, `EventWindowCache`, `OccurrenceQuery`, `EventDetailsParser`
  - Core: `ViewNavigator`, `TimeLabels`, `CalendarStore.GetAll`, `EventStore.Get`, `SyncEngine.DataChanged`
  - App: `LeafServices` (`Options.StartDate`, `Google`, `Database`, `Log`)
- Produces (namespace `LeafCalendar.App.ViewModels`):
  - Records: `sealed record UpcomingItem(CalendarOccurrence Occurrence, string Title, string When, string Relative, string Color)` and `sealed record SelectedEventInfo(CalendarOccurrence Occurrence, EventDetails Details, string When, string CalendarName, string CalendarColor)`
  - `sealed partial class CalendarViewModel : ObservableObject, IDisposable`, constructed as `(LeafServices services, DispatcherQueue dispatcher)`:
    - **State:** `DateOnly Today`, `DateTimeOffset Now`, `TimeZoneInfo Zone`, `LeafSettings Settings`, `EventWindowCache Cache`, `IReadOnlyList<CalendarInfo> Calendars`, `ObservableCollection<UpcomingItem> Upcoming`
    - **View shape:** `CalendarViewMode Mode`, `int VisibleColumns`
    - **Observable:** `DateOnly PeriodStart`, `string PeriodTitle`, `SelectedEventInfo? SelectedInfo`, `bool HasSelection`
    - **Events:** `OccurrencesChanged`, `LayoutChanged`, `CalendarsChanged`, `EventHandler<DateOnly> NavigateRequested`, `EventHandler<DateTimeOffset> ScrollToTimeRequested`
    - **Navigation:** `GoToToday()`, `Previous()`, `Next()`, `NavigateTo(DateOnly)`, `SetMode(CalendarViewMode, int? days = null)`, `OnViewScrolled(DateOnly first, DateOnly lastExclusive, DateOnly? focus = null)`
    - **Settings:** `Update(Func<LeafSettings, LeafSettings> change, bool reloadData = false)`, `ToggleWeekends()`, `ToggleDeclined()`, `ZoomBy(double delta)`, `ZoomReset()`
    - **Selection:** `Select(CalendarOccurrence)`, `ClearSelection()`, `SelectAdjacent(int direction)`
    - **Calendars:** `SetCalendarHidden(CalendarInfo, bool)`, `SetCalendarColor(CalendarInfo, string?)`, `ReorderCalendars(string accountId, IReadOnlyList<string> ids)`, `ReloadCalendars()`
    - **Data:** `Task RefreshAsync()`
  - `LeafServices.GoogleChanged` event, raised after `ReloadGoogleAsync`
  - `CalendarPage` (navigation parameter `CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts)`) with public `Grid ViewHost`, `void ApplyView()`, `void SetSidebarOpen(bool)`
  - Automation IDs:
    - `CalendarRoot`, `ViewHost`, `EmptyState`, `EmptyAddAccountButton`
    - `Sidebar`, `AccountsButton`, `BookingPagesLink`
    - `PeriodTitle`, `TodayButton`, `PreviousButton`, `NextButton`, `ViewModeButton`
    - Menu items: `ViewDay`, `ViewWeek`, `ViewMonth`, `ViewDays2` … `ViewDays9`, `ViewDaysCustom`, `ToggleWeekends`, `ToggleDeclined`, `ToggleWeekNumbers`, `Toggle24Hour`, `WeekStartSunday`, `WeekStartMonday`, `WeekStartSaturday`, `ThemeSystem`, `ThemeLight`, `ThemeDark`
  - `MainWindow.ApplyTheme(AppTheme)`

- [ ] **Step 1: Shared styles and brushes**

`src/LeafCalendar.App/Styles/LeafTheme.xaml`:
```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Toolbar Buttons -->
    <Style x:Key="LeafToolbarButtonStyle" TargetType="Button" BasedOn="{StaticResource DefaultButtonStyle}">
        <Setter Property="Height" Value="32" />
        <Setter Property="MinWidth" Value="32" />
        <Setter Property="Padding" Value="10,0" />
        <Setter Property="VerticalAlignment" Value="Center" />
    </Style>

    <!-- Content Surface (like the Settings app) -->
    <Style x:Key="LeafSurfaceBorderStyle" TargetType="Border">
        <Setter Property="Background" Value="{ThemeResource LayerFillColorDefaultBrush}" />
        <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
        <Setter Property="BorderThickness" Value="1,1,0,0" />
        <Setter Property="CornerRadius" Value="8,0,0,0" />
    </Style>

    <!-- Sidebar Section Header -->
    <Style x:Key="LeafSectionHeaderStyle" TargetType="TextBlock" BasedOn="{StaticResource CaptionTextBlockStyle}">
        <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
        <Setter Property="Margin" Value="4,12,0,4" />
    </Style>
</ResourceDictionary>
```

In `src/LeafCalendar.App/App.xaml`, add inside `MergedDictionaries` after `XamlControlsResources`:
```xml
                <ResourceDictionary Source="ms-appx:///Styles/LeafTheme.xaml" />
```
Add to `LeafCalendar.App.csproj` if the XAML isn't picked up automatically: `<Page Include="Styles\LeafTheme.xaml" />`. Single-project WinUI normally includes it already, so only add it if the build says the resource is missing.

`src/LeafCalendar.App/Controls/LeafBrushes.cs`:
```csharp
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Brushes for code-built calendar visuals. Hex brushes are cached (UI thread only). Grid colors
/// are chosen per theme in code, because code-created elements can't use {ThemeResource}.
/// </summary>
public static class LeafBrushes
{
    static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transparent but hit-testable.</summary>
    public static SolidColorBrush Transparent { get; } = new(Colors.Transparent);

    /// <summary>Current-time line.</summary>
    public static SolidColorBrush NowLine { get; } = new(Color.FromArgb(255, 0xE5, 0x48, 0x4D));

    /// <summary>A brush for <c>#RRGGBB</c> or <c>#AARRGGBB</c> (invalid input gives Google blue).</summary>
    public static SolidColorBrush FromHex(string hex)
    {
        if (Cache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(Parse(hex));
        Cache[hex] = brush;
        return brush;
    }

    /// <summary>Hour and day divider lines.</summary>
    public static SolidColorBrush GridLine(bool dark) => FromHex(dark ? "#1FFFFFFF" : "#1A000000");

    /// <summary>Half-hour lines.</summary>
    public static SolidColorBrush HalfHourLine(bool dark) => FromHex(dark ? "#0DFFFFFF" : "#0A000000");

    /// <summary>Weekend column tint.</summary>
    public static SolidColorBrush WeekendFill(bool dark) => FromHex(dark ? "#08FFFFFF" : "#06000000");

    /// <summary>Secondary text (hour labels, weekday names).</summary>
    public static SolidColorBrush SecondaryText(bool dark) => FromHex(dark ? "#C5FFFFFF" : "#9E000000");

    /// <summary>Primary text.</summary>
    public static SolidColorBrush PrimaryText(bool dark) => FromHex(dark ? "#FFFFFFFF" : "#E4000000");

    /// <summary>Days outside the focused month.</summary>
    public static SolidColorBrush DimText(bool dark) => FromHex(dark ? "#5DFFFFFF" : "#5C000000");

    /// <summary>The system accent brush.</summary>
    public static Brush Accent => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    /// <summary>Text on the accent brush.</summary>
    public static Brush OnAccent => (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];

    static Color Parse(string hex)
    {
        var digits = hex.TrimStart('#');
        if (digits.Length == 6)
        {
            digits = "FF" + digits;
        }

        if (digits.Length != 8 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return Color.FromArgb(255, 0x42, 0x85, 0xF4);
        }

        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }
}
```

- [ ] **Step 2: `LeafServices.GoogleChanged`**

In `src/LeafCalendar.App/LeafServices.cs` add:
```csharp
    /// <summary>Raised after <see cref="ReloadGoogleAsync"/> replaces <see cref="Google"/>.</summary>
    public event EventHandler? GoogleChanged;
```
At the end of `ReloadGoogleAsync`, after `Google = CreateGoogle();`, add:
```csharp
        GoogleChanged?.Invoke(this, EventArgs.Empty);
```

- [ ] **Step 3: The view model**

`src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.ViewModels;

/// <summary>An upcoming event in the details panel.</summary>
public sealed record UpcomingItem(CalendarOccurrence Occurrence, string Title, string When, string Relative, string Color);

/// <summary>The selected event, ready to show.</summary>
public sealed record SelectedEventInfo(CalendarOccurrence Occurrence, EventDetails Details, string When, string CalendarName, string CalendarColor);

/// <summary>
/// State and commands for the calendar views. It owns the sliding event cache, the user's view
/// settings (saved on every change), selection, and the upcoming list.
/// </summary>
/// <remarks>
/// Views listen to the events here and redraw; they never touch the database. Sync notifications
/// arrive on a background thread and are marshaled to the UI thread before the cache refreshes.
/// </remarks>
public sealed partial class CalendarViewModel : ObservableObject, IDisposable
{
    /// <summary>How far ahead the upcoming list looks.</summary>
    public static readonly TimeSpan UpcomingWindow = TimeSpan.FromHours(8);

    readonly LeafServices _services;
    readonly DispatcherQueue _dispatcher;
    readonly DispatcherQueueTimer _minuteTimer;
    readonly CancellationTokenSource _life = new();
    SyncEngine? _attachedSync;

    /// <summary>Loads settings and calendars and starts the minute clock.</summary>
    public CalendarViewModel(LeafServices services, DispatcherQueue dispatcher)
    {
        _services   = services;
        _dispatcher = dispatcher;

        using (var conn = services.Database.Open())
        {
            Settings  = SettingsStore.Load(conn);
            Calendars = CalendarStore.GetAll(conn);
        }

        Today = services.Options.StartDate ?? DateOnly.FromDateTime(DateTime.Now);
        Cache = new EventWindowCache(LoadAsync, Zone);
        Cache.Changed += (_, _) =>
        {
            RefreshUpcoming();
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
        };

        PeriodStart = ViewNavigator.PeriodStart(Settings.ViewMode, Today, Settings.WeekStart);
        PeriodTitle = TitleFor(PeriodStart, PeriodStart.AddDays(VisibleColumns));

        services.GoogleChanged += OnGoogleChanged;
        AttachSync();

        _minuteTimer = dispatcher.CreateTimer();
        _minuteTimer.Interval = TimeSpan.FromMinutes(1);
        _minuteTimer.Tick += (_, _) => OnMinute();
        _minuteTimer.Start();
    }

    /// <summary>"Today": the real date, or <c>--start-date</c> in test mode.</summary>
    public DateOnly Today { get; private set; }

    /// <summary>The current instant (in test mode, 8:00 local on the start date, so tests are stable).</summary>
    public DateTimeOffset Now => _services.Options.StartDate is { } d
        ? OccurrenceQuery.LocalMidnight(d, Zone).AddHours(8)
        : _services.Time.GetUtcNow();

    /// <summary>The zone the grid is drawn in.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.Local;

    /// <summary>The user's view settings.</summary>
    public LeafSettings Settings { get; private set; }

    /// <summary>The ±3-month event cache.</summary>
    public EventWindowCache Cache { get; }

    /// <summary>Every calendar, grouped by account.</summary>
    public IReadOnlyList<CalendarInfo> Calendars { get; private set; }

    /// <summary>Events in the next <see cref="UpcomingWindow"/>.</summary>
    public ObservableCollection<UpcomingItem> Upcoming { get; } = [];

    /// <summary>Current view mode.</summary>
    public CalendarViewMode Mode => Settings.ViewMode;

    /// <summary>Day columns the current view shows.</summary>
    public int VisibleColumns => ViewNavigator.VisibleColumnCount(Settings.ViewMode, Settings.CustomDayCount, Settings.ShowWeekends);

    /// <summary>Start of the visible period.</summary>
    [ObservableProperty]
    public partial DateOnly PeriodStart { get; set; }

    /// <summary>Title-bar text such as "October 2026".</summary>
    [ObservableProperty]
    public partial string PeriodTitle { get; set; }

    /// <summary>The selected event, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial SelectedEventInfo? SelectedInfo { get; set; }

    /// <summary>True when an event is selected.</summary>
    public bool HasSelection => SelectedInfo is not null;

    /// <summary>The cached events changed (redraw).</summary>
    public event EventHandler? OccurrencesChanged;

    /// <summary>A layout setting changed (mode, weekends, hour height, zones, week start, clock).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The calendar list or its colors or visibility changed.</summary>
    public event EventHandler? CalendarsChanged;

    /// <summary>The view should scroll to this period start (animated).</summary>
    public event EventHandler<DateOnly>? NavigateRequested;

    /// <summary>The time grid should scroll this instant into view.</summary>
    public event EventHandler<DateTimeOffset>? ScrollToTimeRequested;

    // =========================================================================
    // NAVIGATION
    // =========================================================================

    /// <summary>Jumps to today.</summary>
    public void GoToToday() => NavigateTo(Today);

    /// <summary>Previous period.</summary>
    public void Previous() => NavigateTo(ViewNavigator.Step(Mode, PeriodStart, -1, Settings.CustomDayCount));

    /// <summary>Next period.</summary>
    public void Next() => NavigateTo(ViewNavigator.Step(Mode, PeriodStart, 1, Settings.CustomDayCount));

    /// <summary>Shows the period containing <paramref name="date"/>.</summary>
    public void NavigateTo(DateOnly date)
    {
        PeriodStart = ViewNavigator.PeriodStart(Mode, date, Settings.WeekStart);
        NavigateRequested?.Invoke(this, PeriodStart);
    }

    /// <summary>Switches view (and day count for <see cref="CalendarViewMode.Days"/>), keeping the current date.</summary>
    public void SetMode(CalendarViewMode mode, int? days = null)
    {
        var anchor = SelectedInfo?.Occurrence is { } selected ? LocalDate(selected.Start) : PeriodStart;
        Update(s => s with { ViewMode = mode, CustomDayCount = days ?? s.CustomDayCount });
        NavigateTo(anchor);
    }

    /// <summary>Called by a view when scrolling settles.</summary>
    public void OnViewScrolled(DateOnly first, DateOnly lastExclusive, DateOnly? focus = null)
    {
        PeriodStart = Mode == CalendarViewMode.Month ? ViewNavigator.MonthStartOf(focus ?? first) : first;
        PeriodTitle = Mode == CalendarViewMode.Month ? ViewNavigator.MonthTitle(focus ?? first) : TitleFor(first, lastExclusive);
        Run(() => Cache.EnsureAsync(first, lastExclusive, _life.Token));
    }

    // =========================================================================
    // SETTINGS
    // =========================================================================

    /// <summary>Changes and saves settings, then tells views to relayout (and reloads data when filters changed).</summary>
    public void Update(Func<LeafSettings, LeafSettings> change, bool reloadData = false)
    {
        Settings = change(Settings).Normalize();
        using (var conn = _services.Database.Open())
        {
            SettingsStore.Save(conn, Settings);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (reloadData)
        {
            Run(RefreshAsync);
        }
    }

    /// <summary>Shows or hides Saturday and Sunday.</summary>
    public void ToggleWeekends() => Update(s => s with { ShowWeekends = !s.ShowWeekends });

    /// <summary>Shows or hides declined events.</summary>
    public void ToggleDeclined() => Update(s => s with { ShowDeclined = !s.ShowDeclined }, reloadData: true);

    /// <summary>Makes the grid taller (positive) or shorter (negative).</summary>
    public void ZoomBy(double delta) => Update(s => s with { HourHeight = s.HourHeight + delta });

    /// <summary>Default grid height.</summary>
    public void ZoomReset() => Update(s => s with { HourHeight = LeafSettings.DefaultHourHeight });

    // =========================================================================
    // SELECTION
    // =========================================================================

    /// <summary>Selects an event and loads its details.</summary>
    public void Select(CalendarOccurrence occurrence)
    {
        try
        {
            using var conn = _services.Database.Open();
            var stored     = EventStore.Get(conn, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId);
            var details    = stored is null ? null : EventDetailsParser.Parse(stored.RawJson);
            var calendar   = Calendars.FirstOrDefault(c => c.AccountId == occurrence.AccountId && c.Id == occurrence.CalendarId);
            if (details is null)
            {
                return;
            }

            SelectedInfo = new SelectedEventInfo(occurrence, details, WhenText(occurrence), calendar?.Summary ?? "", EventColors.ResolveAccent(occurrence.ColorId, occurrence.CalendarColor));
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            _services.Log.Error("calendar.select.failed", ex);
        }
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
        if (SelectedInfo is null)
        {
            return;
        }

        SelectedInfo = null;
        OccurrencesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the next (+1) or previous (-1) event after the selection (or now), up to 90 days away.</summary>
    public void SelectAdjacent(int direction)
    {
        var anchor = SelectedInfo?.Occurrence;
        var from   = anchor?.Start ?? Now;
        var day    = LocalDate(from);

        for (var i = 0; i <= 90; i++, day = day.AddDays(direction))
        {
            var candidates = Cache.ForDay(day)
                .Where(o => direction > 0 ? o.Start > from || (o.Start == from && anchor is not null && string.CompareOrdinal(o.Key, anchor.Key) > 0) : o.Start < from)
                .OrderBy(o => direction * o.Start.UtcTicks)
                .ToList();

            if (candidates.Count > 0)
            {
                var next = candidates[0];
                Select(next);
                NavigateTo(LocalDate(next.Start));
                ScrollToTimeRequested?.Invoke(this, next.Start);
                return;
            }
        }
    }

    // =========================================================================
    // CALENDARS
    // =========================================================================

    /// <summary>Shows or hides a calendar in Leaf.</summary>
    public void SetCalendarHidden(CalendarInfo calendar, bool hidden)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.SetHidden(conn, calendar.AccountId, calendar.Id, hidden);
        }

        ReloadCalendars();
        Run(RefreshAsync);
    }

    /// <summary>Sets Leaf's color for a calendar (null restores Google's).</summary>
    public void SetCalendarColor(CalendarInfo calendar, string? color)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.SetColor(conn, calendar.AccountId, calendar.Id, color);
        }

        ReloadCalendars();
        Run(RefreshAsync);
    }

    /// <summary>Saves the order of an account's calendars.</summary>
    public void ReorderCalendars(string accountId, IReadOnlyList<string> calendarIds)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.Reorder(conn, accountId, calendarIds);
        }

        ReloadCalendars();
    }

    /// <summary>Re-reads the calendar list (after sync, sidebar edits, or account changes).</summary>
    public void ReloadCalendars()
    {
        using (var conn = _services.Database.Open())
        {
            Calendars = CalendarStore.GetAll(conn);
        }

        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reloads the cached events from the database.</summary>
    public Task RefreshAsync() => Cache.RefreshAsync(_life.Token);

    /// <inheritdoc />
    public void Dispose()
    {
        _minuteTimer.Stop();
        _services.GoogleChanged -= OnGoogleChanged;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _life.Cancel();
        _life.Dispose();
        Cache.Dispose();
    }

    // =========================================================================
    // INTERNALS
    // =========================================================================

    Task<IReadOnlyList<CalendarOccurrence>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var includeDeclined = Settings.ShowDeclined;
        return Task.Run<IReadOnlyList<CalendarOccurrence>>(
            () =>
            {
                using var conn = _services.Database.Open();
                return OccurrenceQuery.Load(conn, from, to, Zone, includeDeclined);
            },
            ct);
    }

    void AttachSync()
    {
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _attachedSync = _services.Google?.Sync;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged += OnSyncDataChanged;
        }
    }

    void OnGoogleChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(AttachSync);

    // Sync runs on a background thread; hop to the UI thread before touching the cache
    void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        ReloadCalendars();
        Run(RefreshAsync);
    });

    void OnMinute()
    {
        var today = _services.Options.StartDate ?? DateOnly.FromDateTime(DateTime.Now);
        if (today != Today)
        {
            Today = today;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshUpcoming();
    }

    void RefreshUpcoming()
    {
        var now   = Now;
        var until = now + UpcomingWindow;
        var items = Enumerable.Range(0, 2)
            .SelectMany(i => Cache.ForDay(LocalDate(now).AddDays(i)))
            .DistinctBy(o => o.Key)
            .Where(o => !o.IsAllDay && o.End > now && o.Start < until)
            .OrderBy(o => o.Start)
            .Take(20)
            .Select(o => new UpcomingItem(o, o.Title, TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime), TimeLabels.Relative(o.Start, o.End, now), EventColors.ResolveAccent(o.ColorId, o.CalendarColor)))
            .ToList();

        Upcoming.Clear();
        foreach (var item in items)
        {
            Upcoming.Add(item);
        }
    }

    string WhenText(CalendarOccurrence o)
    {
        if (o.IsAllDay)
        {
            var last = o.AllDayEnd.AddDays(-1);
            return last > o.AllDayStart
                ? $"{TimeLabels.LongDate(o.AllDayStart)} – {TimeLabels.LongDate(last)} · All day"
                : $"{TimeLabels.LongDate(o.AllDayStart)} · All day";
        }

        return $"{TimeLabels.LongDate(LocalDate(o.Start))} · {TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime)}";
    }

    string TitleFor(DateOnly first, DateOnly lastExclusive) => ViewNavigator.PeriodTitle(first, lastExclusive.AddDays(-1) < first ? first : lastExclusive.AddDays(-1));

    DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    // Background work started from UI events: failures are logged, never thrown into the dispatcher
    async void Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _services.Log.Error("calendar.load.failed", ex);
        }
    }
}
```

`Run` is `async void` on purpose. It's the one UI boundary for fire-and-forget loads, and it catches everything. If CA1031 or VSTHRD100-style analyzers object, add a `[SuppressMessage]` on it with the justification "UI boundary: failures are logged; an unhandled exception here would terminate the WinUI process".

- [ ] **Step 4: Calendar page and sidebar footer**

`src/LeafCalendar.App/Views/CalendarPage.xaml`:
```xml
<Page
    x:Class="LeafCalendar.App.Views.CalendarPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:LeafCalendar.App.Views">

    <Grid x:Name="Root" AutomationProperties.AutomationId="CalendarRoot">
        <Grid.ColumnDefinitions>
            <ColumnDefinition x:Name="SidebarColumn" Width="264" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition x:Name="DetailsColumn" Width="0" />
        </Grid.ColumnDefinitions>

        <!-- Sidebar -->
        <local:SidebarView x:Name="Sidebar" AutomationProperties.AutomationId="Sidebar" />

        <!-- View -->
        <Border Grid.Column="1" Style="{StaticResource LeafSurfaceBorderStyle}">
            <Grid x:Name="ViewHost" AutomationProperties.AutomationId="ViewHost" />
        </Border>

        <!-- Empty State -->
        <StackPanel
            x:Name="EmptyState"
            Grid.Column="1"
            Spacing="12"
            HorizontalAlignment="Center"
            VerticalAlignment="Center"
            Visibility="Collapsed"
            AutomationProperties.AutomationId="EmptyState">
            <TextBlock Style="{StaticResource SubtitleTextBlockStyle}" Text="No calendars yet" HorizontalAlignment="Center" />
            <TextBlock Text="Add a Google account to see your events here." Foreground="{ThemeResource TextFillColorSecondaryBrush}" HorizontalAlignment="Center" />
            <Button
                Style="{StaticResource AccentButtonStyle}"
                Content="Add a Google account"
                HorizontalAlignment="Center"
                Click="OnAddAccountClick"
                AutomationProperties.AutomationId="EmptyAddAccountButton" />
        </StackPanel>
        <!-- /Empty State -->
    </Grid>
</Page>
```

`src/LeafCalendar.App/Views/CalendarPage.xaml.cs`:
```csharp
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views;

/// <summary>Navigation parameter for <see cref="CalendarPage"/>.</summary>
public sealed record CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts);

/// <summary>The main calendar page: sidebar, the current view, and (from Task 16) the details panel.</summary>
public sealed partial class CalendarPage : Page
{
    CalendarPageArgs _args = null!;

    /// <summary>Creates the page.</summary>
    public CalendarPage() => InitializeComponent();

    /// <summary>The page's view model.</summary>
    public CalendarViewModel ViewModel => _args.ViewModel;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (CalendarPageArgs)e.Parameter;

        Sidebar.Attach(ViewModel, _args.OpenAccounts);
        ViewModel.LayoutChanged    += OnLayoutChanged;
        ViewModel.CalendarsChanged += OnCalendarsChanged;

        SetSidebarOpen(ViewModel.Settings.SidebarOpen);
        ViewModel.ReloadCalendars();
        UpdateEmptyState();
        ApplyView();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.LayoutChanged    -= OnLayoutChanged;
        ViewModel.CalendarsChanged -= OnCalendarsChanged;
        Sidebar.Detach();
        ViewHost.Children.Clear();
    }

    /// <summary>Shows or hides the sidebar and remembers the choice.</summary>
    public void SetSidebarOpen(bool open)
    {
        SidebarColumn.Width = new GridLength(open ? 264 : 0);
        Sidebar.Visibility  = open ? Visibility.Visible : Visibility.Collapsed;

        if (ViewModel.Settings.SidebarOpen != open)
        {
            ViewModel.Update(s => s with { SidebarOpen = open });
        }
    }

    /// <summary>Puts the view for the current mode into <see cref="ViewHost"/> (views arrive in Tasks 13 and 14).</summary>
    public void ApplyView()
    {
    }

    void OnLayoutChanged(object? sender, EventArgs e) => ApplyView();

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnAddAccountClick(object sender, RoutedEventArgs e) => _args.OpenAccounts();
}
```

`src/LeafCalendar.App/Views/SidebarView.xaml`:
```xml
<UserControl
    x:Class="LeafCalendar.App.Views.SidebarView"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Grid Padding="12,4,12,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Content (mini month and calendars arrive in Task 12) -->
        <ScrollViewer x:Name="ContentScroll">
            <StackPanel x:Name="ContentPanel" Spacing="4" />
        </ScrollViewer>

        <!-- Footer -->
        <StackPanel Grid.Row="1" Spacing="2" Margin="0,8,0,0">
            <HyperlinkButton
                Content="Google booking pages"
                NavigateUri="https://calendar.google.com/calendar/u/0/appointments"
                AutomationProperties.AutomationId="BookingPagesLink" />
            <Button
                HorizontalAlignment="Stretch"
                HorizontalContentAlignment="Left"
                Background="Transparent"
                BorderThickness="0"
                Click="OnAccountsClick"
                AutomationProperties.AutomationId="AccountsButton">
                <StackPanel Orientation="Horizontal" Spacing="10">
                    <SymbolIcon Symbol="People" />
                    <TextBlock Text="Accounts" />
                </StackPanel>
            </Button>
        </StackPanel>
        <!-- /Footer -->
    </Grid>
</UserControl>
```

`src/LeafCalendar.App/Views/SidebarView.xaml.cs`:
```csharp
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>Sidebar: mini month and calendars (Task 12), booking pages link, and Accounts.</summary>
public sealed partial class SidebarView : UserControl
{
    CalendarViewModel? _viewModel;
    Action? _openAccounts;

    /// <summary>Creates the sidebar.</summary>
    public SidebarView() => InitializeComponent();

    /// <summary>Connects the sidebar to the page's view model.</summary>
    public void Attach(CalendarViewModel viewModel, Action openAccounts)
    {
        _viewModel    = viewModel;
        _openAccounts = openAccounts;
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        _viewModel    = null;
        _openAccounts = null;
    }

    void OnAccountsClick(object sender, RoutedEventArgs e) => _openAccounts?.Invoke();
}
```

- [ ] **Step 5: Title-bar toolbar, navigation, and theme in `MainWindow`**

Replace `src/LeafCalendar.App/MainWindow.xaml` with:
```xml
<Window
    x:Class="LeafCalendar.App.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Leaf Calendar">

    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>

    <Grid x:Name="RootGrid">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- Title Bar -->
        <TitleBar
            x:Name="AppTitleBar"
            Title="Leaf Calendar"
            IsBackButtonVisible="{x:Bind ContentFrame.CanGoBack, Mode=OneWay}"
            IsPaneToggleButtonVisible="False"
            BackRequested="OnBackRequested"
            PaneToggleRequested="OnPaneToggleRequested"
            AutomationProperties.AutomationId="AppTitleBar">
            <TitleBar.Resources>
                <x:Double x:Key="TitleBarCompactHeight">48</x:Double>
            </TitleBar.Resources>
            <TitleBar.IconSource>
                <ImageIconSource ImageSource="ms-appx:///Assets/Square44x44Logo.png" />
            </TitleBar.IconSource>

            <!-- Period Title -->
            <TitleBar.Content>
                <TextBlock
                    x:Name="PeriodTitle"
                    Margin="16,0,0,0"
                    VerticalAlignment="Center"
                    FontSize="16"
                    FontWeight="SemiBold"
                    Visibility="Collapsed"
                    AutomationProperties.AutomationId="PeriodTitle" />
            </TitleBar.Content>

            <!-- Toolbar -->
            <TitleBar.RightHeader>
                <StackPanel x:Name="CalendarToolbar" Orientation="Horizontal" Spacing="4" Margin="0,0,8,0" Visibility="Collapsed">
                    <Button Style="{StaticResource LeafToolbarButtonStyle}" Content="Today" Click="OnTodayClick" ToolTipService.ToolTip="Today (T)" AutomationProperties.AutomationId="TodayButton" />
                    <Button Style="{StaticResource LeafToolbarButtonStyle}" Click="OnPreviousClick" ToolTipService.ToolTip="Previous (←)" AutomationProperties.Name="Previous" AutomationProperties.AutomationId="PreviousButton">
                        <FontIcon Glyph="&#xE76B;" FontSize="12" />
                    </Button>
                    <Button Style="{StaticResource LeafToolbarButtonStyle}" Click="OnNextClick" ToolTipService.ToolTip="Next (→)" AutomationProperties.Name="Next" AutomationProperties.AutomationId="NextButton">
                        <FontIcon Glyph="&#xE76C;" FontSize="12" />
                    </Button>
                    <DropDownButton x:Name="ViewModeButton" Height="32" Content="Week" AutomationProperties.AutomationId="ViewModeButton">
                        <DropDownButton.Flyout>
                            <MenuFlyout Placement="BottomEdgeAlignedRight">
                                <MenuFlyoutItem Text="Day" Tag="Day" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDay" />
                                <MenuFlyoutItem Text="Week" Tag="Week" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewWeek" />
                                <MenuFlyoutItem Text="Month" Tag="Month" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewMonth" />
                                <MenuFlyoutSubItem Text="Number of days">
                                    <MenuFlyoutItem Text="2 days" Tag="Days:2" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays2" />
                                    <MenuFlyoutItem Text="3 days" Tag="Days:3" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays3" />
                                    <MenuFlyoutItem Text="4 days" Tag="Days:4" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays4" />
                                    <MenuFlyoutItem Text="5 days" Tag="Days:5" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays5" />
                                    <MenuFlyoutItem Text="6 days" Tag="Days:6" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays6" />
                                    <MenuFlyoutItem Text="7 days" Tag="Days:7" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays7" />
                                    <MenuFlyoutItem Text="8 days" Tag="Days:8" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays8" />
                                    <MenuFlyoutItem Text="9 days" Tag="Days:9" Click="OnViewModeClick" AutomationProperties.AutomationId="ViewDays9" />
                                    <MenuFlyoutSeparator />
                                    <MenuFlyoutItem Text="Custom…" Click="OnCustomDaysClick" AutomationProperties.AutomationId="ViewDaysCustom" />
                                </MenuFlyoutSubItem>
                                <MenuFlyoutSeparator />
                                <ToggleMenuFlyoutItem x:Name="WeekendsItem" Text="Show weekends" Click="OnWeekendsClick" AutomationProperties.AutomationId="ToggleWeekends" />
                                <ToggleMenuFlyoutItem x:Name="DeclinedItem" Text="Show declined events" Click="OnDeclinedClick" AutomationProperties.AutomationId="ToggleDeclined" />
                                <ToggleMenuFlyoutItem x:Name="WeekNumbersItem" Text="Show week numbers" Click="OnWeekNumbersClick" AutomationProperties.AutomationId="ToggleWeekNumbers" />
                                <ToggleMenuFlyoutItem x:Name="Clock24Item" Text="24-hour time" Click="On24HourClick" AutomationProperties.AutomationId="Toggle24Hour" />
                                <MenuFlyoutSubItem Text="Start week on">
                                    <RadioMenuFlyoutItem x:Name="WeekStartSunday" Text="Sunday" GroupName="WeekStart" Tag="Sunday" Click="OnWeekStartClick" AutomationProperties.AutomationId="WeekStartSunday" />
                                    <RadioMenuFlyoutItem x:Name="WeekStartMonday" Text="Monday" GroupName="WeekStart" Tag="Monday" Click="OnWeekStartClick" AutomationProperties.AutomationId="WeekStartMonday" />
                                    <RadioMenuFlyoutItem x:Name="WeekStartSaturday" Text="Saturday" GroupName="WeekStart" Tag="Saturday" Click="OnWeekStartClick" AutomationProperties.AutomationId="WeekStartSaturday" />
                                </MenuFlyoutSubItem>
                                <MenuFlyoutSubItem Text="Theme">
                                    <RadioMenuFlyoutItem x:Name="ThemeSystem" Text="Use Windows setting" GroupName="Theme" Tag="System" Click="OnThemeClick" AutomationProperties.AutomationId="ThemeSystem" />
                                    <RadioMenuFlyoutItem x:Name="ThemeLight" Text="Light" GroupName="Theme" Tag="Light" Click="OnThemeClick" AutomationProperties.AutomationId="ThemeLight" />
                                    <RadioMenuFlyoutItem x:Name="ThemeDark" Text="Dark" GroupName="Theme" Tag="Dark" Click="OnThemeClick" AutomationProperties.AutomationId="ThemeDark" />
                                </MenuFlyoutSubItem>
                            </MenuFlyout>
                        </DropDownButton.Flyout>
                    </DropDownButton>
                </StackPanel>
            </TitleBar.RightHeader>
            <!-- /Toolbar -->
        </TitleBar>
        <!-- /Title Bar -->

        <!-- Content -->
        <Frame x:Name="ContentFrame" Grid.Row="1" Navigated="OnNavigated" />
    </Grid>
</Window>
```

Replace `src/LeafCalendar.App/MainWindow.xaml.cs` with:
```csharp
using System.ComponentModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App;

/// <summary>
/// Main window: a tall XAML title bar (caption buttons match its 48 px height) holding the period
/// title and the calendar toolbar, a Mica backdrop, and a page frame (setup, calendar, accounts).
/// The back button shows only when the frame can go back; the pane toggle only on the calendar.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly LeafServices _services;
    CalendarViewModel? _calendar;
    bool _syncingMenu;

    /// <summary>Creates the window. <see cref="App"/> owns the services.</summary>
    public MainWindow(LeafServices services)
    {
        _services = services;
        InitializeComponent();

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        Activated += OnActivated;
        Closed    += (_, _) => _calendar?.Dispose();

        if (_services.Google is null)
        {
            ShowSetup();
        }
        else
        {
            ShowCalendar();
        }
    }

    /// <summary>Applies the app theme to the content and caption buttons.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark  => TitleBarTheme.Dark,
            _              => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // =========================================================================
    // NAVIGATION
    // =========================================================================

    void ShowSetup() =>
        ContentFrame.Navigate(typeof(SetupPage), new SetupViewModel(_services.Tokens, OnCredentialsSavedAsync, _services.Log));

    void ShowCalendar()
    {
        if (_calendar is null)
        {
            _calendar = new CalendarViewModel(_services, DispatcherQueue);
            _calendar.PropertyChanged += OnCalendarPropertyChanged;
            ApplyTheme(_calendar.Settings.Theme);
        }

        ContentFrame.Navigate(typeof(CalendarPage), new CalendarPageArgs(_calendar, ShowAccounts));
        ContentFrame.BackStack.Clear();
    }

    void ShowAccounts() =>
        ContentFrame.Navigate(typeof(AccountsPage), new AccountsViewModel(_services, ShowSetup));

    async Task OnCredentialsSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        ShowCalendar();
    }

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        var onCalendar = e.Content is CalendarPage;

        CalendarToolbar.Visibility            = onCalendar ? Visibility.Visible : Visibility.Collapsed;
        PeriodTitle.Visibility                = onCalendar ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = onCalendar;

        if (onCalendar && _calendar is not null)
        {
            PeriodTitle.Text = _calendar.PeriodTitle;
            SyncMenu();
        }
    }

    void OnBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    void OnPaneToggleRequested(TitleBar sender, object args)
    {
        if (ContentFrame.Content is CalendarPage page && _calendar is not null)
        {
            page.SetSidebarOpen(!_calendar.Settings.SidebarOpen);
        }
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _services.Google is not { } google)
        {
            return;
        }

        google.Loop.Mode = SyncMode.Visible;
        google.Loop.TriggerNow();
    }

    // =========================================================================
    // TOOLBAR
    // =========================================================================

    void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.PeriodTitle) && _calendar is not null)
        {
            PeriodTitle.Text = _calendar.PeriodTitle;
        }
    }

    void OnTodayClick(object sender, RoutedEventArgs e) => _calendar?.GoToToday();

    void OnPreviousClick(object sender, RoutedEventArgs e) => _calendar?.Previous();

    void OnNextClick(object sender, RoutedEventArgs e) => _calendar?.Next();

    void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is null || sender is not MenuFlyoutItem { Tag: string tag })
        {
            return;
        }

        if (tag.StartsWith("Days:", StringComparison.Ordinal))
        {
            _calendar.SetMode(CalendarViewMode.Days, int.Parse(tag[5..], System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            _calendar.SetMode(Enum.Parse<CalendarViewMode>(tag));
        }

        SyncMenu();
    }

    async void OnCustomDaysClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is null)
        {
            return;
        }

        try
        {
            var box = new NumberBox { Minimum = 1, Maximum = 31, Value = _calendar.Settings.CustomDayCount, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "CustomDaysBox");

            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Number of days", Content = box, PrimaryButtonText = "Show", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !double.IsNaN(box.Value))
            {
                _calendar.SetMode(CalendarViewMode.Days, (int)box.Value);
                SyncMenu();
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("calendar.custom-days.failed", ex);
        }
    }

    void OnWeekendsClick(object sender, RoutedEventArgs e)
    {
        if (!_syncingMenu)
        {
            _calendar?.ToggleWeekends();
        }
    }

    void OnDeclinedClick(object sender, RoutedEventArgs e)
    {
        if (!_syncingMenu)
        {
            _calendar?.ToggleDeclined();
        }
    }

    void OnWeekNumbersClick(object sender, RoutedEventArgs e) =>
        _calendar?.Update(s => s with { ShowWeekNumbers = WeekNumbersItem.IsChecked });

    void On24HourClick(object sender, RoutedEventArgs e) =>
        _calendar?.Update(s => s with { Use24HourTime = Clock24Item.IsChecked });

    void OnWeekStartClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is not null && sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            _calendar.Update(s => s with { WeekStart = Enum.Parse<DayOfWeek>(tag) });
            _calendar.NavigateTo(_calendar.PeriodStart);
        }
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is not null && sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            var theme = Enum.Parse<AppTheme>(tag);
            _calendar.Update(s => s with { Theme = theme });
            ApplyTheme(theme);
        }
    }

    // Keep the menu's check marks and the button label in step with the settings
    void SyncMenu()
    {
        if (_calendar is null)
        {
            return;
        }

        _syncingMenu = true;
        var s = _calendar.Settings;

        ViewModeButton.Content    = s.ViewMode switch
        {
            CalendarViewMode.Day   => "Day",
            CalendarViewMode.Month => "Month",
            CalendarViewMode.Days  => $"{s.CustomDayCount} days",
            _                      => "Week",
        };
        WeekendsItem.IsChecked      = s.ShowWeekends;
        DeclinedItem.IsChecked      = s.ShowDeclined;
        WeekNumbersItem.IsChecked   = s.ShowWeekNumbers;
        Clock24Item.IsChecked       = s.Use24HourTime;
        WeekStartSunday.IsChecked   = s.WeekStart == DayOfWeek.Sunday;
        WeekStartMonday.IsChecked   = s.WeekStart == DayOfWeek.Monday;
        WeekStartSaturday.IsChecked = s.WeekStart == DayOfWeek.Saturday;
        ThemeSystem.IsChecked       = s.Theme == AppTheme.System;
        ThemeLight.IsChecked        = s.Theme == AppTheme.Light;
        ThemeDark.IsChecked         = s.Theme == AppTheme.Dark;

        _syncingMenu = false;
    }
}
```

`ToggleMenuFlyoutItem` flips `IsChecked` itself before `Click`, and the handlers read settings, so the `_syncingMenu` guard shown is enough.

`OnCustomDaysClick` is `async void`, so it catches everything. That's a UI boundary. If CA1031 objects, add a `[SuppressMessage]` with that justification.

If `AppWindow.TitleBar.PreferredTheme` / `TitleBarTheme` doesn't exist in Windows App SDK 2.5.1, use `AppWindow.TitleBar.ButtonForegroundColor` etc. instead: set dark/light caption glyph colors explicitly. Record which API you used under Deviations.

- [ ] **Step 6: Update the existing UI tests for the new landing page**

After saving credentials, Leaf now opens the calendar page, not Accounts.

In `tests/LeafCalendar.UITests/SetupTests.cs`, in `Save_ValidCredentials_OpensAccountsAndPersistsAcrossLaunches`, replace both `WaitFor("AddAccountButton")` with `WaitFor("CalendarRoot")`, and rename the test to `Save_ValidCredentials_OpensCalendarAndPersistsAcrossLaunches`.

In `tests/LeafCalendar.UITests/AccountFlowTests.cs`, in `LaunchAndAddAccount`, insert this before `leaf.WaitFor("AddAccountButton")`:
```csharp
        leaf.WaitFor("AccountsButton").AsButton().Invoke();
```

- [ ] **Step 7: Shell UI tests**

`tests/LeafCalendar.UITests/CalendarShellTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CalendarShellTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Launch_SignedIn_ShowsCalendarWithToolbarAndTitle()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Assert.NotNull(leaf.WaitFor("TodayButton"));
        Assert.Contains("2026", leaf.WaitFor("PeriodTitle").Name, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountsButton_OpensAccounts_BackReturns()
    {
        using var leaf = Launch();

        leaf.WaitFor("AccountsButton").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("AddAccountButton"));

        leaf.WaitForName("Back").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
    }

    [Fact]
    public void ThemeDark_Persists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("ViewModeButton").AsButton().Invoke();
            leaf.WaitForAnywhere("ThemeDark").AsMenuItem().Invoke();
        }

        using var relaunched = Launch();
        relaunched.WaitFor("ViewModeButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => relaunched.WaitForAnywhere("ThemeDark").Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true
            || relaunched.WaitForAnywhere("ThemeDark").AsMenuItem().IsChecked, TimeSpan.FromSeconds(10)).Success);
    }
}
```

`ThemeDark_Persists` depends on how FlaUI exposes `RadioMenuFlyoutItem`: either as SelectionItem or as a checkable MenuItem. The assertion accepts both. If the submenu has to be opened first to realize the item, invoke the "Theme" submenu by name with `leaf.WaitForName("Theme").AsMenuItem().Invoke()` before looking for `ThemeDark`, in both launches.

- [ ] **Step 8: Build and run**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` → all pass (the memory test skips on Debug).

Check by hand:
- Launch the app. The title bar shows the Leaf icon, "Leaf Calendar", and the period title on the left.
- On the right are Today, the arrows, and the view menu, with the caption buttons still 48 px tall.
- The pane toggle hides the sidebar.
- Light, Dark, and System themes each switch the whole window, caption buttons included.

- [ ] **Step 9: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add calendar shell with title-bar toolbar, sidebar footer, and themes"
```

---

### Task 12: Sidebar (Mini Month and Calendar List)

**Files:**
- Modify: `src/LeafCalendar.App/Views/SidebarView.xaml` + `.xaml.cs`
- Test: `tests/LeafCalendar.UITests/SidebarTests.cs`

**Interfaces:**
- Consumes: `CalendarViewModel` (`Calendars`, `CalendarsChanged`, `PeriodStart`, `NavigateTo`, `SetCalendarHidden`, `SetCalendarColor`, `ReorderCalendars`); `EventColors.CalendarPalette`; `LeafBrushes`.
- Produces:
  - Automation IDs: `MiniMonth`, `CalendarList`, `CalendarToggle_{calendarId}` (a toggle; checked means visible), `CalendarColor_{calendarId}`, `ColorSwatch_{hex without #}`, `ColorReset`
  - `sealed partial class CalendarRow : ObservableObject` (App.ViewModels) with `CalendarInfo Info`, `string Name`, `bool IsVisible`, `string Color`
  - `sealed class AccountGroup` with `string Email` and `ObservableCollection<CalendarRow> Calendars`

- [ ] **Step 1: Row types**

Add to `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (bottom of file):
```csharp
/// <summary>One calendar in the sidebar.</summary>
public sealed partial class CalendarRow(CalendarInfo info) : ObservableObject
{
    /// <summary>The stored calendar.</summary>
    public CalendarInfo Info { get; } = info;

    /// <summary>Display name.</summary>
    public string Name => Info.Summary;

    /// <summary>Checked when shown.</summary>
    public bool IsVisible => Info.IsVisible;

    /// <summary>Hex color.</summary>
    public string Color => Info.DisplayColor;
}

/// <summary>An account's calendars in the sidebar.</summary>
public sealed class AccountGroup(string email, IEnumerable<CalendarRow> calendars)
{
    /// <summary>Account email (header).</summary>
    public string Email { get; } = email;

    /// <summary>Calendars in Leaf's order (drag to reorder).</summary>
    public ObservableCollection<CalendarRow> Calendars { get; } = new(calendars);
}
```

The group header needs the account email. Add `IReadOnlyDictionary<string, string> AccountEmails` to `CalendarViewModel`, filled in `ReloadCalendars()` and the constructor from `AccountStore.GetAll(conn)`:
```csharp
    /// <summary>Account ID → email, for sidebar headers.</summary>
    public IReadOnlyDictionary<string, string> AccountEmails { get; private set; } = new Dictionary<string, string>();
```
In the constructor's `using` block and in `ReloadCalendars()`, add `AccountEmails = AccountStore.GetAll(conn).ToDictionary(a => a.Id, a => a.Email);`.

- [ ] **Step 2: Sidebar XAML and code**

Replace the `<!-- Content ... -->` `ScrollViewer` in `src/LeafCalendar.App/Views/SidebarView.xaml` with:
```xml
        <!-- Content -->
        <ScrollViewer x:Name="ContentScroll" Padding="0,0,4,0">
            <StackPanel Spacing="4">

                <!-- Mini Month -->
                <CalendarView
                    x:Name="MiniMonth"
                    SelectionMode="Single"
                    IsTodayHighlighted="True"
                    IsOutOfScopeEnabled="True"
                    BorderThickness="0"
                    Background="Transparent"
                    DayItemFontSize="12"
                    FirstOfMonthLabelFontSize="0"
                    HorizontalAlignment="Stretch"
                    SelectedDatesChanged="OnMiniMonthSelectedDatesChanged"
                    AutomationProperties.AutomationId="MiniMonth" />

                <!-- Calendars -->
                <ItemsControl x:Name="CalendarList" AutomationProperties.AutomationId="CalendarList">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="vm:AccountGroup">
                            <StackPanel>
                                <TextBlock Style="{StaticResource LeafSectionHeaderStyle}" Text="{x:Bind Email}" TextTrimming="CharacterEllipsis" />
                                <ListView
                                    ItemsSource="{x:Bind Calendars}"
                                    SelectionMode="None"
                                    CanReorderItems="True"
                                    CanDragItems="True"
                                    AllowDrop="True"
                                    DragItemsCompleted="OnCalendarsReordered"
                                    Tag="{x:Bind Email}">
                                    <ListView.ItemContainerStyle>
                                        <Style TargetType="ListViewItem" BasedOn="{StaticResource DefaultListViewItemStyle}">
                                            <Setter Property="MinHeight" Value="32" />
                                            <Setter Property="Padding" Value="4,0" />
                                        </Style>
                                    </ListView.ItemContainerStyle>
                                    <ListView.ItemTemplate>
                                        <DataTemplate x:DataType="vm:CalendarRow">
                                            <Grid ColumnSpacing="8">
                                                <Grid.ColumnDefinitions>
                                                    <ColumnDefinition Width="Auto" />
                                                    <ColumnDefinition Width="*" />
                                                </Grid.ColumnDefinitions>
                                                <!-- Visibility Checkbox (colored) -->
                                                <CheckBox
                                                    MinWidth="0"
                                                    IsChecked="{x:Bind IsVisible, Mode=OneWay}"
                                                    Background="{x:Bind local:SidebarView.Brush(Color), Mode=OneWay}"
                                                    BorderBrush="{x:Bind local:SidebarView.Brush(Color), Mode=OneWay}"
                                                    Click="OnVisibilityClick"
                                                    Tag="{x:Bind}"
                                                    AutomationProperties.Name="{x:Bind Name}"
                                                    AutomationProperties.AutomationId="{x:Bind local:SidebarView.ToggleId(Info)}" />
                                                <!-- Name + Color Flyout -->
                                                <Button
                                                    Grid.Column="1"
                                                    Padding="0"
                                                    Background="Transparent"
                                                    BorderThickness="0"
                                                    HorizontalAlignment="Stretch"
                                                    HorizontalContentAlignment="Left"
                                                    Click="OnColorButtonClick"
                                                    Tag="{x:Bind}"
                                                    ToolTipService.ToolTip="Change color"
                                                    AutomationProperties.Name="{x:Bind Name}"
                                                    AutomationProperties.AutomationId="{x:Bind local:SidebarView.ColorId(Info)}">
                                                    <TextBlock Text="{x:Bind Name}" TextTrimming="CharacterEllipsis" />
                                                </Button>
                                            </Grid>
                                        </DataTemplate>
                                    </ListView.ItemTemplate>
                                </ListView>
                            </StackPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </StackPanel>
        </ScrollViewer>
```
Add these namespaces to the `UserControl` root: `xmlns:vm="using:LeafCalendar.App.ViewModels"` and `xmlns:local="using:LeafCalendar.App.Views"`. Also add `x:Name="Self"`.

`ListView.Tag` holds the email, but reordering needs the account ID. The code-behind reads it from the first row's `Info.AccountId`, so the email tag is only a label.

Replace `src/LeafCalendar.App/Views/SidebarView.xaml.cs` with:
```csharp
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// Sidebar: a mini month that jumps the view, the calendars grouped by account (checkbox shows or
/// hides, the name opens a color picker, drag to reorder), the booking pages link, and Accounts.
/// Visibility, color, and order are Leaf-only; Google is not changed.
/// </summary>
public sealed partial class SidebarView : UserControl
{
    CalendarViewModel? _viewModel;
    Action? _openAccounts;
    bool _updatingMiniMonth;

    /// <summary>Creates the sidebar.</summary>
    public SidebarView() => InitializeComponent();

    /// <summary>x:Bind helper: a brush for a hex color.</summary>
    public static SolidColorBrush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>x:Bind helper: automation ID of a calendar's visibility checkbox.</summary>
    public static string ToggleId(CalendarInfo info) => $"CalendarToggle_{info.Id}";

    /// <summary>x:Bind helper: automation ID of a calendar's name/color button.</summary>
    public static string ColorId(CalendarInfo info) => $"CalendarColor_{info.Id}";

    /// <summary>Connects the sidebar to the page's view model.</summary>
    public void Attach(CalendarViewModel viewModel, Action openAccounts)
    {
        _viewModel    = viewModel;
        _openAccounts = openAccounts;

        _viewModel.CalendarsChanged += OnCalendarsChanged;
        _viewModel.PropertyChanged  += OnViewModelPropertyChanged;

        MiniMonth.FirstDayOfWeek = (Windows.Globalization.DayOfWeek)(int)_viewModel.Settings.WeekStart;
        Rebuild();
        ShowMonthOf(_viewModel.PeriodStart);
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.CalendarsChanged -= OnCalendarsChanged;
            _viewModel.PropertyChanged  -= OnViewModelPropertyChanged;
        }

        _viewModel    = null;
        _openAccounts = null;
    }

    void OnCalendarsChanged(object? sender, EventArgs e) => Rebuild();

    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.PeriodStart) && _viewModel is not null)
        {
            ShowMonthOf(_viewModel.PeriodStart);
        }
    }

    void Rebuild()
    {
        if (_viewModel is null)
        {
            return;
        }

        CalendarList.ItemsSource = _viewModel.Calendars
            .GroupBy(c => c.AccountId)
            .Select(g => new AccountGroup(_viewModel.AccountEmails.GetValueOrDefault(g.Key, g.Key), g.Select(c => new CalendarRow(c))))
            .ToList();
    }

    void ShowMonthOf(DateOnly date)
    {
        _updatingMiniMonth = true;
        MiniMonth.SetDisplayDate(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)));
        _updatingMiniMonth = false;
    }

    void OnMiniMonthSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_updatingMiniMonth || _viewModel is null || args.AddedDates.Count == 0)
        {
            return;
        }

        _viewModel.NavigateTo(DateOnly.FromDateTime(args.AddedDates[0].Date));
        sender.SelectedDates.Clear();
    }

    void OnVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is CheckBox { Tag: CalendarRow row } box)
        {
            _viewModel.SetCalendarHidden(row.Info, hidden: box.IsChecked != true);
        }
    }

    void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: CalendarRow row } button)
        {
            return;
        }

        // Palette Flyout
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 32, ItemHeight = 32 };
        var flyout = new Flyout();
        foreach (var hex in EventColors.CalendarPalette)
        {
            var swatch = new Button
            {
                Width           = 26,
                Height          = 26,
                Padding         = new Thickness(0),
                CornerRadius    = new CornerRadius(13),
                Background      = LeafBrushes.FromHex(hex),
                BorderThickness = new Thickness(string.Equals(hex, row.Color, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
            };
            AutomationProperties.SetAutomationId(swatch, $"ColorSwatch_{hex[1..]}");
            AutomationProperties.SetName(swatch, hex);
            swatch.Click += (_, _) =>
            {
                flyout.Hide();
                _viewModel.SetCalendarColor(row.Info, hex);
            };
            grid.Children.Add(swatch);
        }

        var reset = new HyperlinkButton { Content = "Use Google's color", Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(reset, "ColorReset");
        reset.Click += (_, _) =>
        {
            flyout.Hide();
            _viewModel.SetCalendarColor(row.Info, null);
        };

        flyout.Content = new StackPanel { Children = { grid, reset } };
        flyout.ShowAt(button);
    }

    void OnCalendarsReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_viewModel is null || sender.ItemsSource is not IEnumerable<CalendarRow> rows)
        {
            return;
        }

        var list = rows.ToList();
        if (list.Count > 0)
        {
            _viewModel.ReorderCalendars(list[0].Info.AccountId, [.. list.Select(r => r.Info.Id)]);
        }
    }

    void OnAccountsClick(object sender, RoutedEventArgs e) => _openAccounts?.Invoke();
}
```

`CheckBox.Background`/`BorderBrush` color the box in the calendar's color only in some states of the default style. If the checked fill stays the accent color, override the checkbox's resources per row with `CheckBoxCheckBackgroundFillChecked`, `CheckBoxCheckBackgroundFillCheckedPointerOver`, `CheckBoxCheckBackgroundStrokeChecked`, and `CheckBoxCheckBackgroundStrokeUnchecked`, set from code in the ListView's `ContainerContentChanging` handler. A colored checkbox per calendar is the intended look, as in Google and Notion.

- [ ] **Step 3: Sidebar UI tests**

`tests/LeafCalendar.UITests/SidebarTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SidebarTests : IDisposable
{
    const string FamilyId = "family123@group.calendar.google.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Sidebar_AfterSync_ListsBothCalendarsUnderAccount()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor($"CalendarToggle_{FamilyId}"));
        Assert.NotNull(leaf.WaitForName("leaf.tester@gmail.com"));
        Assert.NotNull(leaf.WaitFor("MiniMonth"));
    }

    [Fact]
    public void HideCalendar_PersistsAcrossLaunches()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().Toggle();
            Assert.True(Retry.WhileFalse(() => leaf.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        Assert.Equal(ToggleState.Off, relaunched.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().ToggleState);
    }

    [Fact]
    public void ChangeColor_PicksSwatch()
    {
        using var leaf = Launch();

        leaf.WaitFor($"CalendarColor_{FamilyId}").AsButton().Invoke();
        leaf.WaitForAnywhere("ColorSwatch_16A765").AsButton().Invoke();

        // The flyout closes and the calendar keeps working; the color itself is covered by CalendarPreferencesTests
        Assert.NotNull(leaf.WaitFor($"CalendarToggle_{FamilyId}"));
    }
}
```

- [ ] **Step 4: Build, run, and look**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1` and `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*SidebarTests" --filter-class "*CalendarShellTests"` → PASS.

Check by hand:
- The mini month shows October 2026 with today (Oct 1) highlighted.
- Calendars sit under the account email, each checkbox in its calendar color.
- Dragging reorders a calendar, and the new order survives a relaunch.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add sidebar with mini month and calendar visibility, color, and order"
```

---

### Task 13: Time Grid View (Day, Week, and N Days)

**Files:**
- Create: `src/LeafCalendar.App/Controls/EventBlock.cs`, `DayColumn.cs`, `DayHeaderCell.cs`, `AllDayCanvas.cs`, `TimeZoneGutter.cs`, `TimeGridView.cs`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml.cs` (`ApplyView`)
- Test: `tests/LeafCalendar.UITests/TimeGridTests.cs`

**Interfaces:**
- Consumes:
  - `CalendarViewModel` (Task 11: `Cache`, `Settings`, `Zone`, `Today`, `Now`, `VisibleColumns`, `PeriodStart`, `OnViewScrolled`, `Select`, `SelectedInfo`, `SetMode`, `NavigateTo`, events)
  - Core: `DayStrip`, `DayLayout`, `SpanLayout`, `EventColors`, `TimeLabels`, `TimeZoneCatalog`, `ViewNavigator`, `OccurrenceQuery.LocalMidnight`
  - `LeafBrushes`
- Produces:
  - `sealed partial class TimeGridView : Grid, IDisposable` with:
    - Constants: `const double DayHeaderHeight = 52`, `const double AllDayLaneHeight = 22`, `const int MaxCollapsedLanes = 3`, `const double ZoneColumnWidth = 56`
    - Layout: `double ColumnWidth`, `double HourHeight`, `double BodyHeight`, `bool IsDark`
    - `CalendarViewModel ViewModel`
    - Actions: `void ScrollToDate(DateOnly date, bool animate)`, `void ScrollToTime(DateTimeOffset instant)`, `void RenderRealized()`
    - `Grid Corner` (the gutter header; Task 15 adds the "+" zone button here)
  - `static string EventBlock.AutomationIdFor(CalendarOccurrence o)` → `Event_{EventId}_{UTC start yyyyMMddHHmm}`
  - Automation IDs: `TimeGrid`, `DayHeader_{yyyy-MM-dd}`, `AllDay_{EventId}_{yyyyMMdd}`, `ZoneLabel_Local`, `ZoneLabel_{IANA id}`, `AllDayExpand`

- [ ] **Step 1: Event block**

`src/LeafCalendar.App/Controls/EventBlock.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One timed event card: a tinted fill, a 3 px accent bar in the event's color, the title, and
/// (when tall enough) the time. Declined events show as an outline with struck-through text, and
/// unanswered or tentative events as an outline. Focus time, out of office, and birthdays get an icon.
/// </summary>
public sealed partial class EventBlock : Grid
{
    // Segoe Fluent Icons; if a glyph renders empty on this Windows build, swap it for E787 (Calendar)
    const string FocusGlyph    = ""; // Stopwatch
    const string AwayGlyph     = ""; // Airplane
    const string BirthdayGlyph = ""; // Giftbox

    readonly Border _card = new() { CornerRadius = new CornerRadius(4) };
    readonly Rectangle _accent = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
    readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2 };
    readonly TextBlock _time = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly FontIcon _icon = new() { FontSize = 11, Margin = new Thickness(0, 1, 4, 0), Visibility = Visibility.Collapsed };
    CalendarOccurrence? _occurrence;
    Action<CalendarOccurrence>? _select;

    /// <summary>Builds the card.</summary>
    public EventBlock()
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_icon);
        titleRow.Children.Add(_title);

        var text = new StackPanel { Margin = new Thickness(9, 3, 4, 2) };
        text.Children.Add(titleRow);
        text.Children.Add(_time);

        var inner = new Grid();
        inner.Children.Add(_accent);
        inner.Children.Add(text);

        _card.Child = inner;
        Children.Add(_card);

        Tapped += (_, e) =>
        {
            if (_occurrence is { } o)
            {
                _select?.Invoke(o);
            }

            e.Handled = true;
        };
    }

    /// <summary>The automation ID tests use: <c>Event_{id}_{UTC yyyyMMddHHmm}</c>.</summary>
    public static string AutomationIdFor(CalendarOccurrence o) =>
        string.Create(CultureInfo.InvariantCulture, $"Event_{o.EventId}_{o.Start.UtcDateTime:yyyyMMddHHmm}");

    /// <summary>Shows <paramref name="occurrence"/>.</summary>
    public void Bind(CalendarOccurrence occurrence, EventPalette palette, string timeText, bool selected, bool compact, Action<CalendarOccurrence> select)
    {
        _occurrence = occurrence;
        _select     = select;

        var declined = occurrence.SelfResponse == ResponseStatus.Declined;
        var outlined = declined || occurrence.SelfResponse is ResponseStatus.NeedsAction or ResponseStatus.Tentative;
        var accent   = LeafBrushes.FromHex(palette.Accent);

        // Card
        _card.Background      = declined ? LeafBrushes.Transparent : outlined ? LeafBrushes.FromHex("#33" + palette.Fill[1..]) : LeafBrushes.FromHex(palette.Fill);
        _card.BorderBrush     = accent;
        _card.BorderThickness = new Thickness(selected ? 2 : outlined ? 1 : 0);
        _accent.Fill          = accent;
        _accent.Visibility    = declined ? Visibility.Collapsed : Visibility.Visible;

        // Text
        var textBrush = outlined ? null : LeafBrushes.FromHex(palette.Text);
        _title.Text            = occurrence.Title;
        _title.TextDecorations = declined ? TextDecorations.Strikethrough : TextDecorations.None;
        _title.MaxLines        = compact ? 1 : 2;
        _time.Text             = timeText;
        _time.Visibility       = compact ? Visibility.Collapsed : Visibility.Visible;
        if (textBrush is null)
        {
            _title.ClearValue(TextBlock.ForegroundProperty);
            _time.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            _title.Foreground = textBrush;
            _time.Foreground  = LeafBrushes.FromHex(palette.SecondaryText);
        }

        // Kind Icon
        (_icon.Glyph, _icon.Visibility) = occurrence.Kind switch
        {
            EventKind.FocusTime   => (FocusGlyph, Visibility.Visible),
            EventKind.OutOfOffice => (AwayGlyph, Visibility.Visible),
            EventKind.Birthday    => (BirthdayGlyph, Visibility.Visible),
            _                     => ("", Visibility.Collapsed),
        };
        _icon.Foreground = textBrush ?? accent;

        AutomationProperties.SetName(this, $"{occurrence.Title}, {timeText}");
        AutomationProperties.SetAutomationId(this, AutomationIdFor(occurrence));
    }
}
```

Tuple deconstruction into two properties (`(_icon.Glyph, _icon.Visibility) = ...`) is valid C#. If the analyzer or reviewer finds it unclear, split it into two assignments.

- [ ] **Step 2: Day column**

`src/LeafCalendar.App/Controls/DayColumn.cs`:
```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One day of the time grid: hour and half-hour lines, the left divider, weekend tint, the event
/// cards (laid out with <see cref="DayLayout"/>), and on today a current-time line. Instances are
/// recycled by the grid, and <see cref="Bind"/> repaints for a new date.
/// </summary>
public sealed partial class DayColumn : Canvas
{
    readonly TimeGridView _owner;
    readonly Rectangle[] _hourLines = new Rectangle[24];
    readonly Rectangle[] _halfLines = new Rectangle[24];
    readonly Rectangle _divider = new() { Width = 1 };
    readonly Rectangle _nowLine = new() { Height = 2, Fill = LeafBrushes.NowLine };
    readonly Ellipse _nowDot = new() { Width = 10, Height = 10, Fill = LeafBrushes.NowLine };
    readonly List<EventBlock> _blocks = [];

    /// <summary>Creates a column owned by <paramref name="owner"/>.</summary>
    public DayColumn(TimeGridView owner)
    {
        _owner = owner;
        for (var h = 0; h < 24; h++)
        {
            Children.Add(_hourLines[h] = new Rectangle { Height = 1 });
            Children.Add(_halfLines[h] = new Rectangle { Height = 1 });
        }

        Children.Add(_divider);
        Children.Add(_nowLine);
        Children.Add(_nowDot);
    }

    /// <summary>The day shown.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Shows <paramref name="date"/>.</summary>
    public void Bind(DateOnly date)
    {
        Date = date;
        Render();
    }

    /// <summary>Repaints for the current size, theme, data, and selection.</summary>
    public void Render()
    {
        var vm     = _owner.ViewModel;
        var dark   = _owner.IsDark;
        var width  = _owner.ColumnWidth;
        var hour   = _owner.HourHeight;
        Width      = width;
        Height     = _owner.BodyHeight;
        Background = ViewNavigator.IsWeekend(Date) ? LeafBrushes.WeekendFill(dark) : LeafBrushes.Transparent;

        // Grid Lines
        for (var h = 0; h < 24; h++)
        {
            _hourLines[h].Width = width;
            _hourLines[h].Fill  = LeafBrushes.GridLine(dark);
            SetTop(_hourLines[h], h * hour);
            _halfLines[h].Width = width;
            _halfLines[h].Fill  = LeafBrushes.HalfHourLine(dark);
            SetTop(_halfLines[h], h * hour + hour / 2);
        }

        _divider.Height = Height;
        _divider.Fill   = LeafBrushes.GridLine(dark);

        // Events
        var blocks   = DayLayout.Layout(Date, vm.Cache.ForDay(Date), vm.Zone);
        var selected = vm.SelectedInfo?.Occurrence.Key;
        EnsureBlocks(blocks.Count);

        for (var i = 0; i < blocks.Count; i++)
        {
            var b       = blocks[i];
            var card    = _blocks[i];
            var usable  = width - 10;
            var colW    = usable / b.ColumnCount;
            var height  = Math.Max((b.EndMinute - b.StartMinute) / 60 * hour - 2, 16);
            var palette = EventColors.Palette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark);

            card.Width      = Math.Max(colW - 2, 10);
            card.Height     = height;
            card.Visibility = Visibility.Visible;
            SetLeft(card, 2 + b.Column * colW);
            SetTop(card, b.StartMinute / 60 * hour + 1);
            card.Bind(b.Occurrence, palette, TimeLabels.Range(b.Occurrence.Start, b.Occurrence.End, vm.Zone, vm.Settings.Use24HourTime), b.Occurrence.Key == selected, compact: height < 36, vm.Select);
        }

        for (var i = blocks.Count; i < _blocks.Count; i++)
        {
            _blocks[i].Visibility = Visibility.Collapsed;
        }

        // Now Line
        var isToday = Date == vm.Today;
        _nowLine.Visibility = _nowDot.Visibility = isToday ? Visibility.Visible : Visibility.Collapsed;
        if (isToday)
        {
            var now = TimeZoneInfo.ConvertTime(vm.Now, vm.Zone);
            var top = now.TimeOfDay.TotalMinutes / 60 * hour;
            _nowLine.Width = width;
            SetTop(_nowLine, top - 1);
            SetLeft(_nowDot, -5);
            SetTop(_nowDot, top - 5);
            SetZIndex(_nowLine, 10);
            SetZIndex(_nowDot, 10);
        }
    }

    void EnsureBlocks(int count)
    {
        while (_blocks.Count < count)
        {
            var block = new EventBlock();
            _blocks.Add(block);
            Children.Add(block);
        }
    }
}
```

- [ ] **Step 3: Day header, all-day canvas, and gutter**

`src/LeafCalendar.App/Controls/DayHeaderCell.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>A day header: weekday name over the date number (today on an accent circle). Tap opens Day view.</summary>
public sealed partial class DayHeaderCell : Grid
{
    readonly TimeGridView _owner;
    readonly TextBlock _weekday = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _number = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _circle = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Center };
    readonly Rectangle _divider = new() { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Height = 14 };

    /// <summary>Creates a header owned by <paramref name="owner"/>.</summary>
    public DayHeaderCell(TimeGridView owner)
    {
        _owner = owner;
        Height = TimeGridView.DayHeaderHeight;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        _circle.Child = _number;
        stack.Children.Add(_weekday);
        stack.Children.Add(_circle);

        Children.Add(stack);
        Children.Add(_divider);

        Tapped += (_, _) =>
        {
            _owner.ViewModel.SetMode(CalendarViewMode.Day);
            _owner.ViewModel.NavigateTo(Date);
        };
    }

    /// <summary>The day shown.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>Shows <paramref name="date"/>.</summary>
    public void Bind(DateOnly date)
    {
        Date  = date;
        Width = _owner.ColumnWidth;

        var dark    = _owner.IsDark;
        var isToday = date == _owner.ViewModel.Today;

        _weekday.Text       = TimeLabels.WeekdayShort(date);
        _weekday.Foreground = isToday ? LeafBrushes.Accent : LeafBrushes.SecondaryText(dark);
        _number.Text        = date.Day.ToString(CultureInfo.InvariantCulture);
        _number.Foreground  = isToday ? LeafBrushes.OnAccent : LeafBrushes.PrimaryText(dark);
        _circle.Background  = isToday ? LeafBrushes.Accent : LeafBrushes.Transparent;
        _divider.Fill       = LeafBrushes.GridLine(dark);

        AutomationProperties.SetAutomationId(this, $"DayHeader_{date:yyyy-MM-dd}");
        AutomationProperties.SetName(this, TimeLabels.LongDate(date));
    }
}
```

`src/LeafCalendar.App/Controls/AllDayCanvas.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// The all-day row: all-day and 24-hour-plus events as bars across day columns, packed into lanes
/// with <see cref="SpanLayout"/>. Only a window of columns around the visible ones is drawn.
/// </summary>
public sealed partial class AllDayCanvas : Canvas
{
    readonly TimeGridView _owner;
    readonly List<Border> _chips = [];

    /// <summary>Creates the row owned by <paramref name="owner"/>.</summary>
    public AllDayCanvas(TimeGridView owner) => _owner = owner;

    /// <summary>Lanes used by the last render.</summary>
    public int LaneCount { get; private set; }

    /// <summary>Draws columns <c>[firstIndex, firstIndex + count)</c> of the strip, showing at most <paramref name="maxLanes"/> lanes.</summary>
    public void Render(DayStrip strip, int firstIndex, int count, int maxLanes)
    {
        var vm      = _owner.ViewModel;
        var dark    = _owner.IsDark;
        var width   = _owner.ColumnWidth;
        var first   = Math.Max(0, firstIndex);
        var columns = Enumerable.Range(first, Math.Min(count, strip.Count - first)).Select(i => strip[i]).ToList();
        var items   = columns.SelectMany(vm.Cache.ForDay).DistinctBy(o => o.Key).ToList();
        var blocks  = SpanLayout.Layout(columns, items, vm.Zone, includeTimed: false);

        LaneCount = blocks.Count == 0 ? 0 : blocks.Max(b => b.Lane) + 1;
        var shown = blocks.Where(b => b.Lane < maxLanes).ToList();

        while (_chips.Count < shown.Count)
        {
            var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0) };
            chip.Child = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            chip.Tapped += (s, e) =>
            {
                if (((Border)s).Tag is CalendarOccurrence o)
                {
                    vm.Select(o);
                }

                e.Handled = true;
            };
            _chips.Add(chip);
            Children.Add(chip);
        }

        for (var i = 0; i < _chips.Count; i++)
        {
            var chip = _chips[i];
            if (i >= shown.Count)
            {
                chip.Visibility = Visibility.Collapsed;
                continue;
            }

            var b       = shown[i];
            var palette = EventColors.Palette(EventColors.ResolveAccent(b.Occurrence.ColorId, b.Occurrence.CalendarColor), dark);
            var text    = (TextBlock)chip.Child;
            var start   = SpanLayout.CoveredDates(b.Occurrence, vm.Zone).First;

            chip.Visibility      = Visibility.Visible;
            chip.Tag             = b.Occurrence;
            chip.Width           = Math.Max(b.ColumnSpan * width - 4, 8);
            chip.Height          = TimeGridView.AllDayLaneHeight - 3;
            chip.Background      = LeafBrushes.FromHex(palette.Fill);
            chip.BorderBrush     = LeafBrushes.FromHex(palette.Accent);
            chip.BorderThickness = new Thickness(b.Occurrence.Key == vm.SelectedInfo?.Occurrence.Key ? 2 : 0);
            text.Text            = (b.ContinuesBefore ? "‹ " : "") + b.Occurrence.Title + (b.ContinuesAfter ? " ›" : "");
            text.Foreground      = LeafBrushes.FromHex(palette.Text);

            SetLeft(chip, (first + b.FirstColumn) * width + 2);
            SetTop(chip, b.Lane * TimeGridView.AllDayLaneHeight + 2);
            AutomationProperties.SetAutomationId(chip, string.Create(CultureInfo.InvariantCulture, $"AllDay_{b.Occurrence.EventId}_{start:yyyyMMdd}"));
            AutomationProperties.SetName(chip, b.Occurrence.Title);
        }
    }
}
```

`src/LeafCalendar.App/Controls/TimeZoneGutter.cs`:
```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Hour labels on the left: one column for the local zone, then one per extra zone. An extra zone
/// shows the time there at each local hour of the first visible day, so half-hour zones read "5:30 PM".
/// </summary>
public sealed partial class TimeZoneGutter : Canvas
{
    readonly TimeGridView _owner;

    /// <summary>Creates the gutter owned by <paramref name="owner"/>.</summary>
    public TimeZoneGutter(TimeGridView owner) => _owner = owner;

    /// <summary>Redraws for <paramref name="day"/>.</summary>
    public void Render(DateOnly day)
    {
        var vm    = _owner.ViewModel;
        var dark  = _owner.IsDark;
        var hour  = _owner.HourHeight;
        var zones = new List<TimeZoneInfo> { vm.Zone };
        zones.AddRange(vm.Settings.TimeZones.Select(z => TimeZoneInfo.FindSystemTimeZoneById(z.Id)));

        Children.Clear();
        Width  = zones.Count * TimeGridView.ZoneColumnWidth;
        Height = _owner.BodyHeight;

        var midnight = OccurrenceQuery.LocalMidnight(day, vm.Zone);
        for (var z = 0; z < zones.Count; z++)
        {
            for (var h = 1; h < 24; h++)
            {
                var label = z == 0
                    ? TimeLabels.HourLabel(h, vm.Settings.Use24HourTime)
                    : TimeLabels.TimeOfDay(midnight.AddHours(h), zones[z], vm.Settings.Use24HourTime);

                var text = new TextBlock
                {
                    Text          = label,
                    FontSize      = 11,
                    Width         = TimeGridView.ZoneColumnWidth - 8,
                    TextAlignment = TextAlignment.Right,
                    Foreground    = z == 0 ? LeafBrushes.SecondaryText(dark) : LeafBrushes.DimText(dark),
                };
                SetLeft(text, z * TimeGridView.ZoneColumnWidth);
                SetTop(text, h * hour - 8);
                Children.Add(text);
            }
        }
    }
}
```

- [ ] **Step 4: The time grid**

`src/LeafCalendar.App/Controls/TimeGridView.cs`:
```csharp
using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Day, week, and N-day view.
/// </summary>
/// <remarks>
/// The layout is a 2×2 grid. Top left is the corner (zone labels, week number, all-day expand).
/// Top right is the day headers and the all-day row. Bottom left is the hour gutter. Bottom right is
/// the day columns. The day columns live in an <see cref="ItemsRepeater"/> over a <see cref="DayStrip"/>
/// of about 8 years, so only the visible days plus one screen on each side are built, and they're
/// recycled as you scroll. Header, gutter, and body scroll in step. When scrolling stops, the view
/// snaps to a day edge and tells the view model which days are showing (that loads data and sets the
/// title). Pagers and "today" scroll with animation.
/// </remarks>
public sealed partial class TimeGridView : Grid, IDisposable
{
    /// <summary>Day header height.</summary>
    public const double DayHeaderHeight = 52;

    /// <summary>All-day lane height.</summary>
    public const double AllDayLaneHeight = 22;

    /// <summary>Lanes shown before "expand".</summary>
    public const int MaxCollapsedLanes = 3;

    /// <summary>Width of one time-zone column in the gutter.</summary>
    public const double ZoneColumnWidth = 56;

    // ponytail: ~8 years of days; rebuild the strip around the target date if someone scrolls past the ends
    const int StripDaysEachSide = 1500;

    readonly CalendarViewModel _vm;
    readonly ScrollViewer _headerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ScrollViewer _gutterScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ScrollViewer _bodyScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, ZoomMode = ZoomMode.Disabled };
    readonly ItemsRepeater _headerRepeater = new() { Layout = new StackLayout { Orientation = Orientation.Horizontal }, HorizontalCacheLength = 2 };
    readonly ItemsRepeater _bodyRepeater = new() { Layout = new StackLayout { Orientation = Orientation.Horizontal }, HorizontalCacheLength = 2 };
    readonly Grid _headerContent = new();
    readonly AllDayCanvas _allDay;
    readonly TimeZoneGutter _gutter;
    readonly StackPanel _zoneLabels = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBlock _weekNumber = new() { FontSize = 11, Margin = new Thickness(8, 6, 0, 0) };
    readonly Button _allDayExpand = new() { Padding = new Thickness(4), Background = LeafBrushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    readonly DispatcherQueueTimer _clock;
    readonly HashSet<DayColumn> _columns = [];
    readonly HashSet<DayHeaderCell> _headers = [];
    DayStrip _strip = null!;
    bool _syncing;
    bool _allDayExpanded;
    bool _initialized;
    int _firstIndex;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public TimeGridView(CalendarViewModel vm)
    {
        _vm     = vm;
        _allDay = new AllDayCanvas(this);
        _gutter = new TimeZoneGutter(this);
        AutomationProperties.SetAutomationId(this, "TimeGrid");

        // Rows And Columns
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Corner
        Corner = new Grid();
        Corner.Children.Add(_weekNumber);
        Corner.Children.Add(_zoneLabels);
        Corner.Children.Add(_allDayExpand);
        _allDayExpand.Content = new FontIcon { Glyph = "", FontSize = 10 };
        AutomationProperties.SetAutomationId(_allDayExpand, "AllDayExpand");
        AutomationProperties.SetName(_allDayExpand, "Show all all-day events");
        _allDayExpand.Click += (_, _) =>
        {
            _allDayExpanded = !_allDayExpanded;
            RenderAllDay();
        };
        Children.Add(Corner);

        // Header (day names + all-day row)
        _headerRepeater.ItemTemplate = new DayHeaderFactory(this);
        _headerRepeater.ElementPrepared += (_, e) => _headers.Add((DayHeaderCell)e.Element);
        _headerRepeater.ElementClearing += (_, e) => _headers.Remove((DayHeaderCell)e.Element);
        _headerContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayHeaderHeight) });
        _headerContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _headerContent.Children.Add(_headerRepeater);
        SetRow(_allDay, 1);
        _headerContent.Children.Add(_allDay);
        _headerScroll.Content = _headerContent;
        SetColumn(_headerScroll, 1);
        Children.Add(_headerScroll);

        // Gutter
        _gutterScroll.Content = _gutter;
        SetRow(_gutterScroll, 1);
        Children.Add(_gutterScroll);

        // Body
        _bodyRepeater.ItemTemplate = new DayColumnFactory(this);
        _bodyRepeater.ElementPrepared += (_, e) => _columns.Add((DayColumn)e.Element);
        _bodyRepeater.ElementClearing += (_, e) => _columns.Remove((DayColumn)e.Element);
        _bodyScroll.Content = _bodyRepeater;
        SetRow(_bodyScroll, 1);
        SetColumn(_bodyScroll, 1);
        Children.Add(_bodyScroll);

        // Scroll Sync And Snapping
        _bodyScroll.ViewChanging   += OnBodyViewChanging;
        _bodyScroll.ViewChanged    += OnBodyViewChanged;
        _headerScroll.ViewChanging += (_, e) => Sync(() => _bodyScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true));
        _gutterScroll.ViewChanging += (_, e) => Sync(() => _bodyScroll.ChangeView(null, e.NextView.VerticalOffset, null, true));
        _bodyScroll.SizeChanged    += (_, _) => Relayout(keepIndex: true);

        // View Model
        _vm.OccurrencesChanged    += OnOccurrencesChanged;
        _vm.LayoutChanged         += OnLayoutChanged;
        _vm.NavigateRequested     += OnNavigateRequested;
        _vm.ScrollToTimeRequested += OnScrollToTimeRequested;
        ActualThemeChanged        += (_, _) => RenderRealized();

        // Now Line Clock
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromMinutes(1);
        _clock.Tick += (_, _) => RenderToday();
        _clock.Start();

        BuildStrip(_vm.PeriodStart);
    }

    /// <summary>The top-left corner above the gutter.</summary>
    public Grid Corner { get; }

    /// <summary>The view model.</summary>
    public CalendarViewModel ViewModel => _vm;

    /// <summary>Width of one day column.</summary>
    public double ColumnWidth { get; private set; } = 120;

    /// <summary>Height of one hour.</summary>
    public double HourHeight => _vm.Settings.HourHeight;

    /// <summary>Height of the 24-hour body.</summary>
    public double BodyHeight => HourHeight * 24;

    /// <summary>True in the dark theme.</summary>
    public bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>Scrolls so <paramref name="date"/> is the first visible column.</summary>
    public void ScrollToDate(DateOnly date, bool animate)
    {
        if (!_strip.Contains(date))
        {
            BuildStrip(date);
        }

        _bodyScroll.ChangeView(_strip.IndexOf(date) * ColumnWidth, null, null, !animate);
    }

    /// <summary>Scrolls vertically so <paramref name="instant"/> sits a third of the way down.</summary>
    public void ScrollToTime(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _vm.Zone);
        var y     = local.TimeOfDay.TotalHours * HourHeight - _bodyScroll.ViewportHeight / 3;
        _bodyScroll.ChangeView(null, Math.Max(0, y), null, false);
    }

    /// <summary>Repaints every built column, header, the all-day row, and the gutter.</summary>
    public void RenderRealized()
    {
        foreach (var column in _columns)
        {
            column.Render();
        }

        foreach (var header in _headers)
        {
            header.Bind(header.Date);
        }

        RenderAllDay();
        RenderCorner();
        _gutter.Render(_strip[_firstIndex]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _clock.Stop();
        _vm.OccurrencesChanged    -= OnOccurrencesChanged;
        _vm.LayoutChanged         -= OnLayoutChanged;
        _vm.NavigateRequested     -= OnNavigateRequested;
        _vm.ScrollToTimeRequested -= OnScrollToTimeRequested;
    }

    // =========================================================================
    // LAYOUT
    // =========================================================================

    void BuildStrip(DateOnly around)
    {
        _strip = new DayStrip(around, StripDaysEachSide, StripDaysEachSide, skipWeekends: !_vm.Settings.ShowWeekends);
        var items = Enumerable.Range(0, _strip.Count).Select(i => new DayItem(_strip[i])).ToList();
        _headerRepeater.ItemsSource = items;
        _bodyRepeater.ItemsSource   = items;
        _firstIndex = _strip.IndexOf(around);
    }

    void Relayout(bool keepIndex)
    {
        var viewport = _bodyScroll.ViewportWidth > 0 ? _bodyScroll.ViewportWidth : _bodyScroll.ActualWidth;
        if (viewport <= 0)
        {
            return;
        }

        var index = _firstIndex;
        ColumnWidth = Math.Max(48, viewport / _vm.VisibleColumns);
        _bodyRepeater.Height   = BodyHeight;
        _headerRepeater.Height = DayHeaderHeight;
        _bodyRepeater.InvalidateMeasure();
        _headerRepeater.InvalidateMeasure();
        RenderRealized();

        // First Layout: jump to the period and to 7:30 AM
        if (!_initialized)
        {
            _initialized = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _bodyScroll.ChangeView(_strip.IndexOf(_vm.PeriodStart) * ColumnWidth, Math.Max(0, 7.5 * HourHeight - 20), null, true);
                ReportVisible();
            });
            return;
        }

        if (keepIndex)
        {
            DispatcherQueue.TryEnqueue(() => _bodyScroll.ChangeView(index * ColumnWidth, null, null, true));
        }
    }

    void OnBodyViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        Sync(() =>
        {
            _headerScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
            _gutterScroll.ChangeView(null, e.NextView.VerticalOffset, null, true);
        });
    }

    void OnBodyViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            return;
        }

        // Snap To A Day Edge
        var index  = (int)Math.Round(_bodyScroll.HorizontalOffset / ColumnWidth);
        var target = index * ColumnWidth;
        if (Math.Abs(target - _bodyScroll.HorizontalOffset) > 0.5)
        {
            _bodyScroll.ChangeView(target, null, null, false);
            return;
        }

        ReportVisible();
    }

    void ReportVisible()
    {
        _firstIndex = (int)Math.Round(_bodyScroll.HorizontalOffset / ColumnWidth);
        var first = _strip[_firstIndex];
        var after = _firstIndex + _vm.VisibleColumns >= _strip.Count ? _strip.Last.AddDays(1) : _strip[_firstIndex + _vm.VisibleColumns];

        _vm.OnViewScrolled(first, after);
        RenderAllDay();
        RenderCorner();
        _gutter.Render(first);
    }

    void RenderAllDay()
    {
        var count    = _vm.VisibleColumns;
        var maxLanes = _allDayExpanded ? int.MaxValue : MaxCollapsedLanes;
        _allDay.Render(_strip, _firstIndex - count, count * 3, maxLanes);

        var lanes = Math.Min(_allDay.LaneCount, maxLanes);
        _allDay.Height = lanes * AllDayLaneHeight + 4;
        _allDay.Width  = _strip.Count * ColumnWidth;
        _allDayExpand.Visibility = _allDay.LaneCount > MaxCollapsedLanes ? Visibility.Visible : Visibility.Collapsed;
        Corner.Height = DayHeaderHeight + _allDay.Height;
    }

    void RenderCorner()
    {
        var dark = IsDark;
        _zoneLabels.Children.Clear();

        var zones = new List<(string Id, string Label)> { ("Local", TimeZoneCatalog.OffsetLabel(_vm.Zone.GetUtcOffset(_vm.Now))) };
        zones.AddRange(_vm.Settings.TimeZones.Select(z => (z.Id, TimeZoneCatalog.ShortLabel(z))));

        foreach (var (id, label) in zones)
        {
            var text = new TextBlock { Text = label, FontSize = 10, Width = ZoneColumnWidth - 8, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = LeafBrushes.SecondaryText(dark), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(text, $"ZoneLabel_{id}");
            _zoneLabels.Children.Add(text);
        }

        Corner.Width = zones.Count * ZoneColumnWidth;
        _weekNumber.Text       = _vm.Settings.ShowWeekNumbers ? string.Create(CultureInfo.InvariantCulture, $"W{ViewNavigator.WeekNumber(_strip[_firstIndex])}") : "";
        _weekNumber.Foreground = LeafBrushes.SecondaryText(dark);
    }

    void RenderToday()
    {
        foreach (var column in _columns.Where(c => c.Date == _vm.Today))
        {
            column.Render();
        }
    }

    void Sync(Action action)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        action();
        _syncing = false;
    }

    // =========================================================================
    // VIEW MODEL EVENTS
    // =========================================================================

    void OnOccurrencesChanged(object? sender, EventArgs e) => RenderRealized();

    void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (_strip.SkipsWeekends == _vm.Settings.ShowWeekends)
        {
            BuildStrip(_strip[_firstIndex]);
        }

        Relayout(keepIndex: true);
    }

    void OnNavigateRequested(object? sender, DateOnly date) => ScrollToDate(date, animate: true);

    void OnScrollToTimeRequested(object? sender, DateTimeOffset instant) => ScrollToTime(instant);

    // =========================================================================
    // RECYCLING
    // =========================================================================

    /// <summary>An item in the strip (a class, so WinRT can hold it).</summary>
    public sealed class DayItem(DateOnly date)
    {
        /// <summary>The day.</summary>
        public DateOnly Date { get; } = date;
    }

    sealed partial class DayColumnFactory(TimeGridView owner) : IElementFactory
    {
        readonly Stack<DayColumn> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var column = _pool.Count > 0 ? _pool.Pop() : new DayColumn(owner);
            column.Bind(((DayItem)args.Data).Date);
            return column;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => _pool.Push((DayColumn)args.Element);
    }

    sealed partial class DayHeaderFactory(TimeGridView owner) : IElementFactory
    {
        readonly Stack<DayHeaderCell> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var cell = _pool.Count > 0 ? _pool.Pop() : new DayHeaderCell(owner);
            cell.Bind(((DayItem)args.Data).Date);
            return cell;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => _pool.Push((DayHeaderCell)args.Element);
    }
}
```

In `src/LeafCalendar.App/Views/CalendarPage.xaml.cs`, replace the empty `ApplyView()` with a version that keeps one view per mode family and disposes the old one:
```csharp
    IDisposable? _view;
    bool _viewIsMonth;

    /// <summary>Puts the view for the current mode into <see cref="ViewHost"/>.</summary>
    public void ApplyView()
    {
        var wantMonth = ViewModel.Mode == Core.Settings.CalendarViewMode.Month;
        if (_view is not null && wantMonth == _viewIsMonth)
        {
            return;
        }

        _view?.Dispose();
        ViewHost.Children.Clear();

        var view = new Controls.TimeGridView(ViewModel);
        _view        = view;
        _viewIsMonth = false;
        ViewHost.Children.Add(view);
    }
```
Also dispose `_view` in `OnNavigatedFrom` (`_view?.Dispose(); _view = null;`). Task 14 adds the month branch.

- [ ] **Step 5: Time-grid UI tests**

`tests/LeafCalendar.UITests/TimeGridTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
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
    public void DayView_FromMenu_ShowsOneColumn()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewDay").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Day", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));
    }
}
```

The Oct 5 week holds the standup series; Oct 12 is two weeks after Sep 27. Offscreen columns within one screen may already be built, but the canceled Oct 7 instance must never exist. If `ViewModeButton`'s accessible name doesn't include its content in this FlaUI version, assert with `leaf.WaitFor("ViewModeButton").Patterns.Value.PatternOrDefault` or its text child instead.

- [ ] **Step 6: Build, run, and check it by hand**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1` and `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TimeGridTests"` → PASS.

Launch with your real account and check all of the following:
- Horizontal trackpad and Shift+wheel scrolling is smooth and snaps to day edges.
- The arrows slide a full week.
- Today's column shows the red now-line.
- Overlapping meetings sit side by side.
- Resizing the window keeps the same first day.
- Idle memory hasn't jumped (Task Manager).

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add virtualized day/week/N-day time grid with synced scrolling"
```

---

### Task 14: Month View

**Files:**
- Create: `src/LeafCalendar.App/Controls/MonthGridView.cs`, `src/LeafCalendar.App/Controls/WeekRow.cs`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml.cs` (`ApplyView` month branch)
- Test: `tests/LeafCalendar.UITests/MonthViewTests.cs`

**Interfaces:**
- Consumes: `CalendarViewModel`, `SpanLayout` (`includeTimed: true`), `ViewNavigator`, `EventColors`, `TimeLabels`, `LeafBrushes`.
- Produces:
  - `sealed partial class MonthGridView : Grid, IDisposable` with:
    - `const double MinRowHeight = 96`, `const double ChipHeight = 20`, `const double DayNumberHeight = 26`
    - `double RowHeight`, `double ColumnWidth`, `DateOnly FocusMonth`, `bool IsDark`, `CalendarViewModel ViewModel`
    - `IReadOnlyList<DateOnly> ColumnDates(DateOnly weekStart)`
    - `void ScrollToDate(DateOnly, bool animate)`
  - Automation IDs: `MonthGrid`, `MonthDay_{yyyy-MM-dd}` (day number button), `Chip_{EventId}_{yyyyMMdd}`, `More_{yyyy-MM-dd}`

- [ ] **Step 1: Week row**

`src/LeafCalendar.App/Controls/WeekRow.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One week of the month view.
/// </summary>
/// <remarks>
/// Each day cell shows its date, and events are chips packed into lanes with <see cref="SpanLayout"/>.
/// All-day and multi-day events are filled bars; timed events are a dot, time, and title. When a day
/// has more events than fit, the last lane shows "+N more", which opens that day's full list.
/// </remarks>
public sealed partial class WeekRow : Canvas
{
    readonly MonthGridView _owner;

    /// <summary>Creates a row owned by <paramref name="owner"/>.</summary>
    public WeekRow(MonthGridView owner) => _owner = owner;

    /// <summary>First day of the week shown.</summary>
    public DateOnly WeekStart { get; private set; }

    /// <summary>Shows the week starting <paramref name="weekStart"/>.</summary>
    public void Bind(DateOnly weekStart)
    {
        WeekStart = weekStart;
        Render();
    }

    /// <summary>Repaints (rebuilds its few children; ponytail: pool chips if month scrolling ever stutters).</summary>
    public void Render()
    {
        var vm     = _owner.ViewModel;
        var dark   = _owner.IsDark;
        var dates  = _owner.ColumnDates(WeekStart);
        var colW   = _owner.ColumnWidth;
        var height = _owner.RowHeight;

        Children.Clear();
        Width      = colW * dates.Count;
        Height     = height;
        Background = LeafBrushes.Transparent;

        // Cells
        for (var c = 0; c < dates.Count; c++)
        {
            var date = dates[c];
            AddLine(c * colW, 0, 1, height, dark);
            AddLine(c * colW, 0, colW, 1, dark);
            if (ViewNavigator.IsWeekend(date))
            {
                var tint = new Rectangle { Width = colW, Height = height, Fill = LeafBrushes.WeekendFill(dark) };
                SetLeft(tint, c * colW);
                Children.Add(tint);
            }

            Children.Add(DayNumber(date, c * colW, dark));
        }

        // Chips
        var items    = dates.SelectMany(vm.Cache.ForDay).DistinctBy(o => o.Key).ToList();
        var blocks   = SpanLayout.Layout(dates, items, vm.Zone, includeTimed: true);
        var maxLanes = Math.Max(1, (int)((height - MonthGridView.DayNumberHeight - 4) / MonthGridView.ChipHeight));
        var overflow = new int[dates.Count];

        foreach (var b in blocks)
        {
            var overflowing = Enumerable.Range(b.FirstColumn, b.ColumnSpan).Any(c => blocks.Any(x => x.Lane >= maxLanes && c >= x.FirstColumn && c < x.FirstColumn + x.ColumnSpan));
            var laneLimit   = overflowing ? maxLanes - 1 : maxLanes;
            if (b.Lane < laneLimit)
            {
                Children.Add(Chip(b, colW, dark));
                continue;
            }

            for (var c = b.FirstColumn; c < b.FirstColumn + b.ColumnSpan; c++)
            {
                overflow[c]++;
            }
        }

        // "+N More"
        for (var c = 0; c < dates.Count; c++)
        {
            if (overflow[c] > 0)
            {
                Children.Add(More(dates[c], overflow[c], c * colW, MonthGridView.DayNumberHeight + (maxLanes - 1) * MonthGridView.ChipHeight));
            }
        }
    }

    void AddLine(double x, double y, double w, double h, bool dark)
    {
        var line = new Rectangle { Width = w, Height = h, Fill = LeafBrushes.GridLine(dark) };
        SetLeft(line, x);
        SetTop(line, y);
        Children.Add(line);
    }

    UIElement DayNumber(DateOnly date, double x, bool dark)
    {
        var vm      = _owner.ViewModel;
        var isToday = date == vm.Today;
        var inMonth = date.Month == _owner.FocusMonth.Month && date.Year == _owner.FocusMonth.Year;
        var text    = date.Day == 1 ? date.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")) : date.Day.ToString(CultureInfo.InvariantCulture);

        var button = new Button
        {
            Content         = new TextBlock { Text = text, FontSize = 12, FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal },
            Padding         = new Thickness(6, 1, 6, 1),
            MinWidth        = 24,
            Height          = 22,
            CornerRadius    = new CornerRadius(11),
            BorderThickness = new Thickness(0),
            Background      = isToday ? LeafBrushes.Accent : LeafBrushes.Transparent,
            Foreground      = isToday ? LeafBrushes.OnAccent : inMonth ? LeafBrushes.PrimaryText(dark) : LeafBrushes.DimText(dark),
        };
        AutomationProperties.SetAutomationId(button, $"MonthDay_{date:yyyy-MM-dd}");
        AutomationProperties.SetName(button, TimeLabels.LongDate(date));
        button.Click += (_, _) =>
        {
            vm.SetMode(CalendarViewMode.Day);
            vm.NavigateTo(date);
        };

        SetLeft(button, x + 4);
        SetTop(button, 3);
        return button;
    }

    UIElement Chip(SpanBlock b, double colW, bool dark)
    {
        var vm       = _owner.ViewModel;
        var o        = b.Occurrence;
        var palette  = EventColors.Palette(EventColors.ResolveAccent(o.ColorId, o.CalendarColor), dark);
        var spanning = SpanLayout.IsSpanning(o);
        var selected = o.Key == vm.SelectedInfo?.Occurrence.Key;
        var first    = SpanLayout.CoveredDates(o, vm.Zone).First;

        var text = new TextBlock
        {
            FontSize      = 12,
            TextTrimming  = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Text          = spanning ? o.Title : $"{TimeLabels.Compact(o.Start, vm.Zone, vm.Settings.Use24HourTime)} {o.Title}",
            Foreground    = spanning ? LeafBrushes.FromHex(palette.Text) : LeafBrushes.PrimaryText(dark),
            FontWeight    = spanning ? FontWeights.SemiBold : FontWeights.Normal,
            TextDecorations = o.SelfResponse == ResponseStatus.Declined ? TextDecorations.Strikethrough : TextDecorations.None,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!spanning)
        {
            content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = LeafBrushes.FromHex(palette.Accent), VerticalAlignment = VerticalAlignment.Center });
        }

        content.Children.Add(text);

        var chip = new Border
        {
            Width           = Math.Max(b.ColumnSpan * colW - 6, 8),
            Height          = MonthGridView.ChipHeight - 2,
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6, 0, 6, 0),
            Background      = spanning ? LeafBrushes.FromHex(palette.Fill) : LeafBrushes.Transparent,
            BorderBrush     = LeafBrushes.FromHex(palette.Accent),
            BorderThickness = new Thickness(selected ? 2 : 0),
            Child           = content,
        };
        chip.Tapped += (_, e) =>
        {
            vm.Select(o);
            e.Handled = true;
        };
        AutomationProperties.SetAutomationId(chip, string.Create(CultureInfo.InvariantCulture, $"Chip_{o.EventId}_{first:yyyyMMdd}"));
        AutomationProperties.SetName(chip, o.Title);

        SetLeft(chip, b.FirstColumn * colW + 3);
        SetTop(chip, MonthGridView.DayNumberHeight + b.Lane * MonthGridView.ChipHeight);
        return chip;
    }

    UIElement More(DateOnly date, int count, double x, double y)
    {
        var vm   = _owner.ViewModel;
        var link = new HyperlinkButton { Content = string.Create(CultureInfo.InvariantCulture, $"+{count} more"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0), Height = MonthGridView.ChipHeight - 2 };
        AutomationProperties.SetAutomationId(link, $"More_{date:yyyy-MM-dd}");
        link.Click += (_, _) =>
        {
            var list = new StackPanel { Spacing = 2, MinWidth = 220 };
            list.Children.Add(new TextBlock { Text = TimeLabels.LongDate(date), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

            var flyout = new Flyout();
            foreach (var o in vm.Cache.ForDay(date).OrderBy(o => !o.IsAllDay).ThenBy(o => o.Start))
            {
                var item = new Button
                {
                    Content                    = o.IsAllDay ? o.Title : $"{TimeLabels.Compact(o.Start, vm.Zone, vm.Settings.Use24HourTime)}  {o.Title}",
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background                 = LeafBrushes.Transparent,
                    BorderThickness            = new Thickness(0),
                };
                item.Click += (_, _) =>
                {
                    flyout.Hide();
                    vm.Select(o);
                };
                list.Children.Add(item);
            }

            flyout.Content = list;
            flyout.ShowAt(link);
        };

        SetLeft(link, x + 2);
        SetTop(link, y);
        return link;
    }
}
```

- [ ] **Step 2: Month grid**

`src/LeafCalendar.App/Controls/MonthGridView.cs`:
```csharp
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Month view.
/// </summary>
/// <remarks>
/// Week rows scroll vertically in a virtualized <see cref="ItemsRepeater"/> of about 10 years of weeks.
/// Six rows fill the screen. When scrolling stops, the view snaps to a row edge; the month containing
/// the middle of the third row becomes the "focused" month (the title, with other months' days
/// dimmed). Pagers jump a month.
/// </remarks>
public sealed partial class MonthGridView : Grid, IDisposable
{
    /// <summary>Smallest week row height.</summary>
    public const double MinRowHeight = 96;

    /// <summary>Chip height (one lane).</summary>
    public const double ChipHeight = 20;

    /// <summary>Space for the day number at the top of a cell.</summary>
    public const double DayNumberHeight = 26;

    const int WeeksEachSide = 260;

    readonly CalendarViewModel _vm;
    readonly Grid _weekdays = new() { Height = 32 };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, ZoomMode = ZoomMode.Disabled };
    readonly ItemsRepeater _repeater = new() { Layout = new StackLayout { Orientation = Orientation.Vertical }, VerticalCacheLength = 2 };
    readonly HashSet<WeekRow> _rows = [];
    List<WeekItem> _weeks = [];
    bool _initialized;
    int _firstIndex;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public MonthGridView(CalendarViewModel vm)
    {
        _vm = vm;
        AutomationProperties.SetAutomationId(this, "MonthGrid");

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_weekdays);

        _repeater.ItemTemplate = new WeekRowFactory(this);
        _repeater.ElementPrepared += (_, e) => _rows.Add((WeekRow)e.Element);
        _repeater.ElementClearing += (_, e) => _rows.Remove((WeekRow)e.Element);
        _scroll.Content = _repeater;
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        _scroll.ViewChanged += OnViewChanged;
        _scroll.SizeChanged += (_, _) => Relayout();
        ActualThemeChanged  += (_, _) => RenderAll();

        _vm.OccurrencesChanged += OnOccurrencesChanged;
        _vm.LayoutChanged      += OnLayoutChanged;
        _vm.NavigateRequested  += OnNavigateRequested;

        FocusMonth = ViewNavigator.MonthStartOf(_vm.PeriodStart);
        BuildWeeks(_vm.PeriodStart);
    }

    /// <summary>The view model.</summary>
    public CalendarViewModel ViewModel => _vm;

    /// <summary>Week row height.</summary>
    public double RowHeight { get; private set; } = 120;

    /// <summary>Day column width.</summary>
    public double ColumnWidth => Math.Max(40, (_scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth) / ViewNavigator.VisibleColumnCount(Core.Settings.CalendarViewMode.Month, 0, _vm.Settings.ShowWeekends));

    /// <summary>The month the title shows.</summary>
    public DateOnly FocusMonth { get; private set; }

    /// <summary>True in the dark theme.</summary>
    public bool IsDark => ActualTheme == ElementTheme.Dark;

    /// <summary>The visible days of the week starting <paramref name="weekStart"/>.</summary>
    public IReadOnlyList<DateOnly> ColumnDates(DateOnly weekStart) =>
        [.. Enumerable.Range(0, 7).Select(weekStart.AddDays).Where(d => _vm.Settings.ShowWeekends || !ViewNavigator.IsWeekend(d))];

    /// <summary>Scrolls so the month containing <paramref name="date"/> fills the view.</summary>
    public void ScrollToDate(DateOnly date, bool animate)
    {
        var index = WeekIndexOf(ViewNavigator.MonthStartOf(date));
        if (index < 0)
        {
            BuildWeeks(date);
            index = WeekIndexOf(ViewNavigator.MonthStartOf(date));
        }

        _scroll.ChangeView(null, index * RowHeight, null, !animate);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _vm.OccurrencesChanged -= OnOccurrencesChanged;
        _vm.LayoutChanged      -= OnLayoutChanged;
        _vm.NavigateRequested  -= OnNavigateRequested;
    }

    void BuildWeeks(DateOnly around)
    {
        var start = ViewNavigator.WeekStartOf(around, _vm.Settings.WeekStart);
        _weeks = [.. Enumerable.Range(-WeeksEachSide, WeeksEachSide * 2 + 1).Select(i => new WeekItem(start.AddDays(i * 7)))];
        _repeater.ItemsSource = _weeks;
    }

    int WeekIndexOf(DateOnly date)
    {
        var weekStart = ViewNavigator.WeekStartOf(date, _vm.Settings.WeekStart);
        return _weeks.FindIndex(w => w.WeekStart == weekStart);
    }

    void Relayout()
    {
        var viewport = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight;
        if (viewport <= 0)
        {
            return;
        }

        RowHeight = Math.Max(MinRowHeight, viewport / 6);
        BuildWeekdayHeader();
        _repeater.InvalidateMeasure();
        RenderAll();

        if (!_initialized)
        {
            _initialized = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                ScrollToDate(_vm.PeriodStart, animate: false);
                Report();
            });
        }
    }

    void BuildWeekdayHeader()
    {
        _weekdays.Children.Clear();
        _weekdays.ColumnDefinitions.Clear();

        var dates = ColumnDates(ViewNavigator.WeekStartOf(_vm.Today, _vm.Settings.WeekStart));
        for (var c = 0; c < dates.Count; c++)
        {
            _weekdays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var name = new TextBlock { Text = TimeLabels.WeekdayShort(dates[c]), FontSize = 12, Margin = new Thickness(10, 8, 0, 0), Foreground = LeafBrushes.SecondaryText(IsDark) };
            SetColumn(name, c);
            _weekdays.Children.Add(name);
        }
    }

    void RenderAll()
    {
        foreach (var row in _rows)
        {
            row.Render();
        }
    }

    void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            return;
        }

        // Snap To A Row
        var index  = (int)Math.Round(_scroll.VerticalOffset / RowHeight);
        var target = index * RowHeight;
        if (Math.Abs(target - _scroll.VerticalOffset) > 0.5)
        {
            _scroll.ChangeView(null, target, null, false);
            return;
        }

        Report();
    }

    void Report()
    {
        _firstIndex = Math.Clamp((int)Math.Round(_scroll.VerticalOffset / RowHeight), 0, _weeks.Count - 1);
        var first   = _weeks[_firstIndex].WeekStart;
        var focus   = _weeks[Math.Min(_firstIndex + 2, _weeks.Count - 1)].WeekStart.AddDays(3);
        var month   = ViewNavigator.MonthStartOf(focus);

        if (month != FocusMonth)
        {
            FocusMonth = month;
            RenderAll();
        }

        _vm.OnViewScrolled(first, first.AddDays(42), focus);
    }

    void OnOccurrencesChanged(object? sender, EventArgs e) => RenderAll();

    void OnLayoutChanged(object? sender, EventArgs e)
    {
        BuildWeeks(FocusMonth);
        Relayout();
        ScrollToDate(FocusMonth, animate: false);
    }

    void OnNavigateRequested(object? sender, DateOnly date) => ScrollToDate(date, animate: true);

    /// <summary>A week in the list (a class, so WinRT can hold it).</summary>
    public sealed class WeekItem(DateOnly weekStart)
    {
        /// <summary>First day of the week.</summary>
        public DateOnly WeekStart { get; } = weekStart;
    }

    sealed partial class WeekRowFactory(MonthGridView owner) : IElementFactory
    {
        readonly Stack<WeekRow> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var row = _pool.Count > 0 ? _pool.Pop() : new WeekRow(owner);
            row.Bind(((WeekItem)args.Data).WeekStart);
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args) => _pool.Push((WeekRow)args.Element);
    }
}
```

In `CalendarPage.ApplyView()`, replace the four lines from `var view = new Controls.TimeGridView(ViewModel);` through `ViewHost.Children.Add(view);` with:
```csharp
        if (wantMonth)
        {
            var month = new Controls.MonthGridView(ViewModel);
            _view = month;
            ViewHost.Children.Add(month);
        }
        else
        {
            var grid = new Controls.TimeGridView(ViewModel);
            _view = grid;
            ViewHost.Children.Add(grid);
        }

        _viewIsMonth = wantMonth;
```

- [ ] **Step 3: Month UI tests**

`tests/LeafCalendar.UITests/MonthViewTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class MonthViewTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp LaunchInMonth()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewMonth").AsMenuItem().Invoke();
        leaf.WaitFor("MonthGrid");
        return leaf;
    }

    [Fact]
    public void MonthView_October2026_ShowsChipsAndTitle()
    {
        using var leaf = LaunchInMonth();

        Assert.Equal("October 2026", leaf.WaitFor("PeriodTitle").Name);
        Assert.NotNull(leaf.WaitFor("Chip_evt-single_20261001"));
        Assert.NotNull(leaf.WaitFor("Chip_evt-allday_20261012"));
        Assert.NotNull(leaf.WaitFor("Chip_evt-weekly_20261005"));
        Assert.False(leaf.Exists("Chip_evt-weekly_20261007"));
    }

    [Fact]
    public void MonthDay_Click_OpensDayView()
    {
        using var leaf = LaunchInMonth();

        leaf.WaitFor("MonthDay_2026-10-12").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("TimeGrid"));
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-12"));
    }

    [Fact]
    public void Next_InMonth_ShowsNovember()
    {
        using var leaf = LaunchInMonth();

        leaf.WaitFor("NextButton").AsButton().Invoke();

        Assert.True(FlaUI.Core.Tools.Retry.WhileFalse(() => leaf.WaitFor("PeriodTitle").Name == "November 2026", TimeSpan.FromSeconds(10)).Success);
    }
}
```

- [ ] **Step 4: Build, run, and look**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1` and `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*MonthViewTests" --filter-class "*TimeGridTests"` → PASS.

Check by hand with your real account:
- Six weeks fill the view, and days outside the focused month are dimmed.
- Busy days show "+N more", and clicking it lists every event.
- Scrolling snaps to weeks, and the title follows.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add virtualized month view with lane-packed chips and overflow"
```

---

### Task 15: Time-Zone Columns

**Files:**
- Create: `src/LeafCalendar.App/Views/TimeZonePanel.xaml` + `.xaml.cs`
- Modify: `src/LeafCalendar.App/Controls/TimeGridView.cs` (add the "+" button to `Corner`), `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (add `ZoneRow`)
- Test: `tests/LeafCalendar.UITests/TimeZoneTests.cs`

**Interfaces:**
- Consumes: `TimeZoneCatalog.Search`/`CityFor`, `LeafSettings.TimeZones`/`MaxTimeZones`, `CalendarViewModel.Update`, `TimeGridView.Corner`.
- Produces:
  - `sealed partial class ZoneRow(string id, string city, string detail) : ObservableObject` with `Id`, `City`, `Detail`, observable `Label`, `LabelBoxId`, `RemoveId`
  - `TimeZonePanel` with `Attach(CalendarViewModel)`
  - Automation IDs: `AddTimeZoneButton`, `TimeZoneSearch`, `TimeZoneList`, `ZoneLabelBox_{id}`, `ZoneRemove_{id}`, `TimeZoneLimit`

- [ ] **Step 1: Row type**

Add to the bottom of `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`:
```csharp
/// <summary>An extra time zone in the time-zone panel.</summary>
public sealed partial class ZoneRow(string id, string city, string detail) : ObservableObject
{
    /// <summary>IANA ID.</summary>
    public string Id { get; } = id;

    /// <summary>City name (placeholder when no label).</summary>
    public string City { get; } = city;

    /// <summary>"UTC+9 · Tokyo Standard Time".</summary>
    public string Detail { get; } = detail;

    /// <summary>Custom column label (blank means the city).</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = "";

    /// <summary>Automation ID of the label box.</summary>
    public string LabelBoxId => $"ZoneLabelBox_{Id}";

    /// <summary>Automation ID of the remove button.</summary>
    public string RemoveId => $"ZoneRemove_{Id}";
}
```

- [ ] **Step 2: Panel**

`src/LeafCalendar.App/Views/TimeZonePanel.xaml`:
```xml
<UserControl
    x:Class="LeafCalendar.App.Views.TimeZonePanel"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:LeafCalendar.App.ViewModels">

    <StackPanel Width="320" Spacing="8">
        <TextBlock Style="{StaticResource BodyStrongTextBlockStyle}" Text="Time zones" />

        <!-- Add -->
        <AutoSuggestBox
            x:Name="Search"
            PlaceholderText="Add a city or zone (Tokyo, NYC, UTC)"
            QueryIcon="Add"
            TextChanged="OnSearchTextChanged"
            SuggestionChosen="OnSuggestionChosen"
            QuerySubmitted="OnQuerySubmitted"
            AutomationProperties.AutomationId="TimeZoneSearch" />
        <TextBlock
            x:Name="LimitText"
            Text="You can show up to 4 extra time zones."
            Foreground="{ThemeResource TextFillColorSecondaryBrush}"
            Visibility="Collapsed"
            AutomationProperties.AutomationId="TimeZoneLimit" />

        <!-- Current Zones (drag to reorder) -->
        <ListView
            x:Name="ZoneList"
            SelectionMode="None"
            CanReorderItems="True"
            CanDragItems="True"
            AllowDrop="True"
            DragItemsCompleted="OnReordered"
            AutomationProperties.AutomationId="TimeZoneList">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="vm:ZoneRow">
                    <Grid ColumnSpacing="8" Padding="0,4">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBox
                            Header="{x:Bind Detail}"
                            Text="{x:Bind Label, Mode=TwoWay}"
                            PlaceholderText="{x:Bind City}"
                            MaxLength="24"
                            LostFocus="OnLabelLostFocus"
                            AutomationProperties.AutomationId="{x:Bind LabelBoxId}" />
                        <Button
                            Grid.Column="1"
                            VerticalAlignment="Bottom"
                            Click="OnRemoveClick"
                            Tag="{x:Bind}"
                            AutomationProperties.Name="Remove"
                            AutomationProperties.AutomationId="{x:Bind RemoveId}">
                            <SymbolIcon Symbol="Delete" />
                        </Button>
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </StackPanel>
</UserControl>
```

`src/LeafCalendar.App/Views/TimeZonePanel.xaml.cs`:
```csharp
using System.Collections.ObjectModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>Adds, renames, reorders, and removes extra time-zone columns (up to four). Changes save immediately.</summary>
public sealed partial class TimeZonePanel : UserControl
{
    readonly ObservableCollection<ZoneRow> _rows = [];
    CalendarViewModel? _vm;

    /// <summary>Creates the panel.</summary>
    public TimeZonePanel()
    {
        InitializeComponent();
        ZoneList.ItemsSource = _rows;
    }

    /// <summary>Loads the zones from <paramref name="vm"/>.</summary>
    public void Attach(CalendarViewModel vm)
    {
        _vm = vm;
        _rows.Clear();

        var now = vm.Now;
        foreach (var zone in vm.Settings.TimeZones)
        {
            var match = TimeZoneCatalog.Search(TimeZoneCatalog.CityFor(zone.Id), now).FirstOrDefault(c => c.Id == zone.Id);
            _rows.Add(new ZoneRow(zone.Id, TimeZoneCatalog.CityFor(zone.Id), match?.Detail ?? zone.Id) { Label = zone.Label ?? "" });
        }

        UpdateLimit();
    }

    void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_vm is not null && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = TimeZoneCatalog.Search(sender.Text, _vm.Now);
        }
    }

    void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is TimeZoneChoice choice)
        {
            Add(choice);
        }
    }

    void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_vm is null)
        {
            return;
        }

        var choice = args.ChosenSuggestion as TimeZoneChoice ?? TimeZoneCatalog.Search(args.QueryText, _vm.Now).FirstOrDefault();
        if (choice is not null)
        {
            Add(choice);
        }
    }

    void Add(TimeZoneChoice choice)
    {
        if (_rows.Count >= LeafSettings.MaxTimeZones || _rows.Any(r => r.Id == choice.Id))
        {
            return;
        }

        _rows.Add(new ZoneRow(choice.Id, choice.City, choice.Detail));
        Search.Text = "";
        Save();
    }

    void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZoneRow row })
        {
            _rows.Remove(row);
            Save();
        }
    }

    void OnLabelLostFocus(object sender, RoutedEventArgs e) => Save();

    void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Save();

    void Save()
    {
        _vm?.Update(s => s with { TimeZones = [.. _rows.Select(r => new ExtraTimeZone(r.Id, r.Label))] });
        UpdateLimit();
    }

    void UpdateLimit()
    {
        var full = _rows.Count >= LeafSettings.MaxTimeZones;
        Search.IsEnabled     = !full;
        LimitText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }
}
```

- [ ] **Step 3: "+" in the grid corner**

In `src/LeafCalendar.App/Controls/TimeGridView.cs`, in the constructor right after `Children.Add(Corner);`, add:
```csharp
        // Add Time Zone
        var addZone = new Button
        {
            Content             = new FontIcon { Glyph = "", FontSize = 10 },
            Padding             = new Thickness(4),
            Background          = LeafBrushes.Transparent,
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(4, 4, 0, 0),
        };
        AutomationProperties.SetAutomationId(addZone, "AddTimeZoneButton");
        AutomationProperties.SetName(addZone, "Time zones");
        ToolTipService.SetToolTip(addZone, "Time zones");
        addZone.Click += (_, _) =>
        {
            var panel = new Views.TimeZonePanel();
            panel.Attach(_vm);
            new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft }.ShowAt(addZone);
        };
        Corner.Children.Add(addZone);
```
The week-number text sits at the same corner. Move `_weekNumber`'s margin to `new Thickness(30, 6, 0, 0)` so the two don't overlap.

Adding a zone changes settings, which raises `LayoutChanged`. That runs `Relayout` → `RenderRealized` → `RenderCorner` and `_gutter.Render`, so the new column shows immediately.

- [ ] **Step 4: Time-zone UI test**

`tests/LeafCalendar.UITests/TimeZoneTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TimeZoneTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void AddTokyo_ShowsColumnAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();
            var search = leaf.WaitForAnywhere("TimeZoneSearch");
            search.Focus();
            Keyboard.Type("Tokyo");
            Keyboard.Press(VirtualKeyShort.RETURN);

            Assert.NotNull(leaf.WaitFor("ZoneLabel_Asia/Tokyo"));
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }

        using var relaunched = Launch();
        Assert.NotNull(relaunched.WaitFor("ZoneLabel_Asia/Tokyo"));
    }

    [Fact]
    public void RemoveZone_HidesColumn()
    {
        using var leaf = Launch();
        leaf.WaitFor("AddTimeZoneButton").AsButton().Invoke();
        leaf.WaitForAnywhere("TimeZoneSearch").Focus();
        Keyboard.Type("London");
        Keyboard.Press(VirtualKeyShort.RETURN);
        leaf.WaitFor("ZoneLabel_Europe/London");

        leaf.WaitForAnywhere("ZoneRemove_Europe/London").AsButton().Invoke();

        Assert.True(FlaUI.Core.Tools.Retry.WhileTrue(() => leaf.Exists("ZoneLabel_Europe/London"), TimeSpan.FromSeconds(5)).Success);
    }
}
```

- [ ] **Step 5: Build, run, commit**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1` and `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TimeZoneTests"` → PASS.

Check by hand:
- Add Mumbai: its column shows half-hour labels ("5:30 PM").
- Rename a zone: the label updates when the box loses focus.
- Drag to reorder: the gutter follows.

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add, rename, reorder, and remove time-zone columns"
```

---

### Task 16: Details Panel (Upcoming and Read-Only Event Details)

**Files:**
- Create: `src/LeafCalendar.App/Views/DetailsPanel.xaml` + `.xaml.cs`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml` + `.xaml.cs`, `src/LeafCalendar.App/MainWindow.xaml` + `.xaml.cs` (toolbar toggle)
- Test: `tests/LeafCalendar.UITests/DetailsPanelTests.cs`

**Interfaces:**
- Consumes: `CalendarViewModel` (`Upcoming`, `SelectedInfo`, `Select`, `ClearSelection`, `Settings.DetailsPanelOpen`, `Update`), `EventDetails`, `ResponseStatus`.
- Produces:
  - `CalendarPage.SetDetailsOpen(bool)`
  - `MainWindow` toggle button `DetailsToggleButton`
  - Automation IDs: `DetailsPanel`, `UpcomingHeader`, `UpcomingList`, `DetailsTitle`, `DetailsWhen`, `DetailsCalendar`, `DetailsLocation`, `DetailsConference`, `DetailsResponse`, `DetailsDescription`, `DetailsCloseButton`

- [ ] **Step 1: Panel**

`src/LeafCalendar.App/Views/DetailsPanel.xaml`:
```xml
<UserControl
    x:Class="LeafCalendar.App.Views.DetailsPanel"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:LeafCalendar.App.ViewModels"
    xmlns:local="using:LeafCalendar.App.Views"
    AutomationProperties.AutomationId="DetailsPanel">

    <Grid Padding="20,16,16,16">

        <!-- Upcoming -->
        <StackPanel x:Name="UpcomingView" Spacing="8">
            <TextBlock Style="{StaticResource BodyStrongTextBlockStyle}" Text="Upcoming" AutomationProperties.AutomationId="UpcomingHeader" />
            <TextBlock x:Name="UpcomingEmpty" Text="Nothing in the next 8 hours." Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" />
            <ItemsControl x:Name="UpcomingList" AutomationProperties.AutomationId="UpcomingList">
                <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="vm:UpcomingItem">
                        <Button
                            Padding="8,6"
                            HorizontalAlignment="Stretch"
                            HorizontalContentAlignment="Stretch"
                            Background="Transparent"
                            BorderThickness="0"
                            Click="OnUpcomingClick"
                            Tag="{x:Bind Occurrence}"
                            AutomationProperties.Name="{x:Bind Title}">
                            <Grid ColumnSpacing="10">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <Rectangle Width="3" RadiusX="1.5" RadiusY="1.5" Fill="{x:Bind local:DetailsPanel.Brush(Color)}" />
                                <StackPanel Grid.Column="1">
                                    <TextBlock Text="{x:Bind Title}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                                    <TextBlock FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}">
                                        <Run Text="{x:Bind When}" /><Run Text=" · " /><Run Text="{x:Bind Relative}" />
                                    </TextBlock>
                                </StackPanel>
                            </Grid>
                        </Button>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
        <!-- /Upcoming -->

        <!-- Event Details (plain text only: event content comes from anyone who can invite you) -->
        <ScrollViewer x:Name="DetailsView" Visibility="Collapsed">
            <StackPanel Spacing="14">
                <Grid ColumnSpacing="8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock x:Name="TitleText" Style="{StaticResource SubtitleTextBlockStyle}" TextWrapping="Wrap" IsTextSelectionEnabled="True" AutomationProperties.AutomationId="DetailsTitle" />
                    <Button
                        Grid.Column="1"
                        VerticalAlignment="Top"
                        Background="Transparent"
                        BorderThickness="0"
                        Click="OnCloseClick"
                        ToolTipService.ToolTip="Close (Esc)"
                        AutomationProperties.Name="Close details"
                        AutomationProperties.AutomationId="DetailsCloseButton">
                        <SymbolIcon Symbol="Cancel" />
                    </Button>
                </Grid>
                <TextBlock x:Name="WhenText" TextWrapping="Wrap" AutomationProperties.AutomationId="DetailsWhen" />
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <Ellipse x:Name="CalendarDot" Width="10" Height="10" VerticalAlignment="Center" />
                    <TextBlock x:Name="CalendarText" AutomationProperties.AutomationId="DetailsCalendar" />
                </StackPanel>
                <TextBlock x:Name="LocationText" TextWrapping="Wrap" IsTextSelectionEnabled="True" AutomationProperties.AutomationId="DetailsLocation" />
                <TextBlock x:Name="ConferenceText" TextWrapping="Wrap" IsTextSelectionEnabled="True" AutomationProperties.AutomationId="DetailsConference" />
                <TextBlock x:Name="ResponseText" AutomationProperties.AutomationId="DetailsResponse" />
                <TextBlock x:Name="GuestsText" />
                <TextBlock x:Name="DescriptionText" TextWrapping="Wrap" IsTextSelectionEnabled="True" AutomationProperties.AutomationId="DetailsDescription" />
            </StackPanel>
        </ScrollViewer>
        <!-- /Event Details -->
    </Grid>
</UserControl>
```

`src/LeafCalendar.App/Views/DetailsPanel.xaml.cs`:
```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// Right panel. With nothing selected it lists upcoming events (next 8 hours); with an event
/// selected it shows that event's details as plain text. Links aren't clickable until Milestone 3
/// adds the link allowlist and Join.
/// </summary>
public sealed partial class DetailsPanel : UserControl
{
    CalendarViewModel? _vm;

    /// <summary>Creates the panel.</summary>
    public DetailsPanel() => InitializeComponent();

    /// <summary>x:Bind helper: a brush for a hex color.</summary>
    public static SolidColorBrush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>Connects to the view model.</summary>
    public void Attach(CalendarViewModel vm)
    {
        _vm = vm;
        UpcomingList.ItemsSource = vm.Upcoming;
        vm.Upcoming.CollectionChanged += OnUpcomingChanged;
        vm.PropertyChanged            += OnViewModelPropertyChanged;
        UpdateUpcomingEmpty();
        Show(vm.SelectedInfo);
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Upcoming.CollectionChanged -= OnUpcomingChanged;
        _vm.PropertyChanged            -= OnViewModelPropertyChanged;
        _vm = null;
    }

    void OnUpcomingChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateUpcomingEmpty();

    void UpdateUpcomingEmpty() =>
        UpcomingEmpty.Visibility = _vm?.Upcoming.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.SelectedInfo))
        {
            Show(_vm?.SelectedInfo);
        }
    }

    void Show(SelectedEventInfo? info)
    {
        UpcomingView.Visibility = info is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsView.Visibility  = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is null)
        {
            return;
        }

        var d = info.Details;
        TitleText.Text       = d.Title;
        WhenText.Text        = info.When;
        CalendarText.Text    = info.CalendarName;
        CalendarDot.Fill     = LeafBrushes.FromHex(info.CalendarColor);
        LocationText.Text    = d.Location is { Length: > 0 } location ? $"Location: {location}" : "";
        ConferenceText.Text  = d.ConferenceUri is { } uri ? $"Video call: {uri.AbsoluteUri}" : "";
        ResponseText.Text    = d.SelfResponse switch
        {
            ResponseStatus.Declined    => "Your response: Not going",
            ResponseStatus.Tentative   => "Your response: Maybe",
            ResponseStatus.NeedsAction => "Your response: Not answered yet",
            _                          => d.GuestCount > 0 ? "Your response: Going" : "",
        };
        GuestsText.Text      = d.GuestCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"{d.GuestCount} guests") : "";
        DescriptionText.Text = d.Description;

        foreach (var block in new[] { LocationText, ConferenceText, ResponseText, GuestsText, DescriptionText })
        {
            block.Visibility = block.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void OnUpcomingClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is Button { Tag: CalendarOccurrence occurrence })
        {
            _vm.Select(occurrence);
        }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => _vm?.ClearSelection();
}
```

- [ ] **Step 2: Put it on the page, plus Esc and the toolbar toggle**

In `src/LeafCalendar.App/Views/CalendarPage.xaml`:
1. Add `<Page.KeyboardAccelerators><KeyboardAccelerator Key="Escape" Invoked="OnEscapeInvoked" /></Page.KeyboardAccelerators>` inside `<Page>`.
2. Add after the view `Border`:
```xml
        <!-- Details -->
        <Border x:Name="DetailsHost" Grid.Column="2" Background="{ThemeResource LayerFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1,1,0,0">
            <local:DetailsPanel x:Name="Details" />
        </Border>
```

In `CalendarPage.xaml.cs`:
1. In `OnNavigatedTo`, add:
```csharp
        Details.Attach(ViewModel);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SetDetailsOpen(ViewModel.Settings.DetailsPanelOpen);
```
2. In `OnNavigatedFrom`, add `Details.Detach(); ViewModel.PropertyChanged -= OnViewModelPropertyChanged;`.
3. Add:
```csharp
    /// <summary>Shows or hides the details panel and remembers the choice.</summary>
    public void SetDetailsOpen(bool open)
    {
        DetailsColumn.Width    = new GridLength(open ? 320 : 0);
        DetailsHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (ViewModel.Settings.DetailsPanelOpen != open)
        {
            ViewModel.Update(s => s with { DetailsPanelOpen = open });
        }
    }

    // Selecting an event opens the panel so the details are visible
    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.SelectedInfo) && ViewModel.SelectedInfo is not null && !ViewModel.Settings.DetailsPanelOpen)
        {
            SetDetailsOpen(true);
        }
    }

    void OnEscapeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ClearSelection();
        args.Handled = true;
    }
```

In `MainWindow.xaml`, add after the `ViewModeButton` in `CalendarToolbar`:
```xml
                    <ToggleButton x:Name="DetailsToggle" Height="32" Click="OnDetailsToggleClick" ToolTipService.ToolTip="Details panel" AutomationProperties.Name="Details panel" AutomationProperties.AutomationId="DetailsToggleButton">
                        <SymbolIcon Symbol="DockRight" />
                    </ToggleButton>
```
In `MainWindow.xaml.cs`, add:
```csharp
    void OnDetailsToggleClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is CalendarPage page)
        {
            page.SetDetailsOpen(DetailsToggle.IsChecked == true);
        }
    }
```
and in `SyncMenu()` add `DetailsToggle.IsChecked = s.DetailsPanelOpen;`. Also subscribe `_calendar.LayoutChanged += (_, _) => SyncMenu();` in `ShowCalendar()`, where the view model is created, so the toggle and menu stay in step with settings changed elsewhere.

- [ ] **Step 3: Details UI tests**

`tests/LeafCalendar.UITests/DetailsPanelTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class DetailsPanelTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void ClickEvent_ShowsDetails_EscapeReturnsToUpcoming()
    {
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-single_202610011300").Click();

        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);
        Assert.Contains("Thursday, October 1", leaf.WaitFor("DetailsWhen").Name, StringComparison.Ordinal);

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
    }

    [Fact]
    public void Upcoming_OnStartDateMorning_ListsDentist()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitForName("Dentist appointment"));
        Assert.NotNull(leaf.WaitFor("UpcomingList"));
    }

    [Fact]
    public void DetailsToggle_HidesAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
            Assert.True(FlaUI.Core.Tools.Retry.WhileTrue(() => leaf.Exists("UpcomingHeader"), TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        relaunched.WaitFor("CalendarRoot");
        Assert.False(relaunched.Exists("UpcomingHeader"));
    }
}
```

In test mode "now" is 8:00 local on Oct 1. The Dentist event (9:00–10:00 New York) is within 8 hours in any US zone, so it appears in Upcoming.

- [ ] **Step 4: Build, run, commit**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1` and `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*DetailsPanelTests"` → PASS.

Check by hand: open one of your real events that has a description with links. The text shows, and nothing in it is clickable.

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add details panel with upcoming list and read-only event details"
```

---

### Task 17: Keyboard Shortcuts and Go To Date

**Files:**
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml` + `.xaml.cs`, `src/LeafCalendar.App/MainWindow.xaml.cs`
- Test: `tests/LeafCalendar.UITests/KeyboardTests.cs`

**Interfaces:**
- Consumes: `ShortcutMap.Resolve`, `CalendarCommand`, the `CalendarViewModel` commands, and `MainWindow.ApplyTheme`.
- Produces:
  - `CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts, Action ToggleTheme)` (new third member)
  - Automation IDs: `GoToDateCalendar`
  - Every spec Section 8.7 navigation, app, and display shortcut that Milestone 2 can serve:

    | Keys | Action |
    |---|---|
    | T | Today |
    | ← → J K | Previous / next |
    | D / 1 | Day view |
    | W / 0 | Week view |
    | M | Month view |
    | 2–9 | That many days |
    | . | Go to date |
    | N / B / Shift+N | Next / previous event |
    | Ctrl+Shift+E | Weekends |
    | Ctrl+Shift+D | Declined events |
    | Ctrl+Shift+L | Theme |
    | Ctrl+= / Ctrl+- / Ctrl+0 | Grid height (Ruling: "zoom" is grid density until interface scaling lands; see Task 18) |
    | Esc | Clear selection (Task 16) |

- [ ] **Step 1: Page key handling and go-to-date**

In `src/LeafCalendar.App/Views/CalendarPage.xaml`, add `PreviewKeyDown="OnPreviewKeyDown"` to the `<Page>` element.

In `CalendarPage.xaml.cs`, change the record:
```csharp
public sealed record CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts, Action ToggleTheme);
```
and add (with `using LeafCalendar.Core.Views;`, `using Microsoft.UI.Input;`, `using Windows.System;`, `using Windows.UI.Core;`, `using Microsoft.UI.Xaml.Input;`):
```csharp
    // Calendar shortcuts, ignored while typing in a text field
    void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox or NumberBox or RichEditBox)
        {
            return;
        }

        var result = ShortcutMap.Resolve(e.Key.ToString(), IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu));
        if (result.Command == CalendarCommand.None)
        {
            return;
        }

        e.Handled = true;
        Execute(result);
    }

    void Execute(ShortcutResult result)
    {
        var vm = ViewModel;
        switch (result.Command)
        {
            case CalendarCommand.Today:          vm.GoToToday(); break;
            case CalendarCommand.Previous:       vm.Previous(); break;
            case CalendarCommand.Next:           vm.Next(); break;
            case CalendarCommand.DayView:        vm.SetMode(Core.Settings.CalendarViewMode.Day); break;
            case CalendarCommand.WeekView:       vm.SetMode(Core.Settings.CalendarViewMode.Week); break;
            case CalendarCommand.MonthView:      vm.SetMode(Core.Settings.CalendarViewMode.Month); break;
            case CalendarCommand.Days:           vm.SetMode(Core.Settings.CalendarViewMode.Days, result.Days); break;
            case CalendarCommand.GoToDate:       ShowGoToDate(); break;
            case CalendarCommand.ToggleWeekends: vm.ToggleWeekends(); break;
            case CalendarCommand.ToggleDeclined: vm.ToggleDeclined(); break;
            case CalendarCommand.ZoomIn:         vm.ZoomBy(8); break;
            case CalendarCommand.ZoomOut:        vm.ZoomBy(-8); break;
            case CalendarCommand.ZoomReset:      vm.ZoomReset(); break;
            case CalendarCommand.ToggleTheme:    _args.ToggleTheme(); break;
            case CalendarCommand.NextEvent:      vm.SelectAdjacent(1); break;
            case CalendarCommand.PreviousEvent:  vm.SelectAdjacent(-1); break;
        }
    }

    void ShowGoToDate()
    {
        var picker = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single, IsTodayHighlighted = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "GoToDateCalendar");
        picker.SetDisplayDate(new DateTimeOffset(ViewModel.PeriodStart.ToDateTime(TimeOnly.MinValue)));

        var flyout = new Flyout { Content = picker, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        picker.SelectedDatesChanged += (s, a) =>
        {
            if (a.AddedDates.Count > 0)
            {
                flyout.Hide();
                ViewModel.NavigateTo(DateOnly.FromDateTime(a.AddedDates[0].Date));
            }
        };
        flyout.ShowAt(ViewHost);
    }

    static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
```

In `MainWindow.xaml.cs`:
1. Change the page navigation in `ShowCalendar()` to `new CalendarPageArgs(_calendar, ShowAccounts, ToggleTheme)`.
2. Add:
```csharp
    // Ctrl+Shift+L: flip between light and dark based on what's showing now
    void ToggleTheme()
    {
        if (_calendar is null)
        {
            return;
        }

        var next = RootGrid.ActualTheme == ElementTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _calendar.Update(s => s with { Theme = next });
        ApplyTheme(next);
        SyncMenu();
    }
```

A focused `ScrollViewer` would otherwise use the arrow keys to scroll. `PreviewKeyDown` tunnels, so the page sees them first and paging wins, as in Notion Calendar. Mouse, trackpad, and scrollbars still scroll.

- [ ] **Step 2: Keyboard UI tests**

`tests/LeafCalendar.UITests/KeyboardTests.cs`:
```csharp
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class KeyboardTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        return leaf;
    }

    [Fact]
    public void M_ThenW_SwitchesMonthAndWeek()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.KEY_M);
        Assert.NotNull(leaf.WaitFor("MonthGrid"));

        leaf.Press(VirtualKeyShort.KEY_W);
        Assert.NotNull(leaf.WaitFor("TimeGrid"));
    }

    [Fact]
    public void CtrlShiftE_HidesWeekendColumns()
    {
        using var leaf = Launch();
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-03"));

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_E);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("DayHeader_2026-10-03"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-02"));
    }

    [Fact]
    public void RightArrowThenT_PagesAndReturnsToToday()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.RIGHT);
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));

        leaf.Press(VirtualKeyShort.KEY_T);
        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
    }

    [Fact]
    public void Period_OpensGoToDate()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.OEM_PERIOD);

        Assert.NotNull(leaf.WaitForAnywhere("GoToDateCalendar"));
    }

    [Fact]
    public void N_SelectsNextEvent()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.KEY_N);

        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);
    }
}
```

`N` picks the first event after "now" (8:00 local in test mode), which is Dentist at 9:00 New York time. On a machine set to a zone where 8:00 local is after 9:00 New York (any zone east of New York), the first match could differ. The UI tests assume a US time zone, as the owner's PC uses. If CI or another PC isn't in a US zone, set the assertion to "any title appears" instead.

- [ ] **Step 3: Build, run, commit**

Run: `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Then `pwsh tools/dev-register.ps1` and `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` → all pass (the memory test skips on Debug).

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): add calendar keyboard shortcuts and go to date"
```

---

### Task 18: Milestone Close (Memory, AOT, Security, Spec Updates)

**Files:**
- Modify: `tests/LeafCalendar.UITests/memory-budget.json` (only if the re-measure requires it), `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`
- Modify: whatever the security review finds (each fix gets a test)

**Interfaces:**
- Consumes: everything above.
- Produces: a verified Milestone 2, with the deferred spec items recorded in the spec.

- [ ] **Step 1: Full verification**

Run each command in order:
1. `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.
2. `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → all PASS.
3. `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` → all PASS, 1 skipped (memory).
4. `pwsh tools/publish-aot.ps1 -Register` → **0 IL warnings**. The app runs from the AOT layout, and the calendar renders with your real account after you sign in there.
5. `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*MemoryTests" --output Detailed` 3 times.
   - If it still passes the Task 4 budget, leave the budget alone.
   - If it fails, don't raise the budget blindly. Find what grew first: views not disposed on window close, or the cache holding more than ±3 months.
   - Raise the budget only if the growth is expected, and record why in the spec's Section 3.4.
6. `pwsh tools/dev-register.ps1` to put Debug back.

- [ ] **Step 2: Security review**

Invoke the `security-review` skill on the Milestone 2 diff (`git diff <milestone-2 base>..HEAD`). Then check each item and record the result in the task report:
1. **No clickable content from events.** Run `git grep -n "Hyperlink\|NavigateUri\|Launcher" -- src/LeafCalendar.App`. The only matches should be the booking-pages link (a fixed Google URL) and `LeafServices.OpenSignInPageAsync`.
2. **Descriptions are plain text.** `EventDetailsParser.HtmlToText` is the only path to `DetailsPanel.DescriptionText`, and `TextBlock` never interprets markup.
3. **`--fake-google` stays loopback-only.** `LaunchOptionsTests` covers it, and spec 4.8 has the row.
4. **No event content in logs.** Run the app with your real account, browse a few weeks, then check with `Select-String` on `LocalState\profiles\default\Logs\leaf.log`, searching for a few of your real event titles and your email. Expected: no output.
5. **Settings JSON holds no secrets.** Open the `settings` row: only view preferences and time-zone IDs and labels.

- [ ] **Step 3: Record deferrals in the spec**

In `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` Section 12, add to **Milestone 5**:
```markdown
   - Deferred from Milestone 2:
     - Interface scale (whole-app zoom; Ctrl+= / Ctrl+- currently change grid density)
     - Working-hours shading (Google's Calendar API doesn't expose working hours)
     - Calendar rename (Google summary override; needs the Milestone 3 write path)
     - "Show upcoming events for this calendar"
     - Time travel (Z)
     - Title-bar search icon (opens the command menu)
```

In Section 6.4, next to "Working hours shaded from Google's working hours where available", add: "(not exposed by the Calendar API; see Milestone 5)".

- [ ] **Step 4: Commit**

```bash
git add -A docs tests src
git commit -m "chore: close Milestone 2 with verification, security review, and recorded deferrals"
```

---

## Self-Review Notes

**Spec coverage (Milestone 2 in Section 12, and Section 6):**

| Spec item | Where |
|---|---|
| Title bar | Task 11 |
| Sidebar | Tasks 11–12 |
| Views, scrolling, and pagers | Tasks 13–14 |
| Time-zone columns | Task 15 |
| In-memory event window | Task 7 |
| Fake-Google UI tests | Tasks 3–4 |
| 15-minute calendar-list cadence | Task 2 |
| Right panel (6.5) | Task 16 |
| Settings storage (Section 9 subset) | Task 1, with the view menu in Task 11 |
| Duplicate events across accounts (7.4) | Task 6 |
| Section 8.7 navigation shortcuts | Task 17 |

**Deliberately deferred and recorded in the spec (Task 18):**
- Interface scale
- Working-hours shading
- Calendar rename
- Per-calendar upcoming list
- Time travel
- Title-bar search icon
- Event editing, drag, and create (Milestone 3)
- Clickable links and Join (Milestone 3)
- Command menu (Milestone 5)
