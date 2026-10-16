# Sunsama Projections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show Sunsama's projected and timeboxed task times on the Leaf Calendar calendar, read-only, with each task's channel and a striped tracked-time overlay, behind two sidebar switches and a Sunsama sign-in expander in Settings › Accounts.

**Architecture:** Core gets a `Sunsama` folder: pure text parsing and matching, a tracked-fill calculator, a small MCP client (JSON-RPC over HTTP), dynamic client registration, and a `SunsamaService` that signs in (reusing `ProviderSignIn`/`ProviderTokens` with PKCE added), fetches the days on screen after each Google sync pass and saves rows to a new `sunsama_block` table (migration 14). The App turns saved rows into read-only `CalendarOccurrence`s in the existing event cache, so the grid, layout and `EventBlock` draw them; every action that could change an event skips them. Settings gets an expander, the sidebar a Sunsama group, the details panel a task section, and the user guide a new page with screenshots.

**Tech Stack:** C# 14 / .NET 10, WinUI 3 (Windows App SDK), Native AOT, `Microsoft.Data.Sqlite`, `System.Text.Json` (`JsonDocument` and `Utf8JsonWriter`, no reflection), xunit v3, FlaUI, Jekyll (Just the Docs) for `site/`.

**Spec:** `docs/superpowers/specs/2026-10-16-sunsama-projections-design.md` (read it fully before Task 1).

## Global Constraints

- Run every command from `D:\leaf-calendar` (the folder with `global.json`).
- Build: `dotnet build LeafCalendar.slnx -c Debug` must end with 0 warnings (warnings are errors).
- Logic tests: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug` (one class: `--filter-class "*ClassName"`). Run the full logic suite after every task.
- C# style: Microsoft conventions, file-scoped namespaces matching folders (`LeafCalendar.Core.Sunsama` for `src/LeafCalendar.Core/Sunsama/`), `_camelCase` fields, access modifiers written, no column alignment, doc comments on every public member, CRLF line endings (edit existing files in place; new files CRLF).
- Native AOT: `x:Bind` only; `ItemsSource` gets a concrete `List<T>`; JSON through `JsonDocument`/`Utf8JsonWriter` or a source-generated context; no reflection.
- Time: injected `TimeProvider`, instants stored in UTC, invariant culture for storage and the API.
- Security: never log task titles, channels, notes, emails, tokens, codes or client IDs. Log event names, counts, HTTP status codes, exception type names. Everything from Sunsama is untrusted and shown as plain text.
- Secrets only through `ITokenStore` provider secrets under provider ID `sunsama` (keys `client_id`, `refresh_token`, `email`).
- Read-only: nothing is ever written to Sunsama or Google. Scopes `read offline_access` only.
- The app is always "Leaf Calendar" in full. US English, sentence case. Every user-visible string must be in spec section 9; quote any string you add that isn't there in your report.
- Design: stock WinUI controls and theme brushes, spacing 2/4/8/12/16/24/32, `ControlCornerRadius`/`OverlayCornerRadius`, HighContrast entries for overridden brushes, tooltips and `AutomationProperties.Name` on icon-only controls, an `AutomationId` on everything a UI test touches, no fixed text heights. `XamlLintTests` must pass.
- **Execution (owner, 2026-10-16): run this plan natively** (superpowers:executing-plans, in the main session), not subagent-driven.
- Branch `feat/sunsama-projections`, pull request already open. **Owner: commit as you go and push into the PR.** Each task's checkpoint ends with one commit (`feat(sunsama): <user-visible effect>`, body explains why) and a push to the branch. Never force-push, never comment on the PR, never change its status.
- UI tests drive the installed package: `pwsh tools/sign-install.ps1` first, never two UI runs at once, run only the classes listed in Task 13 (owner: affected areas only, no full suite). If a whole block fails at once, re-run before diagnosing. If a test is blocked by the pause file `%LOCALAPPDATA%\LeafCalendar.UITests\paused`, delete it and run the tests that need running.

## Review Focus

1. **A projected time on a DST day** ("2026-11-01 1:30 AM" in America/Chicago is ambiguous; "2026-03-08 2:30 AM" doesn't exist) — the block lands at one sensible instant (first occurrence; skipped times move forward an hour) and never throws. Pinned in Task 1 (`LocalTime_AmbiguousFallBack_UsesFirst`, `LocalTime_SkippedSpringForward_MovesForward`).
2. **Sunsama answers with missing or odd fields** (`projectedTimeEntries` absent, `timeEstimate` "", `actualTimeSpent` null, a channel of `null`, an unknown `timeboxEventIds` entry) — that task shows what it can and nothing throws. Pinned in Task 1 (`ParseDay_MissingAndOddFields_KeepsWhatItCan`).
3. **A Sunsama block is dragged, Alt+dragged, copied (Ctrl+C), duplicated, deleted or right-clicked** — nothing happens to Google (no outbox entry, no editor), and the right-click menu offers only Open in Sunsama. Pinned in Task 9 (`IsSunsama` guards) and Task 13 (`SunsamaGridTests.Drag_SunsamaBlock_DoesNothing`).
4. **The refresh token is revoked while signed in** — the expander shows "Not signed in" with the reason line, saved blocks stay, Google sync carries on. Pinned in Task 6 (`SyncAsync_RefusedRefresh_MarksSignedOutKeepsRows`).
5. **A task ID that isn't 24 hex characters** (Sunsama changes format, or the fake sends one) — no Open in Sunsama link is built. Pinned in Task 1 (`IsTaskId_RejectsOtherShapes`) and Task 7 (`TaskLink_BadId_ReturnsNull`).

---

### Task 0: Spec Touch-Ups From Planning

Planning settled details the spec must state. Fix them first so the spec stays the authority.

**Files:**
- Modify: `docs/superpowers/specs/2026-10-16-sunsama-projections-design.md`

- [ ] **Step 1: Line endings.** The spec was written with LF. Convert it to CRLF (the repo standard) with `unix2dos docs/superpowers/specs/2026-10-16-sunsama-projections-design.md` (Git Bash ships it), and the same for this plan file.
- [ ] **Step 2: Endpoints (section 3).** Add after the registration bullet: "Authorize `https://api.sunsama.com/oauth/authorize`, token `https://api.sunsama.com/oauth/token`. Leaf Calendar sends `resource=https://api.sunsama.com/mcp` on the authorize and token requests (MCP authorization). Redirect `http://127.0.0.1:53818/sunsama/callback` (Cal.com keeps 53817)."
- [ ] **Step 3: Matching (section 4.4).** Replace the two numbered rules with: "1. A task with a timebox event, or imported from a calendar event (`importedFrom.identifier.sunsamaId` is that event's `eventId` in the day's events), whose event is on a Google calendar (its `calendarId` is in `sunsama://me` with provider `google-calendar`), matches the cached Leaf Calendar occurrence on the same calendar ID with the same start instant and the same title; if none has the same title, the one with the same start on that calendar. 2. A Google-side event with no cached match (its calendar hidden or not synced yet) draws nothing."
- [ ] **Step 4: Fill per day (section 6.3.1).** After "A task's tracked minutes fill its blocks in time order" add "(the task's blocks on that day; each day is filled from the task's whole tracked total, since Sunsama gives no per-day split)".
- [ ] **Step 5: Color (section 6.2 and 6.3).** Replace "Projected tasks has a color choice if calendar rows have one; Tracked time has none (it draws no blocks)." with "Sunsama blocks use one fixed color, Tangerine (`#F4511E`). Calendar colors are chosen in Settings › Calendars, which lists Google calendars only, so a color choice for Sunsama is left for later. The Sunsama rows have no right-click menu and can't be reordered." In 6.3 replace "in the **Projected tasks** item's color" with "in the Sunsama color".
- [ ] **Step 6: Open in Sunsama (section 6.5).** Replace the `LinkSafety` sentence with: "The link is built only by `SunsamaLinks.TaskLink` and opened by its own launcher (`LeafServices.LaunchSunsamaAsync`, like `LaunchReturnLinkAsync`), so `LinkSafety`'s allow-list is unchanged."
- [ ] **Step 7: Wording (section 9).** Replace the row "Browser page after sign-in" with `| Browser page after sign-in | the existing page: "You're signed in" / "You can close this tab and go back to Leaf Calendar." |` and add rows:
  - `| Sign-in refused | "Sunsama didn't finish signing in. Try again." |`
  - `| Port busy | "Port 53818 is busy, so Leaf Calendar can't finish signing in. Close the app using it and try again." (the Cal.com wording with Sunsama's port) |`
  - `| Can't reach Sunsama during sign-in | "Couldn't reach Sunsama. Check your connection and try again." (the Cal.com wording) |`
  - `| Sign-in timed out | "Sign-in timed out. Try again." (existing) |`
  - `| Block tooltip and spoken name | "{title} · {time} · {channel}" plus " · {duration} tracked" when Tracked time is on |`
  - `| Right-click menu on a Sunsama block | "Open in Sunsama" |`
- [ ] **Step 8: Checkpoint.** Re-read the spec once end to end; no contradictions with this plan.

---

### Task 1: Sunsama Text and Day Parsing (Core, Pure)

