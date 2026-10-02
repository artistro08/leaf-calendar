# Leaf Calendar Milestone 5 (Power Features) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This plan runs as **three parallel tracks** after one shared foundation task. Read "Tracks, Branches, and Merge Points" before starting.

**Goal:** Build everything in spec Section 12 item 5, plus its deferrals from Milestones 2, 3, and 4:
- the command menu (Ctrl+K, Ctrl+F, `/`, and a search icon in the title bar over the sidebar) that searches events and runs every app action, with a title-bar Back button after a jump
- the people overlay (P), Meet with (F), and E then F (overlay the selected event's guests)
- share availability (S, the sidebar button, and the command menu), copied as text in a chosen time zone
- the in-app shortcut set (Z, S, P, F, ?, Ctrl+K, Ctrl+F, `/`, Ctrl+,, E then Z, E then F) and the `?` cheat sheet
- the full Section 9 settings: map provider, upcoming lookahead, interface scale, all-day default, working hours, main account, Meet by default per account, primary time zone and the zone-change prompt, tray calendars, the cheat sheet link, log folder, and third-party licenses
- interface scale, working-hours shading, calendar rename, "show upcoming events for this calendar", time travel (Z)
- guest autocomplete from the Workspace directory, people you meet often, and rooms; event type (Focus time, Out of office); editing Busy/Free and Public/Private; the event's own time zone (and E then Z); Bing Maps

**Architecture:** Core does the deciding, each piece with unit tests:
- `EventSearch`, `CommandCatalog`, and `DateQuery` (command menu)
- `ShortcutCatalog` (cheat sheet)
- `DisplayZone` (time travel and the primary zone)
- `BusyMath`, `AvailabilityText`, and `FreeBusyLookup` (overlay, Meet with, share availability)
- `WorkingHoursMath`
- `FrequentPeople`, `Rooms`, and a directory source in `ContactSearch`
- `EditorTimes`, `CalendarEdits`, and new `EventJson` fields

The App adds thin WinUI on top. A foundation task first lays down every shared contract: settings fields, commands and key bindings, Google client calls, fake-Google routes, and empty partial-class hooks. After it, the three tracks own disjoint files and never edit the same file.

**Tech Stack:** .NET 10 / C# 14, Windows App SDK 2.5.1 (WinUI 3, packaged MSIX, Release Native AOT), CommunityToolkit.Mvvm 8.4.2, CsWin32 0.3.335, Microsoft.Data.Sqlite 10.0.12, Meziantou.Framework.Scheduling [4.1.3], xUnit v3 on Microsoft.Testing.Platform, Microsoft.Extensions.TimeProvider.Testing 10.10.0, FlaUI.UIA3 5.0.0. **No new packages.**

**Spec:** `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`. Read:
- Sections 2, 3.2, 4.5, and 4.6
- Section 6, all of it (6.2 title bar, 6.3 sidebar, 6.4 calendar area, 6.6 command menu, 6.7 settings)
- Section 7, all of it (7.2 editor fields, 7.5 people, 7.6 share availability)
- Sections 8.7 (in-app shortcuts) and 9 (settings), all of them
- Sections 10, 11, and 12 item 5

The design standard `docs/design-standard.md` is binding for every screen. Read Sections 2, 5 to 8, and 10 to 14.

For patterns, read:
- this plan's predecessor format, `docs/superpowers/plans/2026-09-30-m3-polish-2.md`
- Milestone 4's plan, `git show m4-tray-alerts:docs/superpowers/plans/2026-09-30-m4-tray-alerts.md`, for its Settings pages (Task 16) and milestone close (Task 17)

## Owner Rulings (Defaults Chosen for Speed)

The owner asked for all of Milestone 5, fast. Where the spec leaves a choice open, this plan picks the default below. Task 14 records each one in the spec. Change any of them before a track starts, and only that track is affected.

- **Working hours:** Google's API doesn't expose them, so Leaf keeps its own. They're on by default, 9 AM to 5 PM, Monday to Friday, set in Settings › General. Shading uses the wall clock of the zone on screen.
- **Interface scale:** Settings › General › Appearance offers 80, 90, 100, 110, 125, and 150%, and the command menu has the same choices. It scales the sidebar, the calendar, and the details panel. The title bar stays at 48 DIPs. **Ctrl+= / Ctrl+- / Ctrl+0 and Ctrl+wheel keep changing grid density** (owner's Milestone 3 polish choice).
- **Appearance settings** stay in the existing "Appearance" group on Settings › General. No new page.
- **Rooms:** the rooms you've booked before, read from your synced events. Listing every room in a Workspace needs an admin-only scope. Rooms show only for Workspace accounts.
- **Workspace accounts** are recognized by Google's `hd` sign-in claim. It's stored per account, and fetched once for accounts that signed in before Milestone 5. It gates rooms and event type.
- **People you meet often:** guests from your own events in the last 180 days and the next 30 days, most frequent first. Computed locally and never stored.
- **Event type** is chosen only when creating an event, because Google can't change an event's type later. It's offered only on a Workspace account's primary calendar, timed events only, without guests, location, or Meet.
- **Calendar rename and default reminders** write to Google right away and need a connection. Offline, Leaf says so and changes nothing. These edits aren't queued.
- **Share availability** uses Google free/busy (spec decision log), so it needs a connection. The zone choices are the zone on screen, Windows' zone, and your extra zone columns.
- **Primary time zone:** "Use Windows time zone" (the default, today's behavior) or a fixed zone. With a fixed zone, when Windows' zone changes and the prompt setting is on (default on), an info bar offers to switch.
- **Time travel (Z):** only for this session. A bar says which zone is on screen and offers "Return". New events made while traveling take the travel zone as their own zone.
- **Show upcoming events for a calendar:** the next 30 days (at most 50 events) in the details panel, with "Show all calendars" to go back.
- **Calendar right-click menu:** Rename…, Show upcoming events, and Change color…. Change color opens Settings › Calendars, where colors moved in Milestone 3.
- **Search icon:** in the title bar row over the sidebar, a bare 32 DIP icon button like the details panel's Edit and Delete icons. It's centered over the mini month's "Next month" button. With the sidebar closed, it sits 8 DIPs after the app title.
- **Back button:** the title bar's stock back button shows only after a command-menu jump, and hides once you go back or navigate elsewhere (spec 6.2 item 2). Alt+Left keeps working everywhere.
- **Cheat sheet:** lists spec 8.7 verbatim, except Shift+drag box select, which waits for Milestone 6 (a sheet shouldn't list a key that does nothing).
- **Bing Maps** uses `https://www.bing.com/maps?q=<location>`. The details button reads "Open in Bing Maps".
- **Teammate details:** when a person's calendar is shared with you with details, the overlay shows their event titles as plain text. Otherwise it shows busy blocks only.

## Tracks, Branches, and Merge Points

```
main (with M4 merged) ──► m5-power-features ──[Task 1, serial]──► M0
                                   │
          ┌────────────────────────┼────────────────────────┐
     m5-track-a               m5-track-b               m5-track-c
     Tasks 2→3→4→5            Tasks 6→7→8→9            Tasks 10→11→12→13
          └──────── M1: merge A, then B, then C into m5-power-features ────────┘
                                   │
                              Task 14 (serial): batch UI tests Debug + AOT, memory ×3,
                              screenshots, security review, spec updates, reinstall, merge to main
```

**Before Task 1:** Milestone 4 must be on `main`. If `git log main..m4-tray-alerts` still lists commits, stop and tell the owner. Don't start Milestone 5 on an unmerged Milestone 4.
```bash
git checkout main
git checkout -b m5-power-features
```

**M0, after Task 1 commits:**
```bash
git worktree add ../leaf-m5-a -b m5-track-a m5-power-features
git worktree add ../leaf-m5-b -b m5-track-b m5-power-features
git worktree add ../leaf-m5-c -b m5-track-c m5-power-features
```
Each track's implementer works only in its own worktree, and runs its tasks in order.

**M1, after a track's last task:** from the main checkout (`D:\leaf-calendar`, on `m5-power-features`):
```bash
git merge --no-ff m5-track-a -m "Merge M5 track A: command menu, cheat sheet, zones, and scale"
dotnet build LeafCalendar.slnx -c Debug
dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj
```
Merge B and then C the same way, as each finishes. Tracks can finish in any order, but merge one at a time, building and running the unit tests after each. With the ownership table below, a merge conflict means a track edited a file it doesn't own. Resolve it in favor of the owner, then tell the owner.

**UI tests in the tracks:** the packaged app has one identity per machine, so only one build can be registered at a time. Tracks **write** their UI tests and make them compile (`dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`). They **never** run `tools/dev-register.ps1`, `tools/publish-aot.ps1`, or the UI suite. Task 14 runs every UI test in one batch on the merged branch, Debug and then AOT. A UI test may assert behavior from another track, because it runs only after all three merge. Unit tests run in every task.

## File Ownership

After Task 1, a file belongs to exactly one track. Only its owner edits it. "New" files are created by the owner.

| File | Owner |
|---|---|
| `Core/Settings/LeafSettings.cs`, `Core/Views/ShortcutMap.cs`, `Core/Views/KeySequence.cs`, `Core/Google/GoogleModels.cs`, `Core/Google/GoogleJsonContext.cs`, `Core/Google/GoogleCalendarClient.cs`, `App/Controls/ScaleHost.cs` (new), `App/Views/CalendarPage.xaml`, `App/Views/CalendarPage.xaml.cs`, `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`, `tests/LeafCalendar.UITests/Support/LeafApp.cs`, `tests/LeafCalendar.UITests/Fixtures/*` | **Task 1 only**, then frozen until Task 14 |
| `Core/Search/*` (new), `Core/Views/ShortcutCatalog.cs` (new), `Core/Views/DisplayZone.cs` (new), `Core/Tray/TrayAgenda.cs`, `App/MainWindow.xaml(.cs)`, `App/App.xaml.cs`, `App/LeafServices.cs`, `App/Views/CalendarPage.Navigate.cs`, `App/ViewModels/CalendarViewModel.Zones.cs`, `App/ViewModels/CalendarViewModel.Search.cs` (new), `App/Views/CommandMenu.xaml(.cs)` (new), `App/Views/ShortcutSheet.cs` (new), `App/Views/TimeTravelBar.xaml(.cs)` (new), `App/Views/Settings/ShortcutsPage.xaml(.cs)`, `App/Views/Settings/AboutPage.xaml(.cs)`, `App/Views/Settings/TrayPage.xaml(.cs)`, `App/Views/Settings/TimeZonesPage.xaml(.cs)`, `App/Assets/ThirdPartyNotices.txt` (new), `App/LeafCalendar.App.csproj` | **Track A** (Tasks 2–5) |
| `Core/People/BusyMath.cs`, `Core/People/AvailabilityText.cs`, `Core/People/FreeBusyLookup.cs`, `Core/Views/WorkingHoursMath.cs` (all new), `App/Views/CalendarPage.People.cs`, `App/ViewModels/CalendarViewModel.People.cs` (new), `App/Views/PeoplePickerDialog.cs`, `App/Views/OverlayBar.xaml(.cs)`, `App/Views/ShareBar.xaml(.cs)` (all new), `App/Controls/TimeGridView.cs`, `App/Controls/DayColumn.cs`, `App/Controls/LeafBrushes.cs` | **Track B** (Tasks 6–9) |
| `Core/People/ContactSearch.cs`, `Core/People/FrequentPeople.cs` (new), `Core/People/Rooms.cs` (new), `Core/Auth/GoogleOAuthClient.cs`, `Core/Auth/SignInFlow.cs`, `Core/Data/*`, `Core/Hosting/GoogleServices.cs`, `Core/Editing/*` (`CalendarEdits.cs` and `EditorTimes.cs` new), `Core/Events/LinkSafety.cs`, `App/ViewModels/CalendarViewModel.cs` (the main file), `App/ViewModels/EventEditorViewModel.cs`, `App/ViewModels/AccountsViewModel.cs`, `App/Views/CalendarPage.Extras.cs`, `App/Views/EventEditorView.xaml(.cs)`, `App/Views/DetailsPanel.xaml(.cs)`, `App/Views/SidebarView.xaml(.cs)`, `App/Views/RenameCalendarDialog.cs` (new), `App/Views/Settings/GeneralPage.xaml(.cs)`, `App/Views/Settings/AccountsPage.xaml(.cs)`, `App/Views/Settings/CalendarsPage.xaml(.cs)`, `App/Views/Settings/SettingsWindow.xaml(.cs)` | **Track C** (Tasks 10–13) |

New test files belong to the task that creates them. Existing unit-test files belong to the owner of the Core file they test. `LeafApp.cs` is frozen after Task 1, so a track that needs a UI-test helper writes it as a private method in its own test class.

## Global Constraints

Every Milestone 3 and 4 constraint still holds:

- `net10.0-windows10.0.22621.0`, min `10.0.22000.0`; Windows App SDK 2.5.1, packaged MSIX.
- Release-only Native AOT with **0 IL warnings**. `TreatWarningsAsErrors` with `AnalysisLevel` `latest-recommended` and NuGet audit on.
- No Google client libraries, no Newtonsoft, no Entity Framework, no WebView2, no Ical.Net/EWSoftware.PDI. `Meziantou.Framework.Scheduling` pinned `[4.1.3]`. **No new packages.**
- **JSON:** `System.Text.Json` source generation only (`GoogleJsonContext`, `LeafJsonContext`). Read and build with `JsonDocument` / `JsonNode`. Never call a serializer overload without `JsonTypeInfo`.
- **XAML:** `x:Bind` only (including function bindings). **No `{Binding}`, no `DisplayMemberPath`.** WinUI classes that implement WinRT interfaces must be `partial`.
- **Native AOT read-back rule:** never read an object back through a typed cast of a WinRT property (`(TextBlock)button.Content`, `x.Tag is Foo`, `(Style)Resources[...]`), and no `is` / `as` on visual-tree objects. Keep your own references in fields, records, or closures. Compare items read back from WinRT by reference (`ReferenceEquals`), as `SettingsWindow.OnSelectionChanged` does.
- **Native AOT list rule:** never hand a list of Core types to a WinRT `ItemsSource`. Wrap each item in an App-project class first (as `UpcomingItem` and `ContactSuggestion` do).
- **Secrets:** OAuth secrets and refresh tokens live only in Credential Locker; access tokens only in memory.
- **Logs:** internal IDs and counts only; `AppLog.Redact` is the safety net. Never log:
  - event titles, descriptions, locations, or guest emails
  - calendar names
  - contact, directory, room, or teammate names or emails
  - **command-menu search text**
  - **availability text**
  - **free/busy addresses**

  Free/busy logs carry `account=`, `count=`, `unknown=`, and `status=` only.
- **Untrusted text:** all of these are plain text, cleaned of control and format characters and capped in length before display:
  - event content
  - People API and directory results
  - teammates' event titles from free/busy details
  - room names
  - calendar names typed by you or set by Google
- **LinkSafety:** every launch goes through `LeafServices.LaunchAsync`, which re-checks `LinkSafety`. Never set `NavigateUri` from event, contact, or calendar content. Fixed addresses (Bing, Google Maps, Google booking pages, GitHub) are built in Core (`LinkSafety`) and still launched through `LaunchAsync`. Opening the log folder goes through a new `LeafServices.OpenFolderAsync`. It's the only folder launch, and in fake-Google mode it writes `folder:<path>` to `launched.txt` instead.
- **Contacts privacy:** suggestions, directory results, frequent people, rooms, and free/busy results live in memory only, for one search or one overlay. They're never written to SQLite, settings, logs, or files.
- **Fake Google:** only on `uitest-*` profiles, loopback only (spec 4.8). Every new fake endpoint lives in `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs` (Task 1).
- **US English** everywhere, sentence case in the UI, English-only formatting (`CultureInfo.GetCultureInfo("en-US")`). Google's own value `"cancelled"` stays as Google spells it.
- **Tests:**
  - Run with Microsoft.Testing.Platform **from the repo root**: `dotnet test --project <csproj> [--filter-class "*Name"] [--filter-method "*Name*"] [--output Detailed]`.
  - Async calls pass `TestContext.Current.CancellationToken`.
  - Test classes are `public`, and a class with a disposable field implements `IDisposable`.
  - UI tests: see "UI tests in the tracks" above. Task 14 runs them with `pwsh tools/dev-register.ps1` first and an unlocked desktop. `Access is denied` means the desktop is locked: report it, don't work around it.
- **AOT UI check:** in Task 14, the whole UI suite runs against the AOT build (`pwsh tools/publish-aot.ps1 -Register`, 0 IL warnings).
- **Memory budget:** tray-only Release AOT, private bytes ≤ **120 MB**, working set ≤ **25 MB** (`tests/LeafCalendar.UITests/memory-budget.json`). Re-measure three times in Task 14. If it's over, stop and report to the owner; the budget is the owner's call.
- **Design standard:**
  - spacing 2/4/8/12/16/24/32
  - radii 4 and 8
  - theme brushes only in XAML, per-theme `LeafBrushes` in code, with `HighContrast` entries for any override
  - 83 ms hover fades
  - `ScrollIndicator.ShowOnHover` on every new `ScrollViewer`
  - tooltips with the shortcut in parentheses ("Search (Ctrl+K)")
  - `AutomationProperties.Name` on icon-only controls
  - dialogs with verb buttons
  - nothing clipped at the 1086 × 540 minimum window
- **Code style (owner):**
  - 4-space indent, with all XAML indented
  - a Title Case block comment above each logical block, and closing comments on long XAML blocks (`<!-- /Command Menu -->`)
  - XML docs on public types and members
  - guard clauses first
  - aligned `=` in related assignment groups
  - `// ====` section banners in long classes
- **Branches and commits:**
  - Task 1 commits on `m5-power-features`. Each track commits once per task on its own branch.
  - Task 14 merges to `main` and pushes. The owner authorized merge and push at each milestone's end.
  - Never open, comment on, or change a PR.

## Review Focus

1. **Search text that looks like SQL or markup.** A query with `%`, `_`, `\`, quotes, emoji, a right-to-left override (`‮`), or 5,000 characters must match literally, be cleaned and capped, and never throw or match everything. Tests: `EventSearchTests.Find_PercentAndUnderscore_MatchLiterally` and `EventSearchTests.Find_HostileQuery_IsCleanedAndCapped` (Task 2).
2. **A person Google won't give free/busy for.** This covers an address outside the domain, a typo, or someone who doesn't share. Google answers with a per-calendar `notFound` error and no busy list. Leaf must show "No free/busy info" for that person, never an empty (all-free) calendar. Tests: `FreeBusyLookupTests.Lookup_NotFound_IsUnknownNotFree` (Task 6) and `PeopleOverlayTests.UnknownPerson_SaysNoInfo` (Task 7).
3. **Availability that crosses midnight or a DST change in the chosen zone.** A slot from 10 AM to 12 PM Eastern is 11 PM to 1 AM in Tokyo, which spans two dates. Each date gets its own line, and a DST day prints the times as that zone's clock shows them. Tests: `AvailabilityTextTests.Format_CrossesMidnightInZone_SplitsDays` and `AvailabilityTextTests.Format_DstFallBack_UsesTheWallClock` (Task 6), and `ShareAvailabilityTests.CopyInTokyoTime_SplitsAtMidnight` (Task 8).
4. **150% interface scale at the minimum window.** With bigger panes and the same title bar, the toolbar, the period title, the panes, and the editor's footer must not overlap or clip, and the minimum window size grows to fit. Test: `ZoneAndScaleTests.Scale150_AtMinimumWindow_NothingOverlaps` (Task 5).
5. **Renaming a calendar to blank, to hostile text, or offline.**
   - A blank name resets to Google's name (`summaryOverride: null`).
   - Control and bidi characters are stripped and the name is capped at 100.
   - Offline, the rename is refused with a message, and the name on screen stays.

   Tests: `CalendarEditsTests.CleanName_BlankOrHidden_*` (Task 12) and `CalendarManageTests.Rename_Offline_KeepsTheName` (Task 12).

---

## File Structure

```
src/LeafCalendar.Core/
    Settings/LeafSettings.cs            ~ T1: new fields, MapProvider, WorkingHours, Normalize
    Views/ShortcutMap.cs, KeySequence.cs ~ T1: new commands and keys
    Google/GoogleModels.cs              ~ T1: FreeBusy*, BusyRange, FreeBusyResult, DirectorySearchResponse, GoogleUserInfo.Hd
    Google/GoogleJsonContext.cs         ~ T1
    Google/GoogleCalendarClient.cs      ~ T1: QueryFreeBusyAsync, PatchCalendarListAsync, ListEventsInRangeAsync
    Search/EventSearch.cs               NEW T2
    Search/CommandCatalog.cs            NEW T2
    Search/DateQuery.cs                 NEW T2
    Views/ShortcutCatalog.cs            NEW T4
    Tray/TrayAgenda.cs                  ~ T4: excluded calendars
    Views/DisplayZone.cs                NEW T5
    People/BusyMath.cs                  NEW T6
    People/AvailabilityText.cs          NEW T6
    People/FreeBusyLookup.cs            NEW T6
    Views/WorkingHoursMath.cs           NEW T9
    Data/Schema.cs, LeafDatabase.cs, AccountStore.cs  ~ T10: hosted_domain
    Auth/GoogleOAuthClient.cs, SignInFlow.cs          ~ T10
    Hosting/GoogleServices.cs           ~ T10: RefreshHostedDomainsAsync
    People/ContactSearch.cs             ~ T10: directory source
    People/FrequentPeople.cs, Rooms.cs  NEW T10
    Editing/EventDraft.cs, EventJson.cs ~ T10 (resource guests), T11 (type, show as, visibility, zone)
    Editing/EditorTimes.cs              NEW T11
    Events/LinkSafety.cs                ~ T1 (BookingPages), T11 (MapsSearch provider)
    Editing/CalendarEdits.cs            NEW T12
    Data/CalendarStore.cs               ~ T12
src/LeafCalendar.App/
    Controls/ScaleHost.cs               NEW T1
    Views/CalendarPage.xaml(.cs)        ~ T1 only: IslandBars, ScaleHost, command cases, hooks, pane-width properties
    Views/CalendarPage.Navigate.cs      NEW T1 stub → A
    Views/CalendarPage.People.cs        NEW T1 stub → B
    Views/CalendarPage.Extras.cs        NEW T1 stub → C
    ViewModels/CalendarViewModel.Zones.cs  NEW T1 (moved code) → A
    Views/SidebarView.xaml(.cs)         ~ T1 (BodyScale, share button) → C
    Views/DetailsPanel.xaml             ~ T1 (BodyScale) → C
    Views/CommandMenu.xaml(.cs)         NEW T3
    ViewModels/CalendarViewModel.Search.cs NEW T3
    MainWindow.xaml(.cs)                ~ T3 (search icon, back), T5 (minimum width with scale)
    Views/ShortcutSheet.cs              NEW T4
    Views/Settings/ShortcutsPage, AboutPage, TrayPage  ~ T4
    LeafServices.cs, App.xaml.cs        ~ T4
    Assets/ThirdPartyNotices.txt        NEW T4
    Views/TimeTravelBar.xaml(.cs)       NEW T5
    Views/Settings/TimeZonesPage        ~ T5
    ViewModels/CalendarViewModel.People.cs NEW T7
    Views/PeoplePickerDialog.cs, OverlayBar.xaml(.cs)  NEW T7
    Controls/TimeGridView.cs, DayColumn.cs, LeafBrushes.cs  ~ T7, T8, T9
    Views/ShareBar.xaml(.cs)            NEW T8
    ViewModels/EventEditorViewModel.cs, Views/EventEditorView.xaml(.cs)  ~ T10, T11
    ViewModels/CalendarViewModel.cs     ~ T10–T13
    Views/DetailsPanel.xaml(.cs)        ~ T11 (maps label), T12 (upcoming for a calendar)
    Views/SidebarView.xaml(.cs)         ~ T12 (right-click menu)
    Views/RenameCalendarDialog.cs       NEW T12
    Views/Settings/CalendarsPage        ~ T12
    Views/Settings/GeneralPage, AccountsPage, ViewModels/AccountsViewModel.cs  ~ T13
tests/LeafCalendar.Tests/                (new: PowerSettingsTests, EventSearchTests, CommandCatalogTests, DateQueryTests,
                                          ShortcutCatalogTests, DisplayZoneTests, BusyMathTests, AvailabilityTextTests,
                                          FreeBusyLookupTests, WorkingHoursMathTests, FrequentPeopleTests, RoomsTests,
                                          EditorTimesTests, CalendarEditsTests; changed: ShortcutMapTests, KeySequenceTests,
                                          GoogleCalendarClientTests, TrayAgendaTests, ContactSearchTests, EventJsonTests,
                                          LinkSafetyTests, AccountStoreTests, LeafDatabaseTests, CalendarStoreTests)
tests/LeafCalendar.UITests/              (Support/FakeGoogleServer.cs and Fixtures in T1; new classes: CommandMenuTests T3,
                                          ShortcutSheetTests T4, TraySettingsCalendarsTests T4, ZoneAndScaleTests T5,
                                          PeopleOverlayTests T7, ShareAvailabilityTests T8, WorkingHoursTests T9,
                                          GuestDirectoryTests T10, EditorExtrasTests T11, CalendarManageTests T12,
                                          SettingsPagesTests T13)
```

(`LinkSafety.BookingPages` is added by Task 1, so Track B can use it without touching Track C's `LinkSafety.cs` afterward.)

---

### Task 1: Foundation (Shared Contracts, Serial)

Everything two tracks would otherwise both edit. Nothing here changes behavior a user can see, except the sidebar's new share icon, which does nothing until Task 8.

**Files:**
- Modify: `src/LeafCalendar.Core/Settings/LeafSettings.cs`
- Modify: `src/LeafCalendar.Core/Views/ShortcutMap.cs`, `src/LeafCalendar.Core/Views/KeySequence.cs`
- Modify: `src/LeafCalendar.Core/Google/GoogleModels.cs`, `GoogleJsonContext.cs`, `GoogleCalendarClient.cs`
- Modify: `src/LeafCalendar.Core/Events/LinkSafety.cs` (add `BookingPages` only)
- Create: `src/LeafCalendar.App/Controls/ScaleHost.cs`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml`, `CalendarPage.xaml.cs`
- Create: `src/LeafCalendar.App/Views/CalendarPage.Navigate.cs`, `CalendarPage.People.cs`, `CalendarPage.Extras.cs`
- Create: `src/LeafCalendar.App/ViewModels/CalendarViewModel.Zones.cs` (code moved out of `CalendarViewModel.cs`)
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (the move only)
- Modify: `src/LeafCalendar.App/Views/SidebarView.xaml(.cs)`, `src/LeafCalendar.App/Views/DetailsPanel.xaml`
- Modify: `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`
- Create: `tests/LeafCalendar.UITests/Fixtures/directory-search.json`, `tests/LeafCalendar.UITests/Fixtures/events-rooms.json`
- Test: `tests/LeafCalendar.Tests/PowerSettingsTests.cs` (new), `ShortcutMapTests.cs`, `KeySequenceTests.cs`, `GoogleCalendarClientTests.cs`, `LinkSafetyTests.cs`, `tests/LeafCalendar.UITests/FakeGoogleServerTests.cs`

**Interfaces:**
- Produces (every later task uses these names verbatim):
  - `LeafSettings`:
    - `double InterfaceScale` (1.0) and `static IReadOnlyList<double> ScaleChoices` = `[0.8, 0.9, 1.0, 1.1, 1.25, 1.5]`
    - `WorkingHours WorkingHours`
    - `bool AllDayExpanded` (false)
    - `MapProvider MapProvider` (Google)
    - `int UpcomingHours` (8) and `static IReadOnlyList<int> UpcomingChoices` = `[2, 4, 8, 12, 24]`
    - `string? PrimaryTimeZone` (null = Windows)
    - `bool PromptOnZoneChange` (true)
    - `string? MainAccountId`
    - `IReadOnlyList<string> MeetByDefaultAccounts` (`[]`)
    - `IReadOnlyList<CalendarRef> TrayExcludedCalendars` (`[]`)
  - `public enum MapProvider { Google, Bing }`
  - `public sealed record WorkingHours { bool Enabled = true; int StartMinute = 540; int EndMinute = 1020; IReadOnlyList<DayOfWeek> Days = Mon–Fri }`
  - `CalendarCommand` gains these values:

    | Value | Keys |
    |---|---|
    | `CommandMenu` | Ctrl+K |
    | `Search` | Ctrl+F or `/` |
    | `ShortcutSheet` | `?` |
    | `OpenSettings` | Ctrl+, |
    | `TimeTravel` | Z |
    | `ShareAvailability` | S |
    | `PeopleOverlay` | P |
    | `MeetWith` | F |
    | `EditTimeZone` | E then Z |
    | `ParticipantOverlay` | E then F |
  - `public sealed record BusyRange(DateTimeOffset Start, DateTimeOffset End)` (namespace `LeafCalendar.Core.Google`)
  - `public sealed record FreeBusyResult(IReadOnlyList<BusyRange> Busy, string? Error)`
  - `GoogleCalendarClient`:
    - `Task<IReadOnlyDictionary<string, FreeBusyResult>> QueryFreeBusyAsync(string accountId, IReadOnlyList<string> ids, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)`
    - `Task<string> PatchCalendarListAsync(string accountId, string calendarId, string patchJson, CancellationToken ct)`
    - `Task<EventsPage?> ListEventsInRangeAsync(string accountId, string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)` (null when the calendar isn't readable)
  - `DirectorySearchResponse { List<PeoplePerson>? People }` in `GoogleJsonContext`
  - `GoogleUserInfo.Hd` (`string?`)
  - `LinkSafety.BookingPages(string accountEmail)` → `https://calendar.google.com/calendar/appointments?authuser=<email>`
  - `ScaleHost` (App): `double Scale` (0.5–2)
  - `CalendarPage`:
    - `public void RunCommand(CalendarCommand command, int days = 0)`
    - `public double SidebarPaneWidth` / `public double DetailsPaneWidth` (read `OpenPaneLength`)
    - `StackPanel IslandBars` (x:Name; a track adds its bar to `IslandBars.Children` from its own partial file)
    - `ScaleHost ViewScale` (x:Name around `ViewHost`)
  - `SidebarView.BodyScale` and `DetailsPanel.BodyScale`: `ScaleHost`, `x:FieldModifier="public"`
  - `SidebarView.ShareAvailabilityRequested` (`event EventHandler?`)
  - Partial hooks: each file holds empty private methods that its owner fills in.

    | File | Owner | Methods |
    |---|---|---|
    | `CalendarPage.Navigate.cs` | Track A | `AttachNavigate()`, `OpenCommandMenu()`, `ShowShortcutSheet()`, `StartTimeTravel()` |
    | `CalendarPage.People.cs` | Track B | `AttachPeople()`, `ShowPeopleOverlay()`, `ShowMeetWith()`, `ShowParticipantOverlay()`, `StartShareAvailability()` |
    | `CalendarPage.Extras.cs` | Track C | `AttachExtras()`, `EditTimeZone()` |
  - `CalendarViewModel.Zones.cs` (Track A) now holds `_zones`, `Zone`, and `CheckTimeZone()`. It also holds a new `ApplyZoneChange(TimeZoneInfo before)`, which is the second half of the old `CheckTimeZone`.
  - `FakeGoogleServer`:
    - `Busy` (`ConcurrentDictionary<string, List<(DateTimeOffset Start, DateTimeOffset End)>>`)
    - `TeammateEvents` (`ConcurrentDictionary<string, JsonArray>`)
    - `HostedDomain` (`string?`)
    - `FreeBusyQueries` (`ConcurrentQueue<string>`, the request bodies)
    - routes: `POST /calendar/v3/freeBusy`, `PATCH /calendar/v3/users/me/calendarList/{id}` (recorded in `Writes` and applied to later list GETs), `GET /people/v1/people:searchDirectoryPeople`, and range GETs (`timeMin` present) of events that answer 404 for unknown calendars and serve `TeammateEvents`
    - the `events-rooms.json` events, seeded on the primary calendar

- [ ] **Step 1: Write the failing settings tests**

`tests/LeafCalendar.Tests/PowerSettingsTests.cs`:

```csharp
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Tests;

public sealed class PowerSettingsTests
{
    [Fact]
    public void Defaults_MatchTheOwnerRulings()
    {
        var s = new LeafSettings().Normalize();

        Assert.Equal(1.0, s.InterfaceScale);
        Assert.True(s.WorkingHours.Enabled);
        Assert.Equal(9 * 60, s.WorkingHours.StartMinute);
        Assert.Equal(17 * 60, s.WorkingHours.EndMinute);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], s.WorkingHours.Days);
        Assert.False(s.AllDayExpanded);
        Assert.Equal(MapProvider.Google, s.MapProvider);
        Assert.Equal(8, s.UpcomingHours);
        Assert.Null(s.PrimaryTimeZone);
        Assert.True(s.PromptOnZoneChange);
        Assert.Null(s.MainAccountId);
        Assert.Empty(s.MeetByDefaultAccounts);
        Assert.Empty(s.TrayExcludedCalendars);
    }

    [Theory]
    [InlineData(0.7, 1.0)]
    [InlineData(1.33, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.25, 1.25)]
    public void Normalize_ScaleNotAChoice_ResetsTo100(double saved, double expected) =>
        Assert.Equal(expected, (new LeafSettings { InterfaceScale = saved }).Normalize().InterfaceScale);

    [Fact]
    public void Normalize_WorkingHoursBackwardsOrOutOfRange_ResetToDefaults()
    {
        var backwards = new LeafSettings { WorkingHours = new WorkingHours { StartMinute = 1000, EndMinute = 600 } }.Normalize();
        var outside   = new LeafSettings { WorkingHours = new WorkingHours { StartMinute = -5, EndMinute = 2000 } }.Normalize();

        Assert.Equal((540, 1020), (backwards.WorkingHours.StartMinute, backwards.WorkingHours.EndMinute));
        Assert.Equal((540, 1020), (outside.WorkingHours.StartMinute, outside.WorkingHours.EndMinute));
    }

    [Fact]
    public void Normalize_WorkingDays_DistinctAndKnownOnly()
    {
        var s = new LeafSettings { WorkingHours = new WorkingHours { Days = [DayOfWeek.Monday, DayOfWeek.Monday, (DayOfWeek)9] } }.Normalize();

        Assert.Equal([DayOfWeek.Monday], s.WorkingHours.Days);
    }

    [Fact]
    public void Normalize_UnknownEnumsAndChoices_Reset()
    {
        var s = new LeafSettings { MapProvider = (MapProvider)7, UpcomingHours = 5 }.Normalize();

        Assert.Equal(MapProvider.Google, s.MapProvider);
        Assert.Equal(8, s.UpcomingHours);
    }

    [Fact]
    public void Normalize_UnknownPrimaryZone_FollowsWindows()
    {
        Assert.Null(new LeafSettings { PrimaryTimeZone = "Mars/Olympus" }.Normalize().PrimaryTimeZone);
        Assert.Equal("Asia/Tokyo", new LeafSettings { PrimaryTimeZone = "Asia/Tokyo" }.Normalize().PrimaryTimeZone);
    }

    [Fact]
    public void Normalize_BlankAndDuplicateIds_Dropped()
    {
        var s = new LeafSettings
        {
            MainAccountId         = "  ",
            MeetByDefaultAccounts = ["a", "a", " ", "b"],
            TrayExcludedCalendars = [new("a", "x"), new("a", "x"), new("", "y")],
        }.Normalize();

        Assert.Null(s.MainAccountId);
        Assert.Equal(["a", "b"], s.MeetByDefaultAccounts);
        Assert.Equal([new CalendarRef("a", "x")], s.TrayExcludedCalendars);
    }

    [Fact]
    public void SavedBeforeM5_LoadsWithDefaults()
    {
        // A settings row written by Milestone 4 has none of the new keys
        var json = """{"WeekStart":1,"ShowWeekends":true,"ViewMode":1}""";
        var s    = System.Text.Json.JsonSerializer.Deserialize(json, LeafJsonContext.Default.LeafSettings)!.Normalize();

        Assert.Equal(1.0, s.InterfaceScale);
        Assert.True(s.WorkingHours.Enabled);
        Assert.Equal(8, s.UpcomingHours);
    }
}
```

Check how `SettingsStore` names properties (camelCase or not) by reading `LeafJsonContext.cs`. If it uses camelCase, write the JSON in `SavedBeforeM5_LoadsWithDefaults` in camelCase.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*PowerSettingsTests"`
Expected: build errors (`InterfaceScale`, `WorkingHours`, and `MapProvider` don't exist).

- [ ] **Step 3: Add the settings**

In `LeafSettings.cs`, add the enum and the record after `CalendarRef`:

```csharp
/// <summary>Which map site "Open in maps" uses (Settings › General).</summary>
public enum MapProvider
{
    /// <summary>Google Maps.</summary>
    Google,

    /// <summary>Bing Maps.</summary>
    Bing,
}

/// <summary>
/// Your working hours. Google's Calendar API doesn't expose the ones set in Google Calendar, so Leaf keeps its own.
/// Minutes from local midnight; the end is exclusive.
/// </summary>
public sealed record WorkingHours
{
    /// <summary>Weekdays, Monday to Friday.</summary>
    public static IReadOnlyList<DayOfWeek> Weekdays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Shade the hours outside these.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Start, minutes from midnight (9 AM).</summary>
    public int StartMinute { get; init; } = 9 * 60;

    /// <summary>End, minutes from midnight (5 PM).</summary>
    public int EndMinute { get; init; } = 17 * 60;

    /// <summary>Days you work.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = Weekdays;
}
```

In `LeafSettings`, add these after `FlyoutShortcut`:

```csharp
    /// <summary>Interface scale choices (Settings › General › Appearance).</summary>
    public static IReadOnlyList<double> ScaleChoices { get; } = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

    /// <summary>Upcoming-list lookahead choices in hours (details panel, nothing selected).</summary>
    public static IReadOnlyList<int> UpcomingChoices { get; } = [2, 4, 8, 12, 24];

    /// <summary>How big the sidebar, calendar, and details panel are drawn (1 = 100%).</summary>
    public double InterfaceScale { get; init; } = 1.0;

    /// <summary>Your working hours (shading outside them).</summary>
    public WorkingHours WorkingHours { get; init; } = new();

    /// <summary>The all-day row starts expanded.</summary>
    public bool AllDayExpanded { get; init; }

    /// <summary>Map site for locations.</summary>
    public MapProvider MapProvider { get; init; } = MapProvider.Google;

    /// <summary>How many hours ahead the details panel's upcoming list looks.</summary>
    public int UpcomingHours { get; init; } = 8;

    /// <summary>Leaf's own time zone (IANA ID); null follows Windows.</summary>
    public string? PrimaryTimeZone { get; init; }

    /// <summary>With <see cref="PrimaryTimeZone"/> set, offer to switch when Windows' time zone changes.</summary>
    public bool PromptOnZoneChange { get; init; } = true;

    /// <summary>The main account: listed first, and used for people overlays and free/busy. Null: the first account.</summary>
    public string? MainAccountId { get; init; }

    /// <summary>Accounts whose new events get a Google Meet link by default.</summary>
    public IReadOnlyList<string> MeetByDefaultAccounts { get; init; } = [];

    /// <summary>Calendars left out of the tray flyout and tooltip (the rest follow what's shown in Leaf).</summary>
    public IReadOnlyList<CalendarRef> TrayExcludedCalendars { get; init; } = [];
```

In `Normalize`, add these to the `with` block (keep the column alignment):

```csharp
            InterfaceScale        = ScaleChoices.Contains(InterfaceScale) ? InterfaceScale : 1.0,
            WorkingHours          = CleanHours(WorkingHours),
            MapProvider           = Enum.IsDefined(MapProvider) ? MapProvider : MapProvider.Google,
            UpcomingHours         = UpcomingChoices.Contains(UpcomingHours) ? UpcomingHours : 8,
            PrimaryTimeZone       = PrimaryTimeZone is { } z && TimeZoneInfo.TryFindSystemTimeZoneById(z, out _) ? z : null,
            MainAccountId         = string.IsNullOrWhiteSpace(MainAccountId) ? null : MainAccountId,
            MeetByDefaultAccounts = [.. (MeetByDefaultAccounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal)],
            TrayExcludedCalendars = [.. (TrayExcludedCalendars ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.AccountId) && !string.IsNullOrWhiteSpace(c.CalendarId)).Distinct()],
```

Add this helper next to `CleanLabel`:

```csharp
    // Out of range or backwards falls back to 9-5; unknown and repeated days are dropped
    static WorkingHours CleanHours(WorkingHours? hours)
    {
        var h     = hours ?? new WorkingHours();
        var valid = h.StartMinute is >= 0 and < 1440 && h.EndMinute is > 0 and <= 1440 && h.StartMinute < h.EndMinute;
        return h with
        {
            StartMinute = valid ? h.StartMinute : 9 * 60,
            EndMinute   = valid ? h.EndMinute : 17 * 60,
            Days        = [.. (h.Days ?? WorkingHours.Weekdays).Where(d => Enum.IsDefined(d)).Distinct()],
        };
    }
```

Also update the `Normalize` remarks: add one sentence per new rule.

- [ ] **Step 4: Run the settings tests and see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*PowerSettingsTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing shortcut tests**

Add to `ShortcutMapTests.cs`:

```csharp
    [Theory]
    [InlineData("K", true, false, CalendarCommand.CommandMenu)]
    [InlineData("F", true, false, CalendarCommand.Search)]
    [InlineData("191", false, false, CalendarCommand.Search)]       // "/"
    [InlineData("Divide", false, false, CalendarCommand.Search)]    // numpad "/"
    [InlineData("191", false, true, CalendarCommand.ShortcutSheet)] // "?" is Shift+"/"
    [InlineData("188", true, false, CalendarCommand.OpenSettings)]  // Ctrl+,
    [InlineData("Z", false, false, CalendarCommand.TimeTravel)]
    [InlineData("S", false, false, CalendarCommand.ShareAvailability)]
    [InlineData("P", false, false, CalendarCommand.PeopleOverlay)]
    [InlineData("F", false, false, CalendarCommand.MeetWith)]
    public void Resolve_M5Keys(string key, bool ctrl, bool shift, CalendarCommand expected) =>
        Assert.Equal(expected, ShortcutMap.Resolve(key, ctrl, shift, alt: false).Command);

    [Fact]
    public void Resolve_CtrlZ_StillUndo_ZAloneTimeTravels()
    {
        Assert.Equal(CalendarCommand.Undo, ShortcutMap.Resolve("Z", true, false, false).Command);
        Assert.Equal(CalendarCommand.TimeTravel, ShortcutMap.Resolve("Z", false, false, false).Command);
    }
```

Add to `KeySequenceTests.cs` (use the file's existing `FakeTimeProvider` setup):

```csharp
    [Theory]
    [InlineData("Z", CalendarCommand.EditTimeZone)]
    [InlineData("F", CalendarCommand.ParticipantOverlay)]
    public void EThen_M5Keys(string second, CalendarCommand expected)
    {
        var time = new FakeTimeProvider();
        var keys = new KeySequence(time);

        keys.Resolve("E", false, false, false);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(expected, keys.Resolve(second, false, false, false).Command);
    }

    [Fact]
    public void FAlone_IsMeetWith()
    {
        var keys = new KeySequence(new FakeTimeProvider());

        Assert.Equal(CalendarCommand.MeetWith, keys.Resolve("F", false, false, false).Command);
    }
```

- [ ] **Step 6: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*ShortcutMapTests"`, then `--filter-class "*KeySequenceTests"`.
Expected: build errors (the new enum values don't exist).

- [ ] **Step 7: Add the commands and keys**

In `CalendarCommand`, add these before `SequenceStarted`, each with a `<summary>` naming its keys:
- `CommandMenu` (Ctrl+K)
- `Search` (Ctrl+F or /)
- `ShortcutSheet` (?)
- `OpenSettings` (Ctrl+,)
- `TimeTravel` (Z)
- `ShareAvailability` (S)
- `PeopleOverlay` (P)
- `MeetWith` (F)
- `EditTimeZone` (E then Z)
- `ParticipantOverlay` (E then F)

In `ShortcutMap.Resolve`:
- **Ctrl** switch: add `"K" => new(CalendarCommand.CommandMenu)`, `"F" => new(CalendarCommand.Search)`, and `"188" => new(CalendarCommand.OpenSettings)`.
- **Shift** branch: `return key switch { "N" => new(CalendarCommand.PreviousEvent), "191" => new(CalendarCommand.ShortcutSheet), _ => default };`
- **Plain** switch: add `"191" or "Divide" => new(CalendarCommand.Search)`, `"Z" => new(CalendarCommand.TimeTravel)`, `"S" => new(CalendarCommand.ShareAvailability)`, `"P" => new(CalendarCommand.PeopleOverlay)`, and `"F" => new(CalendarCommand.MeetWith)`.

In `KeySequence.Second`, add `"Z" => CalendarCommand.EditTimeZone` and `"F" => CalendarCommand.ParticipantOverlay`. Update the class summary to list them.

- [ ] **Step 8: Run the shortcut tests and see them pass**

Same commands as Step 6. Expected: PASS, including every earlier case in both files.

- [ ] **Step 9: Write the failing Google client tests**

Add to `GoogleCalendarClientTests.cs`, using that file's `FakeHttpHandler` setup and its client factory:

```csharp
    [Fact]
    public async Task QueryFreeBusy_PostsRangeAndIds_ReadsBusyAndErrors()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """
            {"calendars":{
              "dana@example.com":{"busy":[{"start":"2026-10-01T15:00:00Z","end":"2026-10-01T16:00:00Z"}]},
              "nobody@example.org":{"errors":[{"domain":"global","reason":"notFound"}],"busy":[]}}}
            """);

        var result = await _client.QueryFreeBusyAsync(Account, ["dana@example.com", "nobody@example.org"],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        var body = _handler.LastBody!;
        Assert.Contains("\"timeMin\":\"2026-10-01T00:00:00Z\"", body, StringComparison.Ordinal);
        Assert.Contains("\"items\":[{\"id\":\"dana@example.com\"},{\"id\":\"nobody@example.org\"}]", body, StringComparison.Ordinal);
        Assert.Equal([new BusyRange(new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero))], result["dana@example.com"].Busy);
        Assert.Null(result["dana@example.com"].Error);
        Assert.Equal("notFound", result["nobody@example.org"].Error);
    }

    [Fact]
    public async Task QueryFreeBusy_IdMissingFromAnswer_IsAnError()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{}}""");

        var result = await _client.QueryFreeBusyAsync(Account, ["dana@example.com"], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal("missing", result["dana@example.com"].Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task QueryFreeBusy_ZeroOrOver50Ids_Throws(int count) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _client.QueryFreeBusyAsync(Account,
            [.. Enumerable.Range(0, count).Select(i => $"p{i}@example.com")], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1),
            TestContext.Current.CancellationToken));

    [Fact]
    public async Task PatchCalendarList_SendsPatchToTheListEntry()
    {
        _handler.Respond(HttpMethod.Patch, "users/me/calendarList/family%40group.calendar.google.com", 200, """{"id":"family@group.calendar.google.com","summaryOverride":"Kids"}""");

        await _client.PatchCalendarListAsync(Account, "family@group.calendar.google.com", """{"summaryOverride":"Kids"}""", TestContext.Current.CancellationToken);

        Assert.Equal("""{"summaryOverride":"Kids"}""", _handler.LastBody);
    }

    [Fact]
    public async Task PatchCalendarList_Refused_ThrowsGoogleApiException()
    {
        _handler.Respond(HttpMethod.Patch, "users/me/calendarList/x", 400, Fixture.Read("error-forbidden.json"));

        await Assert.ThrowsAsync<GoogleApiException>(() => _client.PatchCalendarListAsync(Account, "x", "{}", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ListEventsInRange_NotReadable_ReturnsNull(int status)
    {
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", status, "{}");

        Assert.Null(await _client.ListEventsInRangeAsync(Account, "dana@example.com", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListEventsInRange_AsksForSingleEventsInTheRange()
    {
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """{"items":[{"id":"a","status":"confirmed"}]}""");

        var page = await _client.ListEventsInRangeAsync(Account, "dana@example.com",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Single(page!.Items);
        Assert.Contains("timeMin=2026-10-01T00%3A00%3A00Z", _handler.LastUri!.Query, StringComparison.Ordinal);
        Assert.Contains("singleEvents=true", _handler.LastUri!.Query, StringComparison.Ordinal);
    }
```

Read `tests/LeafCalendar.Tests/Support/FakeHttpHandler.cs` first. If its method names differ from `Respond`, `LastBody`, and `LastUri`, use its real names. If it lacks a "last request" view, add one there: the file is shared support, owned by Task 1.

Add to `LinkSafetyTests.cs`:

```csharp
    [Fact]
    public void BookingPages_IsHttpsGoogleWithTheAccount()
    {
        var uri = LinkSafety.BookingPages("leaf.tester@gmail.com");

        Assert.Equal("https://calendar.google.com/calendar/appointments?authuser=leaf.tester%40gmail.com", uri.AbsoluteUri);
        Assert.True(LinkSafety.CanLaunch(uri));
    }
```

- [ ] **Step 10: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*GoogleCalendarClientTests"`, then `--filter-class "*LinkSafetyTests"`.
Expected: build errors.

- [ ] **Step 11: Add the models, the context entries, and the client calls**

`GoogleModels.cs`: add a `// FREE/BUSY` section banner and these types:

```csharp
/// <summary>One busy stretch.</summary>
public sealed record BusyRange(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Free/busy for one calendar or person: <see cref="Error"/> is Google's reason (e.g. <c>notFound</c>) when it had none to give.</summary>
public sealed record FreeBusyResult(IReadOnlyList<BusyRange> Busy, string? Error);

/// <summary><c>freeBusy.query</c> body.</summary>
public sealed class FreeBusyRequest
{
    /// <summary>RFC 3339 UTC start.</summary>
    public string TimeMin { get; set; } = "";

    /// <summary>RFC 3339 UTC end.</summary>
    public string TimeMax { get; set; } = "";

    /// <summary>Calendars or people.</summary>
    public List<FreeBusyItem> Items { get; set; } = [];
}

/// <summary>One calendar or address to query.</summary>
public sealed class FreeBusyItem
{
    /// <summary>Calendar ID or email.</summary>
    public string Id { get; set; } = "";
}

/// <summary><c>freeBusy.query</c> answer.</summary>
public sealed class FreeBusyResponse
{
    /// <summary>Per ID.</summary>
    public Dictionary<string, FreeBusyCalendar>? Calendars { get; set; }
}

/// <summary>One ID's answer.</summary>
public sealed class FreeBusyCalendar
{
    /// <summary>Busy stretches.</summary>
    public List<FreeBusyPeriod>? Busy { get; set; }

    /// <summary>Why there's no answer.</summary>
    public List<ApiErrorItem>? Errors { get; set; }
}

/// <summary>A busy stretch as Google sends it.</summary>
public sealed class FreeBusyPeriod
{
    /// <summary>Start.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>End.</summary>
    public DateTimeOffset End { get; set; }
}

/// <summary><c>people:searchDirectoryPeople</c> answer.</summary>
public sealed class DirectorySearchResponse
{
    /// <summary>Matching people in your Workspace directory.</summary>
    public List<PeoplePerson>? People { get; set; }
}
```

Check what the error item class inside `ApiErrorEnvelope` is called (`ContactSearch` reads `.Errors` and `.Reason` from it), and use that name in place of `ApiErrorItem`.

Add `/// <summary>Workspace domain (Google's <c>hd</c> claim); absent for personal accounts.</summary> public string? Hd { get; set; }` to `GoogleUserInfo`.

`GoogleJsonContext.cs`: add `[JsonSerializable(typeof(FreeBusyRequest))]`, `[JsonSerializable(typeof(FreeBusyResponse))]`, and `[JsonSerializable(typeof(DirectorySearchResponse))]`.

`GoogleCalendarClient.cs`: add a `// =====` banner `CALENDAR LIST AND FREE/BUSY` before `HTTP`, and under it:

```csharp
    /// <summary>Most IDs Google takes in one free/busy query.</summary>
    public const int MaxFreeBusyIds = 50;

    /// <summary>
    /// Busy times for calendars or people (a person's ID is their email) over <c>[from, to)</c>. An ID Google has no
    /// answer for (not found, outside your domain, not shared) comes back with an <see cref="FreeBusyResult.Error"/>,
    /// never as free.
    /// </summary>
    /// <exception cref="ArgumentException">No IDs, or more than <see cref="MaxFreeBusyIds"/>.</exception>
    public async Task<IReadOnlyDictionary<string, FreeBusyResult>> QueryFreeBusyAsync(string accountId, IReadOnlyList<string> ids, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (ids.Count is 0 or > MaxFreeBusyIds)
        {
            throw new ArgumentException($"Between 1 and {MaxFreeBusyIds} IDs.", nameof(ids));
        }

        // Request
        var body = JsonSerializer.Serialize(new FreeBusyRequest
        {
            TimeMin = Rfc3339(from),
            TimeMax = Rfc3339(to),
            Items   = [.. ids.Select(id => new FreeBusyItem { Id = id })],
        }, GoogleJsonContext.Default.FreeBusyRequest);

        using var response = await SendAsync(accountId, HttpMethod.Post, "freeBusy", body, null, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleJson.ToExceptionAsync(response, ct);
        }

        // Answer (an ID left out counts as an error, never as free)
        var answer = await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.FreeBusyResponse, ct);
        var found  = answer?.Calendars ?? [];
        return ids.Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => found.TryGetValue(id, out var c)
            ? new FreeBusyResult([.. (c.Busy ?? []).Where(b => b.End > b.Start).Select(b => new BusyRange(b.Start, b.End))], c.Errors?.FirstOrDefault()?.Reason)
            : new FreeBusyResult([], "missing"), StringComparer.Ordinal);
    }

    /// <summary>Changes your calendar-list entry (<c>summaryOverride</c>, <c>defaultReminders</c>) and returns Google's JSON.</summary>
    /// <exception cref="GoogleApiException">Google refused it.</exception>
    public async Task<string> PatchCalendarListAsync(string accountId, string calendarId, string patchJson, CancellationToken ct)
    {
        using var response = await SendAsync(accountId, HttpMethod.Patch, $"users/me/calendarList/{Uri.EscapeDataString(calendarId)}", patchJson, null, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleJson.ToExceptionAsync(response, ct);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Single events overlapping <c>[from, to)</c> on someone else's calendar, for an overlay with details. Null when
    /// you can't read it (403 or 404): then only free/busy is available.
    /// </summary>
    public async Task<EventsPage?> ListEventsInRangeAsync(string accountId, string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var path = $"{EventsPath(calendarId)}?timeMin={Uri.EscapeDataString(Rfc3339(from))}&timeMax={Uri.EscapeDataString(Rfc3339(to))}&singleEvents=true&orderBy=startTime&maxResults=250";
        using var response = await SendAsync(accountId, HttpMethod.Get, path, null, null, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await GoogleJson.ToExceptionAsync(response, ct);
        }

        return await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.EventsPage, ct);
    }

    static string Rfc3339(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
```

`LinkSafety.cs`: add, next to `MapsSearch`:

```csharp
    /// <summary>Google Calendar's appointment schedule (booking pages) for an account; Google has no API for them.</summary>
    public static Uri BookingPages(string accountEmail) =>
        new("https://calendar.google.com/calendar/appointments?authuser=" + Uri.EscapeDataString(accountEmail));
```

- [ ] **Step 12: Run the Core tests and see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: all PASS.

- [ ] **Step 13: `ScaleHost`**

`src/LeafCalendar.App/Controls/ScaleHost.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Draws its one child at <see cref="Scale"/> (Settings › General › Interface scale). The child is laid out in the
/// host's size divided by the scale, then scaled back up, so it fills the host exactly at any scale; hit testing
/// follows the transform. At 1 it lays out exactly as if the host weren't there.
/// </summary>
public sealed partial class ScaleHost : Panel
{
    readonly ScaleTransform _transform = new();
    double _scale = 1;

    /// <summary>The scale, from 0.5 to 2.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0.5, 2) : 1;
            if (clamped == _scale)
            {
                return;
            }

            _scale                                  = clamped;
            (_transform.ScaleX, _transform.ScaleY) = (clamped, clamped);
            InvalidateMeasure();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        // The transform is ours, set on the child; never read back (AOT read-back rule)
        var child = Children[0];
        child.RenderTransform = _transform;
        child.Measure(new Size(availableSize.Width / _scale, availableSize.Height / _scale));
        return new Size(child.DesiredSize.Width * _scale, child.DesiredSize.Height * _scale);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count > 0)
        {
            Children[0].Arrange(new Rect(0, 0, finalSize.Width / _scale, finalSize.Height / _scale));
        }

        return finalSize;
    }
}
```

- [ ] **Step 14: Wire the hosts, the island bars, and the pane widths**

`CalendarPage.xaml`, inside `Island`:
- Make the rows `49`, `Auto`, `*`.
- Add `<StackPanel x:Name="IslandBars" Grid.Row="1" />` with a comment: `<!-- Island Bars (time travel, people overlay, share availability; each track adds its own bar from code) -->`.
- Wrap `ViewHost` in `<controls:ScaleHost x:Name="ViewScale" Grid.Row="2">` and move `Grid.Row` from `ViewHost` to the host. Declare `xmlns:controls="using:LeafCalendar.App.Controls"`.
- Put `NoticeBar` on `Grid.Row="2"`.

`CalendarPage.xaml.cs`:
- Add `public double SidebarPaneWidth => SidebarSplit.OpenPaneLength;` and `public double DetailsPaneWidth => DetailsSplit.OpenPaneLength;`, each with a summary. Keep the `SidebarWidth` and `DetailsWidth` constants; they're the 100% widths.
- Add `RunCommand`:

```csharp
    /// <summary>Runs a calendar command as if its shortcut were pressed (the command menu's one path into every action).</summary>
    public void RunCommand(CalendarCommand command, int days = 0) => Execute(new ShortcutResult(command, days));
```

- In `Execute`, add `CalendarCommand.EditTimeZone` and `CalendarCommand.ParticipantOverlay` to the "Select one event" guard, and these cases:

```csharp
            case CalendarCommand.CommandMenu or CalendarCommand.Search: OpenCommandMenu(); break;
            case CalendarCommand.ShortcutSheet:      ShowShortcutSheet(); break;
            case CalendarCommand.OpenSettings:       vm.OpenSettings?.Invoke(SettingsSection.General); break;
            case CalendarCommand.TimeTravel:         StartTimeTravel(); break;
            case CalendarCommand.ShareAvailability:  StartShareAvailability(); break;
            case CalendarCommand.PeopleOverlay:      ShowPeopleOverlay(); break;
            case CalendarCommand.MeetWith:           ShowMeetWith(); break;
            case CalendarCommand.ParticipantOverlay: ShowParticipantOverlay(); break;
            case CalendarCommand.EditTimeZone:       EditTimeZone(); break;
```

- In `HandleShortcut`'s instant-E `switch (second.Command)`, add:

```csharp
                case CalendarCommand.EditTimeZone:
                    EditTimeZone();
                    return true;
                case CalendarCommand.ParticipantOverlay:
                    ViewModel.CancelEdit();
                    Execute(second);
                    return true;
```

- At the end of the existing `OnNavigatedTo` (after `_args` is set and the page is wired), call `AttachNavigate(); AttachPeople(); AttachExtras();`.

Create the three stub files. Each is `namespace LeafCalendar.App.Views; public sealed partial class CalendarPage { ... }` with a header comment naming its owner. For example, in `CalendarPage.Navigate.cs`:

```csharp
// Track A (Milestone 5 Tasks 3-5) owns this file: the command menu, the cheat sheet, time travel, and interface scale.
namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // Called once when the page opens (Task 3 fills it in)
    void AttachNavigate()
    {
    }

    // Ctrl+K, Ctrl+F, /, and the title bar's search icon (Task 3)
    void OpenCommandMenu()
    {
    }

    // ? (Task 4)
    void ShowShortcutSheet()
    {
    }

    // Z (Task 5)
    void StartTimeTravel()
    {
    }
}
```

`CalendarPage.People.cs` (Track B, Tasks 7–8) holds `AttachPeople`, `ShowPeopleOverlay`, `ShowMeetWith`, `ShowParticipantOverlay`, and `StartShareAvailability`. `CalendarPage.Extras.cs` (Track C, Tasks 11–12) holds `AttachExtras` and `EditTimeZone`. Write each method with the same one-line comment form.

`SidebarView.xaml`:
- Make the root `Grid` `Padding="0"` with rows `48` and `*`.
- Put the old grid (its three rows and content unchanged, now `Padding="5,0,5,6"`) inside `<controls:ScaleHost x:Name="BodyScale" x:FieldModifier="public" Grid.Row="1">`.
- Keep the header comment, and update its padding numbers.
- In the bottom row beside the Settings button, add:

```xml
<!-- Share Availability (spec 6.3; Task 8 makes it work) -->
<Button
    x:Name="ShareButton"
    Width="32"
    Height="32"
    Style="{StaticResource LeafIconButtonStyle}"
    Click="OnShareClick"
    ToolTipService.ToolTip="Share availability (S)"
    AutomationProperties.Name="Share availability"
    AutomationProperties.AutomationId="SidebarShareAvailability">
    <FontIcon Glyph="&#xE72D;" FontSize="16" />
</Button>
```

Place it right after the Settings button in a horizontal `StackPanel` (`Spacing="4"`), keeping Settings' position and its optical margins exactly as they are.

`SidebarView.xaml.cs`: add `/// <summary>The share-availability button was clicked.</summary> public event EventHandler? ShareAvailabilityRequested;` and `void OnShareClick(object sender, RoutedEventArgs e) => ShareAvailabilityRequested?.Invoke(this, EventArgs.Empty);`.

`DetailsPanel.xaml`: in the same way, wrap everything below the panel's 48 DIP title-bar row in `<controls:ScaleHost x:Name="BodyScale" x:FieldModifier="public">`, moving the 48 top padding outside the host.

- [ ] **Step 15: Move the zone code to its own partial file**

Create `src/LeafCalendar.App/ViewModels/CalendarViewModel.Zones.cs` (`public sealed partial class CalendarViewModel`, header comment: "Track A owns this file"). Cut and paste from `CalendarViewModel.cs`, unchanged:
- the `_zones` field
- the `Zone` property
- `CheckTimeZone()`

Then split `CheckTimeZone` in two. The body after the `_zones.Check()` guard becomes `void ApplyZoneChange(TimeZoneInfo before)`, and `CheckTimeZone` becomes:

```csharp
    public void CheckTimeZone()
    {
        var before = Zone;
        if (!_zones.Check())
        {
            return;
        }

        ApplyZoneChange(before);
    }
```

Behavior is identical. This only gives Track A the zone code in a file it owns.

- [ ] **Step 16: Fake Google routes and fixtures**

`Fixtures/directory-search.json`:

```json
{
  "people": [
    { "names": [ { "displayName": "Dana Director" } ], "emailAddresses": [ { "value": "dana@example.com" } ] },
    { "names": [ { "displayName": "Ali Directory" } ], "emailAddresses": [ { "value": "ali.dir@example.com" } ] }
  ]
}
```

`Fixtures/events-rooms.json` (two past events, far from the 2026-10-01 test week):

```json
{
  "kind": "calendar#events",
  "items": [
    {
      "id": "evt-planning",
      "status": "confirmed",
      "etag": "\"3181161784712300\"",
      "iCalUID": "evt-planning@google.com",
      "summary": "Planning",
      "start": { "dateTime": "2026-09-15T10:00:00-04:00", "timeZone": "America/New_York" },
      "end": { "dateTime": "2026-09-15T11:00:00-04:00", "timeZone": "America/New_York" },
      "organizer": { "email": "leaf.tester@gmail.com", "self": true },
      "attendees": [
        { "email": "leaf.tester@gmail.com", "self": true, "organizer": true, "responseStatus": "accepted" },
        { "email": "frank@example.com", "displayName": "Frank Often", "responseStatus": "accepted" },
        { "email": "c_1888room4west@resource.calendar.google.com", "displayName": "Room 4 West", "resource": true, "responseStatus": "accepted" }
      ],
      "updated": "2026-09-10T12:00:00.000Z"
    },
    {
      "id": "evt-planning-2",
      "status": "confirmed",
      "etag": "\"3181161784712301\"",
      "iCalUID": "evt-planning-2@google.com",
      "summary": "Planning follow-up",
      "start": { "dateTime": "2026-09-16T10:00:00-04:00", "timeZone": "America/New_York" },
      "end": { "dateTime": "2026-09-16T10:30:00-04:00", "timeZone": "America/New_York" },
      "organizer": { "email": "leaf.tester@gmail.com", "self": true },
      "attendees": [
        { "email": "leaf.tester@gmail.com", "self": true, "organizer": true, "responseStatus": "accepted" },
        { "email": "frank@example.com", "displayName": "Frank Often", "responseStatus": "accepted" }
      ],
      "updated": "2026-09-10T12:00:00.000Z"
    }
  ]
}
```

Add both files to the UITests project's fixture copy item if fixtures are listed one by one; check `LeafCalendar.UITests.csproj`.

`FakeGoogleServer.cs`:
- **Seed:** add `Seed(PrimaryId, "events-rooms.json");` after the meeting fixture.
- **Properties**, each with a summary:

```csharp
    /// <summary>People's busy times for <c>freeBusy</c>, by email; an address not here and not a seeded calendar answers <c>notFound</c>.</summary>
    public ConcurrentDictionary<string, List<(DateTimeOffset Start, DateTimeOffset End)>> Busy { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calendars shared with details: a range GET of events on one of these answers these items.</summary>
    public ConcurrentDictionary<string, JsonArray> TeammateEvents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Workspace domain userinfo reports (<c>hd</c>); null for a personal account.</summary>
    public string? HostedDomain { get; set; }

    /// <summary>Every <c>freeBusy</c> request body, in order.</summary>
    public ConcurrentQueue<string> FreeBusyQueries { get; } = new();
```

- **Userinfo:** after building `user`, add `if (HostedDomain is not null) { user["hd"] = HostedDomain; }`.
- **Scope string:** `ContactsScopes` must include `https://www.googleapis.com/auth/directory.readonly`. Check the constant and add it if it's missing.
- **Calendar list:** keep a `readonly ConcurrentDictionary<string, JsonObject> _listPatches`. After the existing list edits on GET, merge each patch's properties into the matching item. A JSON `null` removes the property.
- **New routes in `Route`**, before `// Events`:

```csharp
        // Calendar List Entry Changes (rename, default reminders)
        if (method == "PATCH" && path.StartsWith("/calendar/v3/users/me/calendarList/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/calendar/v3/users/me/calendarList/".Length..]);
            Writes.Enqueue(new FakeWrite(method, path, uri.Query, ifMatch, body));
            var patch = JsonNode.Parse(body)!.AsObject();
            _listPatches.AddOrUpdate(id, _ => patch, (_, old) => Merge(old, patch));
            return (200, new JsonObject { ["id"] = id }.ToJsonString(), null);
        }

        // Free/Busy
        if (method == "POST" && path == "/calendar/v3/freeBusy")
        {
            FreeBusyQueries.Enqueue(body);
            return FreeBusy(body);
        }
```

- **`FreeBusy`:** for each item ID:
  - in `Busy`: its ranges overlapping `[timeMin, timeMax)`
  - `PrimaryId` or `FamilyId`: the stored events' timed, non-cancelled, non-transparent ranges overlapping the window (under `_gate`)
  - anything else: `{"errors":[{"domain":"global","reason":"notFound"}],"busy":[]}`

  Answer `{"calendars":{...}}`, with times written as `yyyy-MM-ddTHH:mm:ssZ`.
- **`People`:** map `"/people/v1/people:searchDirectoryPeople"` to `directory-search.json`, which has a `people` array, not `results`. Filter it with the same `Matches`, and answer `{"people":[...]}`.
- **`RouteEvents`:** for `GET` with two path parts and a `timeMin` in the query:
  - If `TeammateEvents` has the calendar, answer `{"items": <that array>}`.
  - Else, if the calendar isn't `PrimaryId` or `FamilyId`, answer 404.
  - Else, fall through to `List`.

Add to `FakeGoogleServerTests.cs`. These tests don't launch Leaf, so they run now:

```csharp
    [Fact]
    public async Task FreeBusy_KnownBusyUnknownAndSeeded()
    {
        using var google = new FakeGoogleServer();
        google.Busy["dana@example.com"] = [(new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero))];
        using var http = new HttpClient();

        var response = await http.PostAsync(new Uri(google.BaseUri, "calendar/v3/freeBusy"), new StringContent("""
            {"timeMin":"2026-10-01T00:00:00Z","timeMax":"2026-10-02T00:00:00Z",
             "items":[{"id":"dana@example.com"},{"id":"nobody@example.org"},{"id":"leaf.tester@gmail.com"}]}
            """), TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["calendars"]!;

        Assert.Equal("2026-10-01T15:00:00Z", (string?)json["dana@example.com"]!["busy"]![0]!["start"]);
        Assert.Equal("notFound", (string?)json["nobody@example.org"]!["errors"]![0]!["reason"]);
        Assert.Equal(2, json["leaf.tester@gmail.com"]!["busy"]!.AsArray().Count); // dentist 9-10, design review 14-15 ET
    }

    [Fact]
    public async Task CalendarListPatch_ShowsOnTheNextList()
    {
        using var google = new FakeGoogleServer();
        using var http = new HttpClient();

        await http.PatchAsync(new Uri(google.BaseUri, "calendar/v3/users/me/calendarList/family123%40group.calendar.google.com"),
            new StringContent("""{"summaryOverride":"Kids"}"""), TestContext.Current.CancellationToken);
        var list = JsonNode.Parse(await http.GetStringAsync(new Uri(google.BaseUri, "calendar/v3/users/me/calendarList"), TestContext.Current.CancellationToken))!;

        Assert.Equal("Kids", (string?)list["items"]!.AsArray().First(i => (string?)i!["id"] == "family123@group.calendar.google.com")!["summaryOverride"]);
    }

    [Fact]
    public async Task DirectorySearch_PrefixMatches()
    {
        using var google = new FakeGoogleServer();
        using var http = new HttpClient();

        var json = JsonNode.Parse(await http.GetStringAsync(new Uri(google.BaseUri, "people/v1/people:searchDirectoryPeople?query=dan&readMask=names,emailAddresses"), TestContext.Current.CancellationToken))!;

        Assert.Equal("dana@example.com", (string?)Assert.Single(json["people"]!.AsArray())!["emailAddresses"]![0]!["value"]);
    }

    [Fact]
    public async Task RangeList_UnknownCalendar_Is404()
    {
        using var google = new FakeGoogleServer();
        using var http = new HttpClient();

        var response = await http.GetAsync(new Uri(google.BaseUri, "calendar/v3/calendars/dana%40example.com/events?timeMin=2026-10-01T00:00:00Z&timeMax=2026-10-02T00:00:00Z"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
```

The fake's requests carry no auth header. If its routes demand one, add `Authorization: Bearer x`, as the file's other tests do.

- [ ] **Step 17: Build everything and run the non-UI tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → all PASS
- `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*FakeGoogleServerTests"` → PASS

The FakeGoogleServer tests don't launch Leaf.

- [ ] **Step 18: Commit**

```bash
git add -A src tests
git commit -m "feat: M5 foundation (settings, commands, Google free/busy and calendar list, scale host, track hooks, fake routes)"
```

Then create the three worktrees (M0).

---

## Track A: Command Menu, Cheat Sheet, Zones, and Scale

Track A works in `../leaf-m5-a` on `m5-track-a`. Its tasks run in order: 2, 3, 4, 5.

### Task 2: Event Search, Command Catalog, and Date Parsing (Core)

**Files:**
- Create: `src/LeafCalendar.Core/Search/EventSearch.cs`, `src/LeafCalendar.Core/Search/CommandCatalog.cs`, `src/LeafCalendar.Core/Search/DateQuery.cs`
- Test: `tests/LeafCalendar.Tests/EventSearchTests.cs`, `CommandCatalogTests.cs`, `DateQueryTests.cs` (all new)

**Interfaces:**
- Consumes: `EventDetailsParser.Parse`/`HtmlToText`, `OccurrenceQuery.Load`, `CalendarCommand` (Task 1), `LeafSettings.ScaleChoices` (Task 1), the `events` and `calendars` tables.
- Produces:
  - `public enum SearchField { Title, Location, Guest, Description }`
  - `public sealed record SearchHit(string AccountId, string CalendarId, string EventId, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string Color, SearchField Field)`. For a repeating event, `EventId` and `Start` are its next instance's (else its most recent past one in the last year, else the series start).
  - `public static IReadOnlyList<SearchHit> EventSearch.Find(SqliteConnection conn, string? query, DateTimeOffset now, TimeZoneInfo zone)` (at most `MaxResults` = 20)
  - `public static IReadOnlyList<string> EventSearch.Words(string? query)` (cleaned, capped at `MaxQuery` = 100 characters)
  - `public sealed record CommandItem(string Id, string Title, string Keys, CalendarCommand Command = CalendarCommand.None, int Days = 0, string Keywords = "")`
  - `CommandCatalog.All`, `CommandCatalog.Defaults`, and `CommandCatalog.Match(string? query, int max = 8)`
  - `public static bool DateQuery.TryParse(string? text, DateOnly today, out DateOnly date)` and `public static string DateQuery.Label(DateOnly date, DateOnly today)`

- [ ] **Step 1: Write the failing search tests**

`EventSearchTests.cs`. The setup is `OccurrenceQueryTests`' constructor and `Insert` helper, copied as they are:

```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Search;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class EventSearchTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(-4));

    readonly TestDatabase _db = new();

    public EventSearchTests()
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

    IReadOnlyList<SearchHit> Find(string query)
    {
        using var conn = _db.Database.Open();
        return EventSearch.Find(conn, query, Now, NewYork);
    }

    static string Timed(string id, string title, string start, string end, string extra = "") =>
        $$"""{"id":"{{id}}","status":"confirmed","summary":"{{title}}","start":{"dateTime":"{{start}}"},"end":{"dateTime":"{{end}}"}{{extra}}}""";

    [Fact]
    public void Find_Title_IgnoresCase()
    {
        var hit = Assert.Single(Find("DENTIST"));

        Assert.Equal("evt-single", hit.EventId);
        Assert.Equal(SearchField.Title, hit.Field);
        Assert.Equal("#9fe1e7", hit.Color);
    }

    [Fact]
    public void Find_LocationGuestAndDescription()
    {
        Insert(Timed("evt-x", "Sync", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00",
            ""","location":"Room 4","description":"<b>Agenda</b> inside","attendees":[{"email":"sam@example.com","displayName":"Sam Lee"}]"""));

        Assert.Equal(SearchField.Location, Assert.Single(Find("room 4")).Field);
        Assert.Equal(SearchField.Guest, Assert.Single(Find("sam@example")).Field);
        Assert.Equal(SearchField.Guest, Assert.Single(Find("lee")).Field);
        Assert.Equal(SearchField.Description, Assert.Single(Find("agenda")).Field);
    }

    [Fact]
    public void Find_EveryWordMustMatch() => Assert.Empty(Find("team holiday"));

    [Fact]
    public void Find_Repeating_GivesTheNextInstanceSkippingCanceledOnes()
    {
        // Now is Tue Oct 6 noon; Wed Oct 7 is canceled in the fixture, so the next standup is Fri Oct 9 9:30 ET
        var hit = Assert.Single(Find("standup"));

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 13, 30, 0, TimeSpan.Zero), hit.Start);
    }

    [Fact]
    public void Find_UpcomingFirstThenPastNewestFirst()
    {
        Insert(Timed("r1", "Review one", "2026-09-01T10:00:00-04:00", "2026-09-01T11:00:00-04:00"));
        Insert(Timed("r2", "Review two", "2026-10-20T10:00:00-04:00", "2026-10-20T11:00:00-04:00"));
        Insert(Timed("r3", "Review three", "2026-09-20T10:00:00-04:00", "2026-09-20T11:00:00-04:00"));

        Assert.Equal(["r2", "r3", "r1"], Find("review").Select(h => h.EventId));
    }

    [Fact]
    public void Find_PercentAndUnderscore_MatchLiterally()
    {
        Insert(Timed("p", "100% done", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00"));
        Insert(Timed("u", "a_b plan", "2026-10-08T12:00:00-04:00", "2026-10-08T13:00:00-04:00"));

        Assert.Equal(["p"], Find("%").Select(h => h.EventId));
        Assert.Equal(["u"], Find("_").Select(h => h.EventId));
    }

    [Fact]
    public void Find_HostileQuery_IsCleanedAndCapped()
    {
        Assert.Single(Find("‮dentist\u0000"));
        Assert.Empty(Find(new string('x', 5000)));
        Assert.Empty(Find("'; DROP TABLE events; --"));
        Assert.Single(Find("dentist"));                       // the table is still there
        Assert.Equal(100, EventSearch.Words(new string('y', 5000)).Single().Length);
    }

    [Fact]
    public void Find_BlankQuery_ReturnsNothing() => Assert.Empty(Find("   "));

    [Fact]
    public void Find_CanceledEvent_IsLeftOut()
    {
        Insert("""{"id":"ghost","status":"cancelled","summary":"Ghost","start":{"dateTime":"2026-10-08T10:00:00-04:00"},"end":{"dateTime":"2026-10-08T11:00:00-04:00"}}""");

        Assert.Empty(Find("ghost"));
    }

    [Fact]
    public void Find_SameEventInTwoAccounts_ShownOnce()
    {
        using (var conn = _db.Database.Open())
        {
            AccountStore.Upsert(conn, new Account("222", "two@example.com", null, null, AccountStatus.Ok));
            CalendarStore.ReplaceForAccount(conn, "222", [new CalendarListEntry { Id = "two@example.com", Summary = "two", AccessRole = "owner", Primary = true, Selected = true }]);
        }

        Insert(Timed("shared-a", "Board meeting", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00", ""","iCalUID":"board@x" """));
        Insert(Timed("shared-b", "Board meeting", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00", ""","iCalUID":"board@x" """), "222", "two@example.com");

        Assert.Single(Find("board"));
    }

    [Fact]
    public void Find_NonAsciiWord_StillFound()
    {
        Insert(Timed("cafe", "Café chat", "2026-10-08T10:00:00-04:00", "2026-10-08T11:00:00-04:00"));

        Assert.Single(Find("café"));
    }
}
```

If `CalendarStore.ReplaceForAccount` or `CalendarListEntry` need other required members, set them the way `CalendarStoreTests` does.

- [ ] **Step 2: Write the failing catalog and date tests**

`CommandCatalogTests.cs`:

```csharp
using LeafCalendar.Core.Search;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class CommandCatalogTests
{
    [Fact]
    public void All_IdsAreUnique() =>
        Assert.Equal(CommandCatalog.All.Count, CommandCatalog.All.Select(c => c.Id).Distinct().Count());

    [Fact]
    public void All_HasEverySpecAction()
    {
        // Spec 6.6: create, jump to date, switch view, join, overlay, Meet with, time travel, share availability,
        // toggle settings, open settings, show shortcuts
        var ids = CommandCatalog.All.Select(c => c.Id).ToHashSet();
        string[] expected = ["create-event", "go-to-date", "view-day", "view-week", "view-month", "join", "overlay",
            "meet-with", "time-travel", "share", "toggle-weekends", "settings", "shortcuts"];

        Assert.All(expected, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Match_Empty_ReturnsTheDefaults()
    {
        var match = CommandCatalog.Match("");

        Assert.Equal(CommandCatalog.Defaults, match);
        Assert.Equal("create-event", match[0].Id);
    }

    [Theory]
    [InlineData("share", "share")]
    [InlineData("time", "time-travel")]
    [InlineData("SETTINGS", "settings")]
    [InlineData("month", "view-month")]
    public void Match_TitleStart_RanksFirst(string query, string firstId) =>
        Assert.Equal(firstId, CommandCatalog.Match(query)[0].Id);

    [Fact]
    public void Match_Keywords_Count() =>
        Assert.Contains(CommandCatalog.Match("zoom"), c => c.Id.StartsWith("scale-", StringComparison.Ordinal));

    [Fact]
    public void Match_EveryWordMustHit() => Assert.Empty(CommandCatalog.Match("share zzz"));

    [Fact]
    public void Match_HostileText_DoesNotThrow() => Assert.Empty(CommandCatalog.Match("‮%_\\" + new string('q', 5000)));

    [Fact]
    public void Items_WithACommand_RunThroughIt()
    {
        var days = CommandCatalog.All.Single(c => c.Id == "view-days-3");

        Assert.Equal(CalendarCommand.ShareAvailability, CommandCatalog.All.Single(c => c.Id == "share").Command);
        Assert.Equal((CalendarCommand.Days, 3), (days.Command, days.Days));
    }
}
```

`DateQueryTests.cs`:

```csharp
using LeafCalendar.Core.Search;

namespace LeafCalendar.Tests;

public sealed class DateQueryTests
{
    static readonly DateOnly Today = new(2026, 10, 1); // a Thursday

    [Theory]
    [InlineData("today", 2026, 10, 1)]
    [InlineData("Tomorrow", 2026, 10, 2)]
    [InlineData("yesterday", 2026, 9, 30)]
    [InlineData("fri", 2026, 10, 2)]
    [InlineData("friday", 2026, 10, 2)]
    [InlineData("thu", 2026, 10, 1)]
    [InlineData("next thu", 2026, 10, 8)]
    [InlineData("next friday", 2026, 10, 9)]
    [InlineData("2026-10-12", 2026, 10, 12)]
    [InlineData("10/12", 2026, 10, 12)]
    [InlineData("10/12/2027", 2027, 10, 12)]
    [InlineData("oct 12", 2026, 10, 12)]
    [InlineData("October 12", 2026, 10, 12)]
    [InlineData("12 oct", 2026, 10, 12)]
    [InlineData("oct 12 2027", 2027, 10, 12)]
    [InlineData("jan 5", 2027, 1, 5)]   // more than 2 months back this year means next year
    [InlineData("sep 15", 2026, 9, 15)] // recent past stays this year
    [InlineData("in 3 days", 2026, 10, 4)]
    [InlineData("in 2 weeks", 2026, 10, 15)]
    public void TryParse_Understands(string text, int y, int m, int d)
    {
        Assert.True(DateQuery.TryParse(text, Today, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("feb 30")]
    [InlineData("13/45")]
    [InlineData("in 99999 days")]
    [InlineData("standup")]
    public void TryParse_Rejects(string text) => Assert.False(DateQuery.TryParse(text, Today, out _));

    [Fact]
    public void Label_AddsTheYearOnlyWhenItDiffers()
    {
        Assert.Equal("Mon, Oct 12", DateQuery.Label(new DateOnly(2026, 10, 12), Today));
        Assert.Equal("Tue, Jan 5, 2027", DateQuery.Label(new DateOnly(2027, 1, 5), Today));
    }
}
```

- [ ] **Step 3: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EventSearchTests"`, and the same for `*CommandCatalogTests` and `*DateQueryTests`.
Expected: build errors (the types don't exist).

- [ ] **Step 4: Implement `EventSearch`**

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Search;

/// <summary>Where a search word was found, best first.</summary>
public enum SearchField
{
    /// <summary>Title.</summary>
    Title,

    /// <summary>Location.</summary>
    Location,

    /// <summary>A guest's address or name.</summary>
    Guest,

    /// <summary>Description.</summary>
    Description,
}

/// <summary>An event the command menu found. A repeating event's <see cref="EventId"/> and times are one instance's.</summary>
public sealed record SearchHit(string AccountId, string CalendarId, string EventId, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string Color, SearchField Field);

/// <summary>
/// Searches every event Leaf has stored, past and future (spec 6.6), by title, location, guests, and description.
/// </summary>
/// <remarks>
/// <para>
/// The query is untrusted text: control and format characters are removed, it's capped at <see cref="MaxQuery"/>
/// characters, and it's split into words that must all match (case-insensitive). SQL only narrows the rows. It uses a
/// parameterized <c>LIKE</c> with <c>%</c>, <c>_</c>, and <c>\</c> escaped, and only for a word whose characters JSON
/// never escapes. Matching happens on the parsed event.
/// </para>
/// <para>
/// Title matches come first, then upcoming events (soonest first), then past ones (newest first). The same event in
/// two accounts (one iCalUID) is listed once. Nothing is logged.
/// </para>
/// </remarks>
public static partial class EventSearch
{
    /// <summary>Longest query used.</summary>
    public const int MaxQuery = 100;

    /// <summary>Most results.</summary>
    public const int MaxResults = 20;

    // ponytail: scans at most this many rows nearest to now; an FTS table is the upgrade if people keep years of events
    const int MaxRows = 3000;

    const string Sql = """
        SELECT e.account_id, e.calendar_id, e.id, e.ical_uid, e.start_utc, e.end_utc, e.is_all_day, e.is_recurring_master,
               e.raw_json, COALESCE(c.leaf_color, c.background_color, '#4285F4')
        FROM events e
        JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
        WHERE COALESCE(c.leaf_hidden, c.hidden) = 0
          AND e.status <> 'cancelled'
          AND e.raw_json LIKE $like ESCAPE '\'
        ORDER BY ABS(COALESCE(e.start_utc, 0) - $now)
        LIMIT $max;
        """;

    /// <summary>The cleaned search words.</summary>
    public static IReadOnlyList<string> Words(string? query)
    {
        var plain = new string((query ?? "").Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (plain.Length > MaxQuery)
        {
            plain = plain[..(char.IsHighSurrogate(plain[MaxQuery - 1]) ? MaxQuery - 1 : MaxQuery)];
        }

        return plain.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Finds events matching every word of <paramref name="query"/>.</summary>
    public static IReadOnlyList<SearchHit> Find(SqliteConnection conn, string? query, DateTimeOffset now, TimeZoneInfo zone)
    {
        // Nothing To Search
        var words = Words(query);
        if (words.Count == 0)
        {
            return [];
        }

        // Narrow In SQL (the longest word JSON stores as-is), Then Match The Parsed Event
        var narrow = words.Where(w => SafeForLike().IsMatch(w)).OrderByDescending(w => w.Length).FirstOrDefault();
        var like   = narrow is null ? "%" : "%" + narrow.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var rows   = conn.Query(null, Sql, Row.Read, ("$like", like), ("$now", now.ToUnixTimeMilliseconds()), ("$max", MaxRows));

        var hits = new List<(Row Row, string Title, SearchField Field)>();
        foreach (var row in rows)
        {
            if (Match(row.RawJson, words) is { } match)
            {
                hits.Add((row, match.Title, match.Field));
            }
        }

        // Repeating Events Show One Instance
        var instances = hits.Exists(h => h.Row.IsMaster) ? Instances(conn, now, zone) : [];

        return hits
            .Select(h => ToHit(h.Row, h.Title, h.Field, now, instances))
            .DistinctBy(h => h.Key)
            .Select(h => h.Hit)
            .OrderBy(h => h.Field == SearchField.Title ? 0 : 1)
            .ThenBy(h => h.End > now ? 0 : 1)
            .ThenBy(h => h.End > now ? h.Start.UtcTicks : -h.Start.UtcTicks)
            .Take(MaxResults)
            .ToList();
    }
```

Then write these private members, each documented:
- `Row` record. It holds `AccountId`, `CalendarId`, `Id`, `ICalUid`, `StartMs`, `EndMs`, `IsAllDay`, `IsMaster`, `RawJson`, and `Color`, plus `static Row Read(SqliteDataReader r)`, in the shape of `OccurrenceQuery.Row`.
- `Match(string rawJson, IReadOnlyList<string> words)` → `(string Title, SearchField Field)?`:
  1. Parse with `EventDetailsParser.Parse(rawJson)`.
  2. The guest text is every `attendees[].email` and `attendees[].displayName`, read with `JsonDocument`.
  3. The description is plain text: run `EventDetailsParser.HtmlToText` if `Parse` keeps HTML. Check it.
  4. Every word must be found (`Contains`, `OrdinalIgnoreCase`) in title, location, guest text, or description.
  5. The field is the best field where the **first** word is found (Title > Location > Guest > Description).
  6. Malformed JSON is no match (catch `JsonException`).
- `Instances(conn, now, zone)`: `OccurrenceQuery.Load(conn, today - 366 days, today + 366 days, zone, includeDeclined: true)`, where `today` is `now`'s date in `zone`. The query already honors canceled and moved instances.
- `ToHit(...)` → `(SearchHit Hit, string Key)`:
  - **Single or exception row:** times from `StartMs`/`EndMs`.
  - **Master:** the first instance in `instances` whose `(RecurringEventId ?? EventId) == row.Id` (same account and calendar) and that ends after `now`. Else the latest such instance. Else the master's own times. Use that instance's `EventId`.
  - `Key` is `ICalUid` when present (dedupe across accounts), else `"{AccountId}|{CalendarId}|{EventId}"`.
- `[GeneratedRegex("^[A-Za-z0-9%_.@-]+$")] private static partial Regex SafeForLike();`. These characters are never escaped by System.Text.Json's default encoder, so they appear in `raw_json` exactly as typed. Other words (`café`, `O'Brien`) fall back to `%`, a capped full scan.

- [ ] **Step 5: Implement `CommandCatalog`**

```csharp
using System.Globalization;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Search;

/// <summary>
/// An action in the command menu (spec 6.6). Items with a <see cref="Command"/> run exactly as their shortcut does;
/// the rest are run by <see cref="Id"/> in the app. <see cref="Keys"/> is the shortcut text shown on the right, or empty.
/// </summary>
public sealed record CommandItem(string Id, string Title, string Keys, CalendarCommand Command = CalendarCommand.None, int Days = 0, string Keywords = "");

/// <summary>Every command-menu action, and matching typed text against them.</summary>
public static class CommandCatalog
{
    /// <summary>Every action, in the order shown when scores tie.</summary>
    public static IReadOnlyList<CommandItem> All { get; } =
    [
        new("create-event", "Create event", "C", CalendarCommand.CreateEvent, Keywords: "new add"),
        new("go-to-date", "Jump to date…", ".", CalendarCommand.GoToDate, Keywords: "go calendar"),
        new("today", "Go to today", "T", CalendarCommand.Today),
        new("view-day", "Switch to day view", "D", CalendarCommand.DayView, Keywords: "view"),
        new("view-week", "Switch to week view", "W", CalendarCommand.WeekView, Keywords: "view"),
        new("view-month", "Switch to month view", "M", CalendarCommand.MonthView, Keywords: "view"),
        .. Enumerable.Range(2, 8).Select(n => new CommandItem($"view-days-{n}", $"Show {n} days", n.ToString(CultureInfo.InvariantCulture), CalendarCommand.Days, n, "view")),
        new("join", "Join next meeting", "Ctrl+J", CalendarCommand.JoinMeeting, Keywords: "call video meet"),
        new("overlay", "Overlay a teammate's calendar…", "P", CalendarCommand.PeopleOverlay, Keywords: "people person busy"),
        new("meet-with", "Meet with…", "F", CalendarCommand.MeetWith, Keywords: "people find time schedule"),
        new("time-travel", "Time travel to a time zone…", "Z", CalendarCommand.TimeTravel, Keywords: "zone timezone"),
        new("share", "Share availability", "S", CalendarCommand.ShareAvailability, Keywords: "free slots copy"),
        new("toggle-weekends", "Show or hide weekends", "Ctrl+Shift+E", CalendarCommand.ToggleWeekends, Keywords: "toggle"),
        new("toggle-declined", "Show or hide declined events", "Ctrl+Shift+D", CalendarCommand.ToggleDeclined, Keywords: "toggle"),
        new("toggle-week-numbers", "Show or hide week numbers", "", Keywords: "toggle"),
        new("toggle-24-hour", "Turn 24-hour time on or off", "", Keywords: "toggle clock"),
        new("toggle-working-hours", "Show or hide working hours", "", Keywords: "toggle shading"),
        new("toggle-theme", "Switch between light and dark", "Ctrl+Shift+L", CalendarCommand.ToggleTheme, Keywords: "theme"),
        .. LeafSettings.ScaleChoices.Select(s => new CommandItem($"scale-{Percent(s)}", $"Interface scale {Percent(s)}%", "", Keywords: "zoom size")),
        new("settings", "Open settings", "Ctrl+,", CalendarCommand.OpenSettings, Keywords: "preferences options"),
        new("settings-general", "Settings: General", "", Keywords: "preferences"),
        new("settings-calendars", "Settings: Calendars", "", Keywords: "preferences colors"),
        new("settings-time-zones", "Settings: Time zones", "", Keywords: "preferences"),
        new("settings-notifications", "Settings: Notifications", "", Keywords: "preferences reminders"),
        new("settings-tray", "Settings: Tray", "", Keywords: "preferences"),
        new("settings-shortcuts", "Settings: Shortcuts", "", Keywords: "preferences keys"),
        new("settings-accounts", "Settings: Accounts", "", Keywords: "preferences google"),
        new("settings-about", "Settings: About", "", Keywords: "version licenses logs"),
        new("shortcuts", "Show keyboard shortcuts", "?", CalendarCommand.ShortcutSheet, Keywords: "keys help cheat sheet"),
        new("sync", "Sync now", "", Keywords: "refresh"),
        new("back", "Go back", "Alt+Left", CalendarCommand.NavigateBack),
        new("forward", "Go forward", "Alt+Right", CalendarCommand.NavigateForward),
    ];

    /// <summary>What an empty menu shows.</summary>
    public static IReadOnlyList<CommandItem> Defaults { get; } =
        [.. new[] { "create-event", "go-to-date", "share", "meet-with", "overlay", "time-travel", "settings", "shortcuts" }.Select(id => All.Single(c => c.Id == id))];
```

Then write `Match`:
1. Take the words from `EventSearch.Words(query)`, lowercased invariant.
2. No words: return `Defaults`.
3. For each item, score each word:
   - 3 if the title starts with it
   - 2 if a title word starts with it
   - 1 if the title contains it, or a keyword starts with it
   - 0 otherwise, which drops the item
4. Sum the scores.
5. Order by score descending, then by `All` order, and take `max`.

`static string Percent(double s) => ((int)Math.Round(s * 100)).ToString(CultureInfo.InvariantCulture);`. Declare `Percent` above `All`, so static initialization order can't bite.

- [ ] **Step 6: Implement `DateQuery`**

`static class DateQuery` with `TryParse` and `Label`. Formats use `CultureInfo.GetCultureInfo("en-US")`. Write it as a sequence of guard-style tries:
1. Trim and lowercase the text; it must be 1 to 40 characters.
2. `today`, `tomorrow`, `yesterday`.
3. `in N day(s)|week(s)`, with N from 1 to 3660.
4. `[next ]<weekday>`, full or 3-letter name. The weekday alone means the soonest one on or after today; `next` adds 7 days.
5. ISO `yyyy-MM-dd`.
6. `M/d` or `M/d/yyyy`.
7. `MMM d`, `MMMM d`, `d MMM`, `d MMMM`, optionally followed by ` yyyy`.

A month and day without a year use this year, unless that date is more than 2 months before today, which means next year. Every candidate goes through `DateOnly.TryParseExact` or the `DateOnly` constructor inside `try`, so `feb 30` is a no. `Label` is `"ddd, MMM d"`, plus `", yyyy"` when the year isn't today's.

- [ ] **Step 7: Run the three test classes and see them pass**

Run the Step 3 commands. Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add -A src/LeafCalendar.Core/Search tests/LeafCalendar.Tests
git commit -m "feat(core): event search, command catalog, and typed dates for the command menu"
```

---

### Task 3: Command Menu, Search Icon, and Back Button

**Files:**
- Create: `src/LeafCalendar.App/Views/CommandMenu.xaml`, `CommandMenu.xaml.cs`
- Create: `src/LeafCalendar.App/ViewModels/CalendarViewModel.Search.cs`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.Navigate.cs` (`AttachNavigate`, `OpenCommandMenu`)
- Modify: `src/LeafCalendar.App/MainWindow.xaml`, `MainWindow.xaml.cs` (search icon, back button)
- Create: `tests/LeafCalendar.UITests/CommandMenuTests.cs`

**Interfaces:**
- Consumes:
  - `EventSearch`, `CommandCatalog`, `DateQuery` (Task 2)
  - `CalendarPage.RunCommand` (Task 1)
  - `OccurrenceLookup.Find`, and `LatestSearch` (`Core/People/LatestSearch.cs`, which drops stale answers while typing)
- Produces:
  - `CalendarViewModel`:
    - `Task<IReadOnlyList<SearchHit>> SearchEventsAsync(string query, CancellationToken ct)`
    - `bool OpenSearchHit(SearchHit hit, bool jump)`
    - `[ObservableProperty] bool ShowBack`
    - `void BackFromJump()`
  - `public sealed class CommandRow` (App): `Kind` (`Event`, `Action`, `Date`), `Title`, `Detail`, `Keys`, `Glyph`, `Color`, `AutomationId`, plus the payload (`SearchHit?`, `CommandItem?`, `DateOnly?`)
  - `CalendarPage.MiniMonthNextButton` (`FrameworkElement?`, for the title bar)
  - Automation IDs:
    - `CommandMenu`, `CommandSearchBox`, `CommandResults`, `CommandEmpty`
    - `SearchResult_<eventId>`, `CommandResult_<id>`, `CommandResult_date`
    - `SearchButton` (title bar)

- [ ] **Step 1: Write the UI tests (they compile; Task 14 runs them)**

`CommandMenuTests.cs` follows `SidebarTests`' shape: a `FakeGoogleServer`, a seeded profile, and `Launch()` with `--start-date 2026-10-01`. Write each test fully:

```csharp
    [Fact]
    public void CtrlK_OpensTheMenu_WithFocusInTheSearchBox()
    {
        // Ctrl+K → CommandMenu exists, CommandSearchBox has keyboard focus, CommandResult_create-event is listed first
    }

    [Theory]
    [InlineData("Ctrl+F")]
    [InlineData("/")]
    public void CtrlFAndSlash_OpenTheSameMenu(string keys)
    {
        // press keys → CommandSearchBox focused; Esc closes it (CommandMenu gone)
    }

    [Fact]
    public void SearchIcon_IsCenteredOverTheNextMonthButton_AndOpensTheMenu()
    {
        // SearchButton's center X equals MiniMonthNext's center X within 1 px; its center Y is the title bar's (24 DIPs);
        // click → CommandSearchBox focused
    }

    [Fact]
    public void SearchIcon_SidebarClosed_StaysVisibleAfterTheTitle()
    {
        // toggle the sidebar closed (the title bar pane toggle) → SearchButton visible and enabled, left of the period title's right edge
    }

    [Fact]
    public void TypeATitle_EnterOpensDetails_WithoutMovingTheCalendar()
    {
        // jump with "." to 2026-11-02 first; Ctrl+K, type "dentist" → SearchResult_evt-single → Enter →
        // DetailsTitle reads "Dentist appointment"; PeriodTitle still shows November
    }

    [Fact]
    public void AltEnter_JumpsToTheEvent_AndBackReturns()
    {
        // Ctrl+K, type "holiday" (evt-allday, Oct 12) → Alt+Enter → PeriodTitle covers Oct 12; the title bar's Back button
        // (the stock TitleBar back button, found by Name "Back") is visible; click it → PeriodTitle back to the Oct 1 week; Back gone
    }

    [Fact]
    public void TypeADate_GoesThere()
    {
        // Ctrl+K, type "oct 20" → first row CommandResult_date reads "Go to Tue, Oct 20" → Enter → PeriodTitle covers Oct 20
    }

    [Fact]
    public void TypeAnAction_RunsIt()
    {
        // Ctrl+K, type "month" → first row CommandResult_view-month → Enter → ViewModeButton's Name is "Month"
    }

    [Fact]
    public void ToggleFromTheMenu_ChangesTheSetting()
    {
        // Ctrl+K, type "week numbers" → Enter → open Settings › General: WeekNumbersSwitch is on
    }

    [Fact]
    public void ArrowKeys_MoveTheSelection_FocusStaysInTheBox()
    {
        // Ctrl+K (defaults), Down twice → CommandResults' selected item is the third default; CommandSearchBox still has focus
    }

    [Fact]
    public void HostileQuery_ShowsNothing_AndLeafKeepsRunning()
    {
        // type "%_\\'" + U+202E + 300 × "x" → CommandEmpty visible; then clear and type "dentist" → SearchResult_evt-single
    }

    [Fact]
    public void Searching_WritesNoSearchTextToTheLog()
    {
        // type "dentist", wait for the result, close Leaf; <ProfileFolder>\Logs\leaf.log contains neither "dentist" nor "Dentist"
    }
```

- [ ] **Step 2: The view model side**

`CalendarViewModel.Search.cs`:

```csharp
// Track A owns this file: command-menu search and the jump-back state.
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Search;

namespace LeafCalendar.App.ViewModels;

public sealed partial class CalendarViewModel
{
    DateOnly? _jumpedTo;

    /// <summary>True right after a command-menu jump, until you go back or move elsewhere (spec 6.2 item 2).</summary>
    [ObservableProperty]
    public partial bool ShowBack { get; set; }

    /// <summary>Searches stored events off the UI thread. The query is never logged.</summary>
    public Task<IReadOnlyList<SearchHit>> SearchEventsAsync(string query, CancellationToken ct)
    {
        var (now, zone) = (Now, Zone);
        return Task.Run(() =>
        {
            using var conn = _services.Database.Open();
            return EventSearch.Find(conn, query, now, zone);
        }, ct);
    }

    /// <summary>
    /// Opens a search result: its details (Enter), or the calendar moved to it as well (Alt+Enter, which shows Back).
    /// False when the event is gone (deleted since the search).
    /// </summary>
    public bool OpenSearchHit(SearchHit hit, bool jump)
    {
        CalendarOccurrence? occurrence;
        using (var conn = _services.Database.Open())
        {
            occurrence = OccurrenceLookup.Find(conn, hit.AccountId, hit.CalendarId, hit.EventId, hit.Start, Zone);
        }

        if (occurrence is null)
        {
            ShowMessage("That event isn't in Leaf anymore");
            return false;
        }

        // Jump: move the calendar first, so Back has a place to return to
        if (jump)
        {
            NavigateTo(DateOnly.FromDateTime(occurrence.StartIn(Zone).DateTime));
            _jumpedTo = PeriodStart;
            ShowBack  = true;
            ScrollToTimeRequested?.Invoke(this, occurrence.StartIn(Zone));
        }

        Select(occurrence);
        DetailsOpenRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The title bar's Back after a jump.</summary>
    public void BackFromJump()
    {
        ShowBack  = false;
        _jumpedTo = null;
        GoBack();
    }

    // Any other move hides Back
    partial void OnPeriodStartChanged(DateOnly value)
    {
        if (ShowBack && value != _jumpedTo)
        {
            ShowBack = false;
        }
    }
}
```

If the main file already implements `OnPeriodStartChanged`, don't touch it. Instead, listen to `ViewModel.PropertyChanged` for `PeriodStart` in `AttachNavigate`, and do the same check there. Events declared in the same class can be raised from any part of it. If `DateOnly.FromDateTime(...DateTime)` gives the wrong day for an all-day event, use `occurrence.AllDayStart` when `IsAllDay`.

- [ ] **Step 3: The menu control**

`CommandMenu.xaml` is a `UserControl`, 560 wide. Top to bottom:
1. A `TextBox` `CommandSearchBox`, `PlaceholderText="Search events, or type a command or a date"`, `Margin="12"`, `AutomationProperties.Name="Search"`.
2. A 1 DIP divider (`DividerStrokeColorDefaultBrush`).
3. A `ListView` `CommandResults`, `MaxHeight="400"`, `SelectionMode="Single"`, `IsItemClickEnabled="True"`, `Padding="4"`, with `ScrollIndicator.ShowOnHover` on its scroller.
4. A `TextBlock` `CommandEmpty`, Caption, secondary, `Margin="16,12"`, text "No events or actions match.", collapsed until empty.

The row template:

```xml
<!-- Result Row -->
<DataTemplate x:DataType="local:CommandRow">
    <Grid
        MinHeight="40"
        Padding="8,6"
        ColumnSpacing="12"
        AutomationProperties.Name="{x:Bind Title}"
        AutomationProperties.AutomationId="{x:Bind AutomationId}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="16" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>

        <!-- Event Color, Or The Action's Glyph -->
        <Border
            Width="10"
            Height="10"
            CornerRadius="2"
            Background="{x:Bind local:CommandMenu.Brush(Color)}"
            Visibility="{x:Bind EventVisibility}" />
        <FontIcon
            FontSize="16"
            Glyph="{x:Bind Glyph}"
            Visibility="{x:Bind ActionVisibility}"
            AutomationProperties.AccessibilityView="Raw" />

        <!-- Title And Detail -->
        <StackPanel Grid.Column="1" VerticalAlignment="Center">
            <TextBlock Text="{x:Bind Title}" TextTrimming="CharacterEllipsis" />
            <TextBlock
                Style="{StaticResource CaptionTextBlockStyle}"
                Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                Text="{x:Bind Detail}"
                Visibility="{x:Bind DetailVisibility}" />
        </StackPanel>

        <!-- Shortcut -->
        <TextBlock
            Grid.Column="2"
            VerticalAlignment="Center"
            Style="{StaticResource CaptionTextBlockStyle}"
            Foreground="{ThemeResource TextFillColorSecondaryBrush}"
            Text="{x:Bind Keys}" />
    </Grid>
</DataTemplate>
<!-- /Result Row -->
```

`CommandRow` is a plain sealed App class with get-only properties set in its constructor. `EventVisibility`, `ActionVisibility`, and `DetailVisibility` are `Visibility` values computed once. `Detail` for an event is `"{ddd, MMM d} · {time or "All day"}"`, using `Settings.Use24HourTime`, plus `" · in location"`, `" · in guests"`, or `" · in description"` when the match isn't in the title. `Brush` lives in `CommandMenu`: `public static Brush Brush(string hex)`. It parses `#RRGGBB` into a cached `SolidColorBrush`, using the helper `CalendarsPage.ColorSource` uses if there is one.

`CommandMenu.xaml.cs` behavior:
- **Constructor:** takes `CalendarViewModel` and an `Action<CommandRow, bool> run`. `run`'s bool is "jump" (Alt+Enter).
- **Rows:** `List<CommandRow>`, set as `ItemsSource` in code (a concrete list, AOT).
- **Typing:** `TextChanged` restarts a 120 ms `DispatcherQueueTimer`. When it fires:
  1. Build the date row: `DateQuery.TryParse` → `Kind = Date`, `"Go to " + DateQuery.Label(...)`, `AutomationId = "CommandResult_date"`.
  2. Build action rows from `CommandCatalog.Match`, each with `AutomationId = $"CommandResult_{item.Id}"` and a glyph by ID. Use the design-standard glyphs: Settings `E713`, Search `E721`, and so on. Anything else is `E7C3`.
  3. Run `SearchEventsAsync` through a `LatestSearch` so a slower older answer can't overwrite a newer one.
  4. Show the date row first, then the events (`AutomationId = $"SearchResult_{hit.EventId}"`), then the actions.
  5. Select the first row. Show `CommandEmpty` when there are none.
- **Keys** (`KeyDown` on `CommandSearchBox`):
  - Down and Up move `CommandResults.SelectedIndex` (clamped) and call `ScrollIntoView`. Focus stays in the box.
  - Enter runs the selected row, with jump = `KeyState.IsDown(VirtualKey.Menu)`.
  - If Alt+Enter doesn't arrive in `KeyDown` (WinUI may route it as a system key), add a `KeyboardAccelerator` with `Key="Enter" Modifiers="Menu"` on the box that runs with jump = true.
- **Clicks:** `ItemClick` runs the clicked row (compare by reference against `_rows`).
- **Reset:** `public void Reset()` clears the text and shows the defaults.

- [ ] **Step 4: Open it, and run what it picks**

In `CalendarPage.Navigate.cs`:
- Keep one `Flyout _commandFlyout` (created on first use) whose `Content` is the `CommandMenu`. Give it a `FlyoutPresenterStyle` with `Padding="0"`, an 8 DIP corner radius, and `MaxWidth="600"`. Build that style in code with `Setter`s, not by reading resources back.
- `OpenCommandMenu()`:
  1. `menu.Reset()`.
  2. `_commandFlyout.ShowAt(Root, new FlyoutShowOptions { Position = new Point(Root.ActualWidth / 2, 56), Placement = FlyoutPlacementMode.Bottom, ShowMode = FlyoutShowMode.Standard })`.
  3. On `Opened`, focus `CommandSearchBox` with `FocusState.Programmatic`.
- `Run(CommandRow row, bool jump)`: hide the flyout first, then:
  - **Event:** `ViewModel.OpenSearchHit(hit, jump)`
  - **Date:** `ViewModel.NavigateTo(date)`
  - **Action with a `Command`:** `RunCommand(item.Command, item.Days)`
  - **Action by `Id`:**

    | Id | Does |
    |---|---|
    | `toggle-week-numbers` | `ViewModel.Update(s => s with { ShowWeekNumbers = !s.ShowWeekNumbers })` |
    | `toggle-24-hour` | `ViewModel.Update(s => s with { Use24HourTime = !s.Use24HourTime })` |
    | `toggle-working-hours` | `ViewModel.Update(s => s with { WorkingHours = s.WorkingHours with { Enabled = !s.WorkingHours.Enabled } })` |
    | `scale-NN` | `ViewModel.Update(s => s with { InterfaceScale = NN / 100.0 })` |
    | `settings-<page>` | `ViewModel.OpenSettings?.Invoke(SettingsSection.<Page>)` |
    | `sync` | `ViewModel.Fire(ViewModel.RefreshAsync)` |

    An unknown ID does nothing.
- `public FrameworkElement? MiniMonthNextButton`: walk `Sidebar` with `VisualTreeHelper` once (after load) and keep the element whose `AutomationProperties.GetAutomationId` is `"MiniMonthNext"`. That's a string read, not a typed WinRT read-back, and `SidebarView.xaml` stays untouched.

- [ ] **Step 5: Title bar search icon and Back**

`MainWindow.xaml`, inside `ToolbarHost`, before `ToolbarSlide`:

```xml
<!-- Search (opens the command menu; over the sidebar, centered on the mini month's Next month button; positioned in code) -->
<Button
    x:Name="SearchButton"
    HorizontalAlignment="Left"
    Style="{StaticResource LeafBareIconButtonStyle}"
    Click="OnSearchClick"
    ToolTipService.ToolTip="Search (Ctrl+K)"
    AutomationProperties.Name="Search"
    AutomationProperties.AutomationId="SearchButton">
    <FontIcon Glyph="&#xE721;" FontSize="16" />
</Button>
```

In `MainWindow.xaml.cs`:
- **`PlaceSearchButton()`.** Call it wherever the toolbar is laid out today (the method that sets `CalendarToolbar.Margin`), and when the sidebar opens or closes:
  - **Sidebar open:** take the Next month button's center X in window DIPs (`page.MiniMonthNextButton.TransformToVisual(RootGrid)`). Subtract `ToolbarHost`'s window X and 16, and use that as `SearchButton.Margin.Left`.
  - **Sidebar closed, or the result is under 8:** 8.

  Then call `AppTitleBar.RecomputeDragRegions()`. Only the button takes clicks.
- **`OnSearchClick`:** `if (ContentFrame.Content is CalendarPage page) page.RunCommand(CalendarCommand.CommandMenu);`. This is the same pattern `HandleShortcut` already uses.
- **Back:** listen to `_calendar.PropertyChanged` for `ShowBack`. On a change, set `AppTitleBar.IsBackButtonVisible = _calendar.ShowBack` and recompute the drag regions. Add `BackRequested="OnBackRequested"` to the `TitleBar`, with `void OnBackRequested(TitleBar sender, object args) => _calendar.BackFromJump();`.

- [ ] **Step 6: Build**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "feat(app): command menu with event search, actions, and dates; title bar search icon and Back after a jump"
```

---

### Task 4: Cheat Sheet, Shortcuts Link, About Page, and Tray Calendars

**Files:**
- Create: `src/LeafCalendar.Core/Views/ShortcutCatalog.cs`, `src/LeafCalendar.App/Views/ShortcutSheet.cs`, `src/LeafCalendar.App/Assets/ThirdPartyNotices.txt`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.Navigate.cs` (`ShowShortcutSheet`)
- Modify: `src/LeafCalendar.App/Views/Settings/ShortcutsPage.xaml(.cs)`, `AboutPage.xaml(.cs)`, `TrayPage.xaml(.cs)`
- Modify: `src/LeafCalendar.App/LeafServices.cs` (`OpenFolderAsync`), `src/LeafCalendar.App/App.xaml.cs` (tray exclusions), `src/LeafCalendar.App/LeafCalendar.App.csproj` (notices as content)
- Modify: `src/LeafCalendar.Core/Tray/TrayAgenda.cs`
- Test: `tests/LeafCalendar.Tests/ShortcutCatalogTests.cs` (new), `TrayAgendaTests.cs`; `tests/LeafCalendar.UITests/ShortcutSheetTests.cs`, `TraySettingsCalendarsTests.cs` (new)

**Interfaces:**
- Consumes: `ShortcutMap.Resolve`, `KeySequence`, `LeafSettings.JoinShortcut`/`FlyoutShortcut`/`TrayExcludedCalendars`.
- Produces:
  - `public sealed record ShortcutRow(string Section, string Keys, string Action, CalendarCommand Command = CalendarCommand.None)`
  - `ShortcutCatalog.Rows`, `ShortcutCatalog.Sections`, `ShortcutCatalog.Footnote`, and `ShortcutCatalog.Filter(string? query)`
  - `ShortcutSheet.ShowAsync(XamlRoot root, LeafSettings settings)`
  - `LeafServices.OpenFolderAsync(string path)`
  - `TrayAgenda.Load(..., IReadOnlyCollection<CalendarRef>? excluded = null)` (new last parameter)
  - Automation IDs: `ShortcutSheet`, `ShortcutFilterBox`, `ShortcutRow_<index>`, `ShowCheatSheetButton`, `OpenLogsButton`, `LicensesButton`, `LicensesText`, `TrayCalendar_<calendarId>`

- [ ] **Step 1: Write the failing catalog and tray tests**

`ShortcutCatalogTests.cs`:

```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void Sections_InSpecOrder() =>
        Assert.Equal(["Navigation", "App", "Events", "Selection", "Scheduling and people"], ShortcutCatalog.Sections);

    [Fact]
    public void EveryMappedCommand_IsListed()
    {
        // One sample chord per command ShortcutMap can return
        (string Key, bool Ctrl, bool Shift, bool Alt)[] chords =
        [
            ("T", false, false, false), ("Left", false, false, false), ("Right", false, false, false), ("N", false, false, false),
            ("B", false, false, false), ("190", false, false, false), ("D", false, false, false), ("W", false, false, false),
            ("M", false, false, false), ("Number3", false, false, false), ("Z", false, false, false), ("K", true, false, false),
            ("F", true, false, false), ("191", false, true, false), ("188", true, false, false), ("Z", true, false, false),
            ("Left", false, false, true), ("L", true, true, false), ("187", true, false, false), ("E", true, true, false),
            ("D", true, true, false), ("C", false, false, false), ("Delete", false, false, false), ("Delete", true, true, false),
            ("J", true, false, false), ("V", false, false, false), ("X", false, false, false), ("A", true, false, false),
            ("C", true, false, false), ("S", false, false, false), ("P", false, false, false), ("F", false, false, false),
        ];
        var listed = ShortcutCatalog.Rows.Select(r => r.Command).ToHashSet();

        Assert.All(chords, c => Assert.Contains(ShortcutMap.Resolve(c.Key, c.Ctrl, c.Shift, c.Alt).Command, listed));
        Assert.Contains(CalendarCommand.EditTimeZone, listed);
        Assert.Contains(CalendarCommand.ParticipantOverlay, listed);
    }

    [Theory]
    [InlineData("rsvp", 1)]
    [InlineData("ctrl+k", 1)]
    [InlineData("time zone", 2)] // "Z" time travel, "E then Z"
    public void Filter_MatchesKeysAndActions(string query, int count) => Assert.Equal(count, ShortcutCatalog.Filter(query).Count);

    [Fact]
    public void Filter_Empty_IsEverything() => Assert.Equal(ShortcutCatalog.Rows.Count, ShortcutCatalog.Filter(" ").Count);
}
```

Add to `TrayAgendaTests.cs` (use that file's fixture setup and names):

```csharp
    [Fact]
    public void Load_ExcludedCalendar_IsLeftOut()
    {
        using var conn = _db.Database.Open();

        var all  = TrayAgenda.Load(conn, Now, NewYork, 14, includeAllDay: true, use24Hour: false);
        var some = TrayAgenda.Load(conn, Now, NewYork, 14, includeAllDay: true, use24Hour: false, excluded: [new CalendarRef(Account, "family123@group.calendar.google.com")]);

        Assert.Contains(all.SelectMany(d => d.Items), i => i.Occurrence.CalendarId == "family123@group.calendar.google.com");
        Assert.DoesNotContain(some.SelectMany(d => d.Items), i => i.Occurrence.CalendarId == "family123@group.calendar.google.com");
    }
```

If `TrayAgendaTests` doesn't seed the family calendar's events, insert one family event in this test first.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*ShortcutCatalogTests"`, then `--filter-class "*TrayAgendaTests"`.
Expected: build errors.

- [ ] **Step 3: Implement**

`ShortcutCatalog.cs`: `Rows` is spec 8.7's tables, **copied verbatim** (keys and action text, with the backticks removed), in spec order, each row tagged with its `CalendarCommand`. The section names are the spec's headings in sentence case: Navigation, App, Events, Selection, Scheduling and people.
- **Navigation:** every row, through "Z · Time travel to another time zone".
- **App:** every row of the spec's App table. "Undo the last delete (this session)" and "Back / forward through visited views" are included.
- **Events and Selection:** every row, except "Shift+drag · Box select". Box select is deferred to Milestone 6, and a cheat sheet mustn't list a key that does nothing (owner ruling below, recorded in Task 14).
- **Scheduling and people:** `S`, `P`, `F`.
- **`Footnote`:** "Ctrl+wheel over the time grid zooms like Ctrl+= / Ctrl+-." (spec text, verbatim).
- **`Filter`:** every word of the query (lowercased, with spaces kept inside keys like "ctrl+k") must be found in `Keys` or `Action`, case-insensitive.

`TrayAgenda.Load`: add `IReadOnlyCollection<CalendarRef>? excluded = null` as the last parameter. Filter occurrences whose `new CalendarRef(o.AccountId, o.CalendarId)` is in it, and update the summary. In `App.xaml.cs`, pass `settings.TrayExcludedCalendars` at every `TrayAgenda.Load` call, and add `settings.TrayExcludedCalendars.Count` to the flyout's cache key tuple. The list only changes through Settings, so the count is enough.

`LeafServices.OpenFolderAsync(string path)`:
- In fake-Google mode, append `folder:<path>` to `launched.txt`, as `LaunchAsync` does for links.
- Otherwise, `await Launcher.LaunchFolderPathAsync(path)` inside `try`, logging failures by type only.
- Document that it's the only folder launch and takes Leaf's own folders only.

`ShortcutSheet.cs` is a static class with `ShowAsync(XamlRoot root, LeafSettings settings)`:
- A `ContentDialog`, title "Keyboard shortcuts", close button "Close", `AutomationId` "ShortcutSheet".
- Content: a `TextBox` `ShortcutFilterBox` (`PlaceholderText="Search shortcuts"`), then a `ScrollViewer` (`ShowOnHover`, `MaxHeight="480"`) over a `StackPanel` that's rebuilt on each `TextChanged`.
- For each section with matching rows: a `BodyStrongTextBlockStyle` header (margin 0,16,0,4; 0 top on the first), then one `Grid` per row. The action goes left, and the keys go right in a `Border` (`ControlFillColorDefaultBrush` background, `CardStrokeColorDefaultBrush` 1 px, `CornerRadius` 4, padding 6,2, Caption text). The row's `AutomationProperties.Name` is `"{Action}: {Keys}"`, and its AutomationId is `ShortcutRow_<index in Rows>`.
- Then a last section, "Anywhere in Windows", with the two global shortcuts from `settings` ("Join meeting", "Show or hide the tray flyout"). An empty shortcut reads "Off".
- Then the footnote in Caption, secondary.
- Width 560.

`CalendarPage.Navigate.ShowShortcutSheet()` → `_ = ShortcutSheet.ShowAsync(XamlRoot, ViewModel.Settings);` inside a `try` that logs by type. A second dialog while one is open throws in WinUI, so guard with a `bool _sheetOpen`.

`ShortcutsPage.xaml`: add a group "In-app shortcuts" with a `SettingRow`, `Glyph="&#xE765;"`, `Header="Keyboard shortcuts"`, `Description="See every shortcut you can use in Leaf. Press ? in the main window to open this list."`. Its button is "Show shortcuts" (`ShowCheatSheetButton`), which calls `ShortcutSheet.ShowAsync(XamlRoot, _context.Calendar.Settings)`.

`AboutPage.xaml`:
- A group "Troubleshooting" with a row `Glyph="&#xE9F9;"`, `Header="Logs"`, `Description="Leaf's log never includes your events, guests, or searches."`, and a button "Open log folder" (`OpenLogsButton`) → `_context.Services.OpenFolderAsync(Path.GetDirectoryName(_context.Services.Log.FilePath)!)`.
- Under "Links", a row `Glyph="&#xE8A5;"`, `Header="Third-party licenses"`, `Description="Leaf Calendar is built with open-source software."`, and a button "View licenses" (`LicensesButton`). It opens a `ContentDialog` titled "Third-party licenses", with close "Close", whose content is a `TextBlock` `LicensesText` (`IsTextSelectionEnabled`, wrap, Caption) inside a 480-high `ScrollViewer`. Load the text from `ms-appx:///Assets/ThirdPartyNotices.txt` with `StorageFile.GetFileFromApplicationUriAsync`.

`Assets/ThirdPartyNotices.txt`: one block per shipped package, with its name, the version from `Directory.Packages.props`, its license name, its project URL, and its full license text copied verbatim from the package's own license file in the NuGet cache (`%USERPROFILE%\.nuget\packages\<id>\<version>\`):
- Microsoft.WindowsAppSDK
- CommunityToolkit.Mvvm
- Microsoft.Data.Sqlite and its SQLitePCLRaw dependencies (check `obj/project.assets.json` for exact IDs and versions)
- Meziantou.Framework.Scheduling
- Microsoft.Windows.CsWin32 (generated code ships in the app)
- the .NET runtime (MIT)
- SQLite itself (public domain)

Add it to the App csproj as content, the same way the other assets are included.

`TrayPage.xaml`: add a group "Calendars", described in a Caption line: "Choose which calendars the flyout and the tray icon's tooltip include. Calendars hidden in Leaf never appear." Below it goes an `ItemsControl` of App rows `TrayCalendarRow(Name, AccountEmail, Color, IsOn, AutomationId)`, one `SettingRow` each, with a `CheckBox` (`TrayCalendar_<calendarId>`) on the right. Build the rows from `_context.Calendar.Calendars.Where(c => c.IsVisible)`, grouped by account email, as a plain `List<TrayCalendarRow>`. A toggle saves `TrayExcludedCalendars` with the calendar added or removed through `_context.Save`.

- [ ] **Step 4: Run the unit tests and see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: all PASS.

- [ ] **Step 5: Write the UI tests**

`ShortcutSheetTests.cs`:

```csharp
    [Fact]
    public void QuestionMark_OpensTheSheet_FilterNarrowsIt()
    {
        // Shift+/ → ShortcutSheet; type "rsvp" in ShortcutFilterBox → exactly one ShortcutRow_* whose Name contains "RSVP yes / no / maybe"
    }

    [Fact]
    public void Sheet_ShowsTheGlobalShortcutsAsSet()
    {
        // seed JoinShortcut "Ctrl+Alt+Shift+J"; ? → the sheet has a row named "Join meeting: Ctrl+Alt+Shift+J"
    }

    [Fact]
    public void SettingsShortcutsLink_OpensTheSheet()
    {
        // Settings › Shortcuts → ShowCheatSheetButton → ShortcutSheet appears in the Settings window
    }

    [Fact]
    public void About_OpenLogFolder_LaunchesTheLogsFolder()
    {
        // Settings › About → OpenLogsButton → launched.txt has a line "folder:<ProfileFolder>\Logs"
    }

    [Fact]
    public void About_Licenses_ShowsNotices()
    {
        // Settings › About → LicensesButton → LicensesText contains "CommunityToolkit.Mvvm" and "MIT License"
    }
```

`TraySettingsCalendarsTests.cs`:

```csharp
    [Fact]
    public void UncheckingACalendar_RemovesItFromTheFlyout()
    {
        // add a family event today 15:00 (FakeGoogleServer.AddEvent before launch); open the tray flyout the way FlyoutTests
        // does → the family event is listed; Settings › Tray → uncheck TrayCalendar_family123@group.calendar.google.com →
        // reopen the flyout → the family event isn't listed, and the primary calendar's events still are
    }
```

- [ ] **Step 6: Build**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "feat: shortcut cheat sheet, Settings link to it, log folder and licenses, tray calendar choice"
```

---

### Task 5: Time Travel, Primary Time Zone, and Interface Scale

**Files:**
- Create: `src/LeafCalendar.Core/Views/DisplayZone.cs`, `src/LeafCalendar.App/Views/TimeTravelBar.xaml(.cs)`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.Zones.cs`, `src/LeafCalendar.App/Views/CalendarPage.Navigate.cs`
- Modify: `src/LeafCalendar.App/Views/Settings/TimeZonesPage.xaml(.cs)`, `src/LeafCalendar.App/MainWindow.xaml.cs` (minimum width at scale), `src/LeafCalendar.App/App.xaml.cs` (tray zone)
- Test: `tests/LeafCalendar.Tests/DisplayZoneTests.cs` (new); `tests/LeafCalendar.UITests/ZoneAndScaleTests.cs` (new)

**Interfaces:**
- Consumes: `ApplyZoneChange` and `_zones` (Task 1), `TimeZoneCatalog.Search`, `CityFor`, `OffsetLabel`, `ScaleHost`, `IslandBars`, `SidebarView.BodyScale`, `DetailsPanel.BodyScale` (Task 1).
- Produces:
  - `DisplayZone.Resolve(string? travelZoneId, string? primaryZoneId, TimeZoneInfo windows)`
  - `DisplayZone.ShouldOfferSwitch(string? primaryZoneId, bool prompt, TimeZoneInfo newWindows)`
  - `DisplayZone.Describe(TimeZoneInfo zone, DateTimeOffset at)` → `"Tokyo time (UTC+9)"`
  - `DisplayZone.IanaOf(TimeZoneInfo zone)`
  - `CalendarViewModel`:
    - `string? TravelZoneId`
    - `void TravelTo(string? zoneId)`
    - `[ObservableProperty] TimeZoneInfo? ZoneSwitchOffer`
    - `void AcceptZoneSwitch()`, `void DeclineZoneSwitch()`
    - `void SyncZone()`
  - `CalendarPage.ScaleChanged` (event)
  - Automation IDs:
    - `TimeTravelBox`, `TimeTravelBar`, `TimeTravelReturn`
    - `ZoneSwitchBar`, `ZoneSwitchYes`, `ZoneSwitchNo`
    - `FollowWindowsZoneSwitch`, `PrimaryZoneBox`, `ZonePromptSwitch`

- [ ] **Step 1: Write the failing zone tests**

`DisplayZoneTests.cs`:

```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class DisplayZoneTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Resolve_TravelBeatsPrimaryBeatsWindows()
    {
        Assert.Equal("Asia/Tokyo", DisplayZone.Resolve("Asia/Tokyo", "Europe/London", NewYork).Id);
        Assert.Equal("Europe/London", DisplayZone.Resolve(null, "Europe/London", NewYork).Id);
        Assert.Same(NewYork, DisplayZone.Resolve(null, null, NewYork));
    }

    [Fact]
    public void Resolve_UnknownIds_FallThrough() =>
        Assert.Same(NewYork, DisplayZone.Resolve("Mars/Base", "Moon/Crater", NewYork));

    [Fact]
    public void ShouldOfferSwitch_OnlyWithAPinnedZoneThatDiffers()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        Assert.True(DisplayZone.ShouldOfferSwitch("America/New_York", prompt: true, pacific));
        Assert.False(DisplayZone.ShouldOfferSwitch("America/New_York", prompt: false, pacific));
        Assert.False(DisplayZone.ShouldOfferSwitch(null, prompt: true, pacific));                  // following Windows
        Assert.False(DisplayZone.ShouldOfferSwitch("America/Los_Angeles", prompt: true, pacific));  // already there
        Assert.False(DisplayZone.ShouldOfferSwitch("America/Los_Angeles", prompt: true,
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")));                      // a Windows ID for the same zone
    }

    [Fact]
    public void Describe_CityAndOffset() =>
        Assert.Equal("Tokyo time (UTC+9)", DisplayZone.Describe(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
}
```

`Describe` uses `TimeZoneCatalog.OffsetLabel`, so check that it prints `UTC+9` and adjust the expectation to its exact format.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*DisplayZoneTests"`
Expected: build errors.

- [ ] **Step 3: Implement `DisplayZone`**

A static class:
- **`Resolve`:** try `travelZoneId`, then `primaryZoneId`, with `TimeZoneInfo.TryFindSystemTimeZoneById`. Fall back to `windows`.
- **`ShouldOfferSwitch`:** `prompt && primaryZoneId is not null && !string.Equals(Iana(primaryZoneId), IanaOf(newWindows), StringComparison.OrdinalIgnoreCase)`.
- **`IanaOf`:** returns the zone's ID, converting a Windows ID with `TimeZoneInfo.TryConvertWindowsIdToIanaId`.
- **`Describe`:** `$"{TimeZoneCatalog.CityFor(IanaOf(zone))} time ({TimeZoneCatalog.OffsetLabel(zone.GetUtcOffset(at))})"`.

Document the order: time travel, then Leaf's primary zone, then Windows.

- [ ] **Step 4: Run them and see them pass**

Same command. Expected: PASS.

- [ ] **Step 5: The view model**

In `CalendarViewModel.Zones.cs`:
- **Fields:** `string? _travelZoneId;` and `TimeZoneInfo? _applied;`.
- **`Zone`:** `public TimeZoneInfo Zone => DisplayZone.Resolve(_travelZoneId, Settings?.PrimaryTimeZone, _zones.Zone);`. `Settings` is assigned in the constructor; if anything reads `Zone` before that, keep the `?.`.
- **`TravelZoneId`:** `public string? TravelZoneId => _travelZoneId;`.
- **`TravelTo(string? zoneId)`:**
  1. `_travelZoneId = zoneId;`
  2. `OnPropertyChanged(nameof(TravelZoneId));`
  3. `SyncZone();`
  4. Log `calendar.timetravel` with `on` or `off` only. Never log the zone; it can say where you are.
- **`SyncZone()`:**

```csharp
    /// <summary>Re-sorts and redraws when the zone on screen changed (time travel, or the primary zone setting).</summary>
    public void SyncZone()
    {
        var zone = Zone;
        if (_applied is not null && _applied.Id == zone.Id)
        {
            return;
        }

        var before = _applied ?? zone;
        _applied   = zone;
        ApplyZoneChange(before);
    }
```

  Set `_applied = Zone` at the end of `ApplyZoneChange` as well, so `CheckTimeZone`'s path keeps it current.
- **`CheckTimeZone()`:** becomes:

```csharp
    public void CheckTimeZone()
    {
        var before = Zone;
        if (!_zones.Check())
        {
            return;
        }

        // A Pinned Primary Zone Stays; Offer To Switch To Windows' New One
        if (DisplayZone.ShouldOfferSwitch(Settings.PrimaryTimeZone, Settings.PromptOnZoneChange, _zones.Zone))
        {
            ZoneSwitchOffer = _zones.Zone;
        }

        if (Zone.Id != before.Id)
        {
            ApplyZoneChange(before);
        }
    }
```

- **`ZoneSwitchOffer`:** `[ObservableProperty] public partial TimeZoneInfo? ZoneSwitchOffer { get; set; }`.
- **`AcceptZoneSwitch()`:** take the offer's ID with `DisplayZone.IanaOf`, call `Update(s => s with { PrimaryTimeZone = id })`, then `ZoneSwitchOffer = null; SyncZone();`.
- **`DeclineZoneSwitch()`:** `ZoneSwitchOffer = null;`.

- [ ] **Step 6: The page and the bars**

`TimeTravelBar.xaml` is a `UserControl` holding two stock `InfoBar`s, both `IsClosable="False"`, `Severity="Informational"`, and `Margin="16,0,16,8"`:
1. `TimeTravelBar`, with its message set in code ("Viewing your calendar in {Describe(vm.Zone)}.") and a "Return" `ActionButton` (`TimeTravelReturn`).
2. `ZoneSwitchBar`, with the message "Your PC is now on {Describe(offer)}. Show Leaf in that time zone?", "Switch" (`ZoneSwitchYes`, accent), and "Keep {city}" (`ZoneSwitchNo`).

Both start closed (`IsOpen="False"`). The control exposes `Update(CalendarViewModel vm)`, which sets their `IsOpen` and text from `vm.TravelZoneId`, `vm.Zone`, and `vm.ZoneSwitchOffer`. It gets the solid informational background the page already sets, because the page's `InfoBarInformationalSeverityBackgroundBrush` override applies to everything inside it.

`CalendarPage.Navigate.cs`:
- **`AttachNavigate()`:**
  1. Create `_travelBar = new TimeTravelBar(ViewModel)` and add it to `IslandBars.Children`.
  2. Subscribe `ViewModel.PropertyChanged` (`TravelZoneId`, `ZoneSwitchOffer`) → `_travelBar.Update(ViewModel)`.
  3. Subscribe `ViewModel.LayoutChanged` → `ViewModel.SyncZone(); ApplyScale(); _travelBar.Update(ViewModel);`. Settings changes raise `LayoutChanged`. `SyncZone` is a no-op when nothing moved, so `ApplyZoneChange`'s own `LayoutChanged` can't loop.
  4. Call `ApplyScale()` once.
- **`StartTimeTravel()`:** a `ContentDialog` titled "Time travel":
  - Content: an `AutoSuggestBox` `TimeTravelBox` (`PlaceholderText="Search a city or zone (Tokyo, NYC, UTC)"`), with suggestions from `TimeZoneCatalog.Search(text, ViewModel.Now)` wrapped in App rows, as `TimeZonesPage` does.
  - Primary button "Go", enabled only once a suggestion is picked. Close button "Cancel". `DefaultButton` Primary.
  - On "Go": `ViewModel.TravelTo(picked.Id)`.
- **`TimeTravelReturn`:** `ViewModel.TravelTo(null)`.
- **`ApplyScale()`:**

```csharp
    /// <summary>The interface scale changed (the window recomputes its minimum size and the search icon's place).</summary>
    public event EventHandler? ScaleChanged;

    // Interface Scale (Settings › General): the panes grow with their content; the title bar stays 48 DIPs
    void ApplyScale()
    {
        var scale = ViewModel.Settings.InterfaceScale;
        if (ViewScale.Scale == scale && SidebarSplit.OpenPaneLength == SidebarWidth * scale)
        {
            return;
        }

        ViewScale.Scale             = scale;
        Sidebar.BodyScale.Scale     = scale;
        Details.BodyScale.Scale     = scale;
        SidebarSplit.OpenPaneLength = SidebarWidth * scale;
        DetailsSplit.OpenPaneLength = DetailsWidth * scale;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }
```

`MainWindow.xaml.cs`:
- Replace the `MinimumWidth` constant with:

```csharp
    // Smallest window at an interface scale: both panes scale; the title bar toolbar and insets don't
    static double MinimumWidthAt(double scale) =>
        (CalendarPage.SidebarWidth + CalendarPage.DetailsWidth) * scale + CalendarPage.TitleInset + 170 + 16 + 257 + 36 + CalendarPage.ToolbarInset;
```

  Keep the comment above it, updated to say the panes scale.
- Use `MinimumWidthAt(_calendar.Settings.InterfaceScale)` where `MinimumWidth` was used.
- In the toolbar layout, replace `CalendarPage.DetailsWidth` with `page.DetailsPaneWidth`.
- Subscribe to `page.ScaleChanged` → re-apply the minimum size, then `PlaceSearchButton()`.
- If the window is now smaller than the new minimum, grow it to the minimum (keep its top-left).

`App.xaml.cs`: where the tray agenda takes `_zone.Zone`, pass `DisplayZone.Resolve(null, settings.PrimaryTimeZone, _zone.Zone)` instead. Reminders keep Windows' zone, with this comment: `// ponytail: all-day reminders count from the PC zone's midnight; pass the primary zone into AlertCenter if a pinned-zone user notices`.

`TimeZonesPage.xaml`: add a first group, "Primary time zone":
- A `ToggleSwitch` row, "Use Windows time zone" (`FollowWindowsZoneSwitch`), described "Leaf follows your PC's time zone, so your calendar changes when you travel." Turning it on saves `PrimaryTimeZone = null`.
- An `AutoSuggestBox` row, "Leaf's time zone" (`PrimaryZoneBox`), described "Your calendar shows this time zone." Enabled only when the switch is off. A pick saves `PrimaryTimeZone`. While the switch is off and no zone is picked yet, nothing changes.
- A `ToggleSwitch` row, "Ask when my PC's time zone changes" (`ZonePromptSwitch`), described "Leaf offers to switch when you travel." Enabled only when the switch is off.

The existing "Extra time zones" groups follow, unchanged.

- [ ] **Step 7: Write the UI tests**

`ZoneAndScaleTests.cs`:

```csharp
    [Fact]
    public void Z_TravelToTokyo_ShowsTheBar_AndEventTimesFollow()
    {
        // Z → type "Tokyo" in TimeTravelBox → pick the first suggestion → Go → TimeTravelBar shows "Tokyo time";
        // select evt-single (9:00 ET) → the details time reads 10:00 PM; TimeTravelReturn → it reads 9:00 AM again and the bar closes
    }

    [Fact]
    public void WhileTraveling_DragCreate_UsesTheTravelZone()
    {
        // travel to Tokyo; drag 09:00–10:00 in the Oct 2 column; type "Tokyo sync"; Ctrl+Enter → the POST body's
        // start.dateTime is "2026-10-02T09:00:00+09:00" and start.timeZone is "Asia/Tokyo"
    }

    [Fact]
    public void PrimaryZone_FromSettings_MovesTheGrid()
    {
        // Settings › Time zones: turn FollowWindowsZoneSwitch off, pick "London" in PrimaryZoneBox → in the main window,
        // selecting evt-single shows 2:00 PM; ZonePromptSwitch is enabled; turning FollowWindowsZoneSwitch on again shows 9:00 AM
    }

    [Fact]
    public void Scale125_SeededSetting_PanesAndCalendarGrow()
    {
        // seed InterfaceScale 1.25 → Sidebar width = 264 × 1.25 DIPs (±1 px at the window's scale); MiniDay_2026-10-01 is 28 × 1.25 DIPs wide
    }

    [Fact]
    public void Scale150_AtMinimumWindow_NothingOverlaps()
    {
        // seed InterfaceScale 1.5; shrink the window below its minimum (it stops at the minimum); open the editor with C:
        // PeriodTitle, SearchButton, TodayButton, ViewModeButton, PreviousButton, NextButton, and DetailsEditButton don't
        // intersect one another; EditorSave and EditorCancel are fully inside the window; every MiniDay_* is inside the Sidebar
    }

    [Fact]
    public void ScaleFromTheCommandMenu_Applies()
    {
        // Ctrl+K, type "scale 110" → Enter → Settings › General › InterfaceScaleBox shows "110%" (Task 13's control)
    }
```

The prompt bar (`ZoneSwitchBar`) can't be triggered without changing Windows' time zone. `DisplayZoneTests.ShouldOfferSwitch_*` covers the rule, and Task 14 checks the bar by hand.

- [ ] **Step 8: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 9: Commit**

```bash
git add -A src tests
git commit -m "feat(app): time travel (Z), primary time zone with switch prompt, and interface scale"
```

Track A is done. Merge it at M1.

---

## Track B: People Overlay, Meet With, Share Availability, and Working Hours

Track B works in `../leaf-m5-b` on `m5-track-b`. Its tasks run in order: 6, 7, 8, 9.

### Task 6: Busy Math, Availability Text, and Free/Busy Lookup (Core)

**Files:**
- Create: `src/LeafCalendar.Core/People/BusyMath.cs`, `src/LeafCalendar.Core/People/AvailabilityText.cs`, `src/LeafCalendar.Core/People/FreeBusyLookup.cs`
- Test: `tests/LeafCalendar.Tests/BusyMathTests.cs`, `AvailabilityTextTests.cs`, `FreeBusyLookupTests.cs` (all new)

**Interfaces:**
- Consumes: `BusyRange`, `FreeBusyResult`, `GoogleCalendarClient.QueryFreeBusyAsync` / `ListEventsInRangeAsync` (Task 1), `TimeZoneCatalog.CityFor`, `AppLog`.
- Produces:
  - `BusyMath.Merge(IEnumerable<BusyRange> ranges)` → sorted, overlapping and touching ranges joined, empty ones dropped
  - `BusyMath.Subtract(IEnumerable<BusyRange> wanted, IEnumerable<BusyRange> busy)` → the free parts of `wanted`
  - `AvailabilityText.Format(IReadOnlyList<BusyRange> free, TimeZoneInfo zone, bool use24Hour)` → lines joined with `\r\n`; `""` when empty
  - `AvailabilityText.ZoneLabel(TimeZoneInfo zone)` → `ET`, `CT`, `MT`, `PT`, `AKT`, `HT`, `UTC`, else `"{City} time"`
  - `public enum PersonBusyState { Known, Unknown }`
  - `public sealed record BusyBlock(DateTimeOffset Start, DateTimeOffset End, string? Title)`
  - `public sealed record PersonBusy(string Email, IReadOnlyList<BusyBlock> Blocks, PersonBusyState State)`
  - `public sealed class FreeBusyLookup(GoogleCalendarClient client, AppLog log)` with `const int MaxPeople = 20` and `Task<IReadOnlyList<PersonBusy>> LookupAsync(string accountId, IReadOnlyList<string> emails, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

`BusyMathTests.cs`:

```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Tests;

public sealed class BusyMathTests
{
    static BusyRange R(int startHour, int startMinute, int endHour, int endMinute) =>
        new(new DateTimeOffset(2026, 10, 1, startHour, startMinute, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, endHour, endMinute, 0, TimeSpan.Zero));

    [Fact]
    public void Merge_JoinsOverlappingAndTouching_DropsEmpty() =>
        Assert.Equal([R(9, 0, 11, 0), R(12, 0, 13, 0)], BusyMath.Merge([R(10, 0, 11, 0), R(9, 0, 10, 0), R(12, 0, 13, 0), R(9, 30, 9, 30), R(14, 0, 13, 0)]));

    [Fact]
    public void Subtract_LeavesTheFreeParts() =>
        Assert.Equal([R(10, 0, 10, 30), R(11, 0, 12, 0)], BusyMath.Subtract([R(9, 0, 12, 0)], [R(8, 0, 10, 0), R(10, 30, 11, 0)]));

    [Fact]
    public void Subtract_AllBusy_IsEmpty() => Assert.Empty(BusyMath.Subtract([R(9, 0, 10, 0)], [R(8, 0, 11, 0)]));

    [Fact]
    public void Subtract_OverlappingWantedSlots_AreMergedFirst() =>
        Assert.Equal([R(9, 0, 12, 0)], BusyMath.Subtract([R(9, 0, 11, 0), R(10, 0, 12, 0)], []));
}
```

`AvailabilityTextTests.cs`:

```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Tests;

public sealed class AvailabilityTextTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeZoneInfo Tokyo   = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // Wall-clock times in New York on a given day
    static BusyRange Et(int month, int day, int h1, int m1, int h2, int m2)
    {
        var start = new DateTime(2026, month, day, h1, m1, 0);
        var end   = new DateTime(2026, month, day, h2, m2, 0);
        return new(new DateTimeOffset(start, NewYork.GetUtcOffset(start)), new DateTimeOffset(end, NewYork.GetUtcOffset(end)));
    }

    [Fact]
    public void Format_SpecExampleShape() =>
        Assert.Equal("Wed Sep 30: 10–11 AM, 2–4 PM ET", AvailabilityText.Format([Et(9, 30, 10, 0, 11, 0), Et(9, 30, 14, 0, 16, 0)], NewYork, use24Hour: false));

    [Theory]
    [InlineData(11, 0, 13, 0, "11 AM–1 PM")]
    [InlineData(10, 30, 11, 0, "10:30–11 AM")]
    [InlineData(11, 30, 12, 15, "11:30 AM–12:15 PM")]
    [InlineData(12, 0, 13, 0, "12–1 PM")]
    public void Format_Meridiem(int h1, int m1, int h2, int m2, string expected) =>
        Assert.Equal($"Thu Oct 1: {expected} ET", AvailabilityText.Format([Et(10, 1, h1, m1, h2, m2)], NewYork, use24Hour: false));

    [Fact]
    public void Format_24Hour() =>
        Assert.Equal("Thu Oct 1: 10:00–11:00, 14:00–16:00 ET", AvailabilityText.Format([Et(10, 1, 10, 0, 11, 0), Et(10, 1, 14, 0, 16, 0)], NewYork, use24Hour: true));

    [Fact]
    public void Format_SeveralDays_OneLineEach() =>
        Assert.Equal("Thu Oct 1: 9–10 AM ET\r\nFri Oct 2: 3–4 PM ET", AvailabilityText.Format([Et(10, 2, 15, 0, 16, 0), Et(10, 1, 9, 0, 10, 0)], NewYork, use24Hour: false));

    [Fact]
    public void Format_CrossesMidnightInZone_SplitsDays() =>
        // 10 AM–12 PM in New York is 11 PM–1 AM in Tokyo
        Assert.Equal("Thu Oct 1: 11 PM–12 AM Tokyo time\r\nFri Oct 2: 12–1 AM Tokyo time",
            AvailabilityText.Format([Et(10, 1, 10, 0, 12, 0)], Tokyo, use24Hour: false));

    [Fact]
    public void Format_DstFallBack_UsesTheWallClock()
    {
        // Nov 1, 2026: 12:00 AM EDT (04:00Z) to 3:00 AM EST (08:00Z) is four real hours, shown as the clock reads
        var range = new BusyRange(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal("Sun Nov 1: 12–3 AM ET", AvailabilityText.Format([range], NewYork, use24Hour: false));
    }

    [Fact]
    public void Format_Nothing_IsEmpty() => Assert.Equal("", AvailabilityText.Format([], NewYork, false));

    [Theory]
    [InlineData("America/New_York", "ET")]
    [InlineData("Eastern Standard Time", "ET")]
    [InlineData("America/Chicago", "CT")]
    [InlineData("America/Denver", "MT")]
    [InlineData("America/Phoenix", "MT")]
    [InlineData("America/Los_Angeles", "PT")]
    [InlineData("America/Anchorage", "AKT")]
    [InlineData("Pacific/Honolulu", "HT")]
    [InlineData("UTC", "UTC")]
    [InlineData("Asia/Tokyo", "Tokyo time")]
    public void ZoneLabel_Short(string id, string expected) =>
        Assert.Equal(expected, AvailabilityText.ZoneLabel(TimeZoneInfo.FindSystemTimeZoneById(id)));
}
```

`FreeBusyLookupTests.cs`: build a `GoogleCalendarClient` over the `FakeHttpHandler` exactly as `GoogleCalendarClientTests` does, and an `AppLog` in a `TempFolder`:

```csharp
    [Fact]
    public async Task Lookup_NotFound_IsUnknownNotFree()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"nobody@example.org":{"errors":[{"domain":"global","reason":"notFound"}],"busy":[]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/nobody%40example.org/events", 404, "{}");

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["nobody@example.org"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal(PersonBusyState.Unknown, person.State);
        Assert.Empty(person.Blocks);
    }

    [Fact]
    public async Task Lookup_FreeBusyOnly_BlocksWithoutTitles()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[{"start":"2026-10-01T15:00:00Z","end":"2026-10-01T16:00:00Z"}]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 403, "{}");

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal(PersonBusyState.Known, person.State);
        Assert.Equal([new BusyBlock(new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero), null)], person.Blocks);
    }

    [Fact]
    public async Task Lookup_SharedWithDetails_CleanTitlesOfBusyTimedEventsOnly()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 200, """
            {"items":[
              {"id":"a","status":"confirmed","summary":"‮1:1 with Sam","start":{"dateTime":"2026-10-01T17:00:00Z"},"end":{"dateTime":"2026-10-01T17:30:00Z"}},
              {"id":"b","status":"confirmed","summary":"Lunch","transparency":"transparent","start":{"dateTime":"2026-10-01T16:00:00Z"},"end":{"dateTime":"2026-10-01T17:00:00Z"}},
              {"id":"c","status":"cancelled","summary":"Gone","start":{"dateTime":"2026-10-01T18:00:00Z"},"end":{"dateTime":"2026-10-01T19:00:00Z"}},
              {"id":"d","status":"confirmed","summary":"Holiday","start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}]}
            """);

        var person = Assert.Single(await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));

        Assert.Equal("1:1 with Sam", Assert.Single(person.Blocks).Title);
    }

    [Fact]
    public async Task Lookup_DuplicatesAndOverTheCap_AreTrimmed()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{}}""");
        _handler.RespondToAny(HttpMethod.Get, 404, "{}");

        var emails = Enumerable.Range(0, 30).Select(i => $"p{i}@example.com").Append("P0@example.com").ToList();
        var people = await _lookup.LookupAsync(Account, emails, From, To, TestContext.Current.CancellationToken);

        Assert.Equal(FreeBusyLookup.MaxPeople, people.Count);
    }

    [Fact]
    public async Task Lookup_GoogleUnreachable_Throws()
    {
        _handler.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lookup_LogsNoAddresses()
    {
        _handler.Respond(HttpMethod.Post, "freeBusy", 200, """{"calendars":{"dana@example.com":{"busy":[]}}}""");
        _handler.Respond(HttpMethod.Get, "calendars/dana%40example.com/events", 404, "{}");

        await _lookup.LookupAsync(Account, ["dana@example.com"], From, To, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("dana", File.ReadAllText(_log.FilePath), StringComparison.OrdinalIgnoreCase);
    }
```

`From` and `To` are 2026-10-01T00:00Z and 2026-10-02T00:00Z. If `FakeHttpHandler` lacks `RespondToAny` or `Throw`, write a small local `HttpMessageHandler` in this test file instead. `FakeHttpHandler` is Task 1's frozen file.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*BusyMathTests"`, and the same for `*AvailabilityTextTests` and `*FreeBusyLookupTests`.
Expected: build errors.

- [ ] **Step 3: Implement**

`BusyMath.cs`:

```csharp
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.People;

/// <summary>Arithmetic on busy and free stretches (overlays and share availability).</summary>
public static class BusyMath
{
    /// <summary>Sorted, with overlapping and touching ranges joined; empty and backwards ranges dropped.</summary>
    public static IReadOnlyList<BusyRange> Merge(IEnumerable<BusyRange> ranges)
    {
        var merged = new List<BusyRange>();
        foreach (var r in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = r.End > merged[^1].End ? r.End : merged[^1].End };
                continue;
            }

            merged.Add(r);
        }

        return merged;
    }

    /// <summary>The parts of <paramref name="wanted"/> not covered by <paramref name="busy"/>.</summary>
    public static IReadOnlyList<BusyRange> Subtract(IEnumerable<BusyRange> wanted, IEnumerable<BusyRange> busy)
    {
        var blocks = Merge(busy);
        var free   = new List<BusyRange>();

        foreach (var slot in Merge(wanted))
        {
            var cursor = slot.Start;
            foreach (var b in blocks.Where(b => b.End > slot.Start && b.Start < slot.End))
            {
                if (b.Start > cursor)
                {
                    free.Add(new BusyRange(cursor, b.Start));
                }

                cursor = b.End > cursor ? b.End : cursor;
            }

            if (cursor < slot.End)
            {
                free.Add(new BusyRange(cursor, slot.End));
            }
        }

        return free;
    }
}
```

`AvailabilityText.cs`: a static class.
- **`Format`:**
  1. Merge the ranges.
  2. Convert each to `zone` (`TimeZoneInfo.ConvertTime`) and split it at each local midnight. A piece ending exactly at midnight ends at "12 AM".
  3. Group by local date, ascending.
  4. Each line is `"{ddd MMM d}: {range}, {range} {ZoneLabel(zone)}"`, formatted in en-US with an en dash `–` (U+2013).
- **12-hour rules:** an hour shows minutes only when they aren't `:00`. When both ends share AM or PM, the first drops it ("10–11 AM", "10:30–11 AM"); otherwise both carry it ("11 AM–1 PM", "11:30 AM–12:15 PM"). Noon is "12 PM" and midnight is "12 AM".
- **24-hour rule:** `HH:mm–HH:mm`.
- **`ZoneLabel`:** take the IANA ID (`TimeZoneInfo.TryConvertWindowsIdToIanaId` for Windows IDs), then look it up in a small dictionary:

  | Label | IANA IDs |
  |---|---|
  | ET | `America/New_York`, `America/Detroit`, `America/Toronto`, `America/Indiana/Indianapolis` |
  | CT | `America/Chicago`, `America/Winnipeg` |
  | MT | `America/Denver`, `America/Edmonton`, `America/Phoenix`, `America/Boise` |
  | PT | `America/Los_Angeles`, `America/Vancouver` |
  | AKT | `America/Anchorage` |
  | HT | `Pacific/Honolulu` |
  | UTC | `UTC`, `Etc/UTC` |

  Anything else is `$"{TimeZoneCatalog.CityFor(iana)} time"`.

`FreeBusyLookup.cs`:
- **`LookupAsync`:**
  1. Trim the emails and dedupe them case-insensitively, keeping the first spelling. Cap at `MaxPeople`.
  2. No emails: return `[]`.
  3. Run one `QueryFreeBusyAsync` for all of them, and one `ListEventsInRangeAsync` per person, in parallel (`Task.WhenAll`).
  4. Network and Google exceptions from the free/busy call propagate; the caller shows "Couldn't get busy times". A single details call that throws counts as "no details" (catch it, not `OperationCanceledException`).
- **Per person:**
  - **Details page not null:** blocks from its items that are `confirmed`, timed (`start.dateTime`), and not `transparency: transparent`. The title is the summary cleaned of control and format characters, trimmed, and capped at 200. State `Known`.
  - **Else, free/busy has no `Error`:** blocks from `Busy`, with no titles. State `Known`.
  - **Else:** state `Unknown`, no blocks.
  - Blocks are merged per person when they have no titles; titled blocks stay separate.
- **Log:** `freebusy.lookup` with `account={accountId} count={n} unknown={u}` only.

Doc comment: the result lives in memory for the caller's overlay and is never stored.

- [ ] **Step 4: Run them and see them pass**

Same commands as Step 2. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src/LeafCalendar.Core/People tests/LeafCalendar.Tests
git commit -m "feat(core): busy math, availability text, and free/busy lookup with details when shared"
```

---

### Task 7: People Overlay (P), Meet With (F), and E Then F

**Files:**
- Create: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs`, `src/LeafCalendar.App/Views/PeoplePickerDialog.cs`, `src/LeafCalendar.App/Views/OverlayBar.xaml(.cs)`
- Modify: `src/LeafCalendar.App/Views/CalendarPage.People.cs`, `src/LeafCalendar.App/Controls/TimeGridView.cs`, `src/LeafCalendar.App/Controls/DayColumn.cs`, `src/LeafCalendar.App/Controls/LeafBrushes.cs`
- Create: `tests/LeafCalendar.UITests/PeopleOverlayTests.cs`

**Interfaces:**
- Consumes:
  - `FreeBusyLookup`, `PersonBusy`, `BusyBlock` (Task 6)
  - `ContactSearch.SearchAsync`, and the App's `ContactSuggestion` class (in `EventEditorViewModel.cs`; used, not edited)
  - `LeafSettings.MainAccountId` (Task 1)
  - `SelectedInfo` (its guest list), `BeginCreate`, `Editing.GuestInput`/`AddGuest()`
  - `IslandBars` (Task 1)
- Produces:
  - `public sealed record OverlayPerson(string Email, string Name, int ColorIndex, PersonBusyState State)`
  - `public sealed record OverlayBlock(string Email, int ColorIndex, DateTimeOffset Start, DateTimeOffset End, string? Title)`
  - `CalendarViewModel`:
    - `IReadOnlyList<OverlayPerson> OverlayPeople`, `bool IsMeetWith`
    - `event EventHandler? OverlayChanged`
    - `Task ShowOverlayAsync(IReadOnlyList<Contact> people, bool meetWith)`
    - `void RemoveOverlayPerson(string email)`, `void ClearOverlay()`
    - `IReadOnlyList<OverlayBlock> OverlayBlocks(DateTimeOffset from, DateTimeOffset to)`
    - `Task RefreshOverlayAsync()`
    - `void AddOverlayGuests()`
    - `string? PeopleAccountId()`
    - `Task<ContactResults> SearchPeopleAsync(string text, CancellationToken ct)`
  - `LeafBrushes.Person(int index)` and `LeafBrushes.PersonFill(int index)`: 4 per-theme colors
  - Automation IDs:
    - `PeoplePickerBox`, `PickedPerson_<email>`
    - `OverlayBar`, `OverlayChip_<email>`, `OverlayRemove_<email>`, `OverlayClear`
    - `OverlayBlock_<email>_<n>`

- [ ] **Step 1: Write the UI tests (they compile; Task 14 runs them)**

`PeopleOverlayTests.cs`, with a seeded profile, `--start-date 2026-10-01`, and Week view. Write each fully:

```csharp
    [Fact]
    public void P_PickATeammate_ShowsTheirBusyBlocks()
    {
        // _google.Busy["dana@example.com"] = [(2026-10-01 11:00 ET, 12:00 ET)]; launch; P → type "dana" in PeoplePickerBox →
        // pick "Dana Director <dana@example.com>" (directory fixture; Task 10 adds the directory source, so before the merge
        // type the full address and press Enter) → Show → OverlayChip_dana@example.com is shown; OverlayBlock_dana@example.com_0
        // sits in the Oct 1 column with its top at the 11:00 line and its height one hour (± 2 px)
    }

    [Fact]
    public void UnknownPerson_SaysNoInfo()
    {
        // P → type "nobody@example.org", Enter → Show → OverlayChip_nobody@example.org's Name contains "No free/busy info";
        // no OverlayBlock_nobody@example.org_* exists
    }

    [Fact]
    public void SharedWithDetails_ShowsTheirTitles()
    {
        // _google.TeammateEvents["dana@example.com"] = [event "1:1 with Sam", 2026-10-01 13:00–13:30 ET]; P, dana, Show →
        // OverlayBlock_dana@example.com_0's Name contains "1:1 with Sam"
    }

    [Fact]
    public void MeetWith_DragCreatesAnEventWithThemAsGuests()
    {
        // F → dana → "Find a time" → drag 15:00–16:00 in the Oct 1 column → type "Sync" → Ctrl+Enter → the POST body's
        // attendees include {"email":"dana@example.com"}
    }

    [Fact]
    public void EThenF_OverlaysTheSelectedEventsGuests()
    {
        // select evt-meeting (Design review) → E, F → OverlayChip_boss@example.com and OverlayChip_sam@example.com shown;
        // no chip for leaf.tester@gmail.com (you)
    }

    [Fact]
    public void Overlay_FollowsPaging()
    {
        // Busy for dana on Oct 1 and Oct 8; P, dana, Show → block in the Oct 1 week; Next (→) → a block in the Oct 8 column;
        // _google.FreeBusyQueries has a second query covering Oct 8
    }

    [Fact]
    public void ClearAndRemove()
    {
        // overlay dana and sam; OverlayRemove_sam@example.com → sam's chip and blocks gone; OverlayClear → OverlayBar hidden
    }

    [Fact]
    public void Overlay_WritesNoAddressesToTheLog()
    {
        // after P_PickATeammate's steps, leaf.log doesn't contain "dana"
    }
```

- [ ] **Step 2: The view model side**

`CalendarViewModel.People.cs` (header comment: "Track B owns this file"). The overlay state is fields plus one `OverlayChanged` event, no `ObservableCollection`:
- `List<OverlayPerson> _people`, `Dictionary<string, List<BusyBlock>> _blocks`, `(DateTimeOffset From, DateTimeOffset To)? _loaded`, `bool _meetWith`.
- **`PeopleAccountId()`:** `Settings.MainAccountId` when it's in `AccountEmails`, else the first key of `AccountEmails` in sidebar order, else null.
- **`SearchPeopleAsync(text, ct)`:** `_services.Google?.Contacts.SearchAsync(PeopleAccountId()!, text, ct)`, or an empty `ContactResults` when there's no Google or no account.
- **`ShowOverlayAsync(people, meetWith)`:**
  1. Replace `_people`, assigning color indexes 0–3 cyclically. A name of `""` shows the email.
  2. Set `_meetWith` and clear `_loaded`.
  3. `await RefreshOverlayAsync()`.
- **`RefreshOverlayAsync()`:**
  1. The range is the visible period ± 7 days: `PeriodStart` to `PeriodStart + VisibleColumns`, as instants in `Zone` through `OccurrenceQuery.LocalMidnight`.
  2. Skip when `_loaded` covers it.
  3. Otherwise, run `new FreeBusyLookup(_services.Google!.Calendar, _services.Log).LookupAsync(...)` inside `try`. On any non-cancel exception, `ShowMessage("Couldn't get busy times. Check your connection.")` and keep the old blocks.
  4. On success, store the blocks, update each person's `State`, set `_loaded`, and raise `OverlayChanged`.
- **`OverlayBlocks(from, to)`:** the stored blocks overlapping the range, each with its person's color index.
- **`RemoveOverlayPerson`, `ClearOverlay`:** update the state and raise `OverlayChanged`. `ClearOverlay` also clears `_meetWith`.
- **`AddOverlayGuests()`:** if `Editing is { IsNew: true }`, then for each person `Editing.GuestInput = p.Email; Editing.AddGuest();`.

- [ ] **Step 3: The picker and the bar**

`PeoplePickerDialog.cs` is a static `Task<IReadOnlyList<Contact>?> ShowAsync(XamlRoot root, CalendarViewModel vm, string title, string primaryText)`:
- A `ContentDialog` with `Title` = `title` ("Overlay a teammate" or "Meet with"), `PrimaryButtonText` = `primaryText` ("Show" or "Find a time"), close "Cancel", and `DefaultButton` Primary.
- Content, top to bottom:
  - An `AutoSuggestBox` `PeoplePickerBox` (`PlaceholderText="Name or email"`). Its suggestions come from `vm.SearchPeopleAsync` through a `LatestSearch`, wrapped in `ContactSuggestion`.
  - A Caption line, secondary: "Up to 20 people."
  - A `StackPanel` of picked chips (`PickedPerson_<email>`), each with a remove button (Name "Remove {email}").
- Picking a suggestion adds it. `QuerySubmitted` with text that is exactly a valid address (`MailAddress.TryCreate(text, out var a) && a.Address == text.Trim()`) adds it. Anything else does nothing.
- The primary button is enabled with 1 to 20 people.
- Returns the picked list, or null on Cancel.

`OverlayBar.xaml` is a `UserControl`: a `Border` (`CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush` 1 px, `CornerRadius` 8, `Padding="12,8"`, `Margin="16,0,16,8"`) holding a horizontal `Grid`:
1. A Caption label: "Busy times" or "Meet with". In Meet with mode a second line reads "Drag on the calendar to invite them.". In Month view the line reads "Busy times show in the day and week views."
2. A horizontal `ItemsControl` of chips. Each chip is a `Border` with `CornerRadius="12"` and `Padding="8,2"`, holding a 8×8 person-color dot, the name, and a 20×20 `LeafIconButtonStyle` "×" button (`OverlayRemove_<email>`, Name "Remove {name}"). Unknown people's chips add " · No free/busy info" in `TextFillColorSecondaryBrush`. The chip's `AutomationProperties.Name` is the full text, and its AutomationId is `OverlayChip_<email>`.
3. A "Clear" `Button` (`OverlayClear`).

The chips are App records built from `OverlayPeople`, as a plain `List`. The bar is collapsed when nobody's overlaid.

`CalendarPage.People.cs`:
- **`AttachPeople()`:**
  1. Create the `OverlayBar` and add it to `IslandBars`.
  2. Subscribe `ViewModel.OverlayChanged` → `_overlayBar.Update(ViewModel)`.
  3. Subscribe `ViewModel.PropertyChanged` for `PeriodStart` → `ViewModel.Fire(ViewModel.RefreshOverlayAsync)` when anyone's overlaid.
  4. Subscribe `Sidebar.ShareAvailabilityRequested` → `StartShareAvailability()` (Task 8 fills that in).
- **`ShowPeopleOverlay()`:** `PeoplePickerDialog.ShowAsync(XamlRoot, ViewModel, "Overlay a teammate", "Show")`, then `ShowOverlayAsync(picked, meetWith: false)`.
- **`ShowMeetWith()`:** the same with "Meet with" / "Find a time" and `meetWith: true`.
- **`ShowParticipantOverlay()`:**
  1. Take the selected event's guests, minus your account emails (`ViewModel.AccountEmails.Values`) and addresses ending in `@resource.calendar.google.com`.
  2. None: `ShowMessage("This event has no other guests")`.
  3. Else `ShowOverlayAsync(guests as Contact(name: "", email), meetWith: false)`.

  Read the guest list from `SelectedInfo` (its `GuestItem` list).

- [ ] **Step 4: Draw the blocks**

`LeafBrushes.cs`: add four person colors per theme. Read the file's existing per-theme pattern and follow it.

| Index | Light | Dark |
|---|---|---|
| 0 | `#8764B8` | `#B4A0FF` |
| 1 | `#C239B3` | `#F48FE8` |
| 2 | `#038387` | `#5FD3D6` |
| 3 | `#CA5010` | `#FF9C62` |

`PersonFill(i)` is the same color at 20% alpha. In HighContrast, `Person` is `SystemColorHighlightColor` and `PersonFill` is transparent.

`DayColumn.cs` and `TimeGridView.cs`:
- Add an overlay layer to each day column: a `Canvas` with `IsHitTestVisible="False"`, placed **behind** the event blocks, so drags and clicks still reach the grid and events.
- When the time grid redraws its columns (and on `ViewModel.OverlayChanged`), fill each column's layer from `ViewModel.OverlayBlocks(dayStart, dayEnd)`. Each block is a `Border` with `Background = PersonFill`, `BorderBrush = Person`, `BorderThickness = 3,0,0,0`, `CornerRadius = 4`, and `Margin` 2 DIPs on the right.
- Position and height use the same minute-to-pixel math the event blocks use (reuse `DayLayout` and the column's hour height; don't write new math).
- A title, when present, is a Caption `TextBlock` (`TextTrimming` ellipsis, padding 6,2).
- Set `AutomationProperties.Name` to `"{email} busy {h:mm tt}–{h:mm tt}"` plus `": {title}"`, and AutomationId to `OverlayBlock_{email}_{n}`, where n counts per person per visible range.
- Reuse the `Border`s across redraws: keep a per-column pool, as the event blocks do.

In `TimeGridView`'s drag-to-create path, right after it calls `ViewModel.BeginCreate(...)`, add `if (ViewModel.IsMeetWith) { ViewModel.AddOverlayGuests(); }`.

- [ ] **Step 5: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "feat(app): people overlay (P), Meet with (F), and participant overlay (E then F)"
```

---

### Task 8: Share Availability (S)

**Files:**
- Create: `src/LeafCalendar.App/Views/ShareBar.xaml(.cs)`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs`, `src/LeafCalendar.App/Views/CalendarPage.People.cs`, `src/LeafCalendar.App/Controls/TimeGridView.cs`, `src/LeafCalendar.App/Controls/DayColumn.cs`
- Create: `tests/LeafCalendar.UITests/ShareAvailabilityTests.cs`

**Interfaces:**
- Consumes: `BusyMath`, `AvailabilityText` (Task 6), `GoogleCalendarClient.QueryFreeBusyAsync`, `LinkSafety.BookingPages` (Task 1), `TimeZoneCatalog.CityFor`/`OffsetLabel` (zone labels; never Track A's `DisplayZone`), `SidebarView.ShareAvailabilityRequested` (Task 1).
- Produces:
  - `CalendarViewModel`:
    - `bool IsSharing`, `IReadOnlyList<BusyRange> ShareSlots`
    - `string ShareZoneId`, `IReadOnlySet<CalendarRef> ShareCalendars`
    - `event EventHandler? ShareChanged`
    - `void StartSharing()`, `void StopSharing()`
    - `void AddShareSlot(DateTimeOffset start, DateTimeOffset end)`, `void RemoveShareSlot(int index)`
    - `IReadOnlyList<CalendarInfo> ShareableCalendars()`
    - `Task<string?> BuildAvailabilityAsync(CancellationToken ct)`
    - `Task CopyAvailabilityAsync()`
  - Automation IDs:
    - `ShareBar`, `ShareZoneBox`, `ShareCalendarsButton`, `ShareCalendar_<calendarId>`
    - `ShareCopyButton`, `ShareCancelButton`, `BookingPagesLink`
    - `ShareSlot_<n>`, `ShareSlot_<n>_Remove`

- [ ] **Step 1: Write the UI tests**

`ShareAvailabilityTests.cs`, with a seeded profile and `--start-date 2026-10-01`. Write each fully; use `Support/Clipboard.cs` to read the clipboard:

```csharp
    [Fact]
    public void S_DragSlots_CopyGivesFreeTimesOnly()
    {
        // S → ShareBar shown; drag 09:00–12:00 in the Oct 1 column → ShareSlot_0 shown (no editor opened);
        // ShareCopyButton → clipboard text is exactly "Thu Oct 1: 10 AM–12 PM ET" (the dentist, 9–10, is busy)
    }

    [Fact]
    public void CopyInTokyoTime_SplitsAtMidnight()
    {
        // seed TimeZones [Asia/Tokyo]; S; drag 10:00–12:00 on Oct 1 (free); ShareZoneBox → the Tokyo item; copy →
        // "Thu Oct 1: 11 PM–12 AM Tokyo time\r\nFri Oct 2: 12–1 AM Tokyo time"
    }

    [Fact]
    public void LeavingACalendarOut_ItsEventsDontBlock()
    {
        // add a family event Oct 1 10:00–11:00; S; drag 10:00–12:00; copy → "Thu Oct 1: 11 AM–12 PM ET";
        // ShareCalendarsButton → uncheck ShareCalendar_family123@group.calendar.google.com; copy → "Thu Oct 1: 10 AM–12 PM ET"
    }

    [Fact]
    public void RemoveASlot_AndCancel()
    {
        // S; two slots; ShareSlot_0_Remove → one left; ShareCancelButton → ShareBar gone, no ShareSlot_*; dragging now opens the editor again
    }

    [Fact]
    public void SidebarButton_AndCommandMenu_StartIt()
    {
        // click SidebarShareAvailability → ShareBar shown; Cancel; Ctrl+K, "share", Enter → ShareBar shown
    }

    [Fact]
    public void BookingPagesLink_OpensGoogle()
    {
        // S → BookingPagesLink → launched.txt has "https://calendar.google.com/calendar/appointments?authuser=leaf.tester%40gmail.com"
    }

    [Fact]
    public void Offline_CopySaysWhy_AndKeepsTheSlots()
    {
        // S; drag a slot; _google.Offline = true; copy → NoticeBar reads "Couldn't check your calendars. Check your connection."; ShareSlot_0 still shown
    }

    [Fact]
    public void Copy_WritesNoAvailabilityToTheLog()
    {
        // after a copy, leaf.log contains neither "10 AM" nor "Thu Oct 1"
    }
```

- [ ] **Step 2: The view model side**

Add to `CalendarViewModel.People.cs` a `// SHARE AVAILABILITY` section banner and:
- **Fields:** `bool _sharing`, `List<BusyRange> _slots`, `string _shareZoneId`, `HashSet<CalendarRef> _shareCalendars`.
- **`StartSharing()`:**
  1. `_slots.Clear()`.
  2. `_shareZoneId` = the IANA ID of `Zone`.
  3. `_shareCalendars` = every `ShareableCalendars()` entry.
  4. `_sharing = true`, then `ShareChanged`.
  5. Calling it while sharing does nothing (S toggles from the page).
- **`ShareableCalendars()`:** `Calendars.Where(c => c.IsVisible && c.AccessRole is "owner" or "writer")`.
- **`AddShareSlot`:** add the slot, then `_slots = [.. BusyMath.Merge(_slots)]`, then `ShareChanged`.
- **`RemoveShareSlot`:** remove by index, then `ShareChanged`.
- **`BuildAvailabilityAsync(ct)`:**
  1. Group `_shareCalendars` by account. For each account, call `QueryFreeBusyAsync(account, its calendar IDs, min slot start, max slot end)`. Accounts run in parallel.
  2. Any result with an `Error`: return null.
  3. Union the busy ranges, subtract them from the slots, and format with `AvailabilityText.Format(free, TimeZoneInfo.FindSystemTimeZoneById(_shareZoneId), Settings.Use24HourTime)`.
  4. Exceptions other than cancellation also return null.
- **`CopyAvailabilityAsync()`:**
  - Build the text.
  - **Null:** `ShowMessage("Couldn't check your calendars. Check your connection.")`.
  - **Empty:** `ShowMessage("None of those times are free")`.
  - **Else:** put it on the clipboard (`DataPackage.SetText`, `Clipboard.SetContent`, `Clipboard.Flush`), then `ShowMessage("Copied your free times")`.
  - Log `share.copy` with `slots={n} calendars={m}` only.
- **`StopSharing()`:** clears everything, then `ShareChanged`.

- [ ] **Step 3: The bar, the drag, and the slots**

`ShareBar.xaml` is a `UserControl` with the same card look as `OverlayBar`, laid out as one wrapping row:
1. Caption text: "Drag on the calendar to pick times. Leaf leaves out the busy ones."
2. `ComboBox` `ShareZoneBox`, `MinWidth="180"`, `AutomationProperties.Name="Time zone"`. Its items are App rows built from the zone on screen, Windows' zone, and `Settings.TimeZones`, deduplicated by IANA ID, each labeled `"{City} ({UTC±h})"`.
3. `DropDownButton` `ShareCalendarsButton` labeled "Calendars", whose `MenuFlyout` has a `ToggleMenuFlyoutItem` per shareable calendar (`ShareCalendar_<id>`, text = calendar name). Keep each item in a dictionary keyed by `CalendarRef`, and read `IsChecked` from those references.
4. `HyperlinkButton` "Manage Google booking pages" (`BookingPagesLink`) → `ViewModel.OpenLinkAsync(LinkSafety.BookingPages(<people account's email>))`.
5. `Button` "Cancel" (`ShareCancelButton`).
6. `Button` "Copy" (`ShareCopyButton`, `AccentButtonStyle`, the one accent on the surface), enabled with at least one slot.

`CalendarPage.People.cs`:
- **`StartShareAvailability()`:** if sharing, `StopSharing()`; else `StartSharing()`. S toggles.
- **`AttachPeople`:** add the `ShareBar` to `IslandBars` and subscribe `ShareChanged` → `_shareBar.Update(ViewModel)`. Hook the sidebar event here too, if Task 7 didn't.

`TimeGridView.cs` and `DayColumn.cs`:
- While `ViewModel.IsSharing`, the drag-to-create gesture calls `ViewModel.AddShareSlot(start, end)` instead of `BeginCreate`. Same snapping; a single click without a drag adds nothing.
- Draw the slots on a second hit-test-invisible layer above the overlay layer and below events. Each slot is a `Rectangle` with `Fill` = accent at 15% (`LeafBrushes.Accent` with `Opacity`, no new brush), `Stroke` = accent, `StrokeThickness` 1.5, `StrokeDashArray` "4,2", and radius 4. AutomationId `ShareSlot_<n>`.
- Each slot also gets a 20×20 `LeafIconButtonStyle` "×" button at its top right (`ShareSlot_<n>_Remove`, Name "Remove this time"). This button **is** hit-testable; put it on the column's top layer.
- Redraw on `ShareChanged`.

- [ ] **Step 4: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(app): share availability (S) with slot picking, calendar and zone choice, and booking pages link"
```

---

### Task 9: Working-Hours Shading and the All-Day Default

**Files:**
- Create: `src/LeafCalendar.Core/Views/WorkingHoursMath.cs`
- Modify: `src/LeafCalendar.App/Controls/DayColumn.cs`, `TimeGridView.cs`, `LeafBrushes.cs`
- Test: `tests/LeafCalendar.Tests/WorkingHoursMathTests.cs` (new); `tests/LeafCalendar.UITests/WorkingHoursTests.cs` (new)

**Interfaces:**
- Consumes: `LeafSettings.WorkingHours`, `LeafSettings.AllDayExpanded` (Task 1).
- Produces:
  - `WorkingHoursMath.OffHours(WorkingHours hours, DayOfWeek day)` → `IReadOnlyList<(int StartMinute, int EndMinute)>`
  - `LeafBrushes.OffHours`
  - Automation IDs: `OffHours_<yyyy-MM-dd>_<n>`

- [ ] **Step 1: Write the failing tests**

`WorkingHoursMathTests.cs`:

```csharp
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class WorkingHoursMathTests
{
    [Fact]
    public void Workday_ShadesBeforeAndAfter() =>
        Assert.Equal([(0, 540), (1020, 1440)], WorkingHoursMath.OffHours(new WorkingHours(), DayOfWeek.Thursday));

    [Fact]
    public void DayOff_ShadesAllDay() =>
        Assert.Equal([(0, 1440)], WorkingHoursMath.OffHours(new WorkingHours(), DayOfWeek.Saturday));

    [Fact]
    public void Disabled_ShadesNothing() =>
        Assert.Empty(WorkingHoursMath.OffHours(new WorkingHours { Enabled = false }, DayOfWeek.Saturday));

    [Fact]
    public void MidnightToMidnight_ShadesNothingOnWorkdays() =>
        Assert.Empty(WorkingHoursMath.OffHours(new WorkingHours { StartMinute = 0, EndMinute = 1440 }, DayOfWeek.Monday));
}
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*WorkingHoursMathTests"`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>The stretches of a day outside your working hours, in minutes from local midnight (end exclusive).</summary>
public static class WorkingHoursMath
{
    /// <summary>Nothing when shading is off; the whole day on a day off; before the start and after the end on a workday.</summary>
    public static IReadOnlyList<(int StartMinute, int EndMinute)> OffHours(WorkingHours hours, DayOfWeek day)
    {
        if (!hours.Enabled)
        {
            return [];
        }

        if (!hours.Days.Contains(day))
        {
            return [(0, 1440)];
        }

        return new[] { (0, hours.StartMinute), (hours.EndMinute, 1440) }.Where(r => r.Item2 > r.Item1).ToList();
    }
}
```

`LeafBrushes.OffHours`, per theme: Light `#0A000000`, Dark `#29000000`, HighContrast `Transparent`. In contrast themes the hour lines carry the structure, and a tint would cut contrast.

`DayColumn.cs`: on the overlay layer's level, behind everything (below overlay blocks and slots), draw one `Rectangle` per `OffHours(Settings.WorkingHours, date.DayOfWeek)` range, using the column's existing minute-to-pixel mapping. Each has `Fill = LeafBrushes.OffHours`, `IsHitTestVisible = false`, and AutomationId `OffHours_<date:yyyy-MM-dd>_<n>`. Redraw on `LayoutChanged` (settings) and theme changes, as the column does for its other brushes. The minutes are the wall clock of the zone on screen (`ViewModel.Zone`); state that in a comment.

`TimeGridView.cs`: start `_allDayExpanded` from `ViewModel.Settings.AllDayExpanded` when the view is built. When the setting itself changes (compare against the last seen value on `LayoutChanged`), apply it. Otherwise, your clicks on the chevron stay as they are.

- [ ] **Step 4: Run them and see them pass**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Write the UI tests**

`WorkingHoursTests.cs`:

```csharp
    [Fact]
    public void Default_ShadesBeforeNineAndAfterFive()
    {
        // OffHours_2026-10-01_0 starts at the column top and is 9 × the hour height tall (48 DIPs default, ± 2 px);
        // OffHours_2026-10-01_1 starts at the 17:00 line and is 7 hours tall
    }

    [Fact]
    public void Weekend_IsShadedAllDay()
    {
        // OffHours_2026-10-03_0 is 24 × the hour height tall
    }

    [Fact]
    public void Disabled_NoShading()
    {
        // seed WorkingHours.Enabled = false → no element with an AutomationId starting "OffHours_"
    }

    [Fact]
    public void CustomHours_FromSettings()
    {
        // Settings › General: working hours 8:00–16:00 (Task 13's controls) → OffHours_2026-10-01_0 is 8 hours tall
    }

    [Fact]
    public void AllDayExpanded_Seeded_StartsExpanded()
    {
        // add four all-day events on Oct 1 (FakeGoogleServer.AddEvent) and seed AllDayExpanded = true → all four
        // all-day blocks are on screen without clicking AllDayExpand
    }
```

- [ ] **Step 6: Build**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "feat(app): working-hours shading and the all-day row's default"
```

Track B is done. Merge it at M1.

---

## Track C: Editor Extras, Guests, Calendars, and Settings

Track C works in `../leaf-m5-c` on `m5-track-c`. Its tasks run in order: 10, 11, 12, 13. Track C owns the main `CalendarViewModel.cs`. Tracks A and B use only their own partial files.

### Task 10: Workspace Accounts, Directory, People You Meet Often, and Rooms

**Files:**
- Modify: `src/LeafCalendar.Core/Data/Schema.cs`, `LeafDatabase.cs` (new migration), `AccountStore.cs`
- Modify: `src/LeafCalendar.Core/Auth/GoogleOAuthClient.cs` (`DirectoryScope`), `SignInFlow.cs` (store `hd`)
- Modify: `src/LeafCalendar.Core/Hosting/GoogleServices.cs` (`RefreshHostedDomainsAsync`)
- Modify: `src/LeafCalendar.Core/People/ContactSearch.cs` (directory source; `ValidEmail` and `Plain` become `internal static`)
- Create: `src/LeafCalendar.Core/People/FrequentPeople.cs`, `src/LeafCalendar.Core/People/Rooms.cs`
- Modify: `src/LeafCalendar.Core/Editing/EventDraft.cs` (`Guest.IsResource`), `EventJson.cs` (attendee `resource`)
- Modify: `src/LeafCalendar.App/ViewModels/EventEditorViewModel.cs`, `src/LeafCalendar.App/Views/EventEditorView.xaml(.cs)`, `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`
- Test: `AccountStoreTests.cs`, `LeafDatabaseTests.cs`, `ContactSearchTests.cs`, `EventJsonTests.cs`, `FrequentPeopleTests.cs` (new), `RoomsTests.cs` (new); `tests/LeafCalendar.UITests/GuestDirectoryTests.cs` (new)

**Interfaces:**
- Consumes: `GoogleUserInfo.Hd`, `DirectorySearchResponse`, and the fake's `HostedDomain` and directory route (Task 1).
- Produces:
  - `Account` gains `string? HostedDomain = null` (last positional parameter; null = not known yet, `""` = a personal account)
  - `AccountStore.SetHostedDomain(SqliteConnection conn, string accountId, string hostedDomain)`, `AccountStore.IsWorkspace(Account a)`
  - `GoogleOAuthClient.DirectoryScope`
  - `GoogleServices.RefreshHostedDomainsAsync(CancellationToken ct)`
  - `FrequentPeople.Load(SqliteConnection conn, string accountId, DateTimeOffset now)` → `IReadOnlyList<Contact>`, and `FrequentPeople.Match(IReadOnlyList<Contact> people, string query, int max = 8)`
  - `public sealed record Room(string Name, string Email)`, `Rooms.Load(SqliteConnection conn, string accountId)`, `Rooms.Match(IReadOnlyList<Room> rooms, string query, int max = 8)`
  - `Guest.IsResource` (`bool`, default false)
  - `EventEditorViewModel`:
    - `Func<string, string, IReadOnlyList<Contact>>? LocalPeople` (account, query)
    - `Func<string, bool>? IsWorkspaceAccount`
    - `IReadOnlyList<Room> Rooms`, `bool ShowRooms`
    - `ObservableCollection<RoomSuggestion> RoomSuggestions`, `void PickRoom(RoomSuggestion room)`
  - `CalendarViewModel.IsWorkspace(string accountId)`
  - Automation IDs: `EditorRoomInput`, `EditorGuest_<email>` (unchanged for people; rooms read "{name} (room)")

- [ ] **Step 1: Write the failing Core tests**

`FrequentPeopleTests.cs`. Use `OccurrenceQueryTests`' constructor and `Insert` helper:

```csharp
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    static string With(string id, string start, string attendees) =>
        $$"""{"id":"{{id}}","status":"confirmed","summary":"x","start":{"dateTime":"{{start}}"},"end":{"dateTime":"{{start}}"},"attendees":[{{attendees}}]}""";

    [Fact]
    public void Load_MostFrequentFirst_SkipsYouRoomsAndOldEvents()
    {
        const string self = """{"email":"leaf.tester@gmail.com","self":true}""";
        const string room = """{"email":"c_1@resource.calendar.google.com","displayName":"Room 1","resource":true}""";
        Insert(With("a", "2026-09-20T10:00:00Z", $$"""{{self}},{{room}},{"email":"frank@example.com","displayName":"Frank Often"},{"email":"amy@example.com"}"""));
        Insert(With("b", "2026-09-21T10:00:00Z", $$"""{{self}},{"email":"Frank@example.com"}"""));
        Insert(With("c", "2025-01-01T10:00:00Z", $$"""{{self}},{"email":"old@example.com"}"""));

        using var conn = _db.Database.Open();
        var people = FrequentPeople.Load(conn, Account, Now);

        Assert.Equal(["frank@example.com", "amy@example.com"], people.Select(p => p.Email));
        Assert.Equal("Frank Often", people[0].Name);
    }

    [Fact]
    public void Load_HostileNamesAndAddresses_AreCleanedOrDropped()
    {
        Insert(With("h", "2026-09-20T10:00:00Z", """{"email":"eve@example.com","displayName":"‮Eve\u0000"},{"email":"not an address"}"""));

        using var conn = _db.Database.Open();
        var person = Assert.Single(FrequentPeople.Load(conn, Account, Now));

        Assert.Equal(("Eve", "eve@example.com"), (person.Name, person.Email));
    }

    [Theory]
    [InlineData("fra", 1)]
    [InlineData("often", 1)]
    [InlineData("example", 2)]
    [InlineData("zz", 0)]
    public void Match_PrefixOfANameWordOrTheAddress(string query, int count) =>
        Assert.Equal(count, FrequentPeople.Match([new("Frank Often", "frank@example.com"), new("", "amy@example.com")], query).Count);
```

`example` matches both because an address match is a prefix of the address or of its domain part. State that rule in the summary.

`RoomsTests.cs`: rooms are attendees with `"resource": true`, or addresses ending in `@resource.calendar.google.com`:
- Distinct by address.
- The name is the display name cleaned, else the address's local part.
- Sorted by name.
- `Match` is a case-insensitive contains on the name.

Write `Load_RoomsYouBookedBefore_DistinctAndNamed` and `Match_ContainsIgnoringCase` with the same `Insert` setup.

`ContactSearchTests.cs`: follow the file's existing handler setup, and add:

```csharp
    [Fact]
    public async Task Search_WithDirectoryScope_ListsDirectoryBetweenContactsAndOthers()
    // grant contacts + other contacts + directory; contacts answer Alice, directory answers Dana, others answer Bob →
    // emails in order: alice, dana, bob

    [Fact]
    public async Task Search_DirectoryRefused_KeepsTheOtherSources()
    // directory answers 400 {"error":{"status":"FAILED_PRECONDITION","message":"Must be a G Suite domain user."}} →
    // Access is Allowed, and alice and bob are still listed

    [Fact]
    public async Task Search_WithoutDirectoryScope_NeverAsksTheDirectory()
    // the grant lacks directory.readonly → no request path contains "searchDirectoryPeople"
```

Write each fully in the file's style. The directory request is `./people:searchDirectoryPeople?query=...&readMask=names,emailAddresses&sources=DIRECTORY_SOURCE_TYPE_DOMAIN_PROFILE&pageSize=10`.

`AccountStoreTests.cs`: `SetHostedDomain_RoundTrips_AndIsWorkspace` covers three cases:
- null → `IsWorkspace` false
- `"example.com"` → true
- `""` → false

`LeafDatabaseTests.cs`: a migration test, `Migrate_FromTheMilestone4Schema_AddsHostedDomainAsNull`, in the style of the file's existing migration tests.

`EventJsonTests.cs`:

```csharp
    [Fact]
    public void BuildCreate_RoomGuest_IsSentAsAResource()
    {
        var draft = SampleDraft() with { Guests = [new Guest("c_1@resource.calendar.google.com", IsResource: true)] };

        var body = EventJson.BuildCreate("id1", draft);

        Assert.True((bool)body["attendees"]![0]!["resource"]!);
    }

    [Fact]
    public void ReadDraft_ResourceAttendee_IsARoom()
    {
        var draft = EventJson.ReadDraft("a", "c", """{"id":"x","attendees":[{"email":"c_1@resource.calendar.google.com","resource":true}]}""", Start, End, false);

        Assert.True(Assert.Single(draft.Guests).IsResource);
    }
```

Use the file's own sample-draft helper and times.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*FrequentPeopleTests"`, and the same for `*RoomsTests`, `*ContactSearchTests`, `*AccountStoreTests`, `*LeafDatabaseTests`, and `*EventJsonTests`.
Expected: build errors and failures.

- [ ] **Step 3: Implement the Core side**

- **Schema:** add the next migration (`ALTER TABLE accounts ADD COLUMN hosted_domain TEXT;`), registered in `LeafDatabase.Migrate`'s ordered list. `AccountStore` reads and writes the column. `Upsert` must not overwrite a known domain with null; use `COALESCE(excluded.hosted_domain, accounts.hosted_domain)`.
- **`AccountStore`:** add `SetHostedDomain` and `IsWorkspace(Account a) => !string.IsNullOrEmpty(a.HostedDomain)`.
- **`GoogleOAuthClient`:** add `public const string DirectoryScope = "https://www.googleapis.com/auth/directory.readonly";` and use it in `Scopes` instead of the literal.
- **`SignInFlow`:** store `user.Hd ?? ""` on the account it upserts.
- **`GoogleServices.RefreshHostedDomainsAsync(ct)`:** for each account whose `HostedDomain` is null:
  1. `OAuth.GetUserInfoAsync(await AccessTokens.GetAccessTokenAsync(id, ct), ct)`, then `SetHostedDomain(id, user.Hd ?? "")`.
  2. On failure, log `account.domain.failed` with `account=` and the error type, and try again next launch.
- **`ContactSearch`:**
  - The directory source is searched when `HasScopeAsync(accountId, DirectoryScope)` is true, without a warmup.
  - Its answer is `DirectorySearchResponse`, mapped into the same `Clean` (wrap `People` as results).
  - Any non-success from the directory is just a failed source: it never sets `NeedsConsent` or `ApiDisabled`, since personal accounts always get 400 here. Log it with `contacts.search.failed` by status only, like the others.
  - The merge order is contacts, directory, others.
  - Make `ValidEmail` and `Plain` `internal static`, so `FrequentPeople` and `Rooms` reuse them.
- **`FrequentPeople.Load`:**
  1. Read `raw_json` of this account's events where `status <> 'cancelled'`, `start_utc` is between now − 180 days and now + 30 days, and `raw_json LIKE '%"attendees"%'`.
  2. For each attendee that isn't `self`, isn't a room, and has a valid email: count it (case-insensitive) and keep its latest start and last non-empty cleaned name.
  3. Order by count descending, then latest start descending, and take 50.

  Only addresses and names leave the method.
- **`Rooms.Load`:** the same scan without the date window: every event of the account, rooms only. Write it as described in Step 1.
- **`Guest`:** add `bool IsResource = false` as the last positional parameter.
- **`EventJson`:**
  - The attendee builder writes `"resource": true` for room guests.
  - `ReadDraft` sets `IsResource` from `resource`, or from the `@resource.calendar.google.com` suffix.
  - `MergeAttendees` keeps it.

- [ ] **Step 4: Run them and see them pass**

Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: The editor and the view model**

`CalendarViewModel.cs`:
- At startup, after Google is ready (where the first `RefreshAsync` runs), call `Fire(() => _services.Google!.RefreshHostedDomainsAsync(_life.Token))` once per session. Then call `ReloadCalendars()`, so the editor sees the result.
- Add `public bool IsWorkspace(string accountId)`, reading the account rows already loaded for `AccountEmails` (load the `HostedDomain` alongside).
- Where the editor view model is created (`BeginEdit`, `BeginCreate`), set:
  - `LocalPeople = (account, query) => FrequentPeople.Match(<cached per account per editor session>, query)`. Load once per editor, on a background thread, the first time the guest box gets text.
  - `IsWorkspaceAccount = IsWorkspace`.
  - `Rooms` = `Rooms.Load(...)` for the draft's account, loaded the same lazy way.
- `GuestsMailto` leaves out room guests.

`EventEditorViewModel.cs`:
- **`RefreshSuggestionsAsync`:** show `LocalPeople` matches at once, then merge the Google results after them (dedupe by email, case-insensitive), capped at `ContactSearch.MaxResults`.
- **`ShowRooms`:** `IsWorkspaceAccount?.Invoke(ContactsAccountId) == true`. Notify on `CalendarIndex` changes.
- **Rooms:** `RoomInput` and `RoomSuggestions` (App class `RoomSuggestion(Room room)` with `Display` = name). `PickRoom` adds `new Guest(room.Email, IsResource: true)` unless it's already there, then clears the input.
- **`GuestRow`:** gains `bool IsRoom` and `string DisplayName` ("{name} (room)" for rooms; the room's name comes from `Rooms`, else the email). The Optional toggle is hidden for rooms.

`EventEditorView.xaml`: under the guest input, add a room `AutoSuggestBox` (`EditorRoomInput`, `PlaceholderText="Add a room"`, `Visibility="{x:Bind ViewModel.ShowRooms, Mode=OneWay}"`), with suggestions bound through `x:Bind` to `RoomSuggestions` in the same pattern as the guest box. Room chips use glyph `E7BE` (16) before the name.

- [ ] **Step 6: Write the UI tests**

`GuestDirectoryTests.cs`. `_google.HostedDomain = "example.com"` makes the seeded account a Workspace one:

```csharp
    [Fact]
    public void Directory_SuggestsColleagues()
    {
        // HostedDomain set; C → type "dana" in EditorGuestInput's Edit → "Dana Director <dana@example.com>" suggested → pick → guest chip dana@example.com
    }

    [Fact]
    public void PeopleYouMeetOften_ComeFirst()
    {
        // C → type "fra" → the first suggestion is "Frank Often <frank@example.com>" (events-rooms fixture)
    }

    [Fact]
    public void Rooms_WorkspaceOnly_AddedAsResources()
    {
        // HostedDomain set; C, title "Room test" → EditorRoomInput visible → type "room" → "Room 4 West" → pick →
        // a chip named "Room 4 West (room)" → Ctrl+Enter → the POST body's attendees contain
        // {"email":"c_1888room4west@resource.calendar.google.com","resource":true}
    }

    [Fact]
    public void Rooms_HiddenForPersonalAccounts()
    {
        // no HostedDomain → C → no EditorRoomInput
    }

    [Fact]
    public void ExistingAccount_LooksUpItsDomainOnce()
    {
        // HostedDomain set; launch twice; _google.Requests contains exactly one "GET /userinfo" across both launches
    }

    [Fact]
    public void Suggestions_WriteNoNamesToTheLog()
    {
        // after the directory, frequent, and room steps, leaf.log contains none of "dana", "frank", "Room 4"
    }
```

- [ ] **Step 7: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "feat: Workspace accounts, directory and frequent-people guest suggestions, and rooms"
```

---

### Task 11: Editor Extras (Event Type, Show As, Visibility, Time Zone, Meet Default, Map Provider)

**Files:**
- Modify: `src/LeafCalendar.Core/Editing/EventDraft.cs`, `EventJson.cs`
- Create: `src/LeafCalendar.Core/Editing/EditorTimes.cs`
- Modify: `src/LeafCalendar.Core/Events/LinkSafety.cs` (`MapsSearch` provider)
- Modify: `src/LeafCalendar.App/ViewModels/EventEditorViewModel.cs`, `src/LeafCalendar.App/Views/EventEditorView.xaml(.cs)`, `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`, `src/LeafCalendar.App/Views/DetailsPanel.xaml(.cs)`, `src/LeafCalendar.App/Views/CalendarPage.Extras.cs`
- Test: `EventJsonTests.cs`, `EditorTimesTests.cs` (new), `LinkSafetyTests.cs`; `tests/LeafCalendar.UITests/EditorExtrasTests.cs` (new)

**Interfaces:**
- Consumes: `EventKind` (`Default`, `FocusTime`, `OutOfOffice`), `IsWorkspace` (Task 10), `LeafSettings.MeetByDefaultAccounts` / `MapProvider` (Task 1), `TimeZoneCatalog`.
- Produces:
  - `EventDraft.EventType` (`EventKind`, default `Default`), `EventDraft.IsFree` (`bool`), `EventDraft.Visibility` (`string`, default `"default"`)
  - `EditorTimes.ToInstant(DateOnly date, TimeSpan time, TimeZoneInfo zone)` and `EditorTimes.FromInstant(DateTimeOffset at, TimeZoneInfo zone)` → `(DateOnly Date, TimeSpan Time)`
  - `LinkSafety.MapsSearch(string location, MapProvider provider = MapProvider.Google)`
  - `EventEditorView.FocusTimeZone()`
  - Automation IDs: `EditorEventType`, `EditorShowAs`, `EditorVisibility`, `EditorTimeZoneBox`, `EditorLocalTimeText`

- [ ] **Step 1: Write the failing Core tests**

`EditorTimesTests.cs`:

```csharp
using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public sealed class EditorTimesTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeZoneInfo Tokyo   = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [Fact]
    public void ToInstant_Ordinary() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(9)), EditorTimes.ToInstant(new(2026, 10, 1), TimeSpan.FromHours(9), Tokyo));

    [Fact]
    public void ToInstant_SpringForwardGap_MovesForward() =>
        // 2:30 AM doesn't exist on Mar 8, 2026 in New York; it becomes 3:30 AM EDT
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), EditorTimes.ToInstant(new(2026, 3, 8), new TimeSpan(2, 30, 0), NewYork).ToUniversalTime());

    [Fact]
    public void ToInstant_FallBackRepeat_TakesTheFirst() =>
        // 1:30 AM happens twice on Nov 1, 2026; the first (EDT, 05:30Z) wins
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), EditorTimes.ToInstant(new(2026, 11, 1), new TimeSpan(1, 30, 0), NewYork).ToUniversalTime());

    [Fact]
    public void FromInstant_RoundTrips()
    {
        var at = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal((new DateOnly(2026, 10, 1), TimeSpan.FromHours(22)), EditorTimes.FromInstant(at, Tokyo));
        Assert.Equal(at, EditorTimes.ToInstant(new(2026, 10, 1), TimeSpan.FromHours(22), Tokyo));
    }
}
```

`EventJsonTests.cs`:

```csharp
    [Fact]
    public void BuildCreate_FocusTime_HasTypeAndProperties_NoGuestsOrLocation()
    {
        var draft = SampleDraft() with { EventType = EventKind.FocusTime, Location = "x", Guests = [new Guest("a@example.com")] };

        var body = EventJson.BuildCreate("id1", draft);

        Assert.Equal("focusTime", (string?)body["eventType"]);
        Assert.Equal("declineOnlyNewConflictingInvitations", (string?)body["focusTimeProperties"]!["autoDeclineMode"]);
        Assert.Equal("doNotDisturb", (string?)body["focusTimeProperties"]!["chatStatus"]);
        Assert.False(body.ContainsKey("attendees"));
        Assert.False(body.ContainsKey("location"));
        Assert.False(body.ContainsKey("conferenceData"));
    }

    [Fact]
    public void BuildCreate_OutOfOffice_HasTypeAndProperties()
    {
        var body = EventJson.BuildCreate("id1", SampleDraft() with { EventType = EventKind.OutOfOffice });

        Assert.Equal("outOfOffice", (string?)body["eventType"]);
        Assert.Equal("declineOnlyNewConflictingInvitations", (string?)body["outOfOfficeProperties"]!["autoDeclineMode"]);
    }

    [Fact]
    public void BuildCreate_FreeAndPrivate()
    {
        var body = EventJson.BuildCreate("id1", SampleDraft() with { IsFree = true, Visibility = "private" });

        Assert.Equal("transparent", (string?)body["transparency"]);
        Assert.Equal("private", (string?)body["visibility"]);
    }

    [Fact]
    public void BuildPatch_ShowAsAndVisibility_OnlyWhenChanged()
    {
        var before = SampleDraft();

        Assert.False(EventJson.BuildPatch(before, before).ContainsKey("transparency"));
        Assert.Equal("transparent", (string?)EventJson.BuildPatch(before, before with { IsFree = true })["transparency"]);
        Assert.Equal("opaque", (string?)EventJson.BuildPatch(before with { IsFree = true }, before)["transparency"]);
        Assert.Equal("public", (string?)EventJson.BuildPatch(before, before with { Visibility = "public" })["visibility"]);
    }

    [Fact]
    public void BuildPatch_NeverSendsEventType() =>
        Assert.False(EventJson.BuildPatch(SampleDraft(), SampleDraft() with { EventType = EventKind.FocusTime }).ContainsKey("eventType"));

    [Fact]
    public void BuildPatch_TimeZoneChanged_SendsBothEndsWithTheZone()
    {
        var before = SampleDraft() with { TimeZone = "America/New_York" };
        var patch  = EventJson.BuildPatch(before, before with { TimeZone = "Asia/Tokyo" });

        Assert.Equal("Asia/Tokyo", (string?)patch["start"]!["timeZone"]);
        Assert.Equal("Asia/Tokyo", (string?)patch["end"]!["timeZone"]);
    }

    [Fact]
    public void ReadDraft_TypeShowAsVisibility()
    {
        var draft = EventJson.ReadDraft("a", "c", """{"id":"x","eventType":"outOfOffice","transparency":"transparent","visibility":"private"}""", Start, End, false);

        Assert.Equal((EventKind.OutOfOffice, true, "private"), (draft.EventType, draft.IsFree, draft.Visibility));
    }
```

`LinkSafetyTests.cs`:

```csharp
    [Fact]
    public void MapsSearch_Bing() =>
        Assert.Equal("https://www.bing.com/maps?q=Room%204", LinkSafety.MapsSearch("Room 4", MapProvider.Bing).AbsoluteUri);

    [Fact]
    public void MapsSearch_GoogleStaysTheDefault() =>
        Assert.StartsWith("https://www.google.com/maps/search/", LinkSafety.MapsSearch("Room 4").AbsoluteUri, StringComparison.Ordinal);
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*EditorTimesTests"`, and the same for `*EventJsonTests` and `*LinkSafetyTests`.
Expected: build errors.

- [ ] **Step 3: Implement the Core side**

- **`EventDraft`:** add `EventType` (`EventKind`, default `EventKind.Default`), `IsFree` (`bool`), and `Visibility` (`string`, `"default"`). Each gets a summary, and `EventType` says "set only when creating; Google can't change an event's type".
- **`EventJson.BuildCreate`:**
  - **`FocusTime`:** `eventType: "focusTime"` and `focusTimeProperties: { autoDeclineMode: "declineOnlyNewConflictingInvitations", chatStatus: "doNotDisturb" }`.
  - **`OutOfOffice`:** `eventType: "outOfOffice"` and `outOfOfficeProperties: { autoDeclineMode: "declineOnlyNewConflictingInvitations" }`.
  - Either type removes `attendees`, `location`, and `conferenceData`.
  - `IsFree` → `transparency: "transparent"`. `Visibility` other than `"default"` → `visibility`.
  - Make sure timed events always carry `start.timeZone` and `end.timeZone` from `draft.TimeZone` when it's set.
- **`EventJson.BuildPatch`:**
  - `transparency` (`"transparent"` or `"opaque"`) only when `IsFree` changed, and `visibility` only when it changed.
  - When `TimeZone` changed on a timed event, send both `start` and `end` with `dateTime` and `timeZone`, even if the instants didn't change.
  - Never `eventType`.
- **`EventJson.ReadDraft`:** `eventType` (`focusTime` → `FocusTime`, `outOfOffice` → `OutOfOffice`, else `Default`), `transparency == "transparent"`, and `visibility` (default `"default"`).
- **`EditorTimes.ToInstant`:**
  1. Build the local `DateTime` (`Kind = Unspecified`).
  2. If `zone.IsInvalidTime(local)`, add `zone.GetAdjustmentRules()`' current daylight delta. Ponytail: add one hour, the gap in every zone Leaf users meet; comment it.
  3. If `zone.IsAmbiguousTime(local)`, use the larger of `zone.GetAmbiguousTimeOffsets(local)` (daylight, the first).
  4. Otherwise `zone.GetUtcOffset(local)`.
- **`EditorTimes.FromInstant`:** `TimeZoneInfo.ConvertTime(at, zone)` split into date and time of day.
- **`LinkSafety.MapsSearch(location, provider)`:** Bing is `new("https://www.bing.com/maps?q=" + Uri.EscapeDataString(location))`; Google is unchanged.

- [ ] **Step 4: Run them and see them pass**

Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: The editor**

`EventEditorViewModel.cs`:
- **`EventTypeIndex`** (0 Event, 1 Focus time, 2 Out of office) and **`ShowEventType`**: `IsNew && IsWorkspaceAccount(account) && selected calendar is the account's primary && !IsAllDay`. `CalendarChoice` gains `bool IsPrimary`; set it where choices are built in `CalendarViewModel`. While the type isn't Event:
  - `ShowGuests`, `ShowLocation`, and `ShowConference` are false.
  - Choosing a type while `Title` is empty sets it to "Focus time" or "Out of office".
  - `IsAllDay` is locked off.
- **`ShowAsIndex`** (0 Busy, 1 Free) and **`VisibilityIndex`** (0 Default visibility, 1 Public, 2 Private). `confidential` reads as Private, and a save without a change patches nothing, since the draft keeps the raw value unless you change the box.
- **Time zone:**
  - `TimeZoneId` starts from the draft's zone, else the display zone's IANA ID. `TimeZoneText` is `"{City} (UTC±h)"`, with `ZoneSuggestions` from `TimeZoneCatalog.Search`.
  - The editor's dates and times are shown **in the event's zone**. Convert them with `EditorTimes.FromInstant` on load and `EditorTimes.ToInstant` in `ToDraft`.
  - Changing the zone keeps the wall-clock fields and changes the instants.
  - `LocalTimeText`, shown only when the event's zone differs from the zone on screen: `"In your time: {ddd, MMM d}, {start}–{end}"`.
- **Meet by default:** a new event's `HasConference` starts from `MeetByDefault(accountId)`. While you haven't touched the conference buttons, switching the calendar to another account applies that account's default.

`CalendarViewModel.cs`:
- When building the editor: pass `MeetByDefault = id => Settings.MeetByDefaultAccounts.Contains(id)`, `IsWorkspaceAccount`, and each choice's `IsPrimary`.
- `OpenLocationAsync` uses `LinkSafety.MapsSearch(location, Settings.MapProvider)`.
- Add `public string MapButtonText => Settings.MapProvider == MapProvider.Bing ? "Open in Bing Maps" : "Open in Google Maps";`, refreshed with the selection.

`EventEditorView.xaml`:
- After the calendar picker, add one row of two `ComboBox`es: `EditorShowAs` ("Busy", "Free"; `AutomationProperties.Name="Show as"`) and `EditorVisibility` ("Default visibility", "Public", "Private"; Name "Visibility").
- Above the title, add `EditorEventType` ("Event", "Focus time", "Out of office"; Name "Event type"), shown by `ShowEventType`.
- Under the date and time rows, add the zone box `EditorTimeZoneBox` (`AutoSuggestBox`, Name "Time zone", hidden for all-day), then `EditorLocalTimeText` (Caption, secondary).
- Bind the guest, room, location, and conferencing rows' visibility to the new `Show*` properties.

`EventEditorView.xaml.cs`: `public void FocusTimeZone()` focuses `EditorTimeZoneBox` programmatically. If the event is all-day it does nothing; the caller has already said why.

`DetailsPanel.xaml`: the location button's text binds to `ViewModel.MapButtonText`.

`CalendarPage.Extras.cs`:

```csharp
    // E then Z (spec 8.7): open the editor on the event's time zone
    void EditTimeZone()
    {
        if (ViewModel.SelectedInfo is { Occurrence.IsAllDay: true })
        {
            ViewModel.ShowMessage("All-day events don't have a time zone");
            return;
        }

        if (ViewModel.Editing is null)
        {
            ViewModel.BeginEdit();
        }

        DispatcherQueue.TryEnqueue(() => Details.EditorView?.FocusTimeZone());
    }
```

- [ ] **Step 6: Write the UI tests**

`EditorExtrasTests.cs`:

```csharp
    [Fact]
    public void ShowAsFree_PatchesTransparencyOnly()
    {
        // select evt-single → E → EditorShowAs = "Free" → Ctrl+Enter → the PATCH body is exactly {"transparency":"transparent"}
    }

    [Fact]
    public void VisibilityPrivate_Patches()
    {
        // select evt-single → E → EditorVisibility = "Private" → Ctrl+Enter → the PATCH body has "visibility":"private"
    }

    [Fact]
    public void TimeZoneTokyo_KeepsTheClock_AndShowsYourTime()
    {
        // select evt-single (9–10 ET) → E → EditorTimeZoneBox: pick Tokyo → EditorLocalTimeText reads
        // "In your time: Wed, Sep 30, 8:00 PM–9:00 PM" → Ctrl+Enter → the PATCH start is
        // {"dateTime":"2026-10-01T09:00:00+09:00","timeZone":"Asia/Tokyo"}
    }

    [Fact]
    public void EThenZ_FocusesTheTimeZoneBox()
    {
        // select evt-single → E, Z (within 1.5 s) → the focused element is EditorTimeZoneBox or inside it
    }

    [Fact]
    public void EventType_HiddenForPersonalAccounts()
    {
        // no HostedDomain → C → no EditorEventType
    }

    [Fact]
    public void FocusTime_OnAWorkspacePrimaryCalendar()
    {
        // HostedDomain "example.com" → C → EditorEventType = "Focus time" → the title reads "Focus time"; no EditorGuestInput →
        // Ctrl+Enter → the POST body has "eventType":"focusTime" and focusTimeProperties, and no attendees
    }

    [Fact]
    public void MeetByDefault_NewEventsGetMeet()
    {
        // seed MeetByDefaultAccounts = [SeededProfile.AccountId] → C, title "x", Ctrl+Enter → the POST body has conferenceData.createRequest
    }

    [Fact]
    public void BingMaps_OpensBing()
    {
        // seed MapProvider Bing → select evt-meeting → the location button reads "Open in Bing Maps" → click →
        // launched.txt has "https://www.bing.com/maps?q=Room%204"
    }
```

- [ ] **Step 7: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "feat: event type, show as, visibility, the event's own time zone (E then Z), Meet by default, and Bing Maps"
```

---

### Task 12: Calendar Rename, Default Reminders, Order, and Upcoming for One Calendar

**Files:**
- Create: `src/LeafCalendar.Core/Editing/CalendarEdits.cs`, `src/LeafCalendar.App/Views/RenameCalendarDialog.cs`
- Modify: `src/LeafCalendar.Core/Data/CalendarStore.cs` (`GoogleName`, setters)
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`, `src/LeafCalendar.App/Views/SidebarView.xaml(.cs)` (right-click menu), `src/LeafCalendar.App/Views/DetailsPanel.xaml(.cs)` (upcoming header), `src/LeafCalendar.App/Views/Settings/CalendarsPage.xaml(.cs)` (more menu, reminders dialog)
- Test: `CalendarEditsTests.cs` (new), `CalendarStoreTests.cs`; `tests/LeafCalendar.UITests/CalendarManageTests.cs` (new)

**Interfaces:**
- Consumes: `GoogleCalendarClient.PatchCalendarListAsync` (Task 1), `ReorderCalendars`, `ReminderTimes`, the existing upcoming builder.
- Produces:
  - `CalendarEdits.CleanName(string?)` → `string?` (null = use Google's name), `CalendarEdits.RenamePatch(string?)`, `CalendarEdits.RemindersPatch(IEnumerable<int>)`, `const int MaxName = 100`, `const int MaxReminders = 5`
  - `CalendarInfo` gains `string GoogleName = ""` (last positional parameter)
  - `CalendarStore.SetSummaryOverride(conn, accountId, calendarId, string? name)` and `CalendarStore.SetDefaultReminders(conn, accountId, calendarId, string json)`
  - `CalendarViewModel`:
    - `Task<bool> RenameCalendarAsync(CalendarInfo calendar, string? text)`
    - `Task<bool> SetCalendarRemindersAsync(CalendarInfo calendar, IReadOnlyList<int> minutes)`
    - `void MoveCalendar(CalendarInfo calendar, int delta)`
    - `CalendarInfo? UpcomingCalendar`, `void ShowUpcomingFor(CalendarInfo? calendar)`
  - Automation IDs:
    - `CalendarMenu_Rename`, `CalendarMenu_Upcoming`, `CalendarMenu_Color`
    - `RenameCalendarBox`
    - `UpcomingHeader`, `UpcomingShowAll`
    - `CalendarMore_<calendarId>`, `CalendarMenu_MoveUp`, `CalendarMenu_MoveDown`, `CalendarMenu_Reminders`, `ReminderRow_<n>`, `AddReminderButton`

- [ ] **Step 1: Write the failing Core tests**

`CalendarEditsTests.cs`:

```csharp
using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public sealed class CalendarEditsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("‮\u0000")]
    public void CleanName_BlankOrHidden_UsesGooglesName(string? text) => Assert.Null(CalendarEdits.CleanName(text));

    [Fact]
    public void CleanName_StripsHiddenCharacters_AndCaps()
    {
        Assert.Equal("Kids", CalendarEdits.CleanName("  Ki‮ds\u0007 "));
        Assert.Equal(CalendarEdits.MaxName, CalendarEdits.CleanName(new string('k', 300))!.Length);
    }

    [Fact]
    public void RenamePatch_NullIsJsonNull()
    {
        Assert.Equal("""{"summaryOverride":null}""", CalendarEdits.RenamePatch(null));
        Assert.Equal("""{"summaryOverride":"Kids"}""", CalendarEdits.RenamePatch("Kids"));
    }

    [Fact]
    public void RemindersPatch_PopupsSortedDistinctInRangeCapped() =>
        Assert.Equal("""{"defaultReminders":[{"method":"popup","minutes":1},{"method":"popup","minutes":2},{"method":"popup","minutes":3},{"method":"popup","minutes":4},{"method":"popup","minutes":10}]}""",
            CalendarEdits.RemindersPatch([30, 10, 10, -5, 50000, 1, 2, 3, 4]));
}
```

`CalendarStoreTests.cs`: add `SetSummaryOverride_ShowsTheName_GoogleNameKept`. After `SetSummaryOverride(..., "Kids")`, `GetAll` gives `Summary == "Kids"` and `GoogleName == "Family"`. After `SetSummaryOverride(..., null)`, it gives `Summary == "Family"`.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*CalendarEditsTests"`, then `--filter-class "*CalendarStoreTests"`.
Expected: build errors.

- [ ] **Step 3: Implement the Core side**

- **`CalendarEdits`:**
  - `CleanName` reuses `ContactSearch.Plain` (internal since Task 10) with `MaxName`, and returns null for empty.
  - `RenamePatch` builds `new JsonObject { ["summaryOverride"] = name is null ? null : JsonValue.Create(name) }.ToJsonString()`.
  - `RemindersPatch` keeps 0–40,320, distinct, sorted ascending, at most 5, each `{"method":"popup","minutes":m}`.
  - Document that Google holds these values; Leaf mirrors them locally only after Google accepts.
- **`CalendarStore`:**
  - Select `c.summary` as the new last column (`GoogleName`).
  - `SetSummaryOverride` updates `summary_override`.
  - `SetDefaultReminders` updates `default_reminders` with Google's JSON array, in the same format the calendar-list sync stores.

- [ ] **Step 4: Run them and see them pass**

Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: The app side**

`CalendarViewModel.cs`, in a new `// CALENDAR MANAGEMENT` section banner:
- **`RenameCalendarAsync(calendar, text)`:**
  1. `var name = CalendarEdits.CleanName(text);`
  2. If `_services.Google is null || IsOffline`: `ShowMessage("Connect to the internet to rename a calendar.")` and return false.
  3. `await client.PatchCalendarListAsync(calendar.AccountId, calendar.Id, CalendarEdits.RenamePatch(name), _life.Token)`.
     - On `HttpRequestException` or `TaskCanceledException` (not shutdown): the same offline message, return false.
     - On `GoogleApiException`: `ShowMessage("Google didn't accept that name.")`, return false.
  4. On success: `CalendarStore.SetSummaryOverride`, then `ReloadCalendars()`, and return true.
  5. Log `calendar.rename` with `account=` and `ok` or `failed` only, never the name.
- **`SetCalendarRemindersAsync`:** the same shape, with `RemindersPatch`, `SetDefaultReminders` on success, and the message "Connect to the internet to change default reminders.". The alert planner already reads `default_reminders`, so new reminders apply from the next pass.
- **`MoveCalendar(calendar, delta)`:** reorder within its account with the existing `ReorderCalendars`.
- **`ShowUpcomingFor(calendar)`:** sets `UpcomingCalendar` and rebuilds the upcoming list. With a calendar, the list covers the next 30 days, only that calendar, at most 50 events. Without one, it uses the normal window (Task 13 makes that a setting). Raise `PropertyChanged` for `UpcomingCalendar`.

`RenameCalendarDialog.cs`, static `Task<(bool Ok, string? Text)> ShowAsync(XamlRoot root, CalendarInfo calendar)`:
- `Title` "Rename calendar", primary "Rename", close "Cancel", default Primary.
- Content: a `TextBox` `RenameCalendarBox` with `Text = calendar.Summary`, `PlaceholderText = calendar.GoogleName`, and `MaxLength = 100`, then a Caption line: "Leave it empty to use the name from Google. Google Calendar shows this name too."

`SidebarView.xaml(.cs)`: give each calendar row a `ContextFlyout`, a `MenuFlyout` built in code per row. The row's `CalendarRow` is captured in the closures; nothing is read back from the item.
- "Rename…" (`CalendarMenu_Rename`, glyph `E8AC`) → the dialog, then `RenameCalendarAsync`
- "Show upcoming events" (`CalendarMenu_Upcoming`, glyph `E728`) → `ShowUpcomingFor(info)`, and open the details panel through the existing `DetailsOpenRequested` path
- "Change color…" (`CalendarMenu_Color`, glyph `E790`) → `OpenSettings?.Invoke(SettingsSection.Calendars)`

`DetailsPanel.xaml(.cs)`: the upcoming section's header (`UpcomingHeader`) reads "Upcoming", or "Upcoming in {calendar name}". With a calendar, it adds a `HyperlinkButton` "Show all calendars" (`UpcomingShowAll`) → `ShowUpcomingFor(null)`. The empty text with a calendar is "Nothing in the next 30 days."

`CalendarsPage.xaml(.cs)`: each calendar row gets a 32 DIP `LeafIconButtonStyle` "More options" button (`CalendarMore_<id>`, glyph `E712`, Name "More options for {calendar name}"). Its `MenuFlyout`:
- "Rename…" → the same dialog
- "Move up" (`CalendarMenu_MoveUp`) and "Move down" (`CalendarMenu_MoveDown`), disabled at the ends
- "Default reminders…" (`CalendarMenu_Reminders`), which opens a `ContentDialog`:
  - Title "Default reminders", primary "Save", close "Cancel", description "Google uses these for new events on this calendar."
  - Up to 5 rows (`ReminderRow_<n>`), each a `ComboBox` of `ReminderTimes` choices plus a remove button.
  - "Add reminder" (`AddReminderButton`), off at 5.
  - Save → `SetCalendarRemindersAsync`.

The rows update in place, as they do today.

- [ ] **Step 6: Write the UI tests**

`CalendarManageTests.cs`:

```csharp
    [Fact]
    public void Rename_FromTheSidebar_SavesToGoogle()
    {
        // right-click CalendarToggle_family123@group.calendar.google.com → CalendarMenu_Rename → RenameCalendarBox "Kids" → Rename →
        // WaitForWrite(PATCH .../calendarList/family123%40group.calendar.google.com) body {"summaryOverride":"Kids"}; the sidebar row reads "Kids"
    }

    [Fact]
    public void Rename_Blank_ResetsToGooglesName()
    {
        // rename to "Kids", then to "" → body {"summaryOverride":null}; the row reads "Family"
    }

    [Fact]
    public void Rename_Offline_KeepsTheName()
    {
        // _google.Offline = true (wait for OfflineIndicator) → rename "X" → NoticeBar "Connect to the internet to rename a calendar."; the row still reads "Family"
    }

    [Fact]
    public void ShowUpcoming_ForOneCalendar()
    {
        // add a family event Oct 2 10:00; right-click Family → CalendarMenu_Upcoming → UpcomingHeader "Upcoming in Family";
        // only family events listed; UpcomingShowAll → the header reads "Upcoming"
    }

    [Fact]
    public void ChangeColor_OpensSettingsCalendars()
    {
        // right-click → CalendarMenu_Color → the Settings window opens on Calendars
    }

    [Fact]
    public void Settings_MoveDown_ReordersTheSidebar()
    {
        // Settings › Calendars → CalendarMore_leaf.tester@gmail.com → Move down → in the main window, Family is listed above the primary calendar
    }

    [Fact]
    public void Settings_DefaultReminders_PatchesGoogle()
    {
        // CalendarMore_family... → Default reminders… → AddReminderButton → pick "30 minutes before" → Save →
        // PATCH body {"defaultReminders":[{"method":"popup","minutes":30}]}
    }
```

- [ ] **Step 7: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "feat: calendar rename, default reminders, order, and upcoming events for one calendar"
```

---

### Task 13: Settings Pages (General and Accounts)

**Files:**
- Modify: `src/LeafCalendar.App/Views/Settings/GeneralPage.xaml(.cs)`, `AccountsPage.xaml(.cs)`, `src/LeafCalendar.App/ViewModels/AccountsViewModel.cs`, `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`
- Create: `tests/LeafCalendar.UITests/SettingsPagesTests.cs`

**Interfaces:**
- Consumes: every Task 1 setting, and `LeafTimePicker`.
- Produces:
  - The upcoming list reads `Settings.UpcomingHours`. `CalendarViewModel.UpcomingWindow` is removed; its use becomes `TimeSpan.FromHours(Settings.UpcomingHours)`.
  - `CalendarGroups()` lists the main account first.
  - Automation IDs:
    - `InterfaceScaleBox`, `AllDayExpandedSwitch`, `UpcomingHoursBox`, `MapProviderBox`
    - `WorkingHoursSwitch`, `WorkingStartPicker`, `WorkingEndPicker`, `WorkingHoursError`, `WorkingDay_<DayOfWeek>`
    - `MainAccountBox`, `MeetByDefault_<accountId>`

- [ ] **Step 1: Write the UI tests**

`SettingsPagesTests.cs`:

```csharp
    [Fact]
    public void General_EveryNewSetting_SavesAndSurvivesARelaunch()
    {
        // Settings › General: InterfaceScaleBox "125%", AllDayExpandedSwitch on, UpcomingHoursBox "Next 4 hours",
        // MapProviderBox "Bing Maps", WorkingStartPicker 8:00, WorkingEndPicker 16:00, WorkingDay_Friday off;
        // close and relaunch → every control shows the same values
    }

    [Fact]
    public void WorkingHours_EndBeforeStart_IsRefused()
    {
        // WorkingEndPicker 7:00 with start 9:00 → WorkingHoursError reads "Pick an end time after the start time."; after a relaunch the end is still 17:00
    }

    [Fact]
    public void WorkingHours_SwitchOff_DisablesTheRows()
    {
        // WorkingHoursSwitch off → WorkingStartPicker, WorkingEndPicker, and WorkingDay_* are disabled; the grid has no OffHours_* (Task 9)
    }

    [Fact]
    public void UpcomingHours_TwoHours_ShortensTheList()
    {
        // launch with --now 2026-10-01T08:00:00-04:00; nothing selected: the upcoming list has the dentist (9:00) and the design
        // review (14:00); UpcomingHoursBox "Next 2 hours" → only the dentist
    }

    [Fact]
    public void MainAccount_IsListedFirst()
    {
        // add a second account the way AccountFlowTests does (SignInAsOtherUser); Settings › Accounts › MainAccountBox →
        // other.person@example.com → the sidebar's first account header is other.person@example.com
    }

    [Fact]
    public void MeetByDefault_PerAccountSwitch()
    {
        // Settings › Accounts: MeetByDefault_109876543210 on → main window C, title, Ctrl+Enter → the POST body has conferenceData.createRequest
    }
```

- [ ] **Step 2: Build the pages**

`GeneralPage.xaml`, keeping every existing row as it is:
- **"Appearance" group**, after App theme:
  - "Interface scale" (`Glyph="&#xE740;"`), described "Make the sidebar, calendar, and details panel bigger or smaller." `ComboBox` `InterfaceScaleBox` with "80%" through "150%" from `LeafSettings.ScaleChoices`.
  - "Expand the all-day section" (`Glyph="&#xE8BF;"`), described "Show every all-day event when Leaf opens, not just the first few." `ToggleSwitch` `AllDayExpandedSwitch`.
- **"Calendar view" group**, after the existing rows: "Upcoming meetings" (`Glyph="&#xE823;"`), described "How far ahead the details panel looks when no event is selected." `ComboBox` `UpcomingHoursBox` with "Next 2 hours" through "Next 24 hours" from `UpcomingChoices`.
- **New group "Working hours"**, after "Date and time":
  - "Shade outside working hours" (`Glyph="&#xE823;"`), described "Google doesn't share your working hours with apps, so set them here." `ToggleSwitch` `WorkingHoursSwitch`.
  - Then a `ContentControl` (disabled as one when the switch is off, per the design standard's States rule) holding:
    - "Start" (`WorkingStartPicker`, `LeafTimePicker` with 15-minute increments)
    - "End" (`WorkingEndPicker`), with a Caption line `WorkingHoursError` ("Pick an end time after the start time.") in `SystemFillColorCautionBrush`, collapsed unless an end isn't after its start
    - "Work days": seven 40 DIP `ToggleButton`s (`WorkingDay_<DayOfWeek>`), labeled with the 3-letter day name and ordered from `Settings.WeekStart`
- **New group "Locations"**: "Open locations in" (`Glyph="&#xE707;"`), `ComboBox` `MapProviderBox` with "Google Maps" and "Bing Maps".

Every control saves through `_context.Save(s => s with { ... })` on change, guarded by the page's existing `_loading` pattern. A picker change whose end isn't after the start shows `WorkingHoursError`, puts the picker back, and saves nothing.

`AccountsPage.xaml` and `AccountsViewModel.cs`:
- **"Google accounts" group**, after the account list: "Main account" (`Glyph="&#xE77B;"`), described "Listed first, and used to look up people's free/busy times." `ComboBox` `MainAccountBox` of account emails, App rows. It's disabled with one account; with none set, the first account is shown selected.
- **New group "Video calls"**: one `SettingRow` per account. The header is the email, the description is "Add a Google Meet link to new events.", and the control is a `ToggleSwitch` `MeetByDefault_<accountId>`. The rows are App records from `AccountsViewModel`, as a plain `List`. A toggle saves `MeetByDefaultAccounts` with the ID added or removed.

`CalendarViewModel.cs`:
- Replace `UpcomingWindow` with `TimeSpan.FromHours(Settings.UpcomingHours)`, and rebuild the upcoming list when it changes.
- `CalendarGroups()` sorts the group whose `AccountId == Settings.MainAccountId` first, keeping the rest in order.

- [ ] **Step 3: Build and run the unit tests**

Run these:
- `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings
- `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
- `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → PASS

- [ ] **Step 4: Commit**

```bash
git add -A src tests
git commit -m "feat(app): the rest of Section 9 in Settings (scale, all-day, upcoming, working hours, maps, main account, Meet by default)"
```

Track C is done. Merge it at M1.

---

### Task 14: Milestone Close (Batch UI Tests, AOT, Memory, Screenshots, Security, Spec, Reinstall, Merge)

Serial, on `m5-power-features` after all three tracks are merged. File ownership is lifted. Fix anything anywhere, each fix with a test.

**Files:**
- Modify: `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`, `docs/design-standard.md`
- Modify: `tests/LeafCalendar.UITests/memory-budget.json` (only with the owner's go-ahead)
- Modify: whatever the verification finds

- [ ] **Step 1: Confirm the merges**

`git log --oneline --merges -5` shows the three track merges. `dotnet build LeafCalendar.slnx -c Debug` gives 0 warnings, and `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` passes.

- [ ] **Step 2: Batch UI run, Debug**

Kill any `LeafCalendar.exe --profile uitest-*` processes left over. They hold `leaf.db`.

```bash
pwsh tools/dev-register.ps1
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj
```

Expected: all PASS, 1 skipped (memory). This is the first run of every Milestone 5 UI test.

For each failure:
1. Re-run its class alone with `--output Detailed`.
2. Find the root cause (`superpowers:systematic-debugging`).
3. Fix it, and add a unit test when the cause is in Core.
4. Re-run that class.

Then run the whole suite once more. The desktop must stay unlocked. `Access is denied` means it's locked: stop and report.

- [ ] **Step 3: Batch UI run, AOT**

```bash
pwsh tools/publish-aot.ps1 -Register
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj
```

Expected: **0 IL warnings**, and all PASS. AOT-only failures are almost always the read-back or list rules. Look first at:
- `CommandRow`, `ContactSuggestion`, and `RoomSuggestion` lists
- `ToggleMenuFlyoutItem` reads in the share bar
- `NavigationViewItem` comparisons
- the `MiniMonthNext` lookup

- [ ] **Step 4: Memory, three times (AOT still registered)**

Run `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*MemoryTests" --output Detailed` three times. The budget is private bytes ≤ 120 MB and working set ≤ 25 MB.
- **Within it:** record the numbers for Step 8.
- **Over it:** stop and report to the owner with the numbers and what grew. Likely suspects are the lazily built command-menu flyout, the cheat sheet, overlay and slot pools, and the ScaleHost transforms. The budget is the owner's call.

- [ ] **Step 5: Live tests, and checks by hand**

1. Run `dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --output Detailed` from the repo root. Expected: PASS, or skipped without the live account.
2. Check these by hand on the AOT build with your real profile:
   - **Zone switch:** in Settings › Time zones, pin "New York". Change the Windows time zone to Pacific in Windows Settings. Within a minute, `ZoneSwitchBar` offers Pacific. Switch makes the grid follow; Keep closes the bar. Put Windows back.
   - **Booking pages:** "Manage Google booking pages" opens Google's appointment schedules for the right account. If Google moved the page, fix `LinkSafety.BookingPages` and its test.
   - **Workspace:** on a Workspace account, the directory suggests colleagues; a teammate's overlay shows their busy times; a shared colleague shows titles; Focus time and Out of office create correctly; a room you booked before is suggested.
   - **Scale:** at 125% and 150%, text is sharp, not blurry, in the sidebar, the grid, and the details panel. If it's blurry, report it with screenshots. Don't swap the approach without the owner.
   - **Alt+Enter** in the command menu jumps on the AOT build.

- [ ] **Step 6: Screenshots, light and dark**

Launch a seeded fake-Google profile, as the UI tests do. Capture each screen in **light and dark** (Ctrl+Shift+L) to the session scratchpad under `screens\m5\<name>-<light|dark>.png`:
- `command-menu-empty`, `command-menu-results`
- `cheat-sheet`
- `time-travel-bar`
- `overlay-bar-blocks`, `meet-with`
- `share-bar-slots`
- `working-hours`
- `editor-extras` (type, show as, visibility, zone, local time line), `editor-rooms`
- `rename-dialog`, `calendar-menu`, `upcoming-one-calendar`
- `settings-general`, `settings-accounts`, `settings-calendars-more`, `settings-time-zones`, `settings-tray`, `settings-shortcuts`, `settings-about`
- `scale-150-minimum-window`

Check each against `docs/design-standard.md`:
- spacing 2/4/8/12/16/24/32 and radii 4/8
- theme brushes, and one accent per surface
- 83 ms hover fades
- sentence case and verb buttons
- nothing clipped at 1086 × 540, or at the scaled minimum

Fix what's off, then list the files in the report.

- [ ] **Step 7: Security review**

Invoke the `security-review` skill on `git diff main..HEAD`. Then check each item and record the result in the report:
1. **Logs carry no content:** `git grep -n "Log\.\(Info\|Error\)" -- src` in the new code (search, free/busy, share, rename, contacts, time travel, domain refresh). Details carry only `account=`, `count=`, `unknown=`, `slots=`, `calendars=`, `status=`, and `ok`/`failed`/`on`/`off`. The UI tests `Searching_WritesNoSearchTextToTheLog`, `Overlay_WritesNoAddressesToTheLog`, `Copy_WritesNoAvailabilityToTheLog`, and `Suggestions_WriteNoNamesToTheLog` pass.
2. **SQL:** `EventSearch` passes the query only as a parameter, with `%`, `_`, and `\` escaped (`Find_PercentAndUnderscore_MatchLiterally`). `git grep -n "\$\"SELECT\|\$\"UPDATE\|\$\"INSERT" -- src` finds nothing new.
3. **One launch path:** `git grep -n "Launcher\." -- src/LeafCalendar.App` finds only `LeafServices.LaunchAsync`, `OpenSignInPageAsync`, and `OpenFolderAsync`, which takes only the log folder. Bing, Google Maps, booking pages, and GitHub are fixed addresses built in `LinkSafety`.
4. **Untrusted text is plain:** teammate titles (`FreeBusyLookup`), room and frequent-people names, directory results, and calendar names are cleaned (tests in Tasks 6, 10, and 12). No `NavigateUri` comes from any of them (`git grep -n "NavigateUri" -- src`).
5. **Nothing stored:** `git grep -n "Contact\|Room\|Frequent\|BusyBlock" -- src/LeafCalendar.Core/Data src/LeafCalendar.Core/Settings` shows no storage of people data. `hosted_domain` is the only new column.
6. **Fake routes stay test-only:** the new endpoints live only in `FakeGoogleServer` (spec 4.8 still holds).

- [ ] **Step 8: Record rulings and results in the spec and the design standard**

In `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`:
- **Section 2 (Decision Log):** add these rows:
  - Working hours: Leaf's own setting, since Google's API doesn't expose them (Milestone 5)
  - Interface scale: sidebar, calendar, and details panel; the title bar stays 48 DIPs; Ctrl+= / Ctrl+- stay grid density
  - Rooms: rooms you've booked before
  - Workspace detection: the `hd` claim
  - Command menu: a stock `Flyout` at the top center
  - Event type: chosen only when creating
- **Section 3.4 item 7:** append "Measured 2026-MM-DD at the close of Milestone 5 (command menu, people overlay, share availability, settings pages, interface scale) at A–B MB private bytes and C MB working set (three runs)." Fill in Step 4's numbers.
- **Section 6.2 item 5:** replace "built with the command menu in Milestone 5" with "built in Milestone 5: centered over the mini month's Next month button, or after the app title when the sidebar is closed". **Item 2:** append "The stock title bar back button (Milestone 5)."
- **Section 6.3:** append "Milestone 5: right-click a calendar for Rename… (Google summary override, needs a connection), Show upcoming events (next 30 days, in the details panel), and Change color… (opens Settings › Calendars). A share-availability icon sits beside the Settings icon."
- **Section 6.4:** replace "(not exposed by the Calendar API; see Milestone 5)" with "(Google's API doesn't expose them, so Leaf keeps its own, 9 AM–5 PM Monday–Friday by default, in Settings › General)". After "whole-app interface scale", add "(80–150%: the sidebar, calendar, and details panel; the title bar stays 48 DIPs)".
- **Section 6.6:** append "A typed date (\"oct 12\", \"next fri\", \"in 2 weeks\") offers to go there. A repeating event's result is its next instance."
- **Section 6.7:** append "Milestone 5 completed the Section 9 options; Appearance options live in General's Appearance group."
- **Section 7.2:**
  - On the autocomplete bullet: "Milestone 5 added the Workspace directory, people you meet often (from your events in the last 180 days), and rooms you've booked before (Workspace accounts)."
  - Append to the Event type bullet: "Chosen only when creating (Google can't change it later), on a Workspace account's primary calendar."
  - Append to the conferencing bullet: "Per-account default in Settings › Accounts (Milestone 5)."
- **Section 7.6:** append "Needs a connection (Google free/busy). Zone choices: the zone on screen, Windows' zone, and the extra zone columns."
- **Section 8.7:** append "The cheat sheet leaves out Shift+drag until box select is built (Milestone 6)."
- **Section 9 General:** add "Working hours (start, end, days)" and "Interface scale and all-day default (Appearance group)". **Time Zones:** "Primary zone" becomes "Primary zone (Windows' by default), with the prompt to switch when Windows' changes".
- **Section 12 item 5:** mark it done, "(built in Milestone 5)". Move anything this milestone didn't finish into item 6's list, each with its reason. Expected: nothing.

In `docs/design-standard.md`:
- **Section 7**, a new "Command Menu" subsection: a stock `Flyout` at the window's top center, 560 wide, `TextBox` then `ListView`, rows 40 tall with a 16 glyph or a color square, the shortcut in Caption on the right, Up/Down move while focus stays in the box, Enter runs, Alt+Enter jumps.
- **Section 10**, add: "Mode bars (time travel, people overlay, share availability) are cards or informational `InfoBar`s stacked at the top of the calendar island (`IslandBars`), 16 DIPs from its sides."
- **Section 5**, add the overlay person colors and the off-hours tint to the surface table, with their `LeafBrushes` names.
- **Section 15, "Already follows it":** add "Command menu, cheat sheet, island bars, interface scale via `Controls/ScaleHost.cs`."

- [ ] **Step 9: Reinstall AOT and leave it installed**

`pwsh tools/publish-aot.ps1 -Register`. Tell the owner that switching builds empties the default profile's account list, so they'll need to add their account again.

- [ ] **Step 10: Commit, merge, and push**

```bash
git add -A docs tests src
git commit -m "chore: close Milestone 5 with batch UI runs, AOT, memory, security review, and recorded rulings"
git checkout main
git merge --no-ff m5-power-features -m "Merge Milestone 5: power features"
git push origin main m5-power-features
git worktree remove ../leaf-m5-a
git worktree remove ../leaf-m5-b
git worktree remove ../leaf-m5-c
```

The owner authorized merge and push at each milestone's end. Don't open, comment on, or change a PR.

---

## Self-Review Notes

**Coverage (spec Section 12 item 5 and its deferrals → task):**

| Item | Task |
|---|---|
| Command menu (search, every action, Alt+Enter, Back) | 2, 3 |
| Title-bar search icon over the sidebar | 3 |
| People overlay (P) | 6, 7 |
| Meet with (F) | 6, 7 |
| E then F | 1 (keys), 7 |
| Share availability (S, sidebar button, text, zone, booking pages) | 1 (button), 6, 8 |
| In-app shortcut set (Z S P F ? / Ctrl+K Ctrl+F Ctrl+, E-Z E-F) | 1 |
| Cheat sheet (?) and the Settings › Shortcuts link | 4 |
| Full settings page (Section 9) | 4 (Tray, Shortcuts, About), 5 (Time zones), 12 (Calendars), 13 (General, Accounts) |
| Interface scale | 1 (host), 5, 13 (control) |
| Working-hours shading | 9, 13 (controls) |
| Calendar rename | 12 |
| Show upcoming events for a calendar | 12 |
| Time travel (Z) | 5 |
| Workspace directory, frequent contacts, rooms | 10 |
| Per-account default conferencing | 11, 13 |
| Event type (Focus time, Out of office) | 11 |
| Editing Busy/Free and Public/Private | 11 |
| The event's own time zone, and E then Z | 11 |
| Map provider (Google, Bing) | 11, 13 |
| Tray "which calendars appear" (M4 deferral) | 4 |
| Primary zone and the zone-change prompt (Section 9) | 5 |
| Primary account (Section 9) | 1 (setting), 13 |
| Default reminders per calendar, written to Google (Section 9) | 12 |
| Calendar order and display name in Settings (Section 9) | 12 |
| All-day default (Section 9) | 9, 13 |
| Upcoming lookahead (Section 9) | 13 |
| Open logs folder, third-party licenses (Section 9) | 4 |

**Type consistency checked:**
- `BusyRange` and `FreeBusyResult` are defined in Task 1 (`LeafCalendar.Core.Google`) and used in Tasks 6 and 8.
- `CommandItem.Command` and `ShortcutRow.Command` use Task 1's `CalendarCommand` values.
- `OpenCommandMenu`, `ShowShortcutSheet`, `StartTimeTravel`, `ShowPeopleOverlay`, `ShowMeetWith`, `ShowParticipantOverlay`, `StartShareAvailability`, and `EditTimeZone` are stubbed in Task 1 and filled by their owners.
- `Guest.IsResource` (Task 10) is read by Task 11's editor.
- `CalendarInfo.GoogleName` (Task 12) is the last positional parameter with a default, so existing constructions compile.
- `Account.HostedDomain` (Task 10) likewise.
- `DisplayZone.IanaOf` is Track A's (Task 5). Track B's zone labels use `TimeZoneCatalog` and `AvailabilityText.ZoneLabel`, so B never needs A's file.
- `LeafSettings` fields are named identically in every task (`InterfaceScale`, `WorkingHours`, `AllDayExpanded`, `MapProvider`, `UpcomingHours`, `PrimaryTimeZone`, `PromptOnZoneChange`, `MainAccountId`, `MeetByDefaultAccounts`, `TrayExcludedCalendars`).

**Cross-track runtime links:** these compile on their own and are exercised only by the batch UI run after the merges.
- Task 7's picker shows directory people after Task 10.
- Task 5's `ScaleFromTheCommandMenu_Applies` reads Task 13's `InterfaceScaleBox`.
- Task 9's `CustomHours_FromSettings` uses Task 13's pickers.
- Task 13's `WorkingHours_SwitchOff_DisablesTheRows` reads Task 9's `OffHours_*`.

**Review Focus pinned:** each line names a test in the task that owns the code (Tasks 2, 5, 6, 7, 8, and 12).