**Files:**
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaText.cs`
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaModels.cs`
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaParser.cs`
- Test: `tests/LeafCalendar.Tests/SunsamaTextTests.cs`, `tests/LeafCalendar.Tests/SunsamaParserTests.cs`

**Interfaces:**
- Produces:
  - `SunsamaText.Duration(string? text) -> TimeSpan?`
  - `SunsamaText.LocalTime(string? text, TimeZoneInfo zone) -> DateTimeOffset?`
  - `SunsamaText.IsTaskId(string? id) -> bool`
  - `record SunsamaProfile(string Email, TimeZoneInfo Zone, IReadOnlySet<string> GoogleCalendarIds)`
  - `record SunsamaEventRef(string EventId, string CalendarId, string Title, DateTimeOffset Start, DateTimeOffset End, bool OnGoogle)`
  - `record SunsamaTask(string Id, string Title, string Channel, int? EstimateMinutes, int? TrackedMinutes, bool Completed, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Projected, IReadOnlyList<SunsamaEventRef> Events)`
  - `SunsamaParser.Profile(JsonElement me) -> SunsamaProfile?`
  - `SunsamaParser.Day(JsonElement tasks, JsonElement events, SunsamaProfile profile) -> IReadOnlyList<SunsamaTask>`

- [ ] **Step 1: Write the failing text tests**

```csharp
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaTextTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Theory]
    [InlineData("1 hours and 24 minutes", 84)]
    [InlineData("30 minutes", 30)]
    [InlineData("2 hours", 120)]
    [InlineData("1 hour", 60)]
    [InlineData("1 minute", 1)]
    [InlineData("0 minutes", 0)]
    public void Duration_SunsamaText_ReadsMinutes(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SunsamaText.Duration(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5 minutes")]
    [InlineData("1 hours and")]
    public void Duration_Unreadable_ReturnsNull(string? text) => Assert.Null(SunsamaText.Duration(text));

    [Fact]
    public void LocalTime_OrdinaryDay_ConvertsWithTheZone() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 15, 50, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-10-16 10:50 AM", Chicago));

    [Fact]
    public void LocalTime_Afternoon_ReadsPm() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 18, 50, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-10-16 1:50 PM", Chicago));

    [Fact]
    public void LocalTime_AmbiguousFallBack_UsesFirst() =>
        // 1:30 AM happens twice on 2026-11-01; the first is CDT (UTC-5)
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-11-01 1:30 AM", Chicago));

    [Fact]
    public void LocalTime_SkippedSpringForward_MovesForward() =>
        // 2:30 AM doesn't exist on 2026-03-08; it becomes 3:30 AM CDT
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), SunsamaText.LocalTime("2026-03-08 2:30 AM", Chicago));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10:50 AM")]
    [InlineData("2026-13-01 10:50 AM")]
    public void LocalTime_Unreadable_ReturnsNull(string? text) => Assert.Null(SunsamaText.LocalTime(text, Chicago));

    [Fact]
    public void IsTaskId_SunsamaId_IsTrue() => Assert.True(SunsamaText.IsTaskId("6ac7a44534029300011d076e"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("6ac7a44534029300011d076")]
    [InlineData("6ac7a44534029300011d076eX")]
    [InlineData("6AC7A44534029300011D076E")]
    [InlineData("../../etc/passwd/aaaaaaaa")]
    public void IsTaskId_RejectsOtherShapes(string? id) => Assert.False(SunsamaText.IsTaskId(id));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*SunsamaTextTests"`
Expected: build error, `SunsamaText` does not exist.

- [ ] **Step 3: Implement `SunsamaText`**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Sunsama;

/// <summary>
/// Reads the plain-text values Sunsama's MCP server sends: durations ("1 hours and 24 minutes"), local times without a
/// zone ("2026-10-16 10:50 AM") and task IDs.
/// </summary>
/// <remarks>Everything here comes from Sunsama and is untrusted: anything unreadable gives null, never an exception.</remarks>
public static partial class SunsamaText
{
    /// <summary>Reads a duration such as "1 hours and 24 minutes", "30 minutes" or "1 hour"; null when unreadable.</summary>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || DurationPattern().Match(text.Trim()) is not { Success: true } m)
        {
            return null;
        }

        var hours = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromMinutes((hours * 60) + minutes);
    }

    /// <summary>
    /// Reads a local time such as "2026-10-16 10:50 AM" in <paramref name="zone"/>. A time that happens twice (fall back)
    /// is the first one; a time that doesn't exist (spring forward) moves forward by the gap. Null when unreadable.
    /// </summary>
    public static DateTimeOffset? LocalTime(string? text, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!DateTime.TryParseExact(text?.Trim(), "yyyy-MM-dd h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        // Skipped Hour: move forward by the gap (the zone's DST delta)
        if (zone.IsInvalidTime(local))
        {
            var rule = zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= local && local <= r.DateEnd);
            local = local.Add(rule?.DaylightDelta ?? TimeSpan.FromHours(1));
        }

        // Repeated Hour: the first one has the larger (daylight) offset
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>True for Sunsama's task ID shape: 24 lowercase hex characters.</summary>
    public static bool IsTaskId(string? id) => id is { Length: 24 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    [GeneratedRegex(@"^(?:(?<h>\d{1,4}) hours?)?(?: and )?(?:(?<m>\d{1,4}) minutes?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
```

Note: the pattern also matches "" and " and "; the `IsNullOrWhiteSpace` guard covers "", and add `if (!m.Groups["h"].Success && !m.Groups["m"].Success) return null;` plus reject a match that has " and " without both groups (`"1 hours and"`) by checking `text.Contains(" and ", StringComparison.Ordinal) && !(m.Groups["h"].Success && m.Groups["m"].Success)`. Write those two guards in.

- [ ] **Step 4: Run the text tests; expect PASS.**

- [ ] **Step 5: Write the failing parser tests.** Use the real shapes captured on 2026-10-16 (trimmed, made-up IDs where not needed):

```csharp
using System.Text.Json;
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaParserTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    private const string Me = """
        {"user":{"email":"owner@example.com","timezone":"America/Chicago","calendars":[
          {"id":"sunsama-2c88","provider":"sunsama-calendar"},
          {"id":"work@example.com","provider":"google-calendar"}]}}
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static SunsamaProfile Profile() => SunsamaParser.Profile(Json(Me))!;

    [Fact]
    public void Profile_ReadsEmailZoneAndGoogleCalendars()
    {
        var p = Profile();

        Assert.Equal("owner@example.com", p.Email);
        Assert.Equal("America/Chicago", p.Zone.Id);
        Assert.Equal(["work@example.com"], p.GoogleCalendarIds);
    }

    [Fact]
    public void Profile_UnknownZone_ReturnsNull() =>
        Assert.Null(SunsamaParser.Profile(Json("""{"user":{"email":"a@b.c","timezone":"Mars/Olympus","calendars":[]}}""")));

    [Fact]
    public void Day_ProjectedTask_ReadsEntriesEstimateTrackedAndChannel()
    {
        var tasks = Json("""
            {"tasks":[{"_id":"6ac7a44534029300011d076e","title":"Home Page Redesign","channel":"Illuminate Digital",
              "timeEstimate":"2 hours","completed":false,"timeboxEventIds":[],
              "actualTimeSpent":{"total":"1 hours and 24 minutes"},
              "projectedTimeEntries":[
                {"startDate":"2026-10-16 10:50 AM","endDate":"2026-10-16 11:30 AM"},
                {"startDate":"2026-10-16 12:30 PM","endDate":"2026-10-16 1:50 PM"}]}]}
            """);

        var task = Assert.Single(SunsamaParser.Day(tasks, Json("""{"events":[]}"""), Profile()));

        Assert.Equal("Home Page Redesign", task.Title);
        Assert.Equal("Illuminate Digital", task.Channel);
        Assert.Equal(120, task.EstimateMinutes);
        Assert.Equal(84, task.TrackedMinutes);
        Assert.Equal(
            [
                (new DateTimeOffset(2026, 10, 16, 15, 50, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 16, 30, 0, TimeSpan.Zero)),
                (new DateTimeOffset(2026, 10, 16, 17, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 18, 50, 0, TimeSpan.Zero)),
            ],
            task.Projected);
        Assert.Empty(task.Events);
    }

    [Fact]
    public void Day_TimeboxAndImported_ResolveTheirEvents()
    {
        var tasks = Json("""
            {"tasks":[
              {"_id":"aaaaaaaaaaaaaaaaaaaaaaa1","title":"Write copy","channel":"Work","timeboxEventIds":["ev-box"]},
              {"_id":"aaaaaaaaaaaaaaaaaaaaaaa2","title":"Lunch Break","channel":"Personal","timeboxEventIds":[],
               "importedFrom":{"service":"googleCalendar","identifier":{"sunsamaId":"ev-lunch"}}}]}
            """);
        var events = Json("""
            {"events":[
              {"eventId":"ev-box","title":"Write copy","calendarId":"sunsama-2c88",
               "start":{"dateTime":"2026-10-16T13:00:00-05:00"},"end":{"dateTime":"2026-10-16T14:00:00-05:00"}},
              {"eventId":"ev-lunch","title":"Lunch Break","calendarId":"work@example.com",
               "start":{"dateTime":"2026-10-16T11:30:00-05:00"},"end":{"dateTime":"2026-10-16T12:15:00-05:00"}}]}
            """);

        var day = SunsamaParser.Day(tasks, events, Profile());

        var box = Assert.Single(day[0].Events);
        Assert.False(box.OnGoogle);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 18, 0, 0, TimeSpan.Zero), box.Start);
        var lunch = Assert.Single(day[1].Events);
        Assert.True(lunch.OnGoogle);
        Assert.Equal("work@example.com", lunch.CalendarId);
        Assert.Equal("Lunch Break", lunch.Title);
    }

    [Fact]
    public void Day_MissingAndOddFields_KeepsWhatItCan()
    {
        var tasks = Json("""
            {"tasks":[
              {"_id":"aaaaaaaaaaaaaaaaaaaaaaa3","title":null,"channel":null,"timeEstimate":"","actualTimeSpent":null,
               "timeboxEventIds":["missing-event", 7],
               "projectedTimeEntries":[{"startDate":"bad","endDate":"2026-10-16 1:00 PM"},
                                       {"startDate":"2026-10-16 2:00 PM","endDate":"2026-10-16 1:00 PM"}]},
              "not an object",
              {"title":"no id"}]}
            """);

        var task = Assert.Single(SunsamaParser.Day(tasks, Json("""{"events":"nope"}"""), Profile()));

        Assert.Equal("", task.Title);
        Assert.Equal("", task.Channel);
        Assert.Null(task.EstimateMinutes);
        Assert.Null(task.TrackedMinutes);
        Assert.Empty(task.Projected);
        Assert.Empty(task.Events);
    }

    [Fact]
    public void Day_AllDayEvent_IsSkipped()
    {
        var tasks = Json("""{"tasks":[{"_id":"aaaaaaaaaaaaaaaaaaaaaaa4","title":"x","timeboxEventIds":["ev-day"]}]}""");
        var events = Json("""{"events":[{"eventId":"ev-day","title":"x","calendarId":"sunsama-2c88","start":{"date":"2026-10-16"},"end":{"date":"2026-10-17"}}]}""");

        Assert.Empty(Assert.Single(SunsamaParser.Day(tasks, events, Profile())).Events);
    }
}
```

- [ ] **Step 6: Run; expect build failure.**

- [ ] **Step 7: Implement `SunsamaModels.cs`**

```csharp
namespace LeafCalendar.Core.Sunsama;

/// <summary>Who is signed in to Sunsama: their email, their Sunsama time zone and which of their calendars are Google ones.</summary>
/// <param name="Email">The account email (shown in Settings, never logged).</param>
/// <param name="Zone">The zone Sunsama writes its projected local times in.</param>
/// <param name="GoogleCalendarIds">Calendar IDs whose provider is <c>google-calendar</c>; their events are already in Leaf Calendar.</param>
public sealed record SunsamaProfile(string Email, TimeZoneInfo Zone, IReadOnlySet<string> GoogleCalendarIds);

/// <summary>A timed calendar event a task is tied to (a timebox, or the event the task was imported from).</summary>
/// <param name="EventId">Sunsama's event ID.</param>
/// <param name="CalendarId">The calendar it's on (a Google calendar ID, or Sunsama's own calendar).</param>
/// <param name="Title">The event's title, for matching to the Leaf Calendar copy.</param>
/// <param name="Start">Start instant.</param>
/// <param name="End">End instant.</param>
/// <param name="OnGoogle">True when the calendar is a Google one, so Leaf Calendar already shows this event.</param>
public sealed record SunsamaEventRef(string EventId, string CalendarId, string Title, DateTimeOffset Start, DateTimeOffset End, bool OnGoogle);

/// <summary>One Sunsama task on a day, as read: its text, times and totals. Untrusted text, shown plain.</summary>
/// <param name="Id">Sunsama's task ID.</param>
/// <param name="Title">Title ("" when missing).</param>
/// <param name="Channel">Channel name ("" when missing).</param>
/// <param name="EstimateMinutes">The planned time, or null when unreadable.</param>
/// <param name="TrackedMinutes">The tracked total, or null when unreadable or none.</param>
/// <param name="Completed">The task is done.</param>
/// <param name="Projected">Sunsama's automatic placement for the day, in order.</param>
/// <param name="Events">Timed events the task is tied to (timeboxes and the imported-from event).</param>
public sealed record SunsamaTask(
    string Id,
    string Title,
    string Channel,
    int? EstimateMinutes,
    int? TrackedMinutes,
    bool Completed,
    IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Projected,
    IReadOnlyList<SunsamaEventRef> Events);
```

- [ ] **Step 8: Implement `SunsamaParser.cs`** with `JsonElement` reads only (no deserializer). Rules:
  - `Profile(me)`: `me.user.email` (string, else ""), `me.user.timezone` through `TimeZoneInfo.TryFindSystemTimeZoneById` (null profile when it fails), `calendars[]` where `provider == "google-calendar"` → `id`. Return `null` if `user` isn't an object.
  - `Day(tasks, events, profile)`: build a `Dictionary<string, SunsamaEventRef>` from `events.events[]` that are objects with a string `eventId`, string `calendarId`, and `start.dateTime`/`end.dateTime` strings parsed with `DateTimeOffset.TryParse(..., CultureInfo.InvariantCulture, DateTimeStyles.None, ...)` (skip all-day `date` events and any with `end <= start`). `OnGoogle = profile.GoogleCalendarIds.Contains(calendarId)`. Title from `title` string or "".
  - For each object in `tasks.tasks[]` with a string `_id`: title/channel strings or ""; `EstimateMinutes = SunsamaText.Duration(timeEstimate)?.TotalMinutes` as int; `TrackedMinutes` from `actualTimeSpent.total` the same way, but `0` becomes null (no overlay); `Completed` true only for JSON `true`; `Projected` from each `projectedTimeEntries[]` object's `startDate`/`endDate` through `SunsamaText.LocalTime(..., profile.Zone)`, kept only when both read and `end > start`; `Events` = each string in `timeboxEventIds[]` found in the dictionary, plus `importedFrom.identifier.sunsamaId` (string) when found, distinct by `EventId`.
  - A small private helper `static string? Str(JsonElement obj, string name)` returns the string only when `obj` is an object and the property is a string; every read goes through it or a sibling helper so a wrong kind never throws.

- [ ] **Step 9: Run both test classes; expect PASS.**
- [ ] **Step 10: Checkpoint.** `dotnet build LeafCalendar.slnx -c Debug` (0 warnings) and the full logic suite pass.

---

### Task 2: Matching and Tracked Fill (Core, Pure)

**Files:**
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaMatch.cs`
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaTracked.cs`
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaBlock.cs`
- Test: `tests/LeafCalendar.Tests/SunsamaMatchTests.cs`, `tests/LeafCalendar.Tests/SunsamaTrackedTests.cs`

**Interfaces:**
- Consumes: `SunsamaTask`, `SunsamaEventRef` (Task 1); `CalendarOccurrence` (`LeafCalendar.Core.Events`).
- Produces:
  - `enum SunsamaBlockKind { Projected, Timebox, Matched }`
  - `record SunsamaBlock(DateOnly Day, string TaskId, string Title, string Channel, DateTimeOffset Start, DateTimeOffset End, int? EstimateMinutes, int? TrackedMinutes, bool Completed, SunsamaBlockKind Kind, string? MatchedAccountId, string? MatchedCalendarId, string? MatchedEventId)`
  - `SunsamaMatch.Blocks(DateOnly day, IReadOnlyList<SunsamaTask> tasks, IReadOnlyList<CalendarOccurrence> cached) -> IReadOnlyList<SunsamaBlock>`
  - `SunsamaTracked.Fill(IReadOnlyList<SunsamaBlock> blocksOfOneTaskOnOneDay) -> IReadOnlyList<double>` (same order as input, sorted by start inside)

- [ ] **Step 1: Write the failing tests**

```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaMatchTests
{
    private static readonly DateOnly Day = new(2026, 10, 16);
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 17, 0, 0, TimeSpan.Zero);

    private static CalendarOccurrence Google(string calendar, string id, string title, DateTimeOffset start) =>
        new("acct", calendar, id, null, null, start, start.AddHours(1), false, title, EventKind.Default, ResponseStatus.Accepted, "#000000", null, false, false);

    private static SunsamaTask Task(string id, IReadOnlyList<(DateTimeOffset, DateTimeOffset)> projected, params SunsamaEventRef[] events) =>
        new(id, "Title", "Channel", 60, 30, false, projected, events);

    [Fact]
    public void Blocks_Projected_OneBlockPerEntry()
    {
        var blocks = SunsamaMatch.Blocks(Day, [Task("t1", [(Noon, Noon.AddMinutes(40)), (Noon.AddHours(2), Noon.AddHours(3))])], []);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(SunsamaBlockKind.Projected, b.Kind));
    }

    [Fact]
    public void Blocks_TimeboxOnSunsamaCalendar_IsATimeboxBlock()
    {
        var box = new SunsamaEventRef("e1", "sunsama-cal", "Title", Noon, Noon.AddHours(1), OnGoogle: false);

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [Task("t1", [], box)], []));

        Assert.Equal(SunsamaBlockKind.Timebox, block.Kind);
        Assert.Equal(Noon, block.Start);
    }

    [Fact]
    public void Blocks_GoogleEventSameStartAndTitle_IsMatchedNotDrawn()
    {
        var lunch = new SunsamaEventRef("s-lunch", "work@example.com", "Lunch Break", Noon, Noon.AddMinutes(45), OnGoogle: true);
        var cached = new[] { Google("work@example.com", "g-other", "Other", Noon), Google("work@example.com", "g-lunch", "Lunch Break", Noon) };

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [Task("t1", [], lunch)], cached));

        Assert.Equal(SunsamaBlockKind.Matched, block.Kind);
        Assert.Equal(("acct", "work@example.com", "g-lunch"), (block.MatchedAccountId, block.MatchedCalendarId, block.MatchedEventId));
    }

    [Fact]
    public void Blocks_GoogleEventTitleDiffers_FallsBackToSameStart()
    {
        var ev = new SunsamaEventRef("s", "work@example.com", "Renamed in Sunsama", Noon, Noon.AddHours(1), OnGoogle: true);

        var block = Assert.Single(SunsamaMatch.Blocks(Day, [Task("t1", [], ev)], [Google("work@example.com", "g1", "Original", Noon)]));

        Assert.Equal("g1", block.MatchedEventId);
    }

    [Fact]
    public void Blocks_GoogleEventNotCached_DrawsNothing()
    {
        var ev = new SunsamaEventRef("s", "hidden@example.com", "Title", Noon, Noon.AddHours(1), OnGoogle: true);

        Assert.Empty(SunsamaMatch.Blocks(Day, [Task("t1", [], ev)], [Google("work@example.com", "g1", "Title", Noon)]));
    }
}

public sealed class SunsamaTrackedTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 15, 50, 0, TimeSpan.Zero);

    private static SunsamaBlock Block(DateTimeOffset start, int minutes, int? tracked) =>
        new(new DateOnly(2026, 10, 16), "t1", "x", "c", start, start.AddMinutes(minutes), 120, tracked, false, SunsamaBlockKind.Projected, null, null, null);

    [Fact]
    public void Fill_OneBlock_IsTrackedOverLength() =>
        Assert.Equal([0.7], SunsamaTracked.Fill([Block(Start, 120, 84)]));

    [Fact]
    public void Fill_SplitBlocks_CarryOverInTimeOrder()
    {
        // 40 m then 80 m, 84 m tracked: the first full, the second 44/80
        var fill = SunsamaTracked.Fill([Block(Start.AddMinutes(100), 80, 84), Block(Start, 40, 84)]);

        Assert.Equal(0.55, fill[0], 3);
        Assert.Equal(1.0, fill[1], 3);
    }

    [Fact]
    public void Fill_MoreThanPlanned_FillsEveryBlock() =>
        Assert.Equal([1.0, 1.0], SunsamaTracked.Fill([Block(Start, 30, 500), Block(Start.AddHours(1), 30, 500)]));

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void Fill_NoTrackedTime_IsZero(int? tracked) => Assert.Equal([0.0], SunsamaTracked.Fill([Block(Start, 60, tracked)]));

    [Fact]
    public void Fill_NoEstimate_StillFillsByBlockLength()
    {
        var block = Block(Start, 60, 30) with { EstimateMinutes = null };

        Assert.Equal([0.5], SunsamaTracked.Fill([block]));
    }
}
```

- [ ] **Step 2: Run; expect build failure.**
- [ ] **Step 3: Implement.** `SunsamaBlock.cs` holds the enum and record with doc comments (`Day` is the local day it was fetched for; `Matched*` set only for `Matched`). `SunsamaMatch.Blocks`:
  - For each task, each `Projected` entry → a `Projected` block.
  - Each `Events` entry with `!OnGoogle` → a `Timebox` block with the event's start/end.
  - Each `Events` entry with `OnGoogle` → among `cached` with `CalendarId == ref.CalendarId && Start == ref.Start && !IsAllDay`, prefer `Title == ref.Title` (ordinal), else the first; when found, a `Matched` block with the occurrence's `AccountId`/`CalendarId`/`EventId` and the occurrence's start/end; when none, nothing.
  - Block text and totals copy from the task.

  `SunsamaTracked.Fill`: sort indexes by `Start`; `remaining = TrackedMinutes ?? 0` (from the first block; all blocks of a task share it); for each block in start order `length = (End - Start).TotalMinutes`; `take = Math.Min(remaining, length)`; fraction `length > 0 ? take / length : 0`; `remaining -= take`; return fractions in the input order. Doc comment explains the carry-over and that per-day filling uses the whole total (spec 6.3.1).
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Checkpoint.** Build and full logic suite.

---

### Task 3: Public-Client OAuth With PKCE and Registration (Core)

**Files:**
- Modify: `src/LeafCalendar.Core/Scheduling/SchedulingOAuth.cs` (record + `SchedulingTokenRequest.SendAsync`)
- Modify: `src/LeafCalendar.Core/Scheduling/ProviderSignIn.cs` (`RunAsync`, `BuildAuthorizeUrl`)
- Modify: `src/LeafCalendar.Core/Scheduling/ProviderTokens.cs` (refresh form)
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaRegistration.cs`
- Test: `tests/LeafCalendar.Tests/ProviderSignInTests.cs`, `tests/LeafCalendar.Tests/ProviderTokensTests.cs`, `tests/LeafCalendar.Tests/SunsamaRegistrationTests.cs`

**Interfaces:**
- Produces:
  - `SchedulingOAuth(..., Uri ClientPage, bool UsePkce = false, Uri? Resource = null)` (two new trailing parameters; every existing `new SchedulingOAuth(...)` keeps compiling).
  - A `ProviderClient` with `ClientSecret == ""` is a public client: no `client_secret` is sent.
  - `SunsamaRegistration.RegisterAsync(HttpClient http, Uri register, string redirectUri, CancellationToken ct) -> Task<string>` (the client ID); throws `SchedulingAuthException` on 400/401/403 or no `client_id`, `SchedulingUnavailableException` on network, 429, 5xx, unreadable JSON.

- [ ] **Step 1: Failing tests in `ProviderSignInTests`** (reuse the class's `FreePort`, `Redirects`, `Approve` helpers):

```csharp
[Fact]
public async Task RunAsync_PublicPkceClient_SendsChallengeVerifierAndResourceButNoSecret()
{
    var oauth = _oauth with { UsePkce = true, Resource = new Uri("https://api.example.test/mcp") };
    _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, TokenAnswer);
    IReadOnlyDictionary<string, string>? authorize = null;
    var signIn = new ProviderSignIn(new HttpClient(_provider), oauth, new ProviderClient("public-1", ""), url =>
    {
        authorize = QueryString.Parse(url.Query);
        return Redirects(Approve)(url);
    }, _time);

    await signIn.RunAsync(TestContext.Current.CancellationToken);

    Assert.Equal("S256", authorize!["code_challenge_method"]);
    Assert.Equal("https://api.example.test/mcp", authorize["resource"]);
    var token = Assert.Single(_provider.Requests);
    var verifier = token.Form("code_verifier")!;
    Assert.Equal(Pkce.CreateChallenge(verifier), authorize["code_challenge"]);
    Assert.Equal("https://api.example.test/mcp", token.Form("resource"));
    Assert.Equal("public-1", token.Form("client_id"));
    Assert.Null(token.Form("client_secret"));
}

[Fact]
public async Task RunAsync_ConfidentialClient_StillSendsNoVerifier()
{
    _provider.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, TokenAnswer);
    IReadOnlyDictionary<string, string>? authorize = null;
    var signIn = Create(url =>
    {
        authorize = QueryString.Parse(url.Query);
        return Redirects(Approve)(url);
    });

    await signIn.RunAsync(TestContext.Current.CancellationToken);

    Assert.False(authorize!.ContainsKey("code_challenge"));
    Assert.Null(Assert.Single(_provider.Requests).Form("code_verifier"));
}
```

Add `using LeafCalendar.Core.Auth;` for `Pkce`. In `ProviderTokensTests` add `RefreshAsync_PublicClientWithResource_SendsResourceNoSecret` building `ProviderTokens` with `new ProviderClient("public-1", "")` and an OAuth record `with { Resource = ... }`, asserting the refresh request's `resource` form value and no `client_secret` (follow the file's existing refresh test for setup).

- [ ] **Step 2: Failing registration tests**

```csharp
using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SunsamaRegistrationTests : IDisposable
{
    private const string Register = "https://api.example.test/oauth/register";
    private readonly FakeHttpHandler _server = new();

    public void Dispose() => _server.Dispose();

    private Task<string> Run() =>
        SunsamaRegistration.RegisterAsync(new HttpClient(_server), new Uri(Register), "http://127.0.0.1:53818/sunsama/callback", TestContext.Current.CancellationToken);

    [Fact]
    public async Task RegisterAsync_Accepted_ReturnsClientIdAndSendsPublicClientFields()
    {
        _server.On(HttpMethod.Post, Register, HttpStatusCode.Created, """{"client_id":"abc-123"}""");

        Assert.Equal("abc-123", await Run());

        using var body = JsonDocument.Parse(Assert.Single(_server.Requests).Body!);
        var root = body.RootElement;
        Assert.Equal("Leaf Calendar", root.GetProperty("client_name").GetString());
        Assert.Equal("none", root.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("http://127.0.0.1:53818/sunsama/callback", root.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal("read offline_access", root.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task RegisterAsync_Refused_ThrowsAuth()
    {
        _server.On(HttpMethod.Post, Register, HttpStatusCode.BadRequest, """{"error":"invalid_redirect_uri"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(Run);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task RegisterAsync_MayPass_ThrowsUnavailable(HttpStatusCode status)
    {
        _server.On(HttpMethod.Post, Register, status, "{}");

        await Assert.ThrowsAsync<SchedulingUnavailableException>(Run);
    }

    [Fact]
    public async Task RegisterAsync_NoClientId_ThrowsAuth()
    {
        _server.On(HttpMethod.Post, Register, HttpStatusCode.Created, """{"client_name":"x"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(Run);
    }
}
```

- [ ] **Step 3: Run; expect failures/build errors.**
- [ ] **Step 4: Implement.**
  - `SchedulingOAuth`: add `bool UsePkce = false, Uri? Resource = null` with `<param>` docs ("Sends a PKCE S256 challenge and verifier (public clients such as Sunsama's)", "The protected resource sent as <c>resource</c> on authorize and token requests (MCP servers)").
  - `SchedulingTokenRequest.SendAsync`: `form.Add(new("client_id", client.ClientId)); if (client.ClientSecret.Length > 0) { form.Add(new("client_secret", client.ClientSecret)); } if (oauth.Resource is { } resource) { form.Add(new("resource", resource.AbsoluteUri)); }`. Update its comment.
  - `ProviderSignIn.RunAsync`: `var verifier = oauth.UsePkce ? Pkce.CreateVerifier() : null;` pass it to `BuildAuthorizeUrl(redirectUri, state, verifier)`; there add `code_challenge` (`Pkce.CreateChallenge(verifier)`) and `code_challenge_method=S256` when non-null, and `resource` when set. The code exchange form adds `new("code_verifier", verifier)` when non-null. Update the class `<remarks>`: PKCE is on for providers that ask for it (`UsePkce`), off for Cal.com which refuses `code_verifier`.
  - `ProviderTokens`: nothing extra (the form goes through `SendAsync`), only the test.
  - `SunsamaRegistration`: `internal`-free public static class. Build the body with `Utf8JsonWriter` into a `MemoryStream` (`client_name`, `redirect_uris` [one], `grant_types` ["authorization_code","refresh_token"], `response_types` ["code"], `token_endpoint_auth_method` "none", `scope` "read offline_access"), post as `application/json`, map errors exactly like `SchedulingTokenRequest.SendAsync` (copy its try/catch shape: `HttpRequestException` and non-cancel `TaskCanceledException` → unavailable; 400/401/403 → auth with `SchedulingTokenRequest.ErrorCodes(body)` in the message; 429 → unavailable with Retry-After; other non-success → unavailable), read `client_id` with `JsonDocument` (non-empty string, else auth). Never log or put the body in an exception message.
- [ ] **Step 5: Run the three classes plus `CalComProviderTests` and `SchedulingHubTests`; expect PASS** (Cal.com unchanged).
- [ ] **Step 6: Checkpoint.** Build and full logic suite.

---

### Task 4: MCP Client (Core)

**Files:**
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaMcpClient.cs`
- Test: `tests/LeafCalendar.Tests/SunsamaMcpClientTests.cs`

**Interfaces:**
- Consumes: `SchedulingAuthException`, `SchedulingUnavailableException` (`LeafCalendar.Core.Scheduling`).
- Produces: `sealed class SunsamaMcpClient(HttpClient http, Uri endpoint, Func<CancellationToken, Task<string>> accessToken)` with `Task<JsonDocument> ReadResourceAsync(string uri, CancellationToken ct)` (the parsed JSON inside `result.contents[0].text`; caller disposes) and `void ResetSession()`.

- [ ] **Step 1: Failing tests**

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SunsamaMcpClientTests : IDisposable
{
    private const string Mcp = "https://api.example.test/mcp";
    private readonly FakeHttpHandler _server = new();
    private int _sessions;

    public void Dispose() => _server.Dispose();

    private SunsamaMcpClient Create() => new(new HttpClient(_server), new Uri(Mcp), _ => Task.FromResult("at-1"));

    private static string Method(RecordedRequest r) => JsonDocument.Parse(r.Body!).RootElement.GetProperty("method").GetString()!;

    private static HttpResponseMessage Json(string body, string? session = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (session is not null)
        {
            response.Headers.Add("Mcp-Session-Id", session);
        }

        return response;
    }

    // The resource's JSON goes inside a JSON string, so its quotes and backslashes are escaped
    private static string ReadResult(int id, string innerJson) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"result":{"contents":[{"uri":"x","mimeType":"application/json","text":"{{innerJson.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}}"}]}}""";

    private void ServeSession(Func<RecordedRequest, HttpResponseMessage> read)
    {
        _server.On(r => r.Body is not null && Method(r) == "initialize", _ => Json("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""", $"s{++_sessions}"));
        _server.On(r => r.Body is not null && Method(r) == "notifications/initialized", _ => new HttpResponseMessage(HttpStatusCode.Accepted));
        _server.On(r => r.Body is not null && Method(r) == "resources/read", read);
    }

    [Fact]
    public async Task ReadResourceAsync_JsonReply_StartsSessionAndReturnsInnerJson()
    {
        ServeSession(r => Json(ReadResult(2, """{"tasks":[]}""")));

        using var doc = await Create().ReadResourceAsync("sunsama://tasks/2026-10-16", TestContext.Current.CancellationToken);

        Assert.True(doc.RootElement.TryGetProperty("tasks", out _));
        var read = _server.Requests.Last();
        Assert.Equal("s1", read.Header("Mcp-Session-Id"));
        Assert.Equal("at-1", read.BearerToken);
        Assert.Equal("sunsama://tasks/2026-10-16", JsonDocument.Parse(read.Body!).RootElement.GetProperty("params").GetProperty("uri").GetString());
    }

    [Fact]
    public async Task ReadResourceAsync_SseReply_ReadsTheDataLine()
    {
        ServeSession(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {ReadResult(2, """{"user":{}}""")}\n\n", Encoding.UTF8, "text/event-stream"),
        });

        using var doc = await Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken);

        Assert.True(doc.RootElement.TryGetProperty("user", out _));
    }

    [Fact]
    public async Task ReadResourceAsync_SessionGone_StartsANewOneOnce()
    {
        var reads = 0;
        ServeSession(r => ++reads == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(ReadResult(2, "{}")));

        using var doc = await Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken);

        Assert.Equal(2, _sessions);
    }

    [Fact]
    public async Task ReadResourceAsync_Unauthorized_ThrowsAuth()
    {
        _server.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ReadResourceAsync_MayPass_ThrowsUnavailable(HttpStatusCode status)
    {
        _server.On(_ => true, _ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadResourceAsync_RpcErrorOrNonJsonText_ThrowsUnavailable()
    {
        ServeSession(r => Json("""{"jsonrpc":"2.0","id":2,"error":{"code":-32602,"message":"bad"}}"""));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().ReadResourceAsync("sunsama://me", TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run; expect build failure.**
- [ ] **Step 3: Implement `SunsamaMcpClient`.**
  - Fields: `string? _sessionId`, `int _nextId`, a `SemaphoreSlim _gate = new(1, 1)` so one call at a time (the sync is the only caller; the gate keeps session start single).
  - `SendAsync(string method, Action<Utf8JsonWriter>? writeParams, bool notification, CancellationToken ct)`: writes `{"jsonrpc":"2.0","id":n,"method":...,"params":{...}}` (no `id` for a notification) with `Utf8JsonWriter`; request headers `Authorization: Bearer <token>`, `Accept: application/json, text/event-stream`, `MCP-Protocol-Version: 2025-06-18` after initialize, `Mcp-Session-Id` when known; content type `application/json`.
  - Status mapping: 401/403 → `SchedulingAuthException("Sunsama refused the access token.")`; 404 while a session is set → `ResetSession()` and signal the caller to retry once; 429 → unavailable with Retry-After; other non-success → unavailable with the status code; `HttpRequestException` / non-cancel `TaskCanceledException` → unavailable.
  - Reply: if the content type is `text/event-stream`, take the last `data:` line whose JSON has the same `id`; else the body. Parse with `JsonDocument`; an `error` member → unavailable (log nothing from it but the numeric `code`); `result.contents[0].text` must be a string that parses as JSON, else unavailable.
  - `EnsureSessionAsync`: when `_sessionId` is null, send `initialize` with params `protocolVersion "2025-06-18"`, `capabilities {}`, `clientInfo {"name":"Leaf Calendar","version":"1.0"}`, keep the `Mcp-Session-Id` response header, then send `notifications/initialized`.
  - `ReadResourceAsync(uri)`: `EnsureSessionAsync`, send `resources/read` with `{"uri": uri}`; on the session-gone signal, start a new session and send once more.
  - Class doc comment: what MCP streamable HTTP is in one sentence, that Leaf Calendar only reads, and links `https://modelcontextprotocol.io/specification/2025-06-18/basic/transports`.
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Checkpoint.** Build and full logic suite.

---

### Task 5: Migration 14 and the Block Store (Core)

**Files:**
- Modify: `src/LeafCalendar.Core/Data/Schema.cs` (add `V14`)
- Modify: `src/LeafCalendar.Core/Data/LeafDatabase.cs` (apply version 14)
- Create: `src/LeafCalendar.Core/Data/SunsamaStore.cs`
- Modify: `tests/LeafCalendar.Tests/LeafDatabaseTests.cs:103` (13 → 14) and any other `user_version` assertion (search `13L`)
- Test: `tests/LeafCalendar.Tests/SunsamaStoreTests.cs`

**Interfaces:**
- Consumes: `SunsamaBlock`, `SunsamaBlockKind` (Task 2).
- Produces (static, connection + optional transaction like the other stores):
  - `SunsamaStore.ReplaceDay(SqliteConnection conn, SqliteTransaction? tx, DateOnly day, IReadOnlyList<SunsamaBlock> blocks)`
  - `SunsamaStore.Range(SqliteConnection conn, DateOnly from, DateOnly toExclusive) -> IReadOnlyList<SunsamaBlock>`
  - `SunsamaStore.Clear(SqliteConnection conn)`

- [ ] **Step 1: Failing tests**

```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Sunsama;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SunsamaStoreTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private static readonly DateOnly Day = new(2026, 10, 16);

    public void Dispose() => _db.Dispose();

    private static SunsamaBlock Block(DateOnly day, string task, SunsamaBlockKind kind = SunsamaBlockKind.Projected) =>
        new(day, task, "Title ' \"; DROP", "Channel 💖", new DateTimeOffset(2026, 10, 16, 15, 50, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 16, 30, 0, TimeSpan.Zero),
            120, 84, false, kind, kind == SunsamaBlockKind.Matched ? "acct" : null, kind == SunsamaBlockKind.Matched ? "cal" : null, kind == SunsamaBlockKind.Matched ? "ev" : null);

    [Fact]
    public void ReplaceDay_ThenRange_RoundTripsEveryField()
    {
        using var conn = _db.Database.Open();
        var saved = new[] { Block(Day, "t1"), Block(Day, "t2", SunsamaBlockKind.Matched) };

        SunsamaStore.ReplaceDay(conn, null, Day, saved);

        Assert.Equal(saved, SunsamaStore.Range(conn, Day, Day.AddDays(1)));
    }

    [Fact]
    public void ReplaceDay_SecondTime_ReplacesOnlyThatDay()
    {
        using var conn = _db.Database.Open();
        SunsamaStore.ReplaceDay(conn, null, Day, [Block(Day, "old")]);
        SunsamaStore.ReplaceDay(conn, null, Day.AddDays(1), [Block(Day.AddDays(1), "other")]);

        SunsamaStore.ReplaceDay(conn, null, Day, [Block(Day, "new")]);

        Assert.Equal(["new", "other"], SunsamaStore.Range(conn, Day, Day.AddDays(2)).Select(b => b.TaskId));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        using var conn = _db.Database.Open();
        SunsamaStore.ReplaceDay(conn, null, Day, [Block(Day, "t1")]);

        SunsamaStore.Clear(conn);

        Assert.Empty(SunsamaStore.Range(conn, Day, Day.AddDays(1)));
    }
}
```

- [ ] **Step 2: Run; expect build failure.** Update `LeafDatabaseTests` line 103 to `14L` now so it fails for the right reason after the build.
- [ ] **Step 3: Implement.**

`Schema.V14` (doc comment: "Version 14: <c>sunsama_block</c>, the Sunsama task times shown on the calendar (spec 2026-10-16, Sunsama projections), saved per local day so they show offline. Local only; Sign out deletes every row."):

```sql
CREATE TABLE sunsama_block (
    day                TEXT    NOT NULL,
    task_id            TEXT    NOT NULL,
    title              TEXT    NOT NULL,
    channel            TEXT    NOT NULL,
    start_utc          TEXT    NOT NULL,
    end_utc            TEXT    NOT NULL,
    estimate_minutes   INTEGER,
    tracked_minutes    INTEGER,
    completed          INTEGER NOT NULL,
    kind               TEXT    NOT NULL,
    matched_account_id  TEXT,
    matched_calendar_id TEXT,
    matched_event_id    TEXT
);

CREATE INDEX ix_sunsama_block_day ON sunsama_block (day);
```

`LeafDatabase.Migrate`: add the `// Version 14` block after 13, same shape. `SunsamaStore`: follow `CalendarStore`'s use of `conn.Execute`/`conn.Query` helpers; `day` as `yyyy-MM-dd` and instants as round-trip `"O"` UTC strings, invariant culture; `kind` as `projected`/`timebox`/`matched`; `ReplaceDay` deletes the day's rows then inserts in its own transaction when `tx` is null; `Range` orders by `day`, `start_utc`, `task_id`; every statement parameterized.
- [ ] **Step 4: Run `SunsamaStoreTests` and `LeafDatabaseTests`; expect PASS.**
- [ ] **Step 5: Checkpoint.** Build and full logic suite.

---

### Task 6: `SunsamaService` — Sign In, Sign Out, Sync (Core)

**Files:**
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaEndpoints.cs`
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaService.cs`
- Test: `tests/LeafCalendar.Tests/SunsamaServiceTests.cs`

**Interfaces:**
- Consumes: everything above; `ITokenStore`, `LeafDatabase`, `AppLog`, `OccurrenceQuery.Load(conn, from, to, zone, includeDeclined, keepSharedCopies)`.
- Produces:
  - `record SunsamaEndpoints(Uri Mcp, Uri Authorize, Uri Token, Uri Register)` with `static Default` and `static ForFake(Uri root)` (`<root>sunsama/mcp`, `<root>sunsama/oauth/authorize`, `/oauth/token`, `/oauth/register`).
  - `enum SunsamaState { SignedOut, SignedIn, NeedsSignIn }`
  - `sealed class SunsamaService(HttpClient http, ITokenStore tokens, LeafDatabase database, AppLog log, TimeProvider time, SunsamaEndpoints endpoints) : IDisposable`
    - `const string ProviderId = "sunsama"`, `const int RedirectPort = 53818`, `const string RedirectPath = "/sunsama/callback"`
    - `SunsamaState State`, `string? Email`, `bool Enabled { get; set; }` (the sidebar switches; false skips syncing)
    - `event EventHandler? Changed` (state/email), `event EventHandler? DataChanged` (rows saved) — raised on any thread
    - `void Load()` (reads secrets; `SecretsUnavailable` leaves `SignedOut` without clearing anything and logs the type)
    - `Task SignInAsync(Func<Uri, Task> openBrowser, CancellationToken ct)` (register if no saved client ID, run `ProviderSignIn`, save `client_id` + `refresh_token`, read `sunsama://me` for the email, save `email`, set `SignedIn`)
    - `void SignOut()` (removes provider secrets, `SunsamaStore.Clear`, `SignedOut`, raises both events)
    - `void SetDays(DateOnly from, DateOnly toExclusive)` (the days on screen; thread-safe)
    - `Task SyncAsync(CancellationToken ct)` (one pass, called after each Google sync pass)

- [ ] **Step 1: Failing tests.** Build the service against `FakeHttpHandler` serving `ForFake(new Uri("https://fake.test/"))` routes, `InMemoryTokenStore`, `TestDatabase`, `FakeTimeProvider`, and a test `AppLog` (copy how `SchedulingHubTests` builds its log). Tests (write each fully, following `SchedulingHubTests` style):
  - `Load_SavedRefreshToken_IsSignedInWithEmail` — seed `client_id`, `refresh_token`, `email`; `Load()`; `State == SignedIn`, `Email` matches.
  - `Load_SecretsUnavailable_StaysSignedOutAndKeepsSecrets` — a token store whose `GetProviderSecret` throws the marked `InvalidDataException` (`SecretsUnavailable.Create(...)`); `Load()` doesn't throw; state `SignedOut`; nothing removed.
  - `SignInAsync_FirstTime_RegistersSignsInAndSavesClientTokenEmail` — routes: register → `{"client_id":"c1"}`; token → access/refresh; MCP initialize/initialized/read `sunsama://me` → profile JSON; `openBrowser` uses the `Redirects(Approve)` approach from `ProviderSignInTests` (copy those two helpers into this test class). Assert saved secrets `client_id=c1`, `refresh_token`, `email`, state `SignedIn`, `Changed` raised once.
  - `SignInAsync_SavedClientId_DoesNotRegisterAgain`.
  - `SignInAsync_ClientRefused_ClearsClientIdSoNextTimeRegisters` — token endpoint answers 400 `invalid_client`; `SchedulingAuthException` propagates; `client_id` secret removed.
  - `SyncAsync_SavesBlocksForDaysOnScreen` — signed in; `SetDays(Oct 16, Oct 17)`; MCP serves `me`, `tasks/2026-10-16` (one projected task), `calendar/events/2026-10-16` (empty); `SyncAsync`; `SunsamaStore.Range` has one `Projected` block; `DataChanged` raised.
  - `SyncAsync_Disabled_FetchesNothing` — `Enabled = false`; no MCP requests.
  - `SyncAsync_Offline_KeepsSavedRows` — rows saved first; MCP answers 503; no throw; rows unchanged; state still `SignedIn`.
  - `SyncAsync_RefusedRefresh_MarksSignedOutKeepsRows` — token endpoint answers 400 `invalid_grant`; state `NeedsSignIn`; rows unchanged; refresh token secret kept (so the owner sees why; Sign in replaces it).
  - `SignOut_RemovesSecretsAndRows`.
  - `Logs_NeverContainTaskTextOrEmail` — after a sync and a sign-in, the log text contains none of the title, channel or email strings used.
- [ ] **Step 2: Run; expect build failure.**
- [ ] **Step 3: Implement.**
  - `SunsamaEndpoints.Default`: `https://api.sunsama.com/mcp`, `/oauth/authorize`, `/oauth/token`, `/oauth/register`.
  - OAuth record: `new SchedulingOAuth(endpoints.Authorize, endpoints.Token, ["read", "offline_access"], RedirectPort, RedirectPath, new Uri("https://help.sunsama.com/docs/integrations/mcp"), UsePkce: true, Resource: endpoints.Mcp)`.
  - Holds a `ProviderTokens?` and a `SunsamaMcpClient?` built when signed in (`new ProviderClient(clientId, "")`; rotated refresh token saved through `tokens.SetProviderSecret(ProviderId, "refresh_token", ...)`).
  - `SyncAsync`: return unless `SignedIn && Enabled`; copy the day range under a lock; read `sunsama://me` once per service lifetime (keep the `SunsamaProfile`; read again if it was null); for each day: read tasks and events, `SunsamaParser.Day`, load cached occurrences with `OccurrenceQuery.Load(conn, day, day.AddDays(1), profile.Zone, includeDeclined: false, keepSharedCopies: false)`, `SunsamaMatch.Blocks`, `SunsamaStore.ReplaceDay`. Raise `DataChanged` once if any day saved. `SchedulingAuthException` → `NeedsSignIn` + `Changed`; `SchedulingUnavailableException`, `JsonException`, `SqliteException` → log `sunsama.sync.failed` with the type and stop the pass. Never throw out of `SyncAsync` except `OperationCanceledException` when `ct` is canceled.
  - Range clamp: at most 42 days per pass (a month view), so a stuck range can't fetch forever; mark it `// ponytail: fetches every visible day each pass; cache by day + lastModified if Sunsama rate-limits`.
  - Doc comments on the class (what it reads, that it never writes, where secrets live) and every public member.
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Checkpoint.** Build and full logic suite.

---

### Task 7: Settings Switches, Occurrences and the Task Link (Core)

**Files:**
- Modify: `src/LeafCalendar.Core/Settings/LeafSettings.cs` (two properties near `BookingNotifications`)
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaOccurrences.cs`
- Create: `src/LeafCalendar.Core/Sunsama/SunsamaLinks.cs`
- Test: `tests/LeafCalendar.Tests/SunsamaOccurrencesTests.cs`, `tests/LeafCalendar.Tests/SunsamaLinksTests.cs`, `tests/LeafCalendar.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `LeafSettings.SunsamaProjected { get; set; } = true`, `LeafSettings.SunsamaTracked { get; set; } = true`
  - `SunsamaOccurrences.AccountId = "sunsama"`, `ProjectedCalendarId = "projected"`, `TrackedCalendarId = "tracked"`, `TimeboxCalendarId = "timebox"`, `Color = "#F4511E"`
  - `SunsamaOccurrences.IsSunsama(CalendarOccurrence o) -> bool`
  - `SunsamaOccurrences.ToOccurrence(SunsamaBlock b) -> CalendarOccurrence` (Projected/Timebox only)
  - `record SunsamaShown(SunsamaBlock Block, double Fill)`
  - `SunsamaOccurrences.Build(IReadOnlyList<SunsamaBlock> rows) -> (List<CalendarOccurrence> Blocks, Dictionary<string, SunsamaShown> ByKey)` — `ByKey` holds each drawn block under its occurrence `Key`, and each matched row under `MatchKey(accountId, calendarId, eventId)`
  - `SunsamaOccurrences.MatchKey(string accountId, string calendarId, string eventId) -> string`
  - `SunsamaLinks.TaskLink(string taskId) -> Uri?` (`sunsama://action/details/<id>` when `SunsamaText.IsTaskId`), `SunsamaLinks.Fallback` = `https://app.sunsama.com/`

- [ ] **Step 1: Failing tests**

```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaOccurrencesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 15, 50, 0, TimeSpan.Zero);

    private static SunsamaBlock Block(string task, SunsamaBlockKind kind, int minutes = 60, int? tracked = 30, DateTimeOffset? start = null) =>
        new(new DateOnly(2026, 10, 16), task, "Home Page Redesign", "Illuminate Digital", start ?? Start, (start ?? Start).AddMinutes(minutes), 120, tracked, false, kind,
            kind == SunsamaBlockKind.Matched ? "acct" : null, kind == SunsamaBlockKind.Matched ? "cal" : null, kind == SunsamaBlockKind.Matched ? "ev" : null);

    [Fact]
    public void ToOccurrence_Projected_LooksLikeAnUnansweredInvite()
    {
        var o = SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Projected));

        Assert.True(SunsamaOccurrences.IsSunsama(o));
        Assert.Equal(ResponseStatus.NeedsAction, o.SelfResponse);
        Assert.Equal("Home Page Redesign", o.Title);
        Assert.Equal(SunsamaOccurrences.Color, o.CalendarColor);
        Assert.False(o.IsAllDay);
    }

    [Fact]
    public void ToOccurrence_Timebox_LooksLikeARegularEvent() =>
        Assert.Equal(ResponseStatus.Accepted, SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Timebox)).SelfResponse);

    [Fact]
    public void Build_DrawsProjectedAndTimeboxButNotMatched()
    {
        var (blocks, byKey) = SunsamaOccurrences.Build([Block("t1", SunsamaBlockKind.Projected), Block("t2", SunsamaBlockKind.Timebox), Block("t3", SunsamaBlockKind.Matched)]);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(0.5, byKey[blocks[0].Key].Fill);
        Assert.Equal("t3", byKey[SunsamaOccurrences.MatchKey("acct", "cal", "ev")].Block.TaskId);
    }

    [Fact]
    public void Build_SplitTask_FillsInTimeOrder()
    {
        var (blocks, byKey) = SunsamaOccurrences.Build([
            Block("t1", SunsamaBlockKind.Projected, 40, 84),
            Block("t1", SunsamaBlockKind.Projected, 80, 84, Start.AddMinutes(100)),
        ]);

        Assert.Equal(1.0, byKey[blocks[0].Key].Fill, 3);
        Assert.Equal(0.55, byKey[blocks[1].Key].Fill, 3);
    }

    [Fact]
    public void Build_MatchedFill_IsAgainstTheEventLength() =>
        Assert.Equal(0.5, SunsamaOccurrences.Build([Block("t3", SunsamaBlockKind.Matched, 60, 30)]).ByKey[SunsamaOccurrences.MatchKey("acct", "cal", "ev")].Fill);

    [Fact]
    public void IsSunsama_GoogleOccurrence_IsFalse() =>
        Assert.False(SunsamaOccurrences.IsSunsama(new CalendarOccurrence("acct", "cal", "ev", null, null, Start, Start.AddHours(1), false, "x", EventKind.Default, ResponseStatus.Accepted, "#000000", null, false, false)));
}

public sealed class SunsamaLinksTests
{
    [Fact]
    public void TaskLink_SunsamaId_OpensTheApp() =>
        Assert.Equal(new Uri("sunsama://action/details/6ac7a44534029300011d076e"), SunsamaLinks.TaskLink("6ac7a44534029300011d076e"));

    [Theory]
    [InlineData("")]
    [InlineData("6ac7a44534029300011d076e/../x")]
    [InlineData("javascript:alert(1)")]
    public void TaskLink_BadId_ReturnsNull(string id) => Assert.Null(SunsamaLinks.TaskLink(id));

    [Fact]
    public void Fallback_IsSunsamaHome() => Assert.Equal(new Uri("https://app.sunsama.com/"), SunsamaLinks.Fallback);
}
```

In `SettingsStoreTests` add `Load_OldRow_SunsamaSwitchesAreOn` (a stored settings JSON without the two keys loads both as `true`), following the file's existing "old row" test.

- [ ] **Step 2: Run; expect build failure.**
- [ ] **Step 3: Implement.** `ToOccurrence`: `new CalendarOccurrence(AccountId, kind == Projected ? ProjectedCalendarId : TimeboxCalendarId, $"{TaskId}@{Start.UtcTicks}", null, null, Start, End, false, Title, EventKind.Default, kind == Projected ? ResponseStatus.NeedsAction : ResponseStatus.Accepted, Color, null, IsFree: true, HasConference: false)`. `Build`: group drawn rows by `(Day, TaskId)` and use `SunsamaTracked.Fill`; matched rows fill `min(1, tracked / length)`. The two settings properties get doc comments ("Show Sunsama's task times on the calendar (sidebar › Sunsama › Projected tasks).", "Show Sunsama's tracked time on tasks and matched events (sidebar › Sunsama › Tracked time).") and need nothing in `Normalize`.
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Checkpoint.** Build and full logic suite.

---

### Task 8: App Wiring — Services, Sync Hook, Launching

**Files:**
- Modify: `src/LeafCalendar.App/LeafServices.cs` (construct `SunsamaService`, `afterSync`, `LaunchSunsamaAsync`, `NotifySignInReturned`, reload on secrets recovery next to `LoadScheduling`)
- Modify: `src/LeafCalendar.App/App.xaml.cs` (dispose on exit, `Enabled` from settings)

**Interfaces:**
- Consumes: `SunsamaService`, `SunsamaEndpoints`, `SunsamaLinks`.
- Produces: `LeafServices.Sunsama` (`SunsamaService`), `Task<bool> LeafServices.LaunchSunsamaAsync(string taskId)`, `void LeafServices.NotifySignInReturned()` (raises the existing `SignInReturned`).

- [ ] **Step 1: Construct.** Next to `Scheduling = new SchedulingHub(...)` (`LeafServices.cs:69`): `Sunsama = new SunsamaService(_schedulingHttp, Tokens, Database, Log, Time, options.FakeGoogle is { } root ? SunsamaEndpoints.ForFake(root) : SunsamaEndpoints.Default);` then `Sunsama.Load();` inside the same try/catch shape `LoadScheduling` uses. Call `Sunsama.Load()` again where `LoadScheduling()` runs after secrets recover (`LeafServices.cs:418`).
- [ ] **Step 2: Sync hook.** At `LeafServices.cs:394` change `afterSync: Bookings.CheckAsync` to a method `AfterSyncAsync(CancellationToken ct)` that awaits `Bookings.CheckAsync(ct)` then `Sunsama.SyncAsync(ct)`; doc comment says both run after each Google pass, so they follow `SyncLoop`'s pace. Match `CheckAsync`'s exact delegate type.
- [ ] **Step 3: Launch.** Add:

```csharp
/// <summary>
/// Opens a Sunsama task in the Sunsama app (<c>sunsama://action/details/&lt;id&gt;</c>), or Sunsama's home page in the browser
/// when no app handles <c>sunsama://</c> (spec 2026-10-16, Open in Sunsama). The link is built only by
/// <see cref="SunsamaLinks.TaskLink"/>, so <see cref="LinkSafety"/> doesn't apply. Never throws; false when nothing opened.
/// </summary>
public async Task<bool> LaunchSunsamaAsync(string taskId)
{
    if (SunsamaLinks.TaskLink(taskId) is not { } link)
    {
        Log.Info("sunsama.link.blocked");
        return false;
    }

    if (Options.FakeGoogle is not null)
    {
        return await OpenAsync(link);
    }

    try
    {
        return await Launcher.LaunchUriAsync(link, new LauncherOptions { FallbackUri = SunsamaLinks.Fallback });
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
    {
        Log.Error("sunsama.link.failed", ex);
        return false;
    }
}
```

(`OpenAsync` already writes to `launched.txt` under `--fake-google`; confirm by reading it.) Add `NotifySignInReturned() => SignInReturned?.Invoke(this, EventArgs.Empty);`.
- [ ] **Step 4: Settings to service.** Where `App.xaml.cs` applies settings after a change (find the handler that reacts to `LeafSettings` changes), set `services.Sunsama.Enabled = settings.SunsamaProjected || settings.SunsamaTracked;` and once at startup. Dispose `Sunsama` with the other services on exit.
- [ ] **Step 5: Checkpoint.** Build (0 warnings), full logic suite, then `pwsh tools/publish-aot.ps1` to confirm no trim/AOT warnings from the new Core code.

---

### Task 9: Calendar Grid — Read-Only Blocks, Channel, Tracked Overlay

**Files:**
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (`LoadAsync`, guards, `SunsamaShownFor`, data-changed hook)
- Modify: `src/LeafCalendar.App/Controls/DayColumn.cs:225` (time text + fill)
- Modify: `src/LeafCalendar.App/Controls/EventBlock.cs` (overlay, tooltip/name)
- Modify: `src/LeafCalendar.App/Controls/TimeGridView.cs:1018,1130` and `MonthGridView.cs` drag start (no drag)
- Modify: `src/LeafCalendar.App/Controls/EventContextMenu.cs` (Sunsama menu)
- Modify: `src/LeafCalendar.App/Styles/*` brush dictionary that holds `LeafBrushes` theme entries (HighContrast edge)

**Interfaces:**
- Consumes: `SunsamaOccurrences`, `SunsamaStore.Range`, `SunsamaShown`, `LeafServices.Sunsama`, `LeafServices.LaunchSunsamaAsync`.
- Produces: `CalendarViewModel.SunsamaShownFor(CalendarOccurrence o) -> SunsamaShown?` (null when Tracked time is off for the fill's purposes — see Step 3), `CalendarViewModel.IsSunsama(CalendarOccurrence o) -> bool`.

- [ ] **Step 1: Load.** In `LoadAsync` (`CalendarViewModel.cs:2212`), after `OccurrenceQuery.Load`, when `_services.Sunsama.State != SignedOut`: `var rows = SunsamaStore.Range(conn, from, to.AddDays(1));` (check whether `to` is inclusive in `EventWindowCache` and match it), `var (blocks, byKey) = SunsamaOccurrences.Build(rows);` store `byKey` entries into a `ConcurrentDictionary<string, SunsamaShown> _sunsamaShown` (field), and append `blocks` to the result only when `Settings.SunsamaProjected`. Capture `Settings` values before `Task.Run` like `includeDeclined`.
- [ ] **Step 2: Refresh.** Subscribe to `_services.Sunsama.DataChanged` and `Changed` where `AttachSync` subscribes Google sync (and unsubscribe in the same two places); handler: `_dispatcher.TryEnqueue(() => { _sunsamaShown.Clear(); ReloadCalendars(); })` (same as `OnSyncDataChanged`). When the sidebar switches change (Task 10), do the same. Report the visible days to the service wherever the view model computes the range on screen (search `VisibleRange`/the window title range): `_services.Sunsama.SetDays(first, lastExclusive)`.
- [ ] **Step 3: Lookups.** `public bool IsSunsama(CalendarOccurrence o) => SunsamaOccurrences.IsSunsama(o);` and `public SunsamaShown? SunsamaShownFor(CalendarOccurrence o) => _sunsamaShown.GetValueOrDefault(IsSunsama(o) ? o.Key : SunsamaOccurrences.MatchKey(o.AccountId, o.CalendarId, o.EventId));`.
- [ ] **Step 4: Guards (root cause in the view model, not per caller).** At the top of `Duplicate(...)`, the copy-to-clipboard method (search `CopyOf(`), `Cut`, `DeleteAsync` (filter), and `BeginEdit`/open-editor entry points: drop Sunsama occurrences (`if (IsSunsama(o)) { return; }` or `.Where(o => !IsSunsama(o))`). `ShowSelected` already returns early for an occurrence with no stored event; Task 11 adds the Sunsama branch before that early return. In `TimeGridView` at lines 1018 and 1130 and the `MonthGridView` drag start, return before starting a drag when `_vm.IsSunsama(occurrence)` (a read-only Google event becomes copy-only; a Sunsama block must not move at all). In `EventContextMenu`, when the single target is Sunsama, build a menu with one item, "Open in Sunsama" (`AutomationId` `MenuOpenInSunsama`), calling `LaunchSunsamaAsync(shown.Block.TaskId)`, and nothing else.
- [ ] **Step 5: Channel and fill on the card.** In `DayColumn.cs:225`:

```csharp
var shown = vm.SunsamaShownFor(b.Occurrence);
var time = TimeLabels.GridRange(b.Occurrence.Start, b.Occurrence.End, vm.Zone, vm.Settings.Use24HourTime);
if (vm.IsSunsama(b.Occurrence) && shown is { Block.Channel.Length: > 0 })
{
    time = $"{time} · {shown.Block.Channel}";
}

var fill = vm.Settings.SunsamaTracked ? shown?.Fill ?? 0 : 0;
card.Bind(b.Occurrence, palette, time, vm.IsSelected(b.Occurrence), compact: height < 36, _select, faded || shown?.Block.Completed == true, StripesFor(b.Occurrence, dark), fill, shown?.Block.TrackedMinutes);
```

- [ ] **Step 6: Overlay in `EventBlock`.** Add parameters `double trackedFill = 0, int? trackedMinutes = null` to `Bind`. Inside `inner` (the `Grid` holding `_accents` and `_text`), add a `Grid _trackedArea` as the **first** child (under the text) with two rows: `new RowDefinition { Height = new GridLength(fill, GridUnitType.Star) }` and `new RowDefinition { Height = new GridLength(1 - fill, GridUnitType.Star) }`, holding a `Rectangle _tracked` in row 0. Its brush is a `LinearGradientBrush` with `MappingMode = BrushMappingMode.Absolute`, `StartPoint = (0,0)`, `EndPoint = (6,6)`, `SpreadMethod = GradientSpreadMethod.Repeat`, and four stops making hard 3 px stripes: accent at 35% opacity (offset 0 and 0.5) and transparent (0.5 and 1). Collapse `_trackedArea` when `fill <= 0`. In a contrast theme (`LeafBrushes` already checks this for stripes; reuse that check) use no gradient: a 2 px `Border` bottom edge in `SystemColorHighlightColor` at the fill line instead. Re-apply on `ActualThemeChanged` as the card already does for its palette. The tooltip/name text (`_timeText` use) appends `$" · {SunsamaText.Format(trackedMinutes)} tracked"` when `trackedFill > 0`. Add `public static string Format(int minutes)` to `src/LeafCalendar.Core/Sunsama/SunsamaText.cs` ("1 h 24 m", "30 m", "2 h"), test-first with `[Theory]` cases 84 → "1 h 24 m", 30 → "30 m", 120 → "2 h" in `SunsamaTextTests`. The details panel (Task 11) uses it too.
- [ ] **Step 7: Check the stripe brush.** Run the app (`dotnet build` then launch Debug with `--profile uitest-manual --fake-google` is not needed; a signed-in owner profile is not to be touched — use the UI test in Task 13 or a throwaway `--profile`). If `GradientSpreadMethod.Repeat` doesn't repeat in WinUI 3, replace the rectangle's fill with a Composition `CompositionLinearGradientBrush` (`ExtendMode = CompositionGradientExtendMode.Wrap`) through `ElementCompositionPreview.SetElementChildVisual` on a `SpriteVisual` sized to the rectangle. Note which one shipped in the task report.
- [ ] **Step 8: Checkpoint.** Build, full logic suite, `XamlLintTests`.

---

### Task 10: Sidebar Sunsama Group

**Files:**
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (`CalendarGroups`, `SetCalendarHidden`, `AccountGroup`)
- Modify: `src/LeafCalendar.App/Views/SidebarView.xaml` (`CanReorderItems`/`CanDragItems` bound)
- Modify: `src/LeafCalendar.App/Views/SidebarView.xaml.cs` (no menu for Sunsama rows)

**Interfaces:**
- Consumes: `SunsamaOccurrences` constants, `LeafSettings.SunsamaProjected/SunsamaTracked`, `LeafServices.Sunsama.State`.
- Produces: `AccountGroup(..., bool canReorder = true)` with `public bool CanReorder { get; }`.

- [ ] **Step 1: Group.** In `CalendarGroups()` (`CalendarViewModel.cs:841`), after the Google groups, when `_services.Sunsama.State != SunsamaState.SignedOut` append:

```csharp
new AccountGroup(
    SunsamaOccurrences.AccountId,
    "Sunsama",
    [
        new CalendarRow(SunsamaRow(SunsamaOccurrences.ProjectedCalendarId, "Projected tasks", Settings.SunsamaProjected)),
        new CalendarRow(SunsamaRow(SunsamaOccurrences.TrackedCalendarId, "Tracked time", Settings.SunsamaTracked)),
    ],
    isExpanded: !Settings.CollapsedAccounts.Contains(SunsamaOccurrences.AccountId),
    canReorder: false)
```

with `private static CalendarInfo SunsamaRow(string id, string name, bool on) => new(SunsamaOccurrences.AccountId, id, name, SunsamaOccurrences.Color, "reader", false, false, null, LeafHidden: !on, null, 0);`. Make sure nothing that iterates `Calendars` (the stored list) sees these rows — they exist only in `CalendarGroups()`.
- [ ] **Step 2: Toggle.** At the top of `SetCalendarHidden` (`CalendarViewModel.cs:808`):

```csharp
// Sunsama's Two Rows Are Settings, Not Stored Calendars
if (calendar.AccountId == SunsamaOccurrences.AccountId)
{
    Remember(s => calendar.Id == SunsamaOccurrences.ProjectedCalendarId ? s with { SunsamaProjected = !hidden } : s with { SunsamaTracked = !hidden });
    _sunsamaShown.Clear();
    ReloadCalendars();
    return;
}
```

(`LeafSettings` uses `{ get; set; }` for these; if `with` isn't available on it, follow how `Remember` callers change bool settings elsewhere.) The `Enabled` update from Task 8 Step 4 follows from the settings change.
- [ ] **Step 3: No reorder, no menu.** `AccountGroup` gains `canReorder`; in `SidebarView.xaml` bind `CanReorderItems="{x:Bind CanReorder}"`, `CanDragItems="{x:Bind CanReorder}"`, `AllowDrop="{x:Bind CanReorder}"`. Where the row's `ContextFlyout` is set from `MenuFor(row)` (in `OnCalendarRowChanging`), skip it when `row.Info.AccountId == SunsamaOccurrences.AccountId`. The checkbox color paints with `Color` (fixed Tangerine) like other rows; the Tracked time row uses the same color.
- [ ] **Step 4: Checkpoint.** Build, full logic suite, `XamlLintTests`.

---

### Task 11: Details Panel

**Files:**
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (`ShowSelected`, `SelectedTask`, `SelectedTracked`)
- Modify: `src/LeafCalendar.App/Views/DetailsPanel.xaml` and `.xaml.cs`

**Interfaces:**
- Consumes: `SunsamaShownFor`, `SunsamaText.Format`, `LaunchSunsamaAsync`.
- Produces: `CalendarViewModel.SelectedTask` (`SunsamaShown?`, observable: a Sunsama block is selected), `CalendarViewModel.SelectedTracked` (`SunsamaShown?`, observable: the selected Google event matches a task and Tracked time is on), `CalendarViewModel.OpenSelectedInSunsama()`.

- [ ] **Step 1: Select.** At the start of `ShowSelected`'s `try`, before reading `EventStore`:

```csharp
// A Sunsama Block Has No Google Event Behind It: show the task instead
if (IsSunsama(occurrence))
{
    _selection.Clear();
    _selection.Add(occurrence);
    SelectedInfo = null;
    SelectedTask = SunsamaShownFor(occurrence);
    OnPropertyChanged(nameof(Selection));
    OccurrencesChanged?.Invoke(this, EventArgs.Empty);
    return;
}

SelectedTask = null;
```

and after `SelectedInfo = new SelectedEventInfo(...)`: `SelectedTracked = Settings.SunsamaTracked ? SunsamaShownFor(occurrence) : null;`. Clear both in `ClearSelection` and where `SelectedInfo = null` is set (lines 769, 2124).
- [ ] **Step 2: Panel.** Add a Sunsama section to `DetailsPanel.xaml` shown when `SelectedTask` is not null (bind visibility through a static helper like the panel's other sections): title (same style as the event title), the time (`WhenText`), "Channel" row with the channel, "{duration} planned" when `EstimateMinutes` is set, "{duration} tracked" when Tracked time is on and minutes > 0, and a "Open in Sunsama" `HyperlinkButton` (`AutomationId` `DetailsOpenInSunsama`). For a matched Google event, add under the existing event header a "{duration} tracked" line and the same Open in Sunsama button (`AutomationId` `DetailsTrackedOpenInSunsama`), shown when `SelectedTracked` is not null. AutomationIds: `DetailsSunsamaTitle`, `DetailsSunsamaChannel`, `DetailsSunsamaPlanned`, `DetailsSunsamaTracked`, `DetailsEventTracked`. Text is plain `TextBlock` text (never parsed). Spacing and styles copy the panel's existing rows.
- [ ] **Step 3: Checkpoint.** Build, full logic suite, `XamlLintTests`.

---

### Task 12: Settings › Accounts Sunsama Expander

**Files:**
- Create: `src/LeafCalendar.App/Views/Settings/SunsamaAccountCard.cs` (built in code like `ProviderAccountCard`) **or** XAML in `AccountsPage.xaml` + handlers in `AccountsPage.xaml.cs`; use XAML (the Google expanders right above it are XAML).
- Modify: `src/LeafCalendar.App/Views/Settings/AccountsPage.xaml` (after the Google account list, before "Main account")
- Modify: `src/LeafCalendar.App/Views/Settings/AccountsPage.xaml.cs`

**Interfaces:**
- Consumes: `LeafServices.Sunsama` (`State`, `Email`, `SignInAsync`, `SignOut`, `Changed`), `LeafServices.OpenSignInPageAsync`, `LeafServices.NotifySignInReturned`.

- [ ] **Step 1: XAML.**

```xml
<!-- Sunsama (owner, 2026-10-16: the button sits in the header, like the space on the Google account headers) -->
<InfoBar
    x:Name="SunsamaSuccess"
    Severity="Success"
    IsClosable="True"
    IsOpen="False"
    Message="Successfully signed in to Sunsama"
    AutomationProperties.LiveSetting="Polite"
    AutomationProperties.AutomationId="SunsamaSuccess" />
<Expander
    x:Name="SunsamaExpander"
    Style="{StaticResource LeafSettingExpanderStyle}"
    AutomationProperties.Name="Sunsama"
    AutomationProperties.AutomationId="SunsamaExpander">
    <Expander.Header>
        <controls:SettingRow
            x:Name="SunsamaHeader"
            Style="{StaticResource LeafExpanderHeaderRowStyle}"
            Glyph="&#xE823;"
            Header="Sunsama"
            Description="Not signed in"
            AutomationProperties.AutomationId="SunsamaStatus">
            <StackPanel Orientation="Horizontal" Spacing="8">
                <ProgressRing x:Name="SunsamaRing" Width="20" Height="20" IsActive="False" Visibility="Collapsed" AutomationProperties.Name="Finish signing in in your browser." />
                <Button x:Name="SunsamaCancel" Content="Cancel" Visibility="Collapsed" Click="OnSunsamaCancelClick" AutomationProperties.AutomationId="SunsamaCancelSignIn" />
                <Button x:Name="SunsamaSignOut" Content="Sign out" Visibility="Collapsed" Click="OnSunsamaSignOutClick" AutomationProperties.AutomationId="SunsamaSignOut" />
                <Button x:Name="SunsamaSignIn" Content="Sign in with Sunsama" Style="{StaticResource AccentButtonStyle}" Click="OnSunsamaSignInClick" AutomationProperties.AutomationId="SunsamaSignIn" />
            </StackPanel>
        </controls:SettingRow>
    </Expander.Header>
    <StackPanel>
        <InfoBar x:Name="SunsamaError" Severity="Error" IsClosable="False" IsOpen="False" Visibility="Collapsed" AutomationProperties.LiveSetting="Assertive" AutomationProperties.AutomationId="SunsamaError" />
        <controls:SettingRow
            Style="{StaticResource LeafExpanderItemRowStyle}"
            BorderThickness="0"
            Header="Permissions"
            Description="Leaf Calendar can only read your Sunsama tasks and calendar. It never changes anything in Sunsama." />
    </StackPanel>
</Expander>
<!-- /Sunsama -->
```

(Glyph `E823` is Segoe Fluent "Recent"; pick the closest stock glyph and say which in the report.) Buttons inside an `Expander.Header` receive clicks without toggling the expander in WinUI; verify in Step 4 and, if they toggle it, mark the click `e.Handled`-style by handling `Tapped` with `e.Handled = true` on the button panel.
- [ ] **Step 2: Code-behind.** `RefreshSunsama()` sets `SunsamaHeader.Description` (`Email` when signed in, else "Not signed in"), which buttons show (signed out or needs sign-in → Sign in; signing in → ring + Cancel; signed in → Sign out), and for `NeedsSignIn` shows `SunsamaError` with "Sunsama signed you out. Sign in again to see your task plan." Subscribe to `Sunsama.Changed` on `Loaded`, unsubscribe on `Unloaded`, marshal with `DispatcherQueue.TryEnqueue`. `OnSunsamaSignInClick` is `async void` with a try/catch mirroring `ProviderAccountCard.OnSignInClick` (`ProviderAccountCard.cs:296-350`), messages from spec section 9 (Task 0 Step 7), and on success `SunsamaSuccess.IsOpen = true` and `Services.NotifySignInReturned()`. `OnSunsamaSignOutClick` calls `SignOut()` and clears the success bar.
- [ ] **Step 3: Checkpoint.** Build, full logic suite, `XamlLintTests`.
- [ ] **Step 4: Manual check on a throwaway profile** (never `default`): launch Debug with `--profile uitest-sunsama-manual --fake-google <fake root>` is only possible once Task 13's fake exists; otherwise defer this check to Task 13's `SunsamaSettingsTests`.

---

### Task 13: Fake Sunsama and Affected UI Tests

**Files:**
- Modify: `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs` (route `/sunsama/`)
- Create: `tests/LeafCalendar.UITests/SunsamaGridTests.cs`, `tests/LeafCalendar.UITests/SunsamaSettingsTests.cs`
- Modify: `tests/LeafCalendar.UITests/SidebarTests.cs`, `tests/LeafCalendar.UITests/DetailsPanelTests.cs`

- [ ] **Step 1: Fake routes.** In the router (`FakeGoogleServer.cs:212`) add a `/sunsama/` branch before `/calcom/` to `RouteSunsamaAsync(method, target, authorization, body)`:
  - `GET /sunsama/oauth/authorize` → 302 to `redirect_uri?code=sunsama-code&state=<state>` (like Cal.com).
  - `POST /sunsama/oauth/register` → 201 `{"client_id":"sunsama-client-uitest"}`.
  - `POST /sunsama/oauth/token` → `SunsamaRefuseToken ? 400 invalid_grant : 200 {"access_token":"sunsama_access_uitest","refresh_token":"sunsama_refresh_uitest","expires_in":3600}`.
  - `POST /sunsama/mcp` with bearer `sunsama_access_uitest` (else 401): JSON-RPC by `method`: `initialize` → result + `Mcp-Session-Id: uitest` header (extend the route tuple or set the header in the writer the way `location` is passed; read how the server writes headers first), `notifications/initialized` → 202, `resources/read` → `contents[0].text` from `SunsamaMe` / `SunsamaTasks[day]` / `SunsamaEvents[day]` (public properties holding JSON strings, defaulting to `{"tasks":[]}` / `{"events":[]}`).
  - Constants `SunsamaRefreshToken = "sunsama_refresh_uitest"`, and a helper `SeedSunsamaDay(DateOnly day, string tasksJson, string eventsJson)`.
- [ ] **Step 2: Signed-in profile helper** in `SunsamaGridTests`: like `SchedulingProviderTests.SignedInProfile` — `SeededProfile.Create(settings)` then `new ProtectedFileTokenStore(LeafApp.ProfileFolder(profile))` `.SetProviderSecret("sunsama", "client_id", "sunsama-client-uitest")`, `refresh_token`, `email` (`uitest@example.com`).
- [ ] **Step 3: Tests** (each a full FlaUI test in the style of `SchedulingProviderTests`; launch with `--fake-google {_google.BaseUri} --start-date 2026-10-16 --now 2026-10-16T08:00:00-05:00` and seed one projected task 10:50–11:30 with channel "Illuminate Digital" and 84 tracked minutes of a 2-hour estimate split as in the spec example, one timeboxed task on `sunsama-cal`, and one task imported from a seeded Google event):
  - `SunsamaGridTests.ProjectedBlock_ShowsWithChannelAndTracked` — a block whose name contains the title, "Illuminate Digital" and "tracked".
  - `SunsamaGridTests.MatchedTask_DrawsNoSecondBlock` — only the Google event's block exists at that time.
  - `SunsamaGridTests.Drag_SunsamaBlock_DoesNothing` — drag the block an hour down (`DragTests` helpers); no new event, the block's time unchanged, the fake Google received no write.
  - `SunsamaGridTests.RightClick_SunsamaBlock_OnlyOpenInSunsama` — the menu has exactly `MenuOpenInSunsama`; invoking it appends `sunsama://action/details/<id>` to `launched.txt`.
  - `SidebarTests.Sunsama_TogglesHideBlocksAndTracked` — `CalendarToggle_projected` off hides the block; on again shows it; `CalendarToggle_tracked` off removes "tracked" from the block name.
  - `DetailsPanelTests.SunsamaBlock_ShowsTaskDetails` — click the block: `DetailsSunsamaTitle`, `DetailsSunsamaChannel`, `DetailsSunsamaPlanned` ("2 h planned"), `DetailsSunsamaTracked` ("1 h 24 m tracked"), `DetailsOpenInSunsama`.
  - `DetailsPanelTests.MatchedGoogleEvent_ShowsTracked` — click the Google event: `DetailsEventTracked`.
  - `SunsamaSettingsTests.SignIn_FromHeader_DoesNotExpand` — not signed in; `SunsamaSignIn` invoke; `SunsamaSuccess` shows; `SunsamaStatus` shows the email; `SunsamaExpander` still collapsed.
  - `SunsamaSettingsTests.SignOut_RemovesGroupAndBlocks` — signed in; `SunsamaSignOut`; status "Not signed in"; `AccountHeader_sunsama` gone from the sidebar.
  - `SunsamaSettingsTests.RefusedRefresh_ShowsSignedOutReason` — `SunsamaRefuseToken = true`; the error line shows; the Sign in button shows.
  - `SunsamaSettingsTests.Expand_ShowsPermissions`.
- [ ] **Step 4: Install and run only these classes, one at a time:**

```bash
pwsh tools/sign-install.ps1
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SunsamaGridTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SunsamaSettingsTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SidebarTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*DetailsPanelTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*DragTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SchedulingProviderTests"
```

(`DragTests` and `SchedulingProviderTests` because the drag guard and the shared OAuth code changed.) Expected: all pass. Fix and re-install on failure.

---

### Task 14: User Guide, Screenshots, Privacy and Spec Sync

**Files:**
- Modify: `tests/LeafCalendar.UITests/ScreenshotTour.cs` (new `Capture_Sunsama`)
- Create: `site/calendar/sunsama.md`
- Modify: `site/calendar/sidebar.md`, `site/settings.md`, `site/index.md` (only if it lists features)
- Create: `site/assets/images/sunsama-expander.png`, `-dark.png`, and the same pairs for `sunsama-sidebar`, `sunsama-week`, `sunsama-details`, `sunsama-matched`
- Modify: `PRIVACY.md`
- Modify: `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (sections 2, 4, 5: one paragraph each pointing to the Sunsama spec)

- [ ] **Step 1: Tour.** Add `Capture_Sunsama` modeled on `Capture_Scheduling` (`ScreenshotTour.cs:157`): for light and dark, a signed-in Sunsama profile with the Task 13 seed; capture Settings › Accounts with the expander open (`sunsama-expander-{theme}.png`), the main window at 1366 × 768 in Week view (`sunsama-week-{theme}.png`), a crop or full shot of the sidebar group (`sunsama-sidebar-{theme}.png`), the details panel with a projected block selected (`sunsama-details-{theme}.png`) and with the matched Google event selected (`sunsama-matched-{theme}.png`).
- [ ] **Step 2: Run it.** `LEAF_SCREENSHOTS=<scratch folder>` then `dotnet test ... --filter-class "*ScreenshotTour" --filter-method "*Capture_Sunsama"`. Review every shot (blocks outlined vs solid, stripes readable, channel on the time line, both themes). Copy the light ones to `site/assets/images/sunsama-<name>.png` and the dark ones to `sunsama-<name>-dark.png` (the existing pairs' naming).
- [ ] **Step 3: Guide page** `site/calendar/sunsama.md`, front matter `title: Sunsama tasks`, `parent: Your calendar`, `nav_order: 4`, written in the voice of `site/people-and-scheduling/cal-com.md`, with the sections from spec 8.3 and each screenshot placed under the step it shows (`![…](../../assets/images/sunsama-expander.png)`). Draft text, then list every sentence in the task report for the owner's approval (spec 8.3: guide text needs approval).
- [ ] **Step 4: Other pages.** `sidebar.md`: a short "Sunsama" subsection (the two switches) with `sunsama-sidebar.png`. `settings.md`: the Sunsama expander under Accounts with `sunsama-expander.png`. `PRIVACY.md`: a "Sunsama" section — what's read (tasks and calendar events for the days on screen, your Sunsama email and time zone), where it's kept (`leaf.db`, and the sign-in in `secrets.bin`, encrypted for your Windows account), that nothing is sent back, and that Sign out removes both.
- [ ] **Step 5: Main spec.** Add the pointers in `2026-09-29-leaf-calendar-design.md`.
- [ ] **Step 6: Checkpoint.** `bundle exec jekyll build` is not required locally; check links by path and that every image file referenced exists.

---

### Task 15: Final Verification and Install

- [ ] **Step 1:** `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.
- [ ] **Step 2:** Full logic suite → all pass; note the count.
- [ ] **Step 3:** `pwsh tools/publish-aot.ps1` → no trim/AOT warnings; `pwsh tools/check-package.ps1` → within budget.
- [ ] **Step 4:** `pwsh tools/sign-install.ps1` (updates the owner's install; keeps settings and sign-ins).
- [ ] **Step 5:** Re-run the Task 13 classes once on this optimized install.
- [ ] **Step 6: Real-data checks the spec lists (section 3),** done by the owner or with the owner present, on the owner's own profile only with their go-ahead: sign in to Sunsama for real (confirms registration with the loopback redirect works), check a timeboxed task's block, check Open in Sunsama opens the app.
- [ ] **Step 7: Report:** tests run and results, which stripe brush shipped, the glyph picked, every new user-visible string (UI and guide) for approval, and anything not verified.
