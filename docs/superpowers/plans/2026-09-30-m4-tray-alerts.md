# Leaf Calendar Milestone 4 (Tray and Alerts) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Leaf live in the tray and tell you about meetings. It gets:
- a tray icon with a "next event" tooltip, and Leaf keeps running in the tray when the main window closes (only Quit ends it), starting with Windows
- a left-click acrylic flyout (next event, big Join button, agenda by day, "New event") that slides in from the taskbar edge
- a right-click XAML menu (Open Leaf Calendar, New event, Join next meeting, Sync now, Settings, Quit) placed by the taskbar edge
- real Windows notifications: reminders at Google's reminder times, a persistent "Join now" at start (reminder scenario), new and updated invites with Yes / No / Maybe, "1 change needs your review" (deferred from Milestone 3), and "Sign in again"
- global shortcuts (Ctrl+Alt+J joins the right meeting with the right Google account, Ctrl+Alt+K shows or hides the flyout), changeable in Settings with a "taken by another app" warning
- Settings › Notifications, Settings › Tray, Settings › Shortcuts, and "Start with Windows" in Settings › General

**Architecture:** `LeafCalendar.Core` does all the deciding, with no UI and full unit tests:
- which alerts exist (`AlertPlanner`: Google's calendar defaults or the event's overrides, all-day, repeating events across DST, meeting links)
- when they fire (`AlertScheduler` on `TimeProvider`: a 15-second heartbeat, a one-hour look-back on every pass, "skip what's over" after sleep, one sync a minute before each alert, withdrawal of stale "Join now" toasts)
- what never repeats (`AlertLedger`, a SQLite table, so restarts and full resyncs don't re-notify)
- which meeting the join shortcut opens (`JoinPicker`), what the flyout and tooltip say (`TrayAgenda`), the toast XML and its arguments (`ToastContent`, `ToastArgs`), which invites are new (`InviteWatcher`), and where the flyout and menu go for each taskbar edge (`TrayPlacement`)

`LeafCalendar.App` adds thin Win32 and WinUI shells on top: a hidden message window that owns the `Shell_NotifyIcon` icon and receives `WM_HOTKEY`, one invisible host window (Layers' pattern) that opens the stock `MenuFlyout` and a stock `Flyout` holding the acrylic agenda panel, a `Notifier` over Windows App SDK app notifications (a text file in fake-Google mode), and `AlertCenter`, which connects the scheduler and the sync engine to the notifier. The App owns one `CalendarViewModel`, shared by the main window and Settings, and releases it when neither is open, so tray-only Leaf stays small. Toast clicks reach Leaf through `AppNotificationManager.NotificationInvoked` while it runs, or through the Milestone 3 single-instance redirect (and a cold start) when it doesn't.

**Tech Stack:** .NET 10 / C# 14, Windows App SDK 2.5.1 (WinUI 3, packaged MSIX, Release Native AOT, `Microsoft.Windows.AppNotifications`, `Microsoft.Windows.AppLifecycle`), CsWin32 0.3.335 (`allowMarshaling: false`), CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.12, `System.Xml.Linq` (toast XML), xUnit v3 on Microsoft.Testing.Platform, Microsoft.Extensions.TimeProvider.Testing 10.10.0, FlaUI.UIA3 5.0.0. No new packages.

**Spec:** `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`. Read Sections 1.4, 2, 3.2, 3.4, 4.5, 4.6, 5.3, 5.5, 6.1, 6.7, 8.1–8.6, 9 (General, Notifications, Tray, Shortcuts), 10, and 12. The design standard `docs/design-standard.md` is binding for every screen (Sections 2, 5, 6, 7 Menus, 8, 11, 12, 13). The Milestone 3 plan (`docs/superpowers/plans/2026-09-30-m3-events.md`) and its ledger (`.superpowers/sdd/2026-09-30-m3-events/progress.md`) show the established patterns and the Native AOT traps.

## Scope Rulings

These rulings were made for this milestone. Task 17 records them in the spec and the design standard.

- **Flyout construction:** the flyout follows Layers' HUD pattern (`D:\layers\src\Layers.UI\HudHost.xaml(.cs)`): an invisible, layered, topmost host window opens a stock `Flyout` whose content is the panel. The panel draws desktop acrylic that stays active (`ActiveAcrylicBackdrop` from Sony, inside a `SystemBackdropElement` with `CornerRadius="8"`), has the flyout border and shadow, and slides in from the taskbar edge with the Fluent timings (250 ms decelerate in, 167 ms accelerate out), clipped at the work-area edge. This replaces Sony's transparent-window technique, which needs DWM, subclassing, and `DllImport` marshaling that Leaf's `allowMarshaling: false` rule forbids. Light dismiss, Esc, focus, and screen reader support come from the stock control. The right-click menu uses the same host window (Layers' `TrayMenuHost`).
- **Placement:** the flyout opens next to the icon (`Shell_NotifyIconGetRect`), 12 DIPs from the taskbar and the screen edges, inside the icon monitor's work area. The taskbar edge comes from `SHAppBarMessage(ABM_GETTASKBARPOS)` when that taskbar is on the icon's monitor, else from comparing the monitor to its work area. An auto-hidden taskbar is kept clear. When the icon rectangle is unknown (the icon sits in the overflow), the flyout opens at the far end of the primary taskbar, like Quick Settings.
- **Tray art:** a placeholder: the app's `Square44x44Logo.png` turned into an icon at the taskbar's size. The monochrome light and dark tray glyphs and a fixed `NIF_GUID` identity (which only survives updates on a signed package) move to Milestone 6.
- **Reminders:**
  - Only `popup` reminders count; Google sends `email` reminders itself.
  - `reminders.useDefault` (or no `reminders` field) means the calendar's default popups, for timed events.
  - All-day events remind only from their own overrides, counted back from local midnight of the first day. Google's API doesn't expose the all-day defaults, so an all-day event on defaults gets no reminder.
  - Minutes outside Google's 0–40,320 range are ignored.
  - Only calendars shown in Leaf remind you, and declined events never do.
- **"Join now":** fires at start for timed events with a meeting link you haven't declined. It has no Snooze. It stays on screen through the meeting, and Leaf withdraws it once the meeting ends, moves, is declined, or is deleted.
- **Snooze** on regular reminders is Windows' own system snooze (5, 10, 15, or 30 minutes).
- **After sleep or a late start:** each alert pass looks back one hour. Each meeting shows at most one alert, the latest one that came due, and nothing shows for meetings that are already over.
- **Join shortcut:** the soonest-starting qualifying meeting that hasn't started (within 10 minutes), else the in-progress one that started most recently. All-day events never qualify. Meet links get `authuser=<the event's account email>`.
- **Invites:** when an account finishes its first sync, its existing pending invites are recorded without notifying. After that, a new invite, or a change the organizer makes (Google's `sequence` goes up), notifies once. Another guest's reply doesn't. Yes / No / Maybe from the notification email the organizer (like Google's own buttons). A repeating invite replies for the whole series.
- **Conflict notification:** "1 change needs your review" (or "N changes need your review"), one at a time (a newer one replaces it), withdrawn once no conflicts remain. Clicking it opens the conflict dialog.
- **Sign in again:** shown once when an account's sign-in stops working. Clicking it opens Settings › Accounts.
- **Settings in this milestone:** Settings › Notifications (the four Section 9 switches), Settings › Tray (days in the flyout agenda, include all-day events, next-event lookahead), Settings › Shortcuts (both global shortcuts, with warnings), and "Start with Windows" in Settings › General. The tray's "Which calendars appear" follows the calendars shown in Leaf until the full settings page (Milestone 5). The Shortcuts page's link to the in-app cheat sheet arrives with the cheat sheet (Milestone 5).
- **Lifecycle:**
  - Leaf starts with Windows (MSIX `StartupTask`, on by default) and then stays in the tray.
  - Closing the main window keeps Leaf in the tray. Settings no longer closes with the main window.
  - The App owns the calendar view model and releases it (and trims memory, and turns on efficiency mode) when no window is open.
- **Profiles and toasts:** toast arguments carry the profile. A toast that starts Leaf opens that profile, and a running Leaf ignores another profile's toast. Fake-Google (`uitest-`) profiles never register with Windows. They write each notification to `notifications.txt` in the profile folder, and accept `--toast-action <arguments>` through the single-instance redirect, so UI tests can drive toast clicks.
- **Test clock:** `--now <ISO 8601 instant with offset>` (fake Google only) makes the services clock start at that instant and run on from there, so reminders, the flyout, and the join shortcut can be tested against the fixtures' meetings.
- **Copy:** the menu items and settings labels follow spec 8.3 and Section 9 in sentence case. Notification wording is new copy for the owner to review at milestone close.

## Global Constraints

Every Milestone 3 constraint still holds:

- `net10.0-windows10.0.22621.0`, min `10.0.22000.0`; Windows App SDK 2.5.1, packaged MSIX.
- Release-only Native AOT with **0 IL warnings**. `TreatWarningsAsErrors` with `AnalysisLevel` `latest-recommended` and NuGet audit on.
- No Google client libraries, no Newtonsoft, no Entity Framework, no WebView2, no Ical.Net/EWSoftware.PDI. `Meziantou.Framework.Scheduling` pinned `[4.1.3]`.
- JSON: `System.Text.Json` source generation only (`GoogleJsonContext`, `LeafJsonContext`); `JsonDocument` / `JsonNode` for reading and building. Never call a serializer overload without `JsonTypeInfo`.
- XAML: `x:Bind` only (and x:Bind function or method bindings). **No `{Binding}`, no `DisplayMemberPath`.** WinUI classes that implement WinRT interfaces must be `partial`.
- **Native AOT read-back rule:** never read an object back through a typed cast of a WinRT property (`(Style)Application.Current.Resources[...]`, `(TextBlock)button.Content`, `x.Tag is CalendarOccurrence`), and no `is` / `as` on visual-tree objects. Keep your own references in fields, records, or closures.
- **Native AOT list rule:** never hand a list of Core types to a WinRT `ItemsSource`. Wrap each item in an App-project record first (as `UpcomingItem` does).
- **AOT UI check:** a task that touches UI isn't done until its UI tests pass against the AOT build: `pwsh tools/publish-aot.ps1 -Register`, run the UI tests, then `pwsh tools/dev-register.ps1` to put Debug back.
- **Secrets:** OAuth secrets and refresh tokens only in Credential Locker; access tokens only in memory.
- **Logs:** internal IDs only; `AppLog.Redact` is the safety net. Never log event titles, descriptions, locations, guest emails, calendar names, toast XML, or toast arguments. Alert logs carry the alert kind and its tag (a hash) only.
- **Untrusted event content:** plain text only. Every launch goes through `LeafServices.LaunchAsync` (which re-checks `LinkSafety`). Toast XML is built only by `ToastContent` with `System.Xml.Linq`, so titles, locations, and organizer addresses are always escaped text.
- **Fake Google:** only on `uitest-*` profiles, loopback only (spec 4.8). In fake mode links go to `launched.txt` and notifications to `notifications.txt` in the profile folder. `--now` and `--toast-action` are honored only in fake mode.
- **US English** everywhere, sentence case in the UI, English-only formatting (`CultureInfo.GetCultureInfo("en-US")` / invariant).
- **Tests:** Microsoft.Testing.Platform: `dotnet test --project <csproj> [--filter-class "*Name"] [--filter-method "*Name*"] [--output Detailed]`. Async calls pass `TestContext.Current.CancellationToken`. Test classes `public`; a class with a disposable field implements `IDisposable`. UI tests need `pwsh tools/dev-register.ps1` first and an unlocked desktop.
- **Code style (owner):** 4-space indent, a Title Case block comment above each logical block, closing comments on long XAML blocks (`<!-- /Agenda -->`), XML docs on public types and members, guard clauses first, aligned `=` in related assignment groups, `// ====` section banners in long classes.

Added for Milestone 4:

- **Win32:** CsWin32 with `"allowMarshaling": false` for every new Win32 call (tray icon via `Shell_NotifyIcon`, taskbar edge via `SHAppBarMessage`, global shortcuts via `RegisterHotKey`, efficiency mode via `SetProcessInformation`). Add names to `src/LeafCalendar.App/NativeMethods.txt`; no `DllImport` in the app (UI tests may use it). Message numbers and flag values that aren't functions (`WM_HOTKEY`, `NIN_SELECT`, `ABM_GETTASKBARPOS`, ...) are declared as local `const`s, so a missing metadata name can't break the build.
- **Window procedures:** `[UnmanagedCallersOnly]` static procedures; no exception may escape one (catch, log, and return `DefWindowProc`).
- **Notifications:** Windows App SDK `AppNotificationManager` with the packaged COM activator declared in `Package.appxmanifest`. The persistent join toast uses `scenario="reminder"` with a background-activated Join button. Activation arguments from WinRT objects are read with `WinRT.CastExtensions.As<T>()`, never a C# cast.
- **No "pause notifications" feature** (spec 1.4). Windows Do Not Disturb and Focus apply on their own.
- **Smart polling:** 15 s while the main window or the flyout is visible, 60 s in the tray; a sync right away when the flyout opens and one minute before each alert (spec 5.3).
- **Memory budget:** tray-only Release AOT, private bytes ≤ **120 MB**, working set ≤ **25 MB** (`tests/LeafCalendar.UITests/memory-budget.json`). Re-measure three times at milestone close; if it's over, stop and report to the owner (the budget is the owner's call).
- **Branch and commits:** work on a new branch `m4-tray-alerts` created from the current `m3-events` head (Milestone 3's close). A commit at the end of each task is authorized. At milestone end, merge `m4-tray-alerts` into `main` and push (the owner authorized merge and push per milestone). Never comment on or change a PR.

## Review Focus

1. **The laptop sleeps through a meeting, or Leaf starts late.** On wake (or start) a person expects nothing for meetings that are already over, and for a meeting in progress exactly one "Join now", not a burst of three stale reminders. Tests: `AlertSchedulerTests.Check_WokeAfterMeetingEnded_SkipsIt` and `Check_WokeDuringMeeting_ShowsOnlyJoinNow` (Task 4).
2. **Leaf restarts, or a `410` forces a full resync, after a reminder or invite already showed.** The same notification must not appear again. Tests: `AlertSchedulerTests.Check_NewSchedulerAfterRestart_DoesNotRepeat` (Task 4) and `InviteWatcherTests.TakeNew_GuestReplyOnly_DoesNotNotifyAgain` (Task 8).
3. **The meeting moves, is canceled, or is declined after the persistent "Join now" is on screen.** A reminder-scenario toast never leaves by itself, so a stale "Join now" for a meeting that isn't happening must be withdrawn. Test: `AlertSchedulerTests.Check_MeetingMovedAfterJoinNow_RetractsItsToast` (Task 4).
4. **A hostile invite title** (markup such as `</text><action .../>`, ampersands, quotes, line breaks, or a 5,000-character title). The notification must show it as plain text, add no buttons, and still be valid XML. Test: `ToastContentTests.Reminder_HostileTitle_StaysPlainText` (Task 7).
5. **The taskbar isn't a plain bottom bar:** it's auto-hidden, or on the left of a monitor that sits left of the primary one (negative coordinates). The flyout must stay fully inside the usable area and clear of the taskbar. Tests: `TrayPlacementTests.Flyout_AutoHiddenBottomTaskbar_StaysAboveIt` and `Flyout_LeftTaskbarOnMonitorLeftOfPrimary_StaysInside` (Task 9).

---

## File Structure

```
src/LeafCalendar.Core/
    Settings/LeafSettings.cs            + notification switches, flyout days, all-day, lookahead, two shortcuts
    Tray/Hotkey.cs                      NEW  global shortcut parse/format/validate (MOD_* values)
    Tray/DisplayText.cs                 NEW  control-character cleanup and surrogate-safe clipping
    Tray/TrayAgenda.cs                  NEW  flyout agenda by day, next event, countdown, tooltip, day names
    Tray/TrayPlacement.cs               NEW  PixelRect, TaskbarEdge, flyout/menu placement per taskbar edge
    Data/Schema.cs, Data/LeafDatabase.cs + V4: alert_ledger
    Data/AlertLedger.cs                 NEW  delivered alerts, withdrawals, per-account marks
    Data/CalendarStore.cs               + PopupDefaults
    Events/CalendarOccurrence.cs        + StartIn / EndIn (all-day in the local zone)
    Events/OccurrenceLookup.cs          NEW  find an instance from toast arguments; next instance of an invite
    Alerts/AlertKind.cs                 NEW
    Alerts/AlertPlanner.cs              NEW  Alert record; reminders and Join now from Google's data
    Alerts/AlertScheduler.cs            NEW  TimeProvider heartbeat, due/skip/dedupe/withdraw, sync-soon
    Alerts/JoinPicker.cs                NEW  10-minute rule, meeting links, authuser
    Alerts/ToastArgs.cs                 NEW  toast action arguments (encode/parse)
    Alerts/ToastContent.cs              NEW  toast XML for every notification
    Alerts/InviteWatcher.cs             NEW  new and updated invites
    Hosting/LaunchOptions.cs            + --now, --toast-action (fake Google only)
    Hosting/ShiftedTimeProvider.cs      NEW  the --now clock
    Sync/SyncEngine.cs                  + ConflictsFound, SignInNeeded

src/LeafCalendar.App/
    NativeMethods.txt                   + tray, monitor, app bar, hotkey, efficiency-mode, layered-window names
    Package.appxmanifest                + StartupTask, toast COM activator
    Program.cs                          + start kind, options, activations with arguments, toast profile
    App.xaml.cs                         tray lifecycle, shared calendar view model, tray/toast/shortcut actions
    LeafServices.cs                     + --now clock, Shortcuts
    MainWindow.xaml.cs                  takes the shared view model; ReviewConflictsAsync; Settings outlives it
    ViewModels/CalendarViewModel.cs     + Reveal; SettingsSection + Notifications, Tray, Shortcuts
    Interop/TrayIcon.cs                 NEW  hidden message window, Shell_NotifyIcon v4, WM_HOTKEY
    Interop/EfficiencyMode.cs           NEW  EcoQoS on/off
    Interop/TrayScreen.cs               NEW  monitor, work area, scale, taskbar edge (SHAppBarMessage)
    Interop/InvisibleHost.cs            NEW  Layers' invisible topmost host + client origin
    Interop/GlobalShortcuts.cs          NEW  RegisterHotKey per action, taken-state, suspend/resume
    Tray/ActiveAcrylicBackdrop.cs       NEW  acrylic that stays active
    Tray/TrayHost.xaml(.cs)             NEW  tray menu + acrylic agenda flyout
    Tray/AgendaRows.cs                  NEW  AgendaModel, AgendaDayRow, AgendaRow (App records for the flyout's lists)
    Notifications/Notifier.cs           NEW  AppNotificationManager wrapper / notifications.txt in fake mode
    Notifications/AlertCenter.cs        NEW  scheduler + sync signals -> notifications
    Views/Settings/SettingsWindow.xaml.cs   + three pages
    Views/Settings/NotificationsPage.xaml(.cs), TrayPage.xaml(.cs), ShortcutsPage.xaml(.cs)   NEW
    Views/Settings/ShortcutDialog.cs    NEW  "press the keys" capture
    Views/Settings/GeneralPage.xaml(.cs)    + Start with Windows

tests/LeafCalendar.Tests/
    HotkeyTests.cs, AlertSettingsTests.cs, AlertLedgerTests.cs, AlertPlannerTests.cs, AlertSchedulerTests.cs,
    ShiftedTimeProviderTests.cs, JoinPickerTests.cs, TrayAgendaTests.cs, ToastContentTests.cs, InviteWatcherTests.cs,
    OccurrenceLookupTests.cs, TrayPlacementTests.cs                                   NEW
    LaunchOptionsTests.cs, LeafDatabaseTests.cs, SyncEngineTests.cs                 + cases
tests/LeafCalendar.UITests/
    Support/LeafApp.cs                  + tray window messages, popups, notifications.txt
    Support/SeededProfile.cs            + optional shortcuts
    Support/FakeGoogleServer.cs         + RejectRefresh
    TrayTests.cs, NotificationTests.cs, TrayMenuTests.cs, FlyoutTests.cs, ToastActionTests.cs,
    ShortcutTests.cs, TraySettingsTests.cs                                           NEW
    SettingsTests.cs, OnboardingTests.cs    closing the window no longer ends Leaf
tests/LeafCalendar.LiveTests/
    LiveAlertTests.cs                   NEW  real Google defaults and overrides -> planned reminders
    Support/LiveGoogle.cs               + SetDefaultRemindersAsync, InsertEventWithRemindersAsync
```

---

### Task 1: Alert, Tray, and Shortcut Settings

**Files:**
- Create: `src/LeafCalendar.Core/Tray/Hotkey.cs`
- Modify: `src/LeafCalendar.Core/Settings/LeafSettings.cs`
- Test: `tests/LeafCalendar.Tests/HotkeyTests.cs`, `tests/LeafCalendar.Tests/AlertSettingsTests.cs`

**Interfaces:**
- Consumes: `LeafSettings.Normalize()`, `SettingsStore.Load/Save`, `SqliteExtensions.Execute` (tests, via `InternalsVisibleTo`).
- Produces:
  - `namespace LeafCalendar.Core.Tray`: `[Flags] enum HotkeyModifiers { None = 0, Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8 }` (the Win32 `MOD_*` values)
  - `readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)` with `static bool TryCreate(HotkeyModifiers modifiers, int key, out Hotkey hotkey)`, `static bool TryParse(string? text, out Hotkey hotkey)`, `override string ToString()` ("Ctrl+Alt+Shift+Win+J" order)
  - `LeafSettings` additions: `bool ReminderNotifications = true`, `bool JoinNowNotifications = true`, `bool InviteNotifications = true`, `bool NotificationSound = true`, `int FlyoutDays = 3` (1–14), `bool FlyoutAllDay = true`, `int TrayLookaheadMinutes = 60` (15, 30, 60, 120, 240, 480), `string JoinShortcut = "Ctrl+Alt+J"`, `string FlyoutShortcut = "Ctrl+Alt+K"` (`""` = none); constants `MaxFlyoutDays = 14`, `DefaultJoinShortcut`, `DefaultFlyoutShortcut`, `static IReadOnlyList<int> LookaheadChoices`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/HotkeyTests.cs`:
```csharp
using LeafCalendar.Core.Tray;

namespace LeafCalendar.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+J", "Ctrl+Alt+J")]
    [InlineData("alt + control + j", "Ctrl+Alt+J")]
    [InlineData("Win+Shift+F9", "Shift+Win+F9")]
    [InlineData("Ctrl+Alt+Shift+7", "Ctrl+Alt+Shift+7")]
    [InlineData("Ctrl+F24", "Ctrl+F24")]
    public void TryParse_Valid_WritesCanonicalText(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(expected, hotkey.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("J")]
    [InlineData("Shift+J")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+J+K")]
    [InlineData("Ctrl+Ctrl+J")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+F12")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Alt+ÿ")]
    public void TryParse_Invalid_IsRefused(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void TryCreate_KeyCodes_MatchWin32()
    {
        Assert.True(Hotkey.TryCreate(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x4A, out var join));
        Assert.Equal("Ctrl+Alt+J", join.ToString());
        Assert.Equal(0x3, (int)join.Modifiers);

        Assert.False(Hotkey.TryCreate(HotkeyModifiers.Shift, 0x4A, out _));
        Assert.False(Hotkey.TryCreate(HotkeyModifiers.Ctrl, 0x11, out _));
    }
}
```

`tests/LeafCalendar.Tests/AlertSettingsTests.cs`:
```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AlertSettingsTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var s = new LeafSettings().Normalize();

        Assert.True(s.ReminderNotifications);
        Assert.True(s.JoinNowNotifications);
        Assert.True(s.InviteNotifications);
        Assert.True(s.NotificationSound);
        Assert.Equal(3, s.FlyoutDays);
        Assert.True(s.FlyoutAllDay);
        Assert.Equal(60, s.TrayLookaheadMinutes);
        Assert.Equal("Ctrl+Alt+J", s.JoinShortcut);
        Assert.Equal("Ctrl+Alt+K", s.FlyoutShortcut);
    }

    [Fact]
    public void Load_RowSavedBeforeMilestone4_KeepsItsValuesAndGetsDefaults()
    {
        using var conn = _db.Database.Open();
        conn.Execute(null, "INSERT INTO settings (key, value) VALUES ('app', $value);", ("$value", """{"showWeekends":false,"theme":"Dark"}"""));

        var s = SettingsStore.Load(conn);

        Assert.False(s.ShowWeekends);
        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.True(s.ReminderNotifications);
        Assert.Equal("Ctrl+Alt+J", s.JoinShortcut);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99, 14)]
    [InlineData(7, 7)]
    public void Normalize_FlyoutDays_Clamped(int stored, int expected)
    {
        Assert.Equal(expected, (new LeafSettings { FlyoutDays = stored }).Normalize().FlyoutDays);
    }

    [Theory]
    [InlineData(15, 15)]
    [InlineData(480, 480)]
    [InlineData(45, 60)]
    [InlineData(-1, 60)]
    public void Normalize_Lookahead_OnlyTheOfferedChoices(int stored, int expected)
    {
        Assert.Equal(expected, (new LeafSettings { TrayLookaheadMinutes = stored }).Normalize().TrayLookaheadMinutes);
    }

    [Fact]
    public void Normalize_Shortcuts_CanonicalOrDefaultOrNone()
    {
        var s = new LeafSettings { JoinShortcut = "alt+ctrl+m", FlyoutShortcut = "nonsense" }.Normalize();
        Assert.Equal("Ctrl+Alt+M", s.JoinShortcut);
        Assert.Equal("Ctrl+Alt+K", s.FlyoutShortcut);

        var none = new LeafSettings { JoinShortcut = "", FlyoutShortcut = null! }.Normalize();
        Assert.Equal("", none.JoinShortcut);
        Assert.Equal("Ctrl+Alt+K", none.FlyoutShortcut);
    }

    [Fact]
    public void Normalize_SameShortcutTwice_TurnsTheFlyoutOneOff()
    {
        var s = new LeafSettings { JoinShortcut = "Ctrl+Alt+K", FlyoutShortcut = "Ctrl+Alt+K" }.Normalize();

        Assert.Equal("Ctrl+Alt+K", s.JoinShortcut);
        Assert.Equal("", s.FlyoutShortcut);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheNewFields()
    {
        using var conn = _db.Database.Open();
        SettingsStore.Save(conn, new LeafSettings
        {
            ReminderNotifications = false,
            JoinNowNotifications  = false,
            InviteNotifications   = false,
            NotificationSound     = false,
            FlyoutDays            = 5,
            FlyoutAllDay          = false,
            TrayLookaheadMinutes  = 240,
            JoinShortcut          = "Ctrl+Alt+Shift+F9",
            FlyoutShortcut        = "Ctrl+Alt+Shift+F10",
        });

        var s = SettingsStore.Load(conn);

        Assert.False(s.ReminderNotifications);
        Assert.False(s.JoinNowNotifications);
        Assert.False(s.InviteNotifications);
        Assert.False(s.NotificationSound);
        Assert.Equal(5, s.FlyoutDays);
        Assert.False(s.FlyoutAllDay);
        Assert.Equal(240, s.TrayLookaheadMinutes);
        Assert.Equal("Ctrl+Alt+Shift+F9", s.JoinShortcut);
        Assert.Equal("Ctrl+Alt+Shift+F10", s.FlyoutShortcut);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*HotkeyTests" --filter-class "*AlertSettingsTests"`
Expected: build FAIL — `Hotkey`, `HotkeyModifiers`, and `LeafSettings.ReminderNotifications` (and the other new members) don't exist.

- [ ] **Step 3: Write `Hotkey`**

`src/LeafCalendar.Core/Tray/Hotkey.cs`:
```csharp
using System.Globalization;

namespace LeafCalendar.Core.Tray;

/// <summary>Modifier keys of a global shortcut. The values are Win32's <c>MOD_*</c> flags, so the app hands them to <c>RegisterHotKey</c> as they are.</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Alt (<c>MOD_ALT</c>).</summary>
    Alt = 0x1,

    /// <summary>Ctrl (<c>MOD_CONTROL</c>).</summary>
    Ctrl = 0x2,

    /// <summary>Shift (<c>MOD_SHIFT</c>).</summary>
    Shift = 0x4,

    /// <summary>The Windows key (<c>MOD_WIN</c>).</summary>
    Win = 0x8,
}

/// <summary>
/// A global shortcut such as Ctrl+Alt+J: modifiers plus one key, a Win32 virtual-key code for A–Z, 0–9, or F1–F24.
/// </summary>
/// <remarks>
/// At least one of Ctrl, Alt, or Win is required, so a shortcut never swallows plain typing (Shift+J is a capital J).
/// F12 is refused because Windows keeps it for debuggers. Text is always written Ctrl, Alt, Shift, Win, then the key,
/// which is how settings store it.
/// </remarks>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    const HotkeyModifiers AllModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win;
    const HotkeyModifiers Anchors      = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win;

    /// <summary>Checks a pressed combination (the shortcut dialog) and makes a shortcut from it.</summary>
    public static bool TryCreate(HotkeyModifiers modifiers, int key, out Hotkey hotkey)
    {
        hotkey = new Hotkey(modifiers, key);
        return (modifiers & Anchors) != 0
            && (modifiers & ~AllModifiers) == 0
            && KeyName(key) is not null;
    }

    /// <summary>Reads text such as "Ctrl+Alt+J": any order, any case, "Control" and "Windows" work too.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key      = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim().ToUpperInvariant();
            HotkeyModifiers? modifier = part switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
                "ALT"               => HotkeyModifiers.Alt,
                "SHIFT"             => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS"  => HotkeyModifiers.Win,
                _                   => null,
            };

            // A Modifier (each only once)
            if (modifier is { } m)
            {
                if ((modifiers & m) != 0)
                {
                    return false;
                }

                modifiers |= m;
                continue;
            }

            // The One Key
            if (key is not null || KeyCode(part) is not { } code)
            {
                return false;
            }

            key = code;
        }

        return key is { } k && TryCreate(modifiers, k, out hotkey);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(Key) ?? "?");
        return string.Join("+", parts);
    }

    // A–Z and 0–9 are their own virtual-key codes; F1–F24 are 0x70–0x87 (F12, 0x7B, is the debugger's)
    static string? KeyName(int key) => key switch
    {
        0x7B                => null,
        >= 0x41 and <= 0x5A => new string((char)key, 1),
        >= 0x30 and <= 0x39 => new string((char)key, 1),
        >= 0x70 and <= 0x87 => string.Create(CultureInfo.InvariantCulture, $"F{key - 0x6F}"),
        _                   => null,
    };

    static int? KeyCode(string name)
    {
        if (name.Length == 1 && (char.IsAsciiLetterUpper(name[0]) || char.IsAsciiDigit(name[0])))
        {
            return name[0];
        }

        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            var code = 0x6F + n;
            return KeyName(code) is null ? null : code;
        }

        return null;
    }
}
```

- [ ] **Step 4: Add the settings**

In `src/LeafCalendar.Core/Settings/LeafSettings.cs`, add `using LeafCalendar.Core.Tray;` at the top, then add after `const int MaxLabelLength = 24;`:
```csharp
    /// <summary>Most days the tray flyout's agenda lists.</summary>
    public const int MaxFlyoutDays = 14;

    /// <summary>The join shortcut out of the box (spec 8.6).</summary>
    public const string DefaultJoinShortcut = "Ctrl+Alt+J";

    /// <summary>The flyout shortcut out of the box (spec 8.6).</summary>
    public const string DefaultFlyoutShortcut = "Ctrl+Alt+K";

    /// <summary>The flyout header's and tooltip's lookahead choices in minutes: 15, 30, 60 minutes, 2, 4, 8 hours (spec 9).</summary>
    public static IReadOnlyList<int> LookaheadChoices { get; } = [15, 30, 60, 120, 240, 480];
```

Add after the `TimeZones` property:
```csharp
    /// <summary>Show a notification at each reminder time.</summary>
    public bool ReminderNotifications { get; init; } = true;

    /// <summary>Show the persistent "Join now" notification when a meeting with a link starts.</summary>
    public bool JoinNowNotifications { get; init; } = true;

    /// <summary>Show new and updated invitations.</summary>
    public bool InviteNotifications { get; init; } = true;

    /// <summary>Notifications play the Windows sound.</summary>
    public bool NotificationSound { get; init; } = true;

    /// <summary>Days the tray flyout's agenda lists, starting today (1-14).</summary>
    public int FlyoutDays { get; init; } = 3;

    /// <summary>The tray flyout lists all-day events.</summary>
    public bool FlyoutAllDay { get; init; } = true;

    /// <summary>How far ahead the flyout header and the tray tooltip look for the next event, in minutes.</summary>
    public int TrayLookaheadMinutes { get; init; } = 60;

    /// <summary>Global shortcut that joins the next meeting, such as "Ctrl+Alt+J"; empty for none.</summary>
    public string JoinShortcut { get; init; } = DefaultJoinShortcut;

    /// <summary>Global shortcut that shows or hides the tray flyout; empty for none.</summary>
    public string FlyoutShortcut { get; init; } = DefaultFlyoutShortcut;
```

Replace the `Normalize` remarks and body's `return this with { ... };` so the method ends like this:
```csharp
    /// <remarks>
    /// Day count is clamped to 1-31 and hour height to its range. Unknown enum values reset to defaults.
    /// Time zones are limited to distinct IDs this PC knows, capped at <see cref="MaxTimeZones"/>, with
    /// labels trimmed (blank becomes null) to at most 24 characters. Flyout days are clamped to 1-14, a lookahead
    /// that isn't one of <see cref="LookaheadChoices"/> becomes 60 minutes, shortcuts are rewritten in
    /// <see cref="Hotkey"/>'s order (unreadable ones return to their defaults, empty stays empty), and a flyout
    /// shortcut that repeats the join shortcut is turned off.
    /// </remarks>
    public LeafSettings Normalize()
    {
        var zones = (TimeZones ?? [])
            .Where(z => z is not null && !string.IsNullOrWhiteSpace(z.Id) && TimeZoneInfo.TryFindSystemTimeZoneById(z.Id, out _))
            .DistinctBy(z => z.Id, StringComparer.Ordinal)
            .Take(MaxTimeZones)
            .Select(z => z with { Label = CleanLabel(z.Label) })
            .ToList();

        // Shortcuts (one combination can't do two things)
        var join   = CleanShortcut(JoinShortcut, DefaultJoinShortcut);
        var flyout = CleanShortcut(FlyoutShortcut, DefaultFlyoutShortcut);
        if (flyout.Length > 0 && flyout == join)
        {
            flyout = "";
        }

        return this with
        {
            WeekStart            = Enum.IsDefined(WeekStart) ? WeekStart : DayOfWeek.Sunday,
            ViewMode             = Enum.IsDefined(ViewMode) ? ViewMode : CalendarViewMode.Week,
            Theme                = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
            CustomDayCount       = Math.Clamp(CustomDayCount, 1, 31),
            HourHeight           = double.IsFinite(HourHeight) ? Math.Clamp(HourHeight, MinHourHeight, MaxHourHeight) : DefaultHourHeight,
            TimeZones            = zones,
            FlyoutDays           = Math.Clamp(FlyoutDays, 1, MaxFlyoutDays),
            TrayLookaheadMinutes = LookaheadChoices.Contains(TrayLookaheadMinutes) ? TrayLookaheadMinutes : 60,
            JoinShortcut         = join,
            FlyoutShortcut       = flyout,
        };
    }

    // Empty means "no shortcut"; null (a row saved before the field existed) or unreadable text means the default
    static string CleanShortcut(string? text, string fallback)
    {
        if (text is { Length: 0 })
        {
            return "";
        }

        return Hotkey.TryParse(text, out var hotkey) ? hotkey.ToString() : fallback;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*HotkeyTests" --filter-class "*AlertSettingsTests" --filter-class "*SettingsStoreTests"`
Expected: PASS (the existing `SettingsStoreTests` still pass).

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Tray/Hotkey.cs src/LeafCalendar.Core/Settings/LeafSettings.cs tests/LeafCalendar.Tests/HotkeyTests.cs tests/LeafCalendar.Tests/AlertSettingsTests.cs
git commit -m "feat(core): notification, tray, and global shortcut settings"
```

---

### Task 2: Alert Ledger and Calendar Default Reminders

**Files:**
- Create: `src/LeafCalendar.Core/Alerts/AlertKind.cs`, `src/LeafCalendar.Core/Data/AlertLedger.cs`
- Modify: `src/LeafCalendar.Core/Data/Schema.cs`, `src/LeafCalendar.Core/Data/LeafDatabase.cs`, `src/LeafCalendar.Core/Data/CalendarStore.cs`
- Test: `tests/LeafCalendar.Tests/AlertLedgerTests.cs`, `tests/LeafCalendar.Tests/LeafDatabaseTests.cs:25`

**Interfaces:**
- Consumes: `SqliteExtensions`, `GoogleJsonContext.Default.ListReminderOverride`, `ReminderOverride`.
- Produces:
  - `namespace LeafCalendar.Core.Alerts`: `enum AlertKind { Reminder, JoinNow, Invite }`
  - `namespace LeafCalendar.Core.Data`: `sealed record LedgerEntry(string Key, AlertKind Kind, string Tag, DateTimeOffset EventEnd)`
  - `static class AlertLedger`:
    - `bool TryAdd(SqliteConnection conn, string key, AlertKind kind, string tag, DateTimeOffset eventEnd, DateTimeOffset now)` (true when newly recorded)
    - `bool Contains(SqliteConnection conn, string key)`, `bool HasPrefix(SqliteConnection conn, string prefix)`
    - `IReadOnlyList<LedgerEntry> OpenJoinNow(SqliteConnection conn)`, `void MarkRetracted(SqliteConnection conn, string key)`
    - `void Prune(SqliteConnection conn, DateTimeOffset endedBefore)`
    - `long? GetMark(SqliteConnection conn, string name)`, `void SetMark(SqliteConnection conn, string name, long value)`
  - `CalendarStore.PopupDefaults(SqliteConnection conn)` → `IReadOnlyDictionary<(string AccountId, string CalendarId), IReadOnlyList<int>>`
  - Schema version 4 (`alert_ledger`).

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/AlertLedgerTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AlertLedgerTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 1, 17, 0, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TryAdd_SameKeyTwice_OnlyFirstCounts()
    {
        using var conn = _db.Database.Open();

        Assert.True(AlertLedger.TryAdd(conn, "Reminder|a|10", AlertKind.Reminder, "T1", Now.AddHours(1), Now));
        Assert.False(AlertLedger.TryAdd(conn, "Reminder|a|10", AlertKind.Reminder, "T1", Now.AddHours(1), Now));
        Assert.True(AlertLedger.Contains(conn, "Reminder|a|10"));
        Assert.False(AlertLedger.Contains(conn, "Reminder|a|5"));
    }

    [Fact]
    public void TryAdd_SurvivesReopeningTheDatabase()
    {
        using (var conn = _db.Database.Open())
        {
            AlertLedger.TryAdd(conn, "JoinNow|a|0", AlertKind.JoinNow, "T2", Now.AddHours(1), Now);
        }

        using var again = _db.Database.Open();
        Assert.False(AlertLedger.TryAdd(again, "JoinNow|a|0", AlertKind.JoinNow, "T2", Now.AddHours(1), Now));
    }

    [Fact]
    public void OpenJoinNow_ListsOnlyJoinNowNotYetRetracted()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "JoinNow|a|0", AlertKind.JoinNow, "TA", Now.AddHours(1), Now);
        AlertLedger.TryAdd(conn, "JoinNow|b|0", AlertKind.JoinNow, "TB", Now.AddHours(2), Now);
        AlertLedger.TryAdd(conn, "Reminder|a|10", AlertKind.Reminder, "TC", Now.AddHours(1), Now);

        AlertLedger.MarkRetracted(conn, "JoinNow|a|0");

        var open = Assert.Single(AlertLedger.OpenJoinNow(conn));
        Assert.Equal(new LedgerEntry("JoinNow|b|0", AlertKind.JoinNow, "TB", Now.AddHours(2)), open);
    }

    [Fact]
    public void Prune_RemovesEventsThatEndedBeforeTheCutoff()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "old", AlertKind.Reminder, "T", Now.AddDays(-3), Now);
        AlertLedger.TryAdd(conn, "new", AlertKind.Invite, "T", Now.AddDays(30), Now);

        AlertLedger.Prune(conn, Now.AddDays(-2));

        Assert.False(AlertLedger.Contains(conn, "old"));
        Assert.True(AlertLedger.Contains(conn, "new"));
    }

    [Fact]
    public void HasPrefix_MatchesTheStartOnly()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "Invite|acct|cal|evt_1|0", AlertKind.Invite, "T", Now.AddDays(1), Now);

        Assert.True(AlertLedger.HasPrefix(conn, "Invite|acct|cal|evt_1|"));
        Assert.False(AlertLedger.HasPrefix(conn, "Invite|acct|cal|evt%|"));
        Assert.False(AlertLedger.HasPrefix(conn, "Invite|acct|cal|evt_10|"));
    }

    [Fact]
    public void Marks_RoundTripAndStartEmpty()
    {
        using var conn = _db.Database.Open();

        Assert.Null(AlertLedger.GetMark(conn, "invites-seeded:1"));
        AlertLedger.SetMark(conn, "invites-seeded:1", 1);
        AlertLedger.SetMark(conn, "invites-seeded:1", 2);

        Assert.Equal(2, AlertLedger.GetMark(conn, "invites-seeded:1"));
    }

    [Fact]
    public void PopupDefaults_ReadsPopupMinutesPerCalendar()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        var entries = JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items;
        entries[1].DefaultReminders = [new ReminderOverride { Method = "email", Minutes = 30 }, new ReminderOverride { Method = "popup", Minutes = 50000 }, new ReminderOverride { Method = "popup", Minutes = 5 }];
        CalendarStore.ReplaceForAccount(conn, TestDatabase.SampleAccount.Id, entries);

        var defaults = CalendarStore.PopupDefaults(conn);

        Assert.Equal([10], defaults[(TestDatabase.SampleAccount.Id, "leaf.tester@gmail.com")]);
        Assert.Equal([5], defaults[(TestDatabase.SampleAccount.Id, "family123@group.calendar.google.com")]);
    }
}
```

In `tests/LeafCalendar.Tests/LeafDatabaseTests.cs`, change line 25:
```csharp
        Assert.Equal(4L, (long)version.ExecuteScalar()!);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*AlertLedgerTests" --filter-class "*LeafDatabaseTests"`
Expected: build FAIL — `AlertKind`, `AlertLedger`, `LedgerEntry`, and `CalendarStore.PopupDefaults` don't exist.

- [ ] **Step 3: Add the kind, the schema, and the migration**

`src/LeafCalendar.Core/Alerts/AlertKind.cs`:
```csharp
namespace LeafCalendar.Core.Alerts;

/// <summary>The notifications Leaf schedules and remembers (spec 8.4).</summary>
public enum AlertKind
{
    /// <summary>A reminder at one of the event's reminder times.</summary>
    Reminder,

    /// <summary>The persistent "Join now" at a meeting's start.</summary>
    JoinNow,

    /// <summary>A new or updated invitation.</summary>
    Invite,
}
```

In `src/LeafCalendar.Core/Data/Schema.cs`, add after `V3`:
```csharp
    /// <summary>
    /// Version 4: every notification Leaf has shown, so a restart or a full resync never shows one twice. <c>key</c>
    /// names the alert (kind, event instance, reminder minutes; or kind, event, and Google's <c>sequence</c> for
    /// invites), <c>tag</c> is the short hash Windows knows the notification by, <c>event_end</c> says when the row may
    /// go, and <c>retracted</c> marks a "Join now" that was withdrawn.
    /// </summary>
    public const string V4 = """
        CREATE TABLE alert_ledger (
            key           TEXT PRIMARY KEY,
            kind          TEXT NOT NULL,
            tag           TEXT NOT NULL,
            event_end     INTEGER NOT NULL,
            delivered_utc INTEGER NOT NULL,
            retracted     INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX ix_alert_ledger_open ON alert_ledger (kind, retracted);
        """;
```

In `src/LeafCalendar.Core/Data/LeafDatabase.cs`, add after the `// Version 3` block:
```csharp

        // Version 4
        if (version < 4)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V4);
            conn.Execute(tx, "PRAGMA user_version = 4;");
            tx.Commit();
        }
```

- [ ] **Step 4: Write `AlertLedger`**

`src/LeafCalendar.Core/Data/AlertLedger.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Alerts;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>A notification Leaf showed: its key, kind, Windows tag, and when its event ends.</summary>
public sealed record LedgerEntry(string Key, AlertKind Kind, string Tag, DateTimeOffset EventEnd);

/// <summary>
/// Reads and writes the <c>alert_ledger</c> table, Leaf's memory of the notifications it already showed.
/// </summary>
/// <remarks>
/// Rows stay until their event has been over for a while (<see cref="Prune"/>), so an invite for a meeting weeks away
/// isn't shown again when someone else replies to it. Small named marks (such as "this account's existing invites were
/// recorded") live in the <c>settings</c> table under <c>mark:&lt;name&gt;</c>.
/// </remarks>
public static class AlertLedger
{
    /// <summary>Records a shown notification. False when it was already recorded (so it must not show again).</summary>
    public static bool TryAdd(SqliteConnection conn, string key, AlertKind kind, string tag, DateTimeOffset eventEnd, DateTimeOffset now) =>
        conn.Execute(
            null,
            """
            INSERT INTO alert_ledger (key, kind, tag, event_end, delivered_utc) VALUES ($key, $kind, $tag, $end, $now)
            ON CONFLICT (key) DO NOTHING;
            """,
            ("$key", key),
            ("$kind", kind.ToString()),
            ("$tag", tag),
            ("$end", eventEnd.ToUnixTimeMilliseconds()),
            ("$now", now.ToUnixTimeMilliseconds())) == 1;

    /// <summary>True when a notification with this key was recorded.</summary>
    public static bool Contains(SqliteConnection conn, string key) =>
        conn.Query(null, "SELECT EXISTS (SELECT 1 FROM alert_ledger WHERE key = $key);", r => r.GetBoolean(0), ("$key", key)).Single();

    /// <summary>True when any recorded key starts with <paramref name="prefix"/> (compared exactly, no wildcards).</summary>
    public static bool HasPrefix(SqliteConnection conn, string prefix) =>
        conn.Query(
            null,
            "SELECT EXISTS (SELECT 1 FROM alert_ledger WHERE substr(key, 1, length($prefix)) = $prefix);",
            r => r.GetBoolean(0),
            ("$prefix", prefix)).Single();

    /// <summary>"Join now" notifications that are still on screen (not withdrawn).</summary>
    public static IReadOnlyList<LedgerEntry> OpenJoinNow(SqliteConnection conn) =>
        conn.Query(
            null,
            "SELECT key, tag, event_end FROM alert_ledger WHERE kind = $kind AND retracted = 0 ORDER BY delivered_utc;",
            r => new LedgerEntry(r.GetString(0), AlertKind.JoinNow, r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))),
            ("$kind", nameof(AlertKind.JoinNow)));

    /// <summary>Marks a "Join now" as withdrawn.</summary>
    public static void MarkRetracted(SqliteConnection conn, string key) =>
        conn.Execute(null, "UPDATE alert_ledger SET retracted = 1 WHERE key = $key;", ("$key", key));

    /// <summary>Forgets notifications for events that ended before <paramref name="endedBefore"/>.</summary>
    public static void Prune(SqliteConnection conn, DateTimeOffset endedBefore) =>
        conn.Execute(null, "DELETE FROM alert_ledger WHERE event_end < $cutoff;", ("$cutoff", endedBefore.ToUnixTimeMilliseconds()));

    /// <summary>A named mark's value, or null when it was never set.</summary>
    public static long? GetMark(SqliteConnection conn, string name)
    {
        var text = conn.Query(null, "SELECT value FROM settings WHERE key = $key;", r => r.GetString(0), ("$key", "mark:" + name)).SingleOrDefault();
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Sets a named mark.</summary>
    public static void SetMark(SqliteConnection conn, string name, long value) =>
        conn.Execute(
            null,
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            ("$key", "mark:" + name),
            ("$value", value.ToString(CultureInfo.InvariantCulture)));
}
```

- [ ] **Step 5: Add `PopupDefaults`**

In `src/LeafCalendar.Core/Data/CalendarStore.cs`, add before `static CalendarInfo Map(...)`:
```csharp
    /// <summary>
    /// Each calendar's default popup reminders in minutes (Google's <c>defaultReminders</c>, <c>popup</c> only, within
    /// Google's 0–40,320 range, no repeats). Email reminders are Google's own, so they're left out.
    /// </summary>
    public static IReadOnlyDictionary<(string AccountId, string CalendarId), IReadOnlyList<int>> PopupDefaults(SqliteConnection conn)
    {
        var result = new Dictionary<(string, string), IReadOnlyList<int>>();
        foreach (var (account, id, json) in conn.Query(null, "SELECT account_id, id, default_reminders FROM calendars;", r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2))))
        {
            result[(account, id)] = PopupMinutes(json);
        }

        return result;
    }

    static IReadOnlyList<int> PopupMinutes(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize(json, GoogleJsonContext.Default.ListReminderOverride) ?? [])
                .Where(r => r is not null && r.Method == "popup" && r.Minutes is >= 0 and <= 40320)
                .Select(r => r.Minutes)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: PASS (all logic tests, including the migration test at version 4).

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.Core/Alerts/AlertKind.cs src/LeafCalendar.Core/Data tests/LeafCalendar.Tests/AlertLedgerTests.cs tests/LeafCalendar.Tests/LeafDatabaseTests.cs
git commit -m "feat(core): alert ledger (schema v4) and calendar popup defaults"
```

---

### Task 3: Reminder Planning From Google's Reminders

**Files:**
- Create: `src/LeafCalendar.Core/Alerts/AlertPlanner.cs`, `tests/LeafCalendar.LiveTests/LiveAlertTests.cs`
- Modify: `src/LeafCalendar.Core/Events/CalendarOccurrence.cs`, `tests/LeafCalendar.LiveTests/Support/LiveGoogle.cs`
- Test: `tests/LeafCalendar.Tests/AlertPlannerTests.cs`

**Interfaces:**
- Consumes: `OccurrenceQuery.Load(SqliteConnection, DateOnly, DateOnly, TimeZoneInfo, bool)`, `OccurrenceQuery.LocalMidnight(DateOnly, TimeZoneInfo)`, `EventStore.Get(SqliteConnection, string, string, string)`, `EventDetailsParser.Parse(string, bool)`, `CalendarStore.PopupDefaults` (Task 2), `AlertKind` (Task 2).
- Produces:
  - `CalendarOccurrence.StartIn(TimeZoneInfo zone)` / `EndIn(TimeZoneInfo zone)` → `DateTimeOffset` (all-day: local midnight of the first day / of the exclusive end date)
  - `namespace LeafCalendar.Core.Alerts`: `sealed record Alert(AlertKind Kind, CalendarOccurrence Occurrence, DateTimeOffset FireAt, int MinutesBefore, Uri? MeetingLink)` with `string Key` (`"{Kind}|{Occurrence.Key}|{MinutesBefore}"`), `string Tag` (16 hex characters), `static string TagFor(string key)`
  - `static class AlertPlanner`: `const int MaxMinutes = 40320`; `IReadOnlyList<Alert> Plan(SqliteConnection conn, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)` — alerts with `from < FireAt <= to`, by `FireAt`, then kind

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/AlertPlannerTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AlertPlannerTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();

    public AlertPlannerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Primary, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    void Insert(string json)
    {
        using var conn = _db.Database.Open();
        using var doc  = JsonDocument.Parse(json);
        EventStore.Apply(conn, null, Account, Primary, doc.RootElement);
    }

    IReadOnlyList<Alert> Plan(DateTimeOffset from, DateTimeOffset to)
    {
        using var conn = _db.Database.Open();
        return AlertPlanner.Plan(conn, from, to, NewYork);
    }

    static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_EventOnDefaults_UsesTheCalendarsPopup()
    {
        // Dentist, Oct 1 9:00 EDT (13:00Z), no reminders field; the primary calendar's default is a 10-minute popup
        var alert = Assert.Single(Plan(Utc(10, 1, 12), Utc(10, 1, 14)));

        Assert.Equal(AlertKind.Reminder, alert.Kind);
        Assert.Equal("evt-single", alert.Occurrence.EventId);
        Assert.Equal(Utc(10, 1, 12, 50), alert.FireAt);
        Assert.Equal(10, alert.MinutesBefore);
        Assert.Null(alert.MeetingLink);
    }

    [Fact]
    public void Plan_Overrides_ReplaceTheDefaultsAndSkipEmail()
    {
        Insert("""
            {"id":"evt-ovr","status":"confirmed","summary":"Overrides","start":{"dateTime":"2026-10-02T15:00:00Z"},"end":{"dateTime":"2026-10-02T16:00:00Z"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":5},{"method":"email","minutes":30}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 2, 14), Utc(10, 2, 16)));

        Assert.Equal(Utc(10, 2, 14, 55), alert.FireAt);
        Assert.Equal(5, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_NoDefaultAndNoOverrides_NoReminder()
    {
        Insert("""
            {"id":"evt-quiet","status":"confirmed","summary":"Quiet","start":{"dateTime":"2026-10-02T15:00:00Z"},"end":{"dateTime":"2026-10-02T16:00:00Z"},
             "reminders":{"useDefault":false}}
            """);

        Assert.Empty(Plan(Utc(10, 2, 0), Utc(10, 2, 23)));
    }

    [Fact]
    public void Plan_AllDayOverride_CountsBackFromLocalMidnight()
    {
        // 900 minutes before Oct 3 00:00 EDT (04:00Z) is Oct 2 9:00 AM EDT (13:00Z), Google's "1 day before at 9 AM"
        Insert("""
            {"id":"evt-allday-ovr","status":"confirmed","summary":"Trip","start":{"date":"2026-10-03"},"end":{"date":"2026-10-04"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":900}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 2, 0), Utc(10, 3, 0)));

        Assert.Equal(Utc(10, 2, 13), alert.FireAt);
        Assert.True(alert.Occurrence.IsAllDay);
    }

    [Fact]
    public void Plan_AllDayOnDefaults_NoReminder()
    {
        // Company holiday, Oct 12, no reminders field: Google's API doesn't expose all-day defaults (the Monday standup still reminds)
        Assert.DoesNotContain(Plan(Utc(10, 11, 0), Utc(10, 13, 0)), a => a.Occurrence.EventId == "evt-allday");
    }

    [Fact]
    public void Plan_RepeatingAcrossDaylightSavingEnd_KeepsTheLocalTime()
    {
        // Team standup, 9:30 New York on Mon/Wed/Fri; DST ends Sunday Nov 1, 2026
        var standups = Plan(Utc(10, 30, 0), Utc(11, 3, 0)).Where(a => a.Occurrence.EventId == "evt-weekly").Select(a => a.FireAt).ToList();

        Assert.Equal([Utc(10, 30, 13, 20), Utc(11, 2, 14, 20)], standups);
    }

    [Fact]
    public void Plan_MeetingLink_AddsJoinNowAtStartAndTheLinkOnTheReminder()
    {
        Insert("""
            {"id":"evt-meet","status":"confirmed","summary":"Design review","hangoutLink":"https://meet.google.com/abc-defg-hij",
             "start":{"dateTime":"2026-10-02T18:00:00Z"},"end":{"dateTime":"2026-10-02T19:00:00Z"}}
            """);

        var alerts = Plan(Utc(10, 2, 17), Utc(10, 2, 19));

        Assert.Equal([AlertKind.Reminder, AlertKind.JoinNow], alerts.Select(a => a.Kind));
        Assert.Equal(Utc(10, 2, 17, 50), alerts[0].FireAt);
        Assert.Equal(Utc(10, 2, 18), alerts[1].FireAt);
        Assert.All(alerts, a => Assert.Equal("https://meet.google.com/abc-defg-hij", a.MeetingLink!.AbsoluteUri));
    }

    [Fact]
    public void Plan_Declined_NoAlerts()
    {
        Insert("""
            {"id":"evt-no","status":"confirmed","summary":"Declined","hangoutLink":"https://meet.google.com/abc-defg-hij",
             "attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],
             "start":{"dateTime":"2026-10-02T18:00:00Z"},"end":{"dateTime":"2026-10-02T19:00:00Z"}}
            """);

        Assert.Empty(Plan(Utc(10, 2, 17), Utc(10, 2, 19)));
    }

    [Fact]
    public void Plan_ChangedInstance_UsesItsOwnReminders()
    {
        Insert("""
            {"id":"evt-weekly_20261009T133000Z","status":"confirmed","recurringEventId":"evt-weekly","summary":"Standup",
             "originalStartTime":{"dateTime":"2026-10-09T09:30:00-04:00","timeZone":"America/New_York"},
             "start":{"dateTime":"2026-10-09T09:30:00-04:00"},"end":{"dateTime":"2026-10-09T10:00:00-04:00"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":30}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 9, 12), Utc(10, 9, 14)));

        Assert.Equal(Utc(10, 9, 13), alert.FireAt);
        Assert.Equal(30, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_MinutesOutsideGooglesRange_Ignored()
    {
        Insert("""
            {"id":"evt-odd","status":"confirmed","summary":"Odd","start":{"dateTime":"2026-10-20T15:00:00Z"},"end":{"dateTime":"2026-10-20T16:00:00Z"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":-5},{"method":"popup","minutes":50000},{"method":"popup","minutes":15},{"method":"popup","minutes":"x"}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 20, 0), Utc(10, 21, 0)));

        Assert.Equal(15, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_WindowIsExclusiveAtTheStartAndInclusiveAtTheEnd()
    {
        Assert.Empty(Plan(Utc(10, 1, 12, 50), Utc(10, 1, 13)));
        Assert.Single(Plan(Utc(10, 1, 12, 49), Utc(10, 1, 12, 50)));
    }

    [Fact]
    public void Plan_FourWeekReminder_IsFoundFromAWindowBeforeIt()
    {
        Insert("""
            {"id":"evt-far","status":"confirmed","summary":"Far","start":{"dateTime":"2026-11-20T15:00:00Z"},"end":{"dateTime":"2026-11-20T16:00:00Z"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":40320}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 23, 0), Utc(10, 24, 0)).Where(a => a.Occurrence.EventId == "evt-far"));

        Assert.Equal(Utc(10, 23, 15), alert.FireAt);
    }

    [Fact]
    public void Tag_IsShortStableHex()
    {
        Assert.Equal(Alert.TagFor("x"), Alert.TagFor("x"));
        Assert.NotEqual(Alert.TagFor("x"), Alert.TagFor("y"));
        Assert.Matches("^[0-9A-F]{16}$", Alert.TagFor("Reminder|a|b|c|1|10"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*AlertPlannerTests"`
Expected: build FAIL — `Alert` and `AlertPlanner` don't exist.

- [ ] **Step 3: Add `StartIn` and `EndIn`**

In `src/LeafCalendar.Core/Events/CalendarOccurrence.cs`, add after `AllDayEnd`:
```csharp
    /// <summary>When the instance starts on the clock in <paramref name="zone"/>: <see cref="Start"/>, or local midnight of the first all-day date.</summary>
    public DateTimeOffset StartIn(TimeZoneInfo zone) => IsAllDay ? OccurrenceQuery.LocalMidnight(AllDayStart, zone) : Start;

    /// <summary>When the instance ends on the clock in <paramref name="zone"/>: <see cref="End"/>, or local midnight of the all-day end date.</summary>
    public DateTimeOffset EndIn(TimeZoneInfo zone) => IsAllDay ? OccurrenceQuery.LocalMidnight(AllDayEnd, zone) : End;
```

- [ ] **Step 4: Write `AlertPlanner`**

`src/LeafCalendar.Core/Alerts/AlertPlanner.cs`:
```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>
/// A notification that should appear at <see cref="FireAt"/> for one event instance. <see cref="MinutesBefore"/> is the
/// reminder's lead (0 for "Join now"), and <see cref="MeetingLink"/> the event's meeting link, if any (it gives the
/// reminder a Join button).
/// </summary>
public sealed record Alert(AlertKind Kind, CalendarOccurrence Occurrence, DateTimeOffset FireAt, int MinutesBefore, Uri? MeetingLink)
{
    /// <summary>Names this alert in the ledger: kind, instance, and lead. A moved meeting has a new instance key, so it reminds again.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Kind}|{Occurrence.Key}|{MinutesBefore}");

    /// <summary>The Windows tag of this alert's notification.</summary>
    public string Tag => TagFor(Key);

    /// <summary>
    /// A 16-character hex hash of <paramref name="key"/>. Windows tags are at most 64 characters, and the hash keeps
    /// calendar IDs (often email addresses) out of anything Windows or the log sees.
    /// </summary>
    public static string TagFor(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8);
}

/// <summary>
/// Works out which notifications fall in a time window, from Google's own reminder settings (spec 8.4).
/// </summary>
/// <remarks>
/// <para>
/// An event on <c>reminders.useDefault</c> (or with no <c>reminders</c> at all) gets its calendar's default popups; one
/// with overrides gets its own popups instead. Email reminders are Google's to send. Timed reminders count back from the
/// start; all-day ones from local midnight of the first day, and only from overrides, since Google's API doesn't expose
/// the all-day defaults. Minutes outside 0–40,320 (Google's range) are ignored.
/// </para>
/// <para>
/// A timed event with a meeting link also gets "Join now" at its start. Instances come from
/// <see cref="OccurrenceQuery"/>, so repeating events keep their local time across daylight saving, changed instances
/// use their own row, declined events and hidden calendars are left out, and a canceled instance has no alerts.
/// </para>
/// </remarks>
public static class AlertPlanner
{
    /// <summary>Google's longest reminder: four weeks.</summary>
    public const int MaxMinutes = 40320;

    /// <summary>The alerts with <paramref name="from"/> &lt; fire time ≤ <paramref name="to"/>, by fire time, then kind.</summary>
    public static IReadOnlyList<Alert> Plan(SqliteConnection conn, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        // Instances That Can Have An Alert In The Window: starting up to four weeks after it (the longest reminder)
        var fromDate = LocalDate(from, zone).AddDays(-1);
        var toDate   = LocalDate(to, zone).AddDays(MaxMinutes / 1440 + 2);
        var defaults = CalendarStore.PopupDefaults(conn);
        var rows     = new Dictionary<(string, string, string), RowAlerts?>();
        var alerts   = new List<Alert>();

        foreach (var o in OccurrenceQuery.Load(conn, fromDate, toDate, zone, includeDeclined: false))
        {
            // One Parse Per Stored Row (a series' instances share their master's)
            var id = (o.AccountId, o.CalendarId, o.EventId);
            if (!rows.TryGetValue(id, out var row))
            {
                rows[id] = row = Read(conn, o);
            }

            if (row is null)
            {
                continue;
            }

            // Reminders
            IReadOnlyList<int> minutes = !row.UseDefault ? row.Overrides
                : o.IsAllDay ? []
                : defaults.GetValueOrDefault((o.AccountId, o.CalendarId)) ?? [];
            var anchor = o.StartIn(zone);
            foreach (var m in minutes)
            {
                var at = anchor.AddMinutes(-m);
                if (at > from && at <= to)
                {
                    alerts.Add(new Alert(AlertKind.Reminder, o, at, m, row.Link));
                }
            }

            // Join Now
            if (!o.IsAllDay && row.Link is not null && o.Start > from && o.Start <= to)
            {
                alerts.Add(new Alert(AlertKind.JoinNow, o, o.Start, 0, row.Link));
            }
        }

        return [.. alerts.OrderBy(a => a.FireAt).ThenBy(a => a.Kind)];
    }

    static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // The row's reminder choice and meeting link; null when it's gone or unreadable (Google's JSON, so that's rare)
    static RowAlerts? Read(SqliteConnection conn, CalendarOccurrence o)
    {
        if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
        {
            return null;
        }

        try
        {
            using var doc  = JsonDocument.Parse(stored.RawJson);
            var root       = doc.RootElement;
            var useDefault = true;
            var overrides  = new List<int>();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reminders", out var reminders) && reminders.ValueKind == JsonValueKind.Object)
            {
                useDefault = reminders.TryGetProperty("useDefault", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (reminders.TryGetProperty("overrides", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "popup"
                            && item.TryGetProperty("minutes", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var m)
                            && m is >= 0 and <= MaxMinutes && !overrides.Contains(m))
                        {
                            overrides.Add(m);
                        }
                    }
                }
            }

            return new RowAlerts(useDefault, overrides, EventDetailsParser.Parse(stored.RawJson).ConferenceUri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    sealed record RowAlerts(bool UseDefault, IReadOnlyList<int> Overrides, Uri? Link);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*AlertPlannerTests" --filter-class "*OccurrenceQueryTests"`
Expected: PASS.

- [ ] **Step 6: Add the live test**

In `tests/LeafCalendar.LiveTests/Support/LiveGoogle.cs`, add before `SendAsync`:
```csharp
    /// <summary>Sets a calendar's default popup reminder (in the account's calendar list) to <paramref name="minutes"/>.</summary>
    public async Task SetDefaultRemindersAsync(string calendarId, int minutes, CancellationToken ct)
    {
        // Guard: Only A Calendar This Run Created (the path starts with users/me, so the generic guard can't see it)
        RequireOwned(calendarId);

        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(BaseUri, $"users/me/calendarList/{Uri.EscapeDataString(calendarId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await live.Services.AccessTokens.GetAccessTokenAsync(live.AccountId, ct));
        request.Content = JsonContent.Create(new JsonObject
        {
            ["defaultReminders"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = minutes }),
        });

        using var response = await LiveAccount.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates a one-hour event at <paramref name="start"/> with the given <c>reminders</c> object and returns its ID.</summary>
    public async Task<string> InsertEventWithRemindersAsync(string calendarId, DateTimeOffset start, JsonObject reminders, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["summary"]   = "Leaf live reminders",
            ["start"]     = new JsonObject { ["dateTime"] = start.ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
            ["end"]       = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture) },
            ["reminders"] = reminders,
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }
```

`tests/LeafCalendar.LiveTests/LiveAlertTests.cs`:
```csharp
using System.Text.Json.Nodes;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveAlertTests
{
    const string SkipReason = "Live account not set up. Run LiveSignInTests once (see its comment).";

    [Fact]
    public async Task Plan_RealGoogle_UsesTheCalendarDefaultAndTheEventsOverrides()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip(SkipReason);
            return;
        }

        var google     = new LiveGoogle(live);
        var calendarId = await google.CreateTestCalendarAsync(ct);
        try
        {
            // Arrange On Google: a 7-minute calendar default, one event on it, one with a 3-minute override
            await google.SetDefaultRemindersAsync(calendarId, 7, ct);
            var start      = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(15), TimeSpan.Zero);
            var onDefault  = await google.InsertEventWithRemindersAsync(calendarId, start, new JsonObject { ["useDefault"] = true }, ct);
            var overridden = await google.InsertEventWithRemindersAsync(calendarId, start.AddHours(2), new JsonObject
            {
                ["useDefault"] = false,
                ["overrides"]  = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = 3 }),
            }, ct);

            // Sync, And Show The Test Calendar In Leaf
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            using var conn = live.Database.Open();
            CalendarStore.SetHidden(conn, live.AccountId, calendarId, false);

            var alerts = AlertPlanner.Plan(conn, start.AddHours(-1), start.AddHours(3), TimeZoneInfo.Utc)
                .Where(a => a.Occurrence.CalendarId == calendarId)
                .ToList();

            Assert.Contains(alerts, a => a.Occurrence.EventId == onDefault && a.FireAt == start.AddMinutes(-7));
            Assert.Contains(alerts, a => a.Occurrence.EventId == overridden && a.FireAt == start.AddHours(2).AddMinutes(-3));
            Assert.DoesNotContain(alerts, a => a.Occurrence.EventId == overridden && a.MinutesBefore == 7);
        }
        finally
        {
            await google.DeleteCalendarAsync(calendarId, CancellationToken.None);
        }
    }
}
```

Run: `dotnet build tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj`
Expected: builds with 0 warnings. (The live test itself runs in Task 17 with the live account.)

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.Core/Alerts/AlertPlanner.cs src/LeafCalendar.Core/Events/CalendarOccurrence.cs tests/LeafCalendar.Tests/AlertPlannerTests.cs tests/LeafCalendar.LiveTests
git commit -m "feat(core): plan reminders and Join now from Google's reminder settings"
```

---

### Task 4: Alert Scheduler and the Test Clock

**Files:**
- Create: `src/LeafCalendar.Core/Alerts/AlertScheduler.cs`, `src/LeafCalendar.Core/Hosting/ShiftedTimeProvider.cs`
- Modify: `src/LeafCalendar.Core/Hosting/LaunchOptions.cs`
- Test: `tests/LeafCalendar.Tests/AlertSchedulerTests.cs`, `tests/LeafCalendar.Tests/ShiftedTimeProviderTests.cs`, `tests/LeafCalendar.Tests/LaunchOptionsTests.cs` (add)

**Interfaces:**
- Consumes: `AlertPlanner.Plan`, `Alert` (Task 3), `AlertLedger` (Task 2), `CalendarOccurrence.EndIn` (Task 3), `LeafDatabase.Open()`.
- Produces:
  - `sealed class AlertScheduler(LeafDatabase database, TimeProvider time, Func<TimeZoneInfo> zone) : IDisposable`:
    - `static readonly TimeSpan Tick` (15 s), `LookBack` (1 h), `SyncLead` (1 min)
    - `Func<AlertKind, bool> IsEnabled { get; set; }` (default: all on)
    - `event EventHandler<Alert>? AlertDue`, `event EventHandler<string>? AlertRetracted` (the tag), `event EventHandler? SyncSoon`, `event EventHandler<Exception>? Failed`
    - `void Start()`, `void Invalidate()` (re-plan on the next pass, which runs right away once started), `void Check()` (one pass; public for tests), `void Dispose()`
  - `sealed class ShiftedTimeProvider(TimeProvider inner, DateTimeOffset start) : TimeProvider` (namespace `LeafCalendar.Core.Hosting`)
  - `LaunchOptions` gains `DateTimeOffset? Now = null` (from `--now`, fake Google only)

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/AlertSchedulerTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class AlertSchedulerTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    // A Meet call 18:00-19:00Z on Oct 1; the primary calendar's default reminder is 10 minutes (17:50Z)
    const string Meeting = """
        {"id":"evt-meet","status":"confirmed","summary":"Design review","hangoutLink":"https://meet.google.com/abc-defg-hij",
         "start":{"dateTime":"2026-10-01T18:00:00Z"},"end":{"dateTime":"2026-10-01T19:00:00Z"}}
        """;

    readonly TestDatabase _db = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero));
    readonly List<Alert> _due = [];
    readonly List<string> _retracted = [];
    readonly List<AlertScheduler> _schedulers = [];
    int _syncSoon;

    public AlertSchedulerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        Store(Meeting);
    }

    public void Dispose()
    {
        foreach (var scheduler in _schedulers)
        {
            scheduler.Dispose();
        }

        _db.Dispose();
    }

    void Store(string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, Primary, json);
    }

    AlertScheduler Scheduler()
    {
        var scheduler = new AlertScheduler(_db.Database, _time, () => TimeZoneInfo.Utc);
        scheduler.AlertDue       += (_, alert) => _due.Add(alert);
        scheduler.AlertRetracted += (_, tag) => _retracted.Add(tag);
        scheduler.SyncSoon       += (_, _) => _syncSoon++;
        scheduler.Failed         += (_, ex) => throw new InvalidOperationException("Scheduler failed", ex);
        _schedulers.Add(scheduler);
        return scheduler;
    }

    void At(int hour, int minute, int second = 0) => _time.SetUtcNow(new DateTimeOffset(2026, 10, 1, hour, minute, second, TimeSpan.Zero));

    [Fact]
    public void Check_AtReminderTime_RaisesTheReminderOnce()
    {
        var scheduler = Scheduler();
        At(17, 49, 50);
        scheduler.Check();
        Assert.Empty(_due);

        At(17, 50, 5);
        scheduler.Check();
        scheduler.Check();

        var alert = Assert.Single(_due);
        Assert.Equal(AlertKind.Reminder, alert.Kind);
        Assert.NotNull(alert.MeetingLink);
    }

    [Fact]
    public void Check_NewSchedulerAfterRestart_DoesNotRepeat()
    {
        var first = Scheduler();
        At(17, 45);
        first.Check();
        At(17, 50, 5);
        first.Check();
        Assert.Single(_due);
        first.Dispose();

        // Restart A Minute Later: the one-hour look-back sees the 17:50 reminder, and the ledger stops it
        At(17, 51);
        Scheduler().Check();

        Assert.Single(_due);
    }

    [Fact]
    public void Check_AtStart_RaisesJoinNowWithTheLink()
    {
        var scheduler = Scheduler();
        At(17, 55);
        scheduler.Check();

        At(18, 0, 5);
        scheduler.Check();

        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);
        Assert.Equal("https://meet.google.com/abc-defg-hij", join.MeetingLink!.AbsoluteUri);
    }

    [Fact]
    public void Check_WokeAfterMeetingEnded_SkipsIt()
    {
        var scheduler = Scheduler();
        At(17, 40);
        scheduler.Check();

        // Asleep From 17:40 To 19:30: the reminder and the join both came due while the meeting was on
        At(19, 30);
        scheduler.Check();

        Assert.Empty(_due);
    }

    [Fact]
    public void Check_WokeDuringMeeting_ShowsOnlyJoinNow()
    {
        var scheduler = Scheduler();
        At(17, 40);
        scheduler.Check();

        At(18, 20);
        scheduler.Check();

        var alert = Assert.Single(_due);
        Assert.Equal(AlertKind.JoinNow, alert.Kind);
    }

    [Fact]
    public void Check_StartedDuringMeeting_ShowsJoinNow()
    {
        At(18, 5);
        Scheduler().Check();

        Assert.Equal(AlertKind.JoinNow, Assert.Single(_due).Kind);
    }

    [Fact]
    public void Check_JoinNowTurnedOff_StillShowsTheReminderThatCameDue()
    {
        var scheduler = Scheduler();
        scheduler.IsEnabled = kind => kind != AlertKind.JoinNow;
        At(17, 40);
        scheduler.Check();

        At(18, 20);
        scheduler.Check();

        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }

    [Fact]
    public void Check_KindTurnedOff_NothingShows()
    {
        var scheduler = Scheduler();
        scheduler.IsEnabled = kind => kind == AlertKind.Invite;
        At(17, 45);
        scheduler.Check();

        At(18, 1);
        scheduler.Check();

        Assert.Empty(_due);
    }

    [Fact]
    public void Check_OneMinuteBefore_AsksForOneSync()
    {
        var scheduler = Scheduler();
        At(17, 49, 5);
        scheduler.Check();
        Assert.Equal(1, _syncSoon);

        At(17, 49, 20);
        scheduler.Check();
        Assert.Equal(1, _syncSoon);
    }

    [Fact]
    public void Check_MeetingMovedAfterJoinNow_RetractsItsToast()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();
        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);

        // Moved To 20:00 On Google
        Store(Meeting.Replace("T18:00", "T20:00", StringComparison.Ordinal).Replace("T19:00", "T21:00", StringComparison.Ordinal));
        scheduler.Invalidate();
        scheduler.Check();

        Assert.Equal([join.Tag], _retracted);
    }

    [Fact]
    public void Check_MeetingDeclinedAfterJoinNow_RetractsItsToast()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        Store(Meeting.Replace("\"hangoutLink\"", "\"attendees\":[{\"email\":\"leaf.tester@gmail.com\",\"self\":true,\"responseStatus\":\"declined\"}],\"hangoutLink\"", StringComparison.Ordinal));
        scheduler.Invalidate();
        scheduler.Check();

        Assert.Single(_retracted);
    }

    [Fact]
    public void Check_MeetingEnded_RetractsJoinNowOnce()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        At(19, 0, 5);
        scheduler.Check();
        scheduler.Check();

        Assert.Single(_retracted);
    }

    [Fact]
    public void Start_TimerTicks_RaiseOnTheirOwn()
    {
        At(17, 49, 50);
        Scheduler().Start();

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }
}
```

`tests/LeafCalendar.Tests/ShiftedTimeProviderTests.cs`:
```csharp
using LeafCalendar.Core.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class ShiftedTimeProviderTests
{
    [Fact]
    public void GetUtcNow_StartsAtTheGivenInstantAndRunsOn()
    {
        var real    = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var start   = new DateTimeOffset(2026, 10, 1, 13, 55, 0, TimeSpan.FromHours(-4));
        var shifted = new ShiftedTimeProvider(real, start);

        Assert.Equal(start, shifted.GetUtcNow());

        real.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(start.AddMinutes(5), shifted.GetUtcNow());
    }

    [Fact]
    public void CreateTimer_UsesTheRealClocksTimers()
    {
        var real    = new FakeTimeProvider();
        var shifted = new ShiftedTimeProvider(real, DateTimeOffset.UnixEpoch);
        var ticks   = 0;
        using var timer = shifted.CreateTimer(_ => ticks++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        real.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, ticks);
    }
}
```

Add to `tests/LeafCalendar.Tests/LaunchOptionsTests.cs`:
```csharp
    [Fact]
    public void Parse_NowWithFakeGoogle_IsKept()
    {
        var options = LaunchOptions.Parse(["--profile", "uitest-a", "--fake-google", "http://127.0.0.1:5000/", "--now", "2026-10-01T13:55:00-04:00"]);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 55, 0, TimeSpan.FromHours(-4)), options.Now);
    }

    [Theory]
    [InlineData("--profile", "default")]
    [InlineData("--profile", "uitest-a")]
    public void Parse_NowWithoutFakeGoogle_IsIgnored(string flag, string profile)
    {
        Assert.Null(LaunchOptions.Parse([flag, profile, "--now", "2026-10-01T13:55:00-04:00"]).Now);
    }

    [Theory]
    [InlineData("2026-10-01T13:55:00")]
    [InlineData("tomorrow")]
    [InlineData("")]
    public void Parse_NowWithoutAnOffset_IsIgnored(string value)
    {
        Assert.Null(LaunchOptions.Parse(["--profile", "uitest-a", "--fake-google", "http://127.0.0.1:5000/", "--now", value]).Now);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*AlertSchedulerTests" --filter-class "*ShiftedTimeProviderTests" --filter-class "*LaunchOptionsTests"`
Expected: build FAIL — `AlertScheduler`, `ShiftedTimeProvider`, and `LaunchOptions.Now` don't exist.

- [ ] **Step 3: Write `AlertScheduler`**

`src/LeafCalendar.Core/Alerts/AlertScheduler.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>
/// Decides, every 15 seconds on <see cref="TimeProvider"/>, which notifications are due (spec 8.4).
/// </summary>
/// <remarks>
/// <para>
/// Each pass looks at what came due in the last hour, so an alert missed while the PC slept, while Leaf wasn't running,
/// or before a sync brought the event still shows, as long as its meeting isn't over. Per event instance only the latest
/// alert that came due counts, so waking in the middle of a meeting shows one "Join now" and not three stale reminders.
/// Every shown alert goes into <see cref="AlertLedger"/> first, so the next pass, a restart, or a full resync never
/// shows it again.
/// </para>
/// <para>
/// A "Join now" stays on screen until clicked, so once its meeting ends, moves, is declined, or is deleted, the pass
/// raises <see cref="AlertRetracted"/> with its tag. A minute before any alert, <see cref="SyncSoon"/> asks for a sync,
/// so a last-minute change on Google is caught first (spec 5.3).
/// </para>
/// <para>
/// The plan (a day back to a day ahead) is cached and rebuilt after <see cref="Invalidate"/> (data changed) or when it
/// runs out. Events are raised outside the lock, on the timer's thread or the caller's.
/// </para>
/// </remarks>
public sealed class AlertScheduler(LeafDatabase database, TimeProvider time, Func<TimeZoneInfo> zone) : IDisposable
{
    /// <summary>How often a pass runs.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>How far each pass looks back for alerts that came due.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromHours(1);

    /// <summary>How long before an alert the sync is asked for.</summary>
    public static readonly TimeSpan SyncLead = TimeSpan.FromMinutes(1);

    static readonly TimeSpan PlanSpan   = TimeSpan.FromDays(1);
    static readonly TimeSpan LedgerKeep = TimeSpan.FromDays(2);

    readonly Lock _gate = new();
    ITimer? _timer;
    IReadOnlyList<Alert>? _plan;
    DateTimeOffset _planTo;
    DateTimeOffset _syncedUntil;
    bool _disposed;

    /// <summary>Whether a kind may show (the Notifications settings). Read on every pass.</summary>
    public Func<AlertKind, bool> IsEnabled { get; set; } = _ => true;

    /// <summary>An alert is due; show it.</summary>
    public event EventHandler<Alert>? AlertDue;

    /// <summary>A shown "Join now" no longer applies; the argument is its tag.</summary>
    public event EventHandler<string>? AlertRetracted;

    /// <summary>An alert fires within a minute; sync now.</summary>
    public event EventHandler? SyncSoon;

    /// <summary>A timer pass failed (database or data error); the next pass tries again.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Starts the 15-second passes, the first one right away (no-op once started or disposed).</summary>
    public void Start()
    {
        // The timer is created stopped and started outside the lock, since a test clock may run its first pass at once
        ITimer timer;
        lock (_gate)
        {
            if (_timer is not null || _disposed)
            {
                return;
            }

            _timer = timer = time.CreateTimer(_ => SafeCheck(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        timer.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>Events changed: the next pass re-plans, and runs right away when the timer is going.</summary>
    public void Invalidate()
    {
        ITimer? timer;
        lock (_gate)
        {
            _plan = null;
            timer = _timer;
        }

        try
        {
            timer?.Change(TimeSpan.Zero, Tick);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile (Quit); nothing left to plan
        }
    }

    /// <summary>Runs one pass now.</summary>
    public void Check()
    {
        List<Alert> due;
        List<string> retracted;
        bool syncSoon;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now  = time.GetUtcNow();
            var tz   = zone();
            var from = now - LookBack;

            using var conn = database.Open();

            // Plan (a day each side, so a long meeting's "Join now" is still known while it runs)
            if (_plan is null || now + SyncLead + Tick > _planTo)
            {
                _planTo = now + PlanSpan;
                _plan   = AlertPlanner.Plan(conn, now - PlanSpan, _planTo, tz);
                AlertLedger.Prune(conn, now - LedgerKeep);
            }

            due       = Due(conn, _plan, from, now, tz);
            retracted = Retract(conn, _plan, now, tz);
            syncSoon  = SyncDue(_plan, now);
        }

        // Raised Outside The Lock (handlers show notifications)
        foreach (var alert in due)
        {
            AlertDue?.Invoke(this, alert);
        }

        foreach (var tag in retracted)
        {
            AlertRetracted?.Invoke(this, tag);
        }

        if (syncSoon)
        {
            SyncSoon?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // A timer callback must never throw (it would end the process), so failures are reported and the next pass retries
    void SafeCheck()
    {
        try
        {
            Check();
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException or IOException)
        {
            Failed?.Invoke(this, ex);
        }
    }

    // Per instance, the latest enabled alert that came due, unless the instance is over or it was shown before
    List<Alert> Due(SqliteConnection conn, IReadOnlyList<Alert> plan, DateTimeOffset from, DateTimeOffset now, TimeZoneInfo tz)
    {
        var result = new List<Alert>();
        foreach (var group in plan.Where(a => a.FireAt > from && a.FireAt <= now && IsEnabled(a.Kind)).GroupBy(a => a.Occurrence.Key, StringComparer.Ordinal))
        {
            var latest = group.OrderByDescending(a => a.FireAt).ThenByDescending(a => a.Kind).First();
            if (IsOver(latest.Occurrence, now, tz))
            {
                continue;
            }

            if (AlertLedger.TryAdd(conn, latest.Key, latest.Kind, latest.Tag, latest.Occurrence.EndIn(tz), now))
            {
                result.Add(latest);
            }
        }

        return result;
    }

    // A shown "Join now" that isn't in the plan as a running meeting any more (ended, moved, declined, deleted)
    static List<string> Retract(SqliteConnection conn, IReadOnlyList<Alert> plan, DateTimeOffset now, TimeZoneInfo tz)
    {
        var running = plan
            .Where(a => a.Kind == AlertKind.JoinNow && a.FireAt <= now && !IsOver(a.Occurrence, now, tz))
            .Select(a => a.Key)
            .ToHashSet(StringComparer.Ordinal);

        var tags = new List<string>();
        foreach (var entry in AlertLedger.OpenJoinNow(conn))
        {
            if (!running.Contains(entry.Key))
            {
                AlertLedger.MarkRetracted(conn, entry.Key);
                tags.Add(entry.Tag);
            }
        }

        return tags;
    }

    // Once per alert: something enabled fires between the last look-ahead and a minute from now
    bool SyncDue(IReadOnlyList<Alert> plan, DateTimeOffset now)
    {
        var ahead = now + SyncLead;
        var after = _syncedUntil > now ? _syncedUntil : now;
        var soon  = plan.Any(a => a.FireAt > after && a.FireAt <= ahead && IsEnabled(a.Kind));
        _syncedUntil = ahead;
        return soon;
    }

    // Over once it has ended (a zero-length event counts as a one-minute one)
    static bool IsOver(CalendarOccurrence o, DateTimeOffset now, TimeZoneInfo tz)
    {
        var start = o.StartIn(tz);
        var end   = o.EndIn(tz);
        return now >= (end > start ? end : start + TimeSpan.FromMinutes(1));
    }
}
```

- [ ] **Step 4: Write `ShiftedTimeProvider` and `--now`**

`src/LeafCalendar.Core/Hosting/ShiftedTimeProvider.cs`:
```csharp
namespace LeafCalendar.Core.Hosting;

/// <summary>
/// A clock that starts at a chosen instant and then runs at real speed (UI tests' <c>--now</c>), so reminders, the
/// flyout, and the join shortcut can be tested against the fixtures' meetings. Timers are the real clock's.
/// </summary>
public sealed class ShiftedTimeProvider : TimeProvider
{
    readonly TimeProvider _inner;
    readonly DateTimeOffset _start;
    readonly long _origin;

    /// <summary>Starts at <paramref name="start"/> now.</summary>
    public ShiftedTimeProvider(TimeProvider inner, DateTimeOffset start)
    {
        _inner  = inner;
        _start  = start;
        _origin = inner.GetTimestamp();
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => (_start + _inner.GetElapsedTime(_origin)).ToUniversalTime();

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    /// <inheritdoc />
    public override long TimestampFrequency => _inner.TimestampFrequency;

    /// <inheritdoc />
    public override long GetTimestamp() => _inner.GetTimestamp();

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);
}
```

In `src/LeafCalendar.Core/Hosting/LaunchOptions.cs`:

Add to the remarks list:
```csharp
/// <item><c>--now &lt;instant&gt;</c> starts Leaf's clock at an ISO 8601 instant with an offset (<c>2026-10-01T13:55:00-04:00</c>). It's honored only with <c>--fake-google</c>.</item>
```

Change the record declaration to:
```csharp
public sealed record LaunchOptions(string Profile, bool TrayProbe, Uri? FakeGoogle = null, DateOnly? StartDate = null, DateTimeOffset? Now = null)
```

In `Parse`, add `DateTimeOffset? now = null;` under `DateOnly? date = null;` (aligned), add the case:
```csharp
                case "--now" when i + 1 < args.Count:
                    now = ParseInstant(args[++i]);
                    break;
```
and change the return to:
```csharp
        return new LaunchOptions(profile, trayProbe, fake, fake is null ? null : date, fake is null ? null : now);
```

Add the helper after `ParseLoopback`:
```csharp
    // An instant with an explicit offset only, so the test clock never depends on the PC's zone
    static DateTimeOffset? ParseInstant(string value) =>
        DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant
            : null;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*AlertSchedulerTests" --filter-class "*ShiftedTimeProviderTests" --filter-class "*LaunchOptionsTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Alerts/AlertScheduler.cs src/LeafCalendar.Core/Hosting tests/LeafCalendar.Tests/AlertSchedulerTests.cs tests/LeafCalendar.Tests/ShiftedTimeProviderTests.cs tests/LeafCalendar.Tests/LaunchOptionsTests.cs
git commit -m "feat(core): alert scheduler with sleep, restart, and stale Join now handling; --now test clock"
```

---

### Task 5: Join Picker

**Files:**
- Create: `src/LeafCalendar.Core/Alerts/JoinPicker.cs`
- Test: `tests/LeafCalendar.Tests/JoinPickerTests.cs`

**Interfaces:**
- Consumes: `OccurrenceQuery.Load`, `EventStore.Get`, `EventDetailsParser.Parse`, `AccountStore.GetAll`, `LinkSafety.JoinUri(Uri, string)`.
- Produces (namespace `LeafCalendar.Core.Alerts`):
  - `sealed record JoinTarget(CalendarOccurrence Occurrence, Uri Link)`
  - `static class JoinPicker`:
    - `static readonly TimeSpan Lead` (10 minutes)
    - `JoinTarget? Pick(IEnumerable<JoinTarget> candidates, DateTimeOffset now)` (pure)
    - `IReadOnlyList<JoinTarget> Candidates(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)`
    - `Uri? MeetingLink(SqliteConnection conn, CalendarOccurrence occurrence)` (the event's own link, or null)
    - `Uri JoinLink(SqliteConnection conn, JoinTarget target)` (Meet gets `authuser=<the event's account email>`)
    - `Uri? Find(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)` (all of the above; null = "No meeting to join")

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/JoinPickerTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class JoinPickerTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string SecondId = "222222222222";
    const string Second = "second@example.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly DateTimeOffset Now = new(2026, 10, 1, 17, 58, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public JoinPickerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        AccountStore.Upsert(conn, new Account(SecondId, Second, "Second", null, AccountStatus.Ok));
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        CalendarStore.ReplaceForAccount(conn, SecondId, [new CalendarListEntry { Id = Second, Summary = Second, AccessRole = "owner", Primary = true, Selected = true }]);
    }

    public void Dispose() => _db.Dispose();

    void Store(string account, string calendar, string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, account, calendar, json);
    }

    Uri? Find(DateTimeOffset? now = null)
    {
        using var conn = _db.Database.Open();
        return JoinPicker.Find(conn, now ?? Now, TimeZoneInfo.Utc);
    }

    static JoinTarget Target(string id, DateTimeOffset start, int minutes = 30, bool allDay = false) =>
        new(new CalendarOccurrence("a", "cal", id, null, null, start, start.AddMinutes(minutes), allDay, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, true),
            new Uri("https://meet.google.com/" + id));

    static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Pick_TwoUpcoming_TakesTheSoonest()
    {
        var picked = JoinPicker.Pick([Target("later", At(18, 6)), Target("sooner", At(18, 3))], Now);

        Assert.Equal("sooner", picked!.Occurrence.EventId);
    }

    [Fact]
    public void Pick_ExactlyTenMinutesOut_Qualifies()
    {
        Assert.NotNull(JoinPicker.Pick([Target("ten", Now.AddMinutes(10))], Now));
        Assert.Null(JoinPicker.Pick([Target("eleven", Now.AddMinutes(11))], Now));
    }

    [Fact]
    public void Pick_UpcomingBeatsInProgress()
    {
        var picked = JoinPicker.Pick([Target("running", At(17, 30), 60), Target("next", At(18, 5))], Now);

        Assert.Equal("next", picked!.Occurrence.EventId);
    }

    [Fact]
    public void Pick_OnlyInProgress_TakesTheOneThatStartedLast()
    {
        var picked = JoinPicker.Pick([Target("long", At(17, 0), 120), Target("recent", At(17, 45), 30)], Now);

        Assert.Equal("recent", picked!.Occurrence.EventId);
    }

    [Fact]
    public void Pick_EndedOrAllDay_NeverQualify()
    {
        Assert.Null(JoinPicker.Pick([Target("over", At(17, 0), 58), Target("allday", At(0, 0), 1440, allDay: true)], Now));
    }

    [Fact]
    public void Find_MeetingsInTwoAccounts_JoinsTheSoonestWithItsOwnAccount()
    {
        Store(Account, Primary, """{"id":"evt-a","status":"confirmed","summary":"A","hangoutLink":"https://meet.google.com/aaa-aaaa-aaa","start":{"dateTime":"2026-10-01T17:30:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");
        Store(SecondId, Second, """{"id":"evt-b","status":"confirmed","summary":"B","hangoutLink":"https://meet.google.com/bbb-bbbb-bbb","start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Equal("https://meet.google.com/bbb-bbbb-bbb?authuser=second%40example.com", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_OnlyTheRunningMeeting_JoinsItWithItsAccount()
    {
        Store(Account, Primary, """{"id":"evt-a","status":"confirmed","summary":"A","hangoutLink":"https://meet.google.com/aaa-aaaa-aaa","start":{"dateTime":"2026-10-01T17:30:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Equal("https://meet.google.com/aaa-aaaa-aaa?authuser=leaf.tester%40gmail.com", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_DeclinedMeeting_Skipped()
    {
        Store(Account, Primary, """
            {"id":"evt-no","status":"confirmed","summary":"No","hangoutLink":"https://meet.google.com/ccc-cccc-ccc",
             "attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],
             "start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.Null(Find());
    }

    [Fact]
    public void Find_ZoomLinkInTheDescription_OpensAsIs()
    {
        Store(Account, Primary, """
            {"id":"evt-zoom","status":"confirmed","summary":"Zoom","description":"Join: https://example.zoom.us/j/123456789",
             "start":{"dateTime":"2026-10-01T18:05:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}
            """);

        Assert.Equal("https://example.zoom.us/j/123456789", Find()!.AbsoluteUri);
    }

    [Fact]
    public void Find_NoMeetingWithALink_ReturnsNull()
    {
        Store(Account, Primary, """{"id":"evt-plain","status":"confirmed","summary":"Plain","start":{"dateTime":"2026-10-01T18:03:00Z"},"end":{"dateTime":"2026-10-01T18:30:00Z"}}""");

        Assert.Null(Find());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*JoinPickerTests"`
Expected: build FAIL — `JoinPicker` and `JoinTarget` don't exist.

- [ ] **Step 3: Write `JoinPicker`**

`src/LeafCalendar.Core/Alerts/JoinPicker.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>A meeting that could be joined and its own meeting link.</summary>
public sealed record JoinTarget(CalendarOccurrence Occurrence, Uri Link);

/// <summary>
/// Picks the meeting the join shortcut, the tray menu, and notifications open (spec 8.5).
/// </summary>
/// <remarks>
/// A meeting qualifies when it's timed, has a meeting link (Google's conference data, or a known meeting host pasted in
/// the location or description), you haven't declined it, and it starts within <see cref="Lead"/> or is running. The
/// soonest-starting one that hasn't started wins; with none, the running one that started most recently. Google Meet
/// links get <c>authuser=&lt;email&gt;</c> of the account the event came from, so the right Google account joins.
/// </remarks>
public static class JoinPicker
{
    /// <summary>How soon a meeting must start to qualify.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);

    /// <summary>The meeting to join from <paramref name="candidates"/>, or null.</summary>
    public static JoinTarget? Pick(IEnumerable<JoinTarget> candidates, DateTimeOffset now)
    {
        var open = candidates.Where(c => !c.Occurrence.IsAllDay && c.Occurrence.End > now).ToList();

        // Soonest Upcoming Within The Lead, Else The Latest-Started Running One
        return open.Where(c => c.Occurrence.Start > now && c.Occurrence.Start - now <= Lead).MinBy(c => c.Occurrence.Start)
            ?? open.Where(c => c.Occurrence.Start <= now).MaxBy(c => c.Occurrence.Start);
    }

    /// <summary>Timed, not declined instances around <paramref name="now"/> that have a meeting link and could qualify.</summary>
    public static IReadOnlyList<JoinTarget> Candidates(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today  = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var result = new List<JoinTarget>();
        foreach (var o in OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(2), zone, includeDeclined: false))
        {
            if (o.IsAllDay || o.End <= now || o.Start - now > Lead)
            {
                continue;
            }

            if (MeetingLink(conn, o) is { } link)
            {
                result.Add(new JoinTarget(o, link));
            }
        }

        return result;
    }

    /// <summary>The instance's meeting link as the event has it (descriptions included), or null.</summary>
    public static Uri? MeetingLink(SqliteConnection conn, CalendarOccurrence occurrence)
    {
        if (EventStore.Get(conn, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId) is not { } stored)
        {
            return null;
        }

        try
        {
            return EventDetailsParser.Parse(stored.RawJson).ConferenceUri;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The address to open: Meet links with the event's account as <c>authuser</c>, others as they are.</summary>
    public static Uri JoinLink(SqliteConnection conn, JoinTarget target)
    {
        var email = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == target.Occurrence.AccountId)?.Email ?? "";
        return LinkSafety.JoinUri(target.Link, email);
    }

    /// <summary>The address the join shortcut opens now, or null when no meeting qualifies ("No meeting to join").</summary>
    public static Uri? Find(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone) =>
        Pick(Candidates(conn, now, zone), now) is { } target ? JoinLink(conn, target) : null;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*JoinPickerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Alerts/JoinPicker.cs tests/LeafCalendar.Tests/JoinPickerTests.cs
git commit -m "feat(core): join picker with the 10-minute rule and authuser routing"
```

---

### Task 6: Tray Agenda and Tooltip Text

**Files:**
- Create: `src/LeafCalendar.Core/Tray/DisplayText.cs`, `src/LeafCalendar.Core/Tray/TrayAgenda.cs`
- Test: `tests/LeafCalendar.Tests/TrayAgendaTests.cs`

**Interfaces:**
- Consumes: `OccurrenceQuery.Load`, `CalendarOccurrence.StartIn/EndIn` (Task 3), `JoinPicker.MeetingLink`, `JoinPicker.Lead` (Task 5), `TimeLabels.Range`, `TimeLabels.LongDate`, `TimeLabels.Relative`.
- Produces (namespace `LeafCalendar.Core.Tray`):
  - `static class DisplayText`: `string Clean(string? text, int max)` (control characters to spaces, trimmed, clipped with "…" without splitting a surrogate pair)
  - `sealed record AgendaItem(CalendarOccurrence Occurrence, string Title, string When, Uri? Link)`
  - `sealed record AgendaDay(DateOnly Date, string Header, IReadOnlyList<AgendaItem> Items)`
  - `sealed record NextUp(AgendaItem Item, string Countdown)`
  - `static class TrayAgenda`:
    - `const int MaxTooltip = 127`
    - `IReadOnlyList<AgendaDay> Load(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone, int days, bool includeAllDay, bool use24Hour)`
    - `NextUp? Next(IReadOnlyList<AgendaDay> days, DateTimeOffset now, TimeSpan lookahead)`
    - `string Tooltip(NextUp? next)` ("Standup in 12 min", "Standup now", or "Leaf Calendar")
    - `string NothingNext(int lookaheadMinutes)` ("Nothing in the next hour.")
    - `string DayHeader(DateOnly day, DateOnly today)` ("Today", "Tomorrow", "Saturday, October 3")

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/TrayAgendaTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
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
        Store($$"""{"id":"evt-long","status":"confirmed","summary":"{{title}}","start":{"dateTime":"2026-10-01T08:10:00-04:00"},"end":{"dateTime":"2026-10-01T08:20:00-04:00"}}""");

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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*TrayAgendaTests"`
Expected: build FAIL — `TrayAgenda`, `DisplayText`, `AgendaDay`, and `NextUp` don't exist.

- [ ] **Step 3: Write `DisplayText`**

`src/LeafCalendar.Core/Tray/DisplayText.cs`:
```csharp
namespace LeafCalendar.Core.Tray;

/// <summary>Event text made safe for one-line places: the tray tooltip, flyout rows, and notifications.</summary>
public static class DisplayText
{
    /// <summary>
    /// Control characters (line breaks, tabs) become spaces, the ends are trimmed, and text longer than
    /// <paramref name="max"/> is clipped with "…", never between the two halves of an emoji.
    /// </summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return "";
        }

        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length <= max)
        {
            return clean;
        }

        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(clean[cut - 1]))
        {
            cut--;
        }

        return clean[..cut].TrimEnd() + "…";
    }
}
```

- [ ] **Step 4: Write `TrayAgenda`**

`src/LeafCalendar.Core/Tray/TrayAgenda.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Tray;

/// <summary>One flyout row: the instance, its one-line title, "9 AM – 10 AM" (or "All day"), and its meeting link.</summary>
public sealed record AgendaItem(CalendarOccurrence Occurrence, string Title, string When, Uri? Link);

/// <summary>One day of the flyout's agenda, headed "Today", "Tomorrow", or the date.</summary>
public sealed record AgendaDay(DateOnly Date, string Header, IReadOnlyList<AgendaItem> Items);

/// <summary>The flyout header's and the tooltip's next event, with "in 12 min" or "Now".</summary>
public sealed record NextUp(AgendaItem Item, string Countdown);

/// <summary>
/// What the tray shows: the flyout's agenda by day, its "next event" header, and the icon's tooltip (spec 8.1, 8.2).
/// </summary>
/// <remarks>
/// Days start today and skip empty ones. Events that already ended leave today; one still running since yesterday
/// shows under today. All-day events come first, and only when asked for. Declined events and hidden calendars are left
/// out, like everywhere else. The next event is the soonest one starting within the lookahead, except that a running
/// meeting stays in the header until the next one is within 10 minutes of starting (the join rule's window).
/// </remarks>
public static class TrayAgenda
{
    /// <summary>The longest tooltip the shell shows (<c>NOTIFYICONDATA.szTip</c> is 128 characters with the terminator).</summary>
    public const int MaxTooltip = 127;

    const int MaxTitle = 200;

    /// <summary>The agenda for <paramref name="days"/> days from today (local to <paramref name="zone"/>).</summary>
    public static IReadOnlyList<AgendaDay> Load(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone, int days, bool includeAllDay, bool use24Hour)
    {
        var today       = LocalDate(now, zone);
        var occurrences = OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(days), zone, includeDeclined: false);
        var links       = new Dictionary<(string, string, string), Uri?>();
        var result      = new List<AgendaDay>();

        for (var i = 0; i < days; i++)
        {
            var date  = today.AddDays(i);
            var items = occurrences
                .Where(o => o.Kind != EventKind.WorkingLocation && (includeAllDay || !o.IsAllDay) && o.EndIn(zone) > now && IsOn(o, date, today, now, zone))
                .OrderBy(o => !o.IsAllDay)
                .ThenBy(o => o.Start)
                .Select(o => new AgendaItem(
                    o,
                    DisplayText.Clean(o.Title, MaxTitle),
                    o.IsAllDay ? "All day" : TimeLabels.Range(o.Start, o.End, zone, use24Hour),
                    o.IsAllDay ? null : Link(conn, links, o)))
                .ToList();

            if (items.Count > 0)
            {
                result.Add(new AgendaDay(date, DayHeader(date, today), items));
            }
        }

        return result;
    }

    /// <summary>The header's event within <paramref name="lookahead"/>, or null.</summary>
    public static NextUp? Next(IReadOnlyList<AgendaDay> days, DateTimeOffset now, TimeSpan lookahead)
    {
        var timed = days
            .SelectMany(d => d.Items)
            .Where(i => !i.Occurrence.IsAllDay && i.Occurrence.End > now)
            .DistinctBy(i => i.Occurrence.Key)
            .ToList();

        var upcoming = timed.Where(i => i.Occurrence.Start > now && i.Occurrence.Start - now <= lookahead).MinBy(i => i.Occurrence.Start);
        var running  = timed.Where(i => i.Occurrence.Start <= now).MaxBy(i => i.Occurrence.Start);
        var pick     = upcoming is not null && (running is null || upcoming.Occurrence.Start - now <= JoinPicker.Lead) ? upcoming : running ?? upcoming;

        return pick is null ? null : new NextUp(pick, TimeLabels.Relative(pick.Occurrence.Start, pick.Occurrence.End, now));
    }

    /// <summary>"Standup in 12 min", "Standup now", or "Leaf Calendar" when nothing is coming up; at most 127 characters.</summary>
    public static string Tooltip(NextUp? next)
    {
        if (next is null)
        {
            return "Leaf Calendar";
        }

        var suffix = " " + (next.Countdown == "Now" ? "now" : next.Countdown);
        return DisplayText.Clean(next.Item.Title, MaxTooltip - suffix.Length) + suffix;
    }

    /// <summary>The header's empty sentence for a lookahead: "Nothing in the next hour."</summary>
    public static string NothingNext(int lookaheadMinutes) => lookaheadMinutes switch
    {
        60   => "Nothing in the next hour.",
        < 60 => string.Create(CultureInfo.InvariantCulture, $"Nothing in the next {lookaheadMinutes} minutes."),
        _    => string.Create(CultureInfo.InvariantCulture, $"Nothing in the next {lookaheadMinutes / 60} hours."),
    };

    /// <summary>"Today", "Tomorrow", or "Saturday, October 3".</summary>
    public static string DayHeader(DateOnly day, DateOnly today) =>
        day == today ? "Today" : day == today.AddDays(1) ? "Tomorrow" : TimeLabels.LongDate(day);

    static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // All-day: every date it covers. Timed: the day it starts, or today when it's been running since before today.
    static bool IsOn(CalendarOccurrence o, DateOnly date, DateOnly today, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (o.IsAllDay)
        {
            return date >= o.AllDayStart && date < o.AllDayEnd;
        }

        var startDay = LocalDate(o.Start, zone);
        return startDay == date || (date == today && startDay < today && o.Start <= now);
    }

    // One lookup per stored row (a series' instances share their master's link)
    static Uri? Link(SqliteConnection conn, Dictionary<(string, string, string), Uri?> links, CalendarOccurrence o)
    {
        var key = (o.AccountId, o.CalendarId, o.EventId);
        if (!links.TryGetValue(key, out var link))
        {
            links[key] = link = JoinPicker.MeetingLink(conn, o);
        }

        return link;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*TrayAgendaTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Tray/DisplayText.cs src/LeafCalendar.Core/Tray/TrayAgenda.cs tests/LeafCalendar.Tests/TrayAgendaTests.cs
git commit -m "feat(core): tray agenda, next event, and tooltip text"
```

---

### Task 7: Notification Content and Arguments

**Files:**
- Create: `src/LeafCalendar.Core/Alerts/ToastArgs.cs`, `src/LeafCalendar.Core/Alerts/ToastContent.cs`
- Test: `tests/LeafCalendar.Tests/ToastContentTests.cs`

**Interfaces:**
- Consumes: `Alert` (Task 3), `EventDetails`, `CalendarOccurrence`, `DisplayText.Clean`, `TrayAgenda.DayHeader` (Task 6), `TimeLabels.Range`.
- Produces (namespace `LeafCalendar.Core.Alerts`):
  - `enum ToastAction { Open, Join, Accept, Decline, Maybe, ReviewConflicts, SignIn }`
  - `sealed record ToastArgs(ToastAction Action, string Profile, string? AccountId = null, string? CalendarId = null, string? EventId = null, DateTimeOffset? Start = null)` with `static ToastArgs For(ToastAction action, string profile, CalendarOccurrence occurrence)`, `string Encode()`, `static ToastArgs? Parse(string? text)`
  - `sealed record ToastMessage(string Tag, string Group, string Xml)`
  - `static class ToastContent`:
    - group constants `ReminderGroup`, `JoinGroup`, `InviteGroup`, `ConflictGroup`, `SignInGroup`, `NoticeGroup`; `ConflictTag`; `SnoozeInputId`
    - `string When(CalendarOccurrence o, TimeZoneInfo zone, bool use24Hour, DateTimeOffset now)` ("Today · 2 PM – 3 PM")
    - `ToastMessage Reminder(Alert alert, EventDetails details, string when, string profile, bool sound)`
    - `ToastMessage JoinNow(Alert alert, EventDetails details, string when, string profile, bool sound)`
    - `ToastMessage Invite(CalendarOccurrence occurrence, EventDetails details, bool isUpdate, string tag, string when, string profile, bool sound)`
    - `ToastMessage Conflicts(int count, string profile, bool sound)`
    - `ToastMessage SignIn(string accountId, string email, string profile, bool sound)`
    - `ToastMessage NoMeeting(bool sound)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/ToastContentTests.cs`:
```csharp
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

    [Fact]
    public void Reminder_HostileTitle_StaysPlainText()
    {
        var hostile = "Standup</text><action content=\"Pwn\" arguments=\"x\"/><text>&\"'\r\n" + new string('x', 5000);

        var toast = Parse(ToastContent.Reminder(Reminder(Meet, hostile), Details(hostile, location: "<b>Room</b>"), "Today · 2 PM – 3 PM", "default", sound: true));

        var texts = Texts(toast);
        Assert.StartsWith("Standup</text><action", texts[0], StringComparison.Ordinal);
        Assert.True(texts[0].Length <= 200);
        Assert.DoesNotContain('\n', texts[0]);
        Assert.Equal("<b>Room</b>", texts[2]);
        Assert.Equal(3, Actions(toast).Count);
        Assert.DoesNotContain(Actions(toast), a => (string?)a.Attribute("content") == "Pwn");
    }

    [Fact]
    public void Reminder_WithLink_HasJoinSnoozeAndDismiss()
    {
        var toast   = Parse(ToastContent.Reminder(Reminder(Meet), Details(), "Today · 2 PM – 3 PM", "default", sound: true));
        var actions = Actions(toast);

        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal(["Design review", "Today · 2 PM – 3 PM", "Room 4"], Texts(toast));
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
        var message = ToastContent.JoinNow(alert, Details(), "Today · 2 PM – 3 PM", "default", sound: true);
        var toast   = Parse(message);
        var actions = Actions(toast);

        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
        Assert.Equal(["Design review", "Starting now · Today · 2 PM – 3 PM"], Texts(toast));
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
        var toast   = Parse(ToastContent.Invite(Occurrence(), Details(), isUpdate: true, "TAG", "Today · 2 PM – 3 PM", "default", sound: true));
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

        Assert.Equal("Today · 6 PM – 7 PM", ToastContent.When(Occurrence(), TimeZoneInfo.Utc, false, now));
        Assert.Equal("Tomorrow · 6 PM – 7 PM", ToastContent.When(Occurrence(), TimeZoneInfo.Utc, false, now.AddDays(-1)));
        var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Today · All day", ToastContent.When(Occurrence(allDay: true) with { Start = day, End = day.AddDays(1) }, TimeZoneInfo.Utc, false, now));
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
    [InlineData("action=ReviewConflicts")]
    [InlineData("action=Join;profile=default;account=1;calendar=c")]
    [InlineData("action=Join;profile=default;account=1;calendar=c;event=e;start=99999999999999999999")]
    [InlineData("action=SignIn;profile=default")]
    public void Args_Malformed_ReadAsNothing(string? text)
    {
        Assert.Null(ToastArgs.Parse(text));
    }

    [Fact]
    public void Args_TooLong_ReadAsNothing()
    {
        Assert.Null(ToastArgs.Parse("action=ReviewConflicts;profile=default;x=" + new string('x', 5000)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*ToastContentTests"`
Expected: build FAIL — `ToastContent`, `ToastArgs`, `ToastAction`, and `ToastMessage` don't exist.

- [ ] **Step 3: Write `ToastArgs`**

`src/LeafCalendar.Core/Alerts/ToastArgs.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Alerts;

/// <summary>What clicking a notification (or one of its buttons) does.</summary>
public enum ToastAction
{
    /// <summary>Open the main window on the event.</summary>
    Open,

    /// <summary>Open the event's meeting link.</summary>
    Join,

    /// <summary>Reply Yes.</summary>
    Accept,

    /// <summary>Reply No.</summary>
    Decline,

    /// <summary>Reply Maybe.</summary>
    Maybe,

    /// <summary>Open the conflict dialog.</summary>
    ReviewConflicts,

    /// <summary>Open Settings › Accounts.</summary>
    SignIn,
}

/// <summary>
/// A notification's activation arguments: the action, the Leaf profile it belongs to, and the event instance (account,
/// calendar, event ID, and start) or account it's about.
/// </summary>
/// <remarks>
/// Written as <c>key=value;key=value</c> with every value URL-escaped, so calendar IDs with <c>;</c>, <c>=</c>, or
/// <c>%</c> survive. <see cref="Parse"/> never throws: anything malformed, unknown, or missing a field its action needs
/// reads as null, because a notification's arguments come back from outside Leaf.
/// </remarks>
public sealed record ToastArgs(ToastAction Action, string Profile, string? AccountId = null, string? CalendarId = null, string? EventId = null, DateTimeOffset? Start = null)
{
    const int MaxLength = 2048;

    // Largest instant DateTimeOffset can hold, in Unix milliseconds
    const long MaxUnixMs = 253402300799999;

    /// <summary>Arguments about one event instance.</summary>
    public static ToastArgs For(ToastAction action, string profile, CalendarOccurrence occurrence) =>
        new(action, profile, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId, occurrence.Start);

    /// <summary>The <c>key=value;...</c> text.</summary>
    public string Encode()
    {
        var parts = new List<string> { "action=" + Action, "profile=" + Uri.EscapeDataString(Profile) };
        Add(parts, "account", AccountId);
        Add(parts, "calendar", CalendarId);
        Add(parts, "event", EventId);
        Add(parts, "start", Start?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return string.Join(";", parts);
    }

    /// <summary>Reads <see cref="Encode"/>'s text; null when it's malformed or incomplete.</summary>
    public static ToastArgs? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }

        // Fields
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return null;
            }

            values[part[..equals]] = Uri.UnescapeDataString(part[(equals + 1)..]);
        }

        // Action (a name, never a number) And Profile
        if (!values.TryGetValue("action", out var name) || !name.All(char.IsAsciiLetter) || !Enum.TryParse<ToastAction>(name, out var action)
            || !values.TryGetValue("profile", out var profile) || profile.Length == 0)
        {
            return null;
        }

        // Event Instance
        var account  = values.GetValueOrDefault("account");
        var calendar = values.GetValueOrDefault("calendar");
        var eventId  = values.GetValueOrDefault("event");
        DateTimeOffset? start = null;
        if (values.TryGetValue("start", out var ms))
        {
            if (!long.TryParse(ms, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) || unix > MaxUnixMs)
            {
                return null;
            }

            start = DateTimeOffset.FromUnixTimeMilliseconds(unix);
        }

        // What Each Action Needs
        var aboutEvent = action is ToastAction.Open or ToastAction.Join or ToastAction.Accept or ToastAction.Decline or ToastAction.Maybe;
        if ((aboutEvent && (account is null || calendar is null || eventId is null || start is null)) || (action == ToastAction.SignIn && account is null))
        {
            return null;
        }

        return new ToastArgs(action, profile, account, calendar, eventId, start);
    }

    static void Add(List<string> parts, string key, string? value)
    {
        if (value is not null)
        {
            parts.Add(key + "=" + Uri.EscapeDataString(value));
        }
    }
}
```

- [ ] **Step 4: Write `ToastContent`**

`src/LeafCalendar.Core/Alerts/ToastContent.cs`:
```csharp
using System.Globalization;
using System.Xml.Linq;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Alerts;

/// <summary>A notification ready for Windows: its tag and group (for replacing and withdrawing it) and its XML.</summary>
public sealed record ToastMessage(string Tag, string Group, string Xml);

/// <summary>
/// Builds every notification Leaf shows (spec 8.4) as Windows toast XML.
/// </summary>
/// <remarks>
/// <para>
/// Event text comes from anyone who can send an invite, so it only ever enters as <see cref="XElement"/> text and
/// attribute values (always escaped), after <see cref="DisplayText.Clean"/> removed control characters and clipped it
/// to 200 characters. Nothing from an event can add an element, a button, or an attribute.
/// </para>
/// <para>
/// Reminders carry Join (when there's a link), Windows' own Snooze with a 5/10/15/30-minute choice, and Dismiss.
/// "Join now" uses <c>scenario="reminder"</c>, so it stays on screen, with a background-activated Join button (which
/// Windows requires for that scenario) and Dismiss, and no Snooze. Invites carry Yes / No / Maybe. Clicking a
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

    const int MaxText = 200;

    /// <summary>"Today · 2 PM – 3 PM", "Tomorrow · All day", or "Saturday, October 3 · 9 AM – 10 AM".</summary>
    public static string When(CalendarOccurrence o, TimeZoneInfo zone, bool use24Hour, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day   = o.IsAllDay ? o.AllDayStart : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start, zone).DateTime);
        var date  = TrayAgenda.DayHeader(day, today);
        return o.IsAllDay ? $"{date} · All day" : $"{date} · {TimeLabels.Range(o.Start, o.End, zone, use24Hour)}";
    }

    /// <summary>A reminder: title, time, location; Join (with a link), Snooze, Dismiss.</summary>
    public static ToastMessage Reminder(Alert alert, EventDetails details, string when, string profile, bool sound)
    {
        var o       = alert.Occurrence;
        var actions = new List<XElement> { SnoozeInput() };
        if (alert.MeetingLink is not null)
        {
            actions.Add(Button("Join", ToastArgs.For(ToastAction.Join, profile, o)));
        }

        actions.Add(SystemButton("snooze", SnoozeInputId));
        actions.Add(SystemButton("dismiss"));

        return Build(alert.Tag, ReminderGroup, ToastArgs.For(ToastAction.Open, profile, o), null, [details.Title, when, details.Location], actions, sound);
    }

    /// <summary>The persistent "Join now": title and "Starting now · time"; Join and Dismiss.</summary>
    public static ToastMessage JoinNow(Alert alert, EventDetails details, string when, string profile, bool sound)
    {
        var o = alert.Occurrence;
        return Build(
            alert.Tag,
            JoinGroup,
            ToastArgs.For(ToastAction.Open, profile, o),
            "reminder",
            [details.Title, "Starting now · " + when],
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

    /// <summary>"Sign in again" for an account whose sign-in stopped working; clicking opens Settings › Accounts.</summary>
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

    static ToastMessage Build(string tag, string group, ToastArgs? launch, string? scenario, IEnumerable<string?> lines, IReadOnlyList<XElement> actions, bool sound)
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
                    lines.Select(l => DisplayText.Clean(l, MaxText)).Where(l => l.Length > 0).Select(l => new XElement("text", l)))));

        if (actions.Count > 0)
        {
            toast.Add(new XElement("actions", actions));
        }

        if (!sound)
        {
            toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        }

        return new ToastMessage(tag, group, toast.ToString(SaveOptions.DisableFormatting));
    }

    // Windows' own snooze choices; the selection IDs are minutes
    static XElement SnoozeInput() => new(
        "input",
        new XAttribute("id", SnoozeInputId),
        new XAttribute("type", "selection"),
        new XAttribute("defaultInput", "5"),
        Choice("5", "5 minutes"),
        Choice("10", "10 minutes"),
        Choice("15", "15 minutes"),
        Choice("30", "30 minutes"));

    static XElement Choice(string minutes, string content) =>
        new("selection", new XAttribute("id", minutes), new XAttribute("content", content));

    // Background activation: Leaf acts without coming to the front
    static XElement Button(string content, ToastArgs args) => new(
        "action",
        new XAttribute("content", content),
        new XAttribute("arguments", args.Encode()),
        new XAttribute("activationType", "background"));

    // Windows' Snooze and Dismiss (empty content = Windows' own localized label)
    static XElement SystemButton(string arguments, string? inputId = null) => new(
        "action",
        new XAttribute("content", ""),
        new XAttribute("arguments", arguments),
        new XAttribute("activationType", "system"),
        inputId is null ? null : new XAttribute("hint-inputId", inputId));
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*ToastContentTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Alerts/ToastArgs.cs src/LeafCalendar.Core/Alerts/ToastContent.cs tests/LeafCalendar.Tests/ToastContentTests.cs
git commit -m "feat(core): notification XML and activation arguments, escaped and bounded"
```

---

### Task 8: Invites, Conflict and Sign-In Signals

**Files:**
- Create: `src/LeafCalendar.Core/Events/OccurrenceLookup.cs`, `src/LeafCalendar.Core/Alerts/InviteWatcher.cs`
- Modify: `src/LeafCalendar.Core/Sync/SyncEngine.cs`
- Test: `tests/LeafCalendar.Tests/OccurrenceLookupTests.cs`, `tests/LeafCalendar.Tests/InviteWatcherTests.cs`, `tests/LeafCalendar.Tests/SyncEngineTests.cs` (add)

**Interfaces:**
- Consumes: `OccurrenceQuery.Load`, `AlertLedger` (Task 2), `Alert.TagFor` (Task 3), `CalendarOccurrence.EndIn` (Task 3), `EventDetailsParser.Parse`, `SendReport.Conflicts`, `AccountNeedsSignInException`.
- Produces:
  - `namespace LeafCalendar.Core.Events`: `static class OccurrenceLookup` with `CalendarOccurrence? Find(SqliteConnection conn, string accountId, string calendarId, string eventId, DateTimeOffset start, TimeZoneInfo zone)`
  - `namespace LeafCalendar.Core.Alerts`: `sealed record InviteAlert(CalendarOccurrence Occurrence, EventDetails Details, bool IsUpdate, string Tag)`; `static class InviteWatcher` with `IReadOnlyList<InviteAlert> TakeNew(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)`
  - `SyncEngine`: `event EventHandler<int>? ConflictsFound` (new conflicts in a pass), `event EventHandler<string>? SignInNeeded` (account ID; once, when sign-in stops working)

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/OccurrenceLookupTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OccurrenceLookupTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();

    public OccurrenceLookupTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Primary, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    CalendarOccurrence? Find(string eventId, DateTimeOffset start)
    {
        using var conn = _db.Database.Open();
        return OccurrenceLookup.Find(conn, Account, Primary, eventId, start, NewYork);
    }

    [Fact]
    public void Find_SingleEvent()
    {
        var start = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal(start, Find("evt-single", start)!.Start);
    }

    [Fact]
    public void Find_SeriesInstanceByItsStart()
    {
        var friday = new DateTimeOffset(2026, 10, 9, 13, 30, 0, TimeSpan.Zero);

        var o = Find("evt-weekly", friday)!;

        Assert.Equal(friday, o.Start);
        Assert.Equal("evt-weekly", o.RecurringEventId);
    }

    [Fact]
    public void Find_StartMovedSince_FallsBackToTheEventThatDay()
    {
        var o = Find("evt-single", new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));

        Assert.Equal("evt-single", o!.EventId);
    }

    [Fact]
    public void Find_Unknown_Null()
    {
        Assert.Null(Find("evt-nope", new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero)));
    }
}
```

`tests/LeafCalendar.Tests/InviteWatcherTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class InviteWatcherTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Family  = "family123@group.calendar.google.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    readonly TestDatabase _db = new();

    public InviteWatcherTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        Synced(Primary);
        Synced(Family);
    }

    public void Dispose() => _db.Dispose();

    void Synced(string calendarId)
    {
        using var conn = _db.Database.Open();
        CalendarStore.SetSyncToken(conn, null, Account, calendarId, "sync-token-1");
    }

    static string Invite(string id = "evt-inv", int sequence = 0, string response = "needsAction", string start = "2026-10-05T15:00:00Z", string end = "2026-10-05T16:00:00Z", string others = "accepted", string extra = "") => $$"""
        {"id":"{{id}}","status":"confirmed","summary":"Planning","sequence":{{sequence}},"organizer":{"email":"boss@example.com"},{{extra}}
         "attendees":[{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"},
                      {"email":"sam@example.com","responseStatus":"{{others}}"},
                      {"email":"leaf.tester@gmail.com","self":true,"responseStatus":"{{response}}"}],
         "start":{"dateTime":"{{start}}"},"end":{"dateTime":"{{end}}"}}
        """;

    void Store(string json, string calendarId = Primary)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, calendarId, json);
    }

    IReadOnlyList<InviteAlert> TakeNew()
    {
        using var conn = _db.Database.Open();
        return InviteWatcher.TakeNew(conn, Now, TimeZoneInfo.Utc);
    }

    [Fact]
    public void TakeNew_FirstLookAtAnAccount_RecordsItsInvitesQuietly()
    {
        Store(Invite());

        Assert.Empty(TakeNew());
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_NewInviteLater_NotifiesOnce()
    {
        TakeNew();
        Store(Invite());

        var invite = Assert.Single(TakeNew());
        Assert.Equal("evt-inv", invite.Occurrence.EventId);
        Assert.Equal("Planning", invite.Details.Title);
        Assert.False(invite.IsUpdate);
        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_GuestReplyOnly_DoesNotNotifyAgain()
    {
        TakeNew();
        Store(Invite());
        TakeNew();

        Store(Invite(others: "declined"));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_OrganizerChange_NotifiesAsAnUpdate()
    {
        TakeNew();
        Store(Invite());
        TakeNew();

        Store(Invite(sequence: 1, start: "2026-10-05T17:00:00Z", end: "2026-10-05T18:00:00Z"));

        Assert.True(Assert.Single(TakeNew()).IsUpdate);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("declined")]
    [InlineData("tentative")]
    public void TakeNew_AlreadyAnswered_Ignored(string response)
    {
        TakeNew();
        Store(Invite(response: response));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_YouOrganizeIt_Ignored()
    {
        TakeNew();
        Store(Invite(extra: "\"creator\":{\"self\":true},").Replace("\"organizer\":{\"email\":\"boss@example.com\"}", "\"organizer\":{\"email\":\"leaf.tester@gmail.com\",\"self\":true}", StringComparison.Ordinal));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_PastInvite_Ignored()
    {
        TakeNew();
        Store(Invite(start: "2026-09-29T15:00:00Z", end: "2026-09-29T16:00:00Z"));

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_HiddenCalendar_Ignored()
    {
        TakeNew();
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Family, true);
        }

        Store(Invite(), Family);

        Assert.Empty(TakeNew());
    }

    [Fact]
    public void TakeNew_AccountStillOnItsFirstSync_WaitsThenRecordsQuietly()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetSyncToken(conn, null, Account, Family, null);
        }

        Store(Invite());
        Assert.Empty(TakeNew());

        Synced(Family);
        Assert.Empty(TakeNew());

        Store(Invite(id: "evt-inv2"));
        Assert.Equal("evt-inv2", Assert.Single(TakeNew()).Occurrence.EventId);
    }

    [Fact]
    public void TakeNew_RepeatingInvite_PointsAtTheNextInstance()
    {
        TakeNew();
        Store(Invite(id: "evt-series", start: "2026-09-28T15:00:00Z", end: "2026-09-28T16:00:00Z", extra: "\"recurrence\":[\"RRULE:FREQ=WEEKLY;BYDAY=MO\"],"));

        var invite = Assert.Single(TakeNew());

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), invite.Occurrence.Start);
    }
}
```

Add to `tests/LeafCalendar.Tests/SyncEngineTests.cs`:
```csharp
    [Fact]
    public async Task SyncAllAsync_Conflict_RaisesConflictsFound()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "evt-single", OutboxOperation.Patch, """{"summary":"Mine"}""", "\"3181161784712000\"", false, EventStore.Snapshot(conn, null, Account, Primary, "evt-single"), null));
        }

        _h.Google.On(HttpMethod.Patch, SyncHarness.PrimaryEventsUrl + "/evt-single", HttpStatusCode.PreconditionFailed, "{}");
        _h.Google.On(HttpMethod.Get, SyncHarness.PrimaryEventsUrl + "/evt-single", HttpStatusCode.OK, """{"id":"evt-single","etag":"\"G9\"","status":"confirmed","summary":"Google's","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");
        var found = new List<int>();
        _h.Engine.ConflictsFound += (_, count) => found.Add(count);

        await _h.Engine.SyncAllAsync(ct);
        await _h.Engine.SyncAllAsync(ct);

        Assert.Equal([1], found);
    }

    [Fact]
    public async Task SyncAllAsync_RefreshTokenRevoked_RaisesSignInNeededOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(r => r.Form("refresh_token") == "1//revoked", _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json")));
        _h.RouteStandardGoogle();
        _h.Tokens.SetRefreshToken(Account, "1//revoked");
        var engine  = _h.NewEngine();
        var signIns = new List<string>();
        engine.SignInNeeded += (_, account) => signIns.Add(account);

        await engine.SyncAllAsync(ct);
        await engine.SyncAllAsync(ct);

        Assert.Equal([Account], signIns);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*OccurrenceLookupTests" --filter-class "*InviteWatcherTests" --filter-class "*SyncEngineTests"`
Expected: build FAIL — `OccurrenceLookup`, `InviteWatcher`, `InviteAlert`, `SyncEngine.ConflictsFound`, and `SyncEngine.SignInNeeded` don't exist.

- [ ] **Step 3: Write `OccurrenceLookup`**

`src/LeafCalendar.Core/Events/OccurrenceLookup.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Events;

/// <summary>Finds an event instance again from what a notification carried (account, calendar, event ID, start).</summary>
public static class OccurrenceLookup
{
    /// <summary>
    /// The instance with this event ID that starts at <paramref name="start"/>, or, when it has moved since, the one with
    /// this ID around that day; null when it's gone. Declined instances are found too (a reply can change a "No").
    /// </summary>
    public static CalendarOccurrence? Find(SqliteConnection conn, string accountId, string calendarId, string eventId, DateTimeOffset start, TimeZoneInfo zone)
    {
        var day     = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var matches = OccurrenceQuery.Load(conn, day.AddDays(-1), day.AddDays(2), zone, includeDeclined: true)
            .Where(o => o.AccountId == accountId && o.CalendarId == calendarId && o.EventId == eventId)
            .ToList();

        return matches.FirstOrDefault(o => o.Start == start) ?? matches.FirstOrDefault();
    }
}
```

- [ ] **Step 4: Write `InviteWatcher`**

`src/LeafCalendar.Core/Alerts/InviteWatcher.cs`:
```csharp
using System.Globalization;
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>An invitation to notify about: the next instance, the event's details, whether it's an update, and its tag.</summary>
public sealed record InviteAlert(CalendarOccurrence Occurrence, EventDetails Details, bool IsUpdate, string Tag);

/// <summary>
/// Finds invitations to notify about (spec 8.4 item 3), after each sync that wrote something.
/// </summary>
/// <remarks>
/// <para>
/// An invitation is an event on a shown calendar, not canceled, not over, that someone else organizes and that you
/// haven't answered (<c>needsAction</c>). Each one is recorded in <see cref="AlertLedger"/> under its event and
/// Google's <c>sequence</c> number, which Google raises only when the organizer changes something that matters (time,
/// place, ...). So another guest's reply, which changes the event but not its sequence, never notifies twice, and an
/// organizer's change notifies once as an update.
/// </para>
/// <para>
/// An account is looked at only once every calendar of it finished its first sync, and that first look records its
/// existing invitations without notifying, so adding an account doesn't bring a flood.
/// </para>
/// </remarks>
public static class InviteWatcher
{
    const string SeededMark = "invites-seeded:";

    // How far ahead the next instance of a repeating invitation is looked for
    const int LookaheadDays = 366;

    // "needsAction" in the JSON is a cheap first filter; the JSON is then read properly
    const string Sql = """
        SELECT e.account_id, e.calendar_id, e.id, e.raw_json, e.is_recurring_master
        FROM events e
        JOIN calendars c ON c.account_id = e.account_id AND c.id = e.calendar_id
        WHERE e.account_id = $account
          AND COALESCE(c.leaf_hidden, c.hidden) = 0
          AND e.status <> 'cancelled'
          AND (e.end_utc > $now OR e.is_recurring_master = 1)
          AND e.raw_json LIKE '%needsAction%'
        ORDER BY e.start_utc;
        """;

    /// <summary>Invitations that are new since the last call (each only once, ever), recorded as it goes.</summary>
    public static IReadOnlyList<InviteAlert> TakeNew(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var result = new List<InviteAlert>();
        IReadOnlyList<CalendarOccurrence>? upcoming = null;

        foreach (var account in AccountStore.GetAll(conn))
        {
            // Not Before The Account's First Sync Finished
            if (!IsSynced(conn, account.Id))
            {
                continue;
            }

            var seeded = AlertLedger.GetMark(conn, SeededMark + account.Id) is not null;
            var rows   = conn.Query(null, Sql, r => (Calendar: r.GetString(1), Id: r.GetString(2), Json: r.GetString(3), IsMaster: r.GetBoolean(4)), ("$account", account.Id), ("$now", now.ToUnixTimeMilliseconds()));

            foreach (var row in rows)
            {
                if (!IsOpenInvite(row.Json, out var sequence))
                {
                    continue;
                }

                // Seen Before (same event, same sequence)
                var prefix = $"Invite|{account.Id}|{row.Calendar}|{row.Id}|";
                var key    = prefix + sequence.ToString(CultureInfo.InvariantCulture);
                if (AlertLedger.Contains(conn, key))
                {
                    continue;
                }

                // Its Next Instance (loaded once, only when something needs it)
                upcoming ??= Upcoming(conn, now, zone);
                var occurrence = upcoming.FirstOrDefault(o => o.AccountId == account.Id && o.CalendarId == row.Calendar && o.EventId == row.Id && o.EndIn(zone) > now);
                if (occurrence is null && seeded)
                {
                    continue;
                }

                // Record, Then Notify (unless this is the account's first look)
                var isUpdate = AlertLedger.HasPrefix(conn, prefix);
                var tag      = Alert.TagFor(key);
                var end      = row.IsMaster || occurrence is null ? now.AddDays(LookaheadDays) : occurrence.EndIn(zone);
                AlertLedger.TryAdd(conn, key, AlertKind.Invite, tag, end, now);
                if (seeded && occurrence is not null)
                {
                    result.Add(new InviteAlert(occurrence, EventDetailsParser.Parse(row.Json), isUpdate, tag));
                }
            }

            if (!seeded)
            {
                AlertLedger.SetMark(conn, SeededMark + account.Id, 1);
            }
        }

        return result;
    }

    static IReadOnlyList<CalendarOccurrence> Upcoming(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(LookaheadDays), zone, includeDeclined: true);
    }

    // Every calendar of the account has a sync token (its first sync finished)
    static bool IsSynced(SqliteConnection conn, string accountId) =>
        conn.Query(
            null,
            "SELECT COUNT(*) > 0 AND SUM(CASE WHEN sync_token IS NULL THEN 1 ELSE 0 END) = 0 FROM calendars WHERE account_id = $account;",
            r => !r.IsDBNull(0) && r.GetBoolean(0),
            ("$account", accountId)).Single();

    // Someone else organizes it and your own reply is still "needsAction"; also returns Google's sequence number
    static bool IsOpenInvite(string json, out int sequence)
    {
        sequence = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("organizer", out var organizer) && organizer.ValueKind == JsonValueKind.Object
                && organizer.TryGetProperty("self", out var mine) && mine.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            if (root.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt32(out var n))
            {
                sequence = n;
            }

            if (!root.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var attendee in attendees.EnumerateArray())
            {
                if (attendee.ValueKind == JsonValueKind.Object
                    && attendee.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
                {
                    return attendee.TryGetProperty("responseStatus", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "needsAction";
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 5: Raise the conflict and sign-in signals from `SyncEngine`**

In `src/LeafCalendar.Core/Sync/SyncEngine.cs`:

Add under `int _rejected;`:
```csharp
    int _conflicts;
    readonly List<string> _signInsNeeded = [];
```

Add after the `ChangesRejected` event:
```csharp
    /// <summary>Raised after a sync in which Google reported new conflicts ("1 change needs your review"). The argument is how many.</summary>
    public event EventHandler<int>? ConflictsFound;

    /// <summary>Raised once when an account's sign-in stops working (it's marked "needs sign-in" and no longer syncs). The argument is its ID.</summary>
    public event EventHandler<string>? SignInNeeded;
```

Replace `SyncAllAsync(bool, CancellationToken)` and `SyncAccountAsync` with:
```csharp
    /// <summary>Syncs every account that can sync. <paramref name="refreshCalendarLists"/> forces a calendar-list refresh ("Sync now").</summary>
    public async Task SyncAllAsync(bool refreshCalendarLists, CancellationToken ct)
    {
        bool changed;
        int rejected;
        bool? offline;
        int conflicts;
        string[] signIns;
        await _gate.WaitAsync(ct);
        try
        {
            Begin();

            IReadOnlyList<Account> accounts;
            using (var conn = database.Open())
            {
                accounts = AccountStore.GetAll(conn);
            }

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Ok))
            {
                await SyncAccountCoreAsync(account.Id, refreshCalendarLists, ct);
            }

            (changed, rejected, offline) = End();
            conflicts = _conflicts;
            signIns   = [.. _signInsNeeded];
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed, rejected, offline, conflicts, signIns);
    }

    /// <summary>Syncs one account now, including its calendar list (used right after sign-in).</summary>
    public async Task SyncAccountAsync(string accountId, CancellationToken ct)
    {
        bool changed;
        int rejected;
        bool? offline;
        int conflicts;
        string[] signIns;
        await _gate.WaitAsync(ct);
        try
        {
            Begin();
            await SyncAccountCoreAsync(accountId, refreshCalendarList: true, ct);
            (changed, rejected, offline) = End();
            conflicts = _conflicts;
            signIns   = [.. _signInsNeeded];
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed, rejected, offline, conflicts, signIns);
    }
```

In `Begin()`, add:
```csharp
        _conflicts   = 0;
        _signInsNeeded.Clear();
```

Replace `Raise` with:
```csharp
    // Outside the lock, so handlers may start another sync
    void Raise(bool changed, int rejected, bool? offline, int conflicts, string[] signIns)
    {
        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        if (rejected > 0)
        {
            ChangesRejected?.Invoke(this, rejected);
        }

        if (conflicts > 0)
        {
            ConflictsFound?.Invoke(this, conflicts);
        }

        foreach (var account in signIns)
        {
            SignInNeeded?.Invoke(this, account);
        }

        if (offline is not null)
        {
            OfflineChanged?.Invoke(this, EventArgs.Empty);
        }
    }
```

In `SyncAccountCoreAsync`, after `_rejected += sent.Rejected;` add:
```csharp
            _conflicts += sent.Conflicts;
```
and in its `catch (AccountNeedsSignInException)` block, after `AccountStore.SetStatus(...)`, add:
```csharp
            _signInsNeeded.Add(accountId);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: PASS (all logic tests).

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.Core/Events/OccurrenceLookup.cs src/LeafCalendar.Core/Alerts/InviteWatcher.cs src/LeafCalendar.Core/Sync/SyncEngine.cs tests/LeafCalendar.Tests/OccurrenceLookupTests.cs tests/LeafCalendar.Tests/InviteWatcherTests.cs tests/LeafCalendar.Tests/SyncEngineTests.cs
git commit -m "feat(core): invite watcher, occurrence lookup, and conflict/sign-in sync signals"
```

---

### Task 9: Taskbar-Aware Placement

**Files:**
- Create: `src/LeafCalendar.Core/Tray/TrayPlacement.cs`
- Test: `tests/LeafCalendar.Tests/TrayPlacementTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `LeafCalendar.Core.Tray`):
  - `readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)` with `Width`, `Height`
  - `enum TaskbarEdge { Bottom, Top, Left, Right }`
  - `static class TrayPlacement`:
    - `const double WidthDip = 360`, `HeightDip = 560`, `MarginDip = 12`
    - `TaskbarEdge EdgeFromAppBar(uint edge)` (`ABE_LEFT` 0, `ABE_TOP` 1, `ABE_RIGHT` 2, `ABE_BOTTOM` 3)
    - `TaskbarEdge DetectEdge(PixelRect monitor, PixelRect workArea)`
    - `PixelRect UsableArea(PixelRect workArea, PixelRect? taskbar, TaskbarEdge edge)` (clear of an auto-hidden taskbar)
    - `PixelRect Flyout(PixelRect area, TaskbarEdge edge, PixelRect? icon, double scale)` (the panel)
    - `PixelRect Frame(PixelRect panel, double scale)` (the panel plus one margin all round: the slide's clip and the shadow's room)
    - `(int X, int Y) FlyoutAnchor(PixelRect frame, TaskbarEdge edge)` (the frame corner on the taskbar side)
    - `(int X, int Y) MenuAnchor(int x, int y, PixelRect area, TaskbarEdge edge, double scale)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/TrayPlacementTests.cs`:
```csharp
using LeafCalendar.Core.Tray;

namespace LeafCalendar.Tests;

public class TrayPlacementTests
{
    // A 1920 × 1080 primary monitor
    static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    static readonly PixelRect BottomWork = new(0, 0, 1920, 1032);

    static bool Inside(PixelRect inner, PixelRect outer, int margin) =>
        inner.Left >= outer.Left + margin && inner.Top >= outer.Top + margin && inner.Right <= outer.Right - margin && inner.Bottom <= outer.Bottom - margin;

    [Theory]
    [InlineData(0u, TaskbarEdge.Left)]
    [InlineData(1u, TaskbarEdge.Top)]
    [InlineData(2u, TaskbarEdge.Right)]
    [InlineData(3u, TaskbarEdge.Bottom)]
    [InlineData(9u, TaskbarEdge.Bottom)]
    public void EdgeFromAppBar_MapsTheWin32Values(uint edge, TaskbarEdge expected)
    {
        Assert.Equal(expected, TrayPlacement.EdgeFromAppBar(edge));
    }

    [Fact]
    public void DetectEdge_FromTheMissingPartOfTheWorkArea()
    {
        Assert.Equal(TaskbarEdge.Bottom, TrayPlacement.DetectEdge(Monitor, BottomWork));
        Assert.Equal(TaskbarEdge.Top, TrayPlacement.DetectEdge(Monitor, new(0, 48, 1920, 1080)));
        Assert.Equal(TaskbarEdge.Left, TrayPlacement.DetectEdge(Monitor, new(48, 0, 1920, 1080)));
        Assert.Equal(TaskbarEdge.Right, TrayPlacement.DetectEdge(Monitor, new(0, 0, 1872, 1080)));
        Assert.Equal(TaskbarEdge.Bottom, TrayPlacement.DetectEdge(Monitor, Monitor));
    }

    [Fact]
    public void Flyout_BottomTaskbar_CenteredOnTheIconAboveTheTaskbar()
    {
        var panel = TrayPlacement.Flyout(BottomWork, TaskbarEdge.Bottom, new PixelRect(1700, 1040, 1724, 1064), 1.0);

        Assert.Equal(new PixelRect(1532, 460, 1892, 1020), panel);
    }

    [Fact]
    public void Flyout_IconNearTheCornerOrUnknown_KeepsTheMarginFromTheEdge()
    {
        var corner  = TrayPlacement.Flyout(BottomWork, TaskbarEdge.Bottom, new PixelRect(1890, 1040, 1914, 1064), 1.0);
        var unknown = TrayPlacement.Flyout(BottomWork, TaskbarEdge.Bottom, null, 1.0);

        Assert.Equal(1908, corner.Right);
        Assert.Equal(corner, unknown);
    }

    [Fact]
    public void Flyout_TopTaskbar_BelowIt()
    {
        var work  = new PixelRect(0, 48, 1920, 1080);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Top, new PixelRect(1700, 12, 1724, 36), 1.0);

        Assert.Equal(60, panel.Top);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_RightTaskbar_BesideItNearTheIcon()
    {
        var work  = new PixelRect(0, 0, 1872, 1080);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Right, new PixelRect(1884, 1000, 1908, 1024), 1.0);

        Assert.Equal(1860, panel.Right);
        Assert.Equal(508, panel.Top);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_LeftTaskbarOnMonitorLeftOfPrimary_StaysInside()
    {
        // A monitor at x -1920..0 with a 48-pixel taskbar on its left edge
        var work  = new PixelRect(-1872, 0, 0, 1080);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Left, new PixelRect(-1910, 1000, -1886, 1024), 1.0);

        Assert.Equal(new PixelRect(-1860, 508, -1500, 1068), panel);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_AutoHiddenBottomTaskbar_StaysAboveIt()
    {
        // Auto-hide: the work area is the whole monitor, and the taskbar pops up over its bottom 48 pixels
        var area  = TrayPlacement.UsableArea(Monitor, new PixelRect(0, 1032, 1920, 1080), TaskbarEdge.Bottom);
        var panel = TrayPlacement.Flyout(area, TaskbarEdge.Bottom, null, 1.0);

        Assert.Equal(1020, panel.Bottom);
    }

    [Fact]
    public void UsableArea_TaskbarOnAnotherMonitor_LeavesTheWorkArea()
    {
        Assert.Equal(BottomWork, TrayPlacement.UsableArea(BottomWork, new PixelRect(-1920, 1032, 0, 1080), TaskbarEdge.Bottom));
    }

    [Fact]
    public void Flyout_ShortScreen_GetsShorter()
    {
        var work  = new PixelRect(0, 0, 1280, 500);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Bottom, null, 1.0);

        Assert.Equal(476, panel.Height);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_At150Percent_ScalesSizeAndMargin()
    {
        var work  = new PixelRect(0, 0, 2880, 1548);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Bottom, null, 1.5);

        Assert.Equal(new PixelRect(2322, 690, 2862, 1530), panel);
    }

    [Fact]
    public void FrameAndAnchor_OnTheTaskbarSide()
    {
        var panel = new PixelRect(1532, 460, 1892, 1020);
        var frame = TrayPlacement.Frame(panel, 1.0);

        Assert.Equal(new PixelRect(1520, 448, 1904, 1032), frame);
        Assert.Equal((1520, 1032), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Bottom));
        Assert.Equal((1520, 448), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Top));
        Assert.Equal((1520, 1032), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Left));
        Assert.Equal((1904, 1032), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Right));
    }

    [Fact]
    public void MenuAnchor_MovesOffTheTaskbarByTheMargin()
    {
        Assert.Equal((1700, 1020), TrayPlacement.MenuAnchor(1700, 1050, BottomWork, TaskbarEdge.Bottom, 1.0));
        Assert.Equal((1700, 60), TrayPlacement.MenuAnchor(1700, 20, new PixelRect(0, 48, 1920, 1080), TaskbarEdge.Top, 1.0));
        Assert.Equal((60, 900), TrayPlacement.MenuAnchor(20, 900, new PixelRect(48, 0, 1920, 1080), TaskbarEdge.Left, 1.0));
        Assert.Equal((1860, 900), TrayPlacement.MenuAnchor(1900, 900, new PixelRect(0, 0, 1872, 1080), TaskbarEdge.Right, 1.0));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*TrayPlacementTests"`
Expected: build FAIL — `TrayPlacement`, `PixelRect`, and `TaskbarEdge` don't exist.

- [ ] **Step 3: Write `TrayPlacement`**

`src/LeafCalendar.Core/Tray/TrayPlacement.cs`:
```csharp
namespace LeafCalendar.Core.Tray;

/// <summary>A screen rectangle in physical pixels; right and bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels.</summary>
    public int Height => Bottom - Top;
}

/// <summary>The screen edge the taskbar sits on.</summary>
public enum TaskbarEdge
{
    /// <summary>Along the bottom (the default).</summary>
    Bottom,

    /// <summary>Along the top.</summary>
    Top,

    /// <summary>Along the left.</summary>
    Left,

    /// <summary>Along the right.</summary>
    Right,
}

/// <summary>
/// Where the tray flyout and the tray menu open, for any taskbar edge (spec 8.2, 8.3; design standard window sizes).
/// </summary>
/// <remarks>
/// The flyout is 360 × 560 DIPs (shorter when the screen is), 12 DIPs from the taskbar and the screen edges, next to
/// the tray icon: centered on it along a top or bottom taskbar, level with it beside a left or right one, and at the
/// far end when the icon's place is unknown. It never leaves the usable area, which is the icon monitor's work area
/// minus an auto-hidden taskbar (auto-hide gives the taskbar no work area, but it pops up over the edge). Coordinates
/// may be negative (monitors left of or above the primary one).
/// </remarks>
public static class TrayPlacement
{
    /// <summary>Flyout width in DIPs.</summary>
    public const double WidthDip = 360;

    /// <summary>Flyout height in DIPs (the tallest it gets).</summary>
    public const double HeightDip = 560;

    /// <summary>Gap to the taskbar and the screen edges in DIPs.</summary>
    public const double MarginDip = 12;

    /// <summary>The edge from <c>APPBARDATA.uEdge</c>.</summary>
    public static TaskbarEdge EdgeFromAppBar(uint edge) => edge switch
    {
        0 => TaskbarEdge.Left,
        1 => TaskbarEdge.Top,
        2 => TaskbarEdge.Right,
        _ => TaskbarEdge.Bottom,
    };

    /// <summary>The edge from the part of the monitor its work area leaves out (bottom when nothing is, like auto-hide).</summary>
    public static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect workArea)
    {
        if (workArea.Bottom < monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (workArea.Top > monitor.Top)
        {
            return TaskbarEdge.Top;
        }

        if (workArea.Left > monitor.Left)
        {
            return TaskbarEdge.Left;
        }

        return workArea.Right < monitor.Right ? TaskbarEdge.Right : TaskbarEdge.Bottom;
    }

    /// <summary>The work area, pulled in off a taskbar that overlaps it (auto-hide).</summary>
    public static PixelRect UsableArea(PixelRect workArea, PixelRect? taskbar, TaskbarEdge edge)
    {
        if (taskbar is not { } bar || bar.Right <= workArea.Left || bar.Left >= workArea.Right || bar.Bottom <= workArea.Top || bar.Top >= workArea.Bottom)
        {
            return workArea;
        }

        return edge switch
        {
            TaskbarEdge.Top   => workArea with { Top = Math.Max(workArea.Top, bar.Bottom) },
            TaskbarEdge.Left  => workArea with { Left = Math.Max(workArea.Left, bar.Right) },
            TaskbarEdge.Right => workArea with { Right = Math.Min(workArea.Right, bar.Left) },
            _                 => workArea with { Bottom = Math.Min(workArea.Bottom, bar.Top) },
        };
    }

    /// <summary>The flyout panel's rectangle.</summary>
    public static PixelRect Flyout(PixelRect area, TaskbarEdge edge, PixelRect? icon, double scale)
    {
        var margin = Px(MarginDip, scale);
        var width  = Math.Min(Px(WidthDip, scale), area.Width - 2 * margin);
        var height = Math.Min(Px(HeightDip, scale), area.Height - 2 * margin);

        // Next To The Icon, Or At The Far End (Quick Settings' corner)
        var (cx, cy) = icon is { } i ? ((i.Left + i.Right) / 2, (i.Top + i.Bottom) / 2) : (area.Right, area.Bottom);
        var minLeft  = area.Left + margin;
        var maxLeft  = area.Right - margin - width;
        var minTop   = area.Top + margin;
        var maxTop   = area.Bottom - margin - height;

        var (left, top) = edge switch
        {
            TaskbarEdge.Top   => (Clamp(cx - width / 2, minLeft, maxLeft), minTop),
            TaskbarEdge.Left  => (minLeft, Clamp(cy - height / 2, minTop, maxTop)),
            TaskbarEdge.Right => (maxLeft, Clamp(cy - height / 2, minTop, maxTop)),
            _                 => (Clamp(cx - width / 2, minLeft, maxLeft), maxTop),
        };

        return new PixelRect(left, top, left + width, top + height);
    }

    /// <summary>The panel plus one margin on every side: its taskbar-side edge is the usable area's edge, where the slide is clipped.</summary>
    public static PixelRect Frame(PixelRect panel, double scale)
    {
        var margin = Px(MarginDip, scale);
        return new PixelRect(panel.Left - margin, panel.Top - margin, panel.Right + margin, panel.Bottom + margin);
    }

    /// <summary>The frame corner the flyout opens from: its bottom-left for a bottom or left taskbar, top-left for a top one, bottom-right for a right one.</summary>
    public static (int X, int Y) FlyoutAnchor(PixelRect frame, TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => (frame.Left, frame.Top),
        TaskbarEdge.Right => (frame.Right, frame.Bottom),
        _                 => (frame.Left, frame.Bottom),
    };

    /// <summary>Where the menu opens for a click: moved off the taskbar to the usable area's edge plus the margin.</summary>
    public static (int X, int Y) MenuAnchor(int x, int y, PixelRect area, TaskbarEdge edge, double scale)
    {
        var margin = Px(MarginDip, scale);
        return edge switch
        {
            TaskbarEdge.Top   => (x, area.Top + margin),
            TaskbarEdge.Left  => (area.Left + margin, y),
            TaskbarEdge.Right => (area.Right - margin, y),
            _                 => (x, area.Bottom - margin),
        };
    }

    static int Px(double dip, double scale) => (int)Math.Round(dip * scale);

    static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*TrayPlacementTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Tray/TrayPlacement.cs tests/LeafCalendar.Tests/TrayPlacementTests.cs
git commit -m "feat(core): taskbar-aware flyout and menu placement"
```

---

### Task 10: Tray Icon and Tray-Only Lifecycle

**Files:**
- Create: `src/LeafCalendar.App/Interop/TrayIcon.cs`, `src/LeafCalendar.App/Interop/EfficiencyMode.cs`, `tests/LeafCalendar.UITests/TrayTests.cs`
- Modify: `src/LeafCalendar.App/NativeMethods.txt`, `src/LeafCalendar.App/Package.appxmanifest`, `src/LeafCalendar.App/Program.cs`, `src/LeafCalendar.App/App.xaml.cs`, `src/LeafCalendar.App/LeafServices.cs`, `src/LeafCalendar.App/MainWindow.xaml.cs`, `tests/LeafCalendar.UITests/Support/LeafApp.cs`, `tests/LeafCalendar.UITests/SettingsTests.cs`, `tests/LeafCalendar.UITests/OnboardingTests.cs`

**Interfaces:**
- Consumes: `TrayAgenda.Load/Next/Tooltip` (Task 6), `PixelRect` (Task 9), `ShiftedTimeProvider`, `LaunchOptions.Now` (Task 4), `MemoryTrimmer.Trim()`, `SettingsWindow.Open/Current`, `CalendarViewModel`, `SyncLoop.Mode`.
- Produces:
  - `internal sealed class TrayIcon(AppLog log) : IDisposable` — `event EventHandler? Invoked`, `event EventHandler<(int X, int Y)>? ContextMenuRequested`, `event EventHandler<int>? HotkeyPressed`, `nint Handle`, `void SetTooltip(string text)`, `PixelRect? IconRect()`; hidden window class `LeafCalendarTray`, callback message `WM_APP + 1`, icon ID 1
  - `internal static class EfficiencyMode` — `void Set(bool on)`
  - `internal sealed record Activation(ExtendedActivationKind Kind, string? Arguments)`; `Program.Options`, `Program.StartKind`, `Program.HandleActivations(Action<Activation>)`
  - `MainWindow(LeafServices services, CalendarViewModel calendar)` (the App owns the view model)
  - App members later tasks extend: `StartTray(LeafServices)`, `ShowMainWindow()`, `AcquireCalendar()`, `OpenSettings(SettingsSection)`, `ReleaseIfHidden()`, `GoToTray()`, `OnMinute()`, `RefreshTooltip()`, `CurrentSettings()`, `OnActivated(Activation)`, fields `_tray`, `_zone`, `_dispatcher`
  - UI test helper `LeafApp.TrayWindow()` (the hidden window's handle, 0 when none) and `LeafApp.PostTrayMessage(uint trayEvent, int x = 0, int y = 0)`

- [ ] **Step 1: Write the failing UI tests**

`tests/LeafCalendar.UITests/TrayTests.cs`:
```csharp
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TrayTests : IDisposable
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
    public void CloseMainWindow_KeepsLeafInTheTray()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        Assert.NotEqual(0, leaf.TrayWindow());

        leaf.MainWindow.Close();

        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Assert.False(leaf.App.HasExited);
        Assert.NotEqual(0, leaf.TrayWindow());
    }

    [Fact]
    public void LaunchAgainAfterClosing_OpensTheMainWindow()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        using var second = Launch();
        Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Assert.True(Retry.WhileFalse(() => leaf.IsInFront, TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void TrayIconClick_WhileInTheTray_OpensTheMainWindow()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        leaf.PostTrayMessage(LeafApp.TraySelect);

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
    }
}
```

In `tests/LeafCalendar.UITests/SettingsTests.cs`, replace `ClosingMainWindow_ClosesSettings` with:
```csharp
    [Fact]
    public void ClosingMainWindow_LeavesSettingsOpenAndLeafRunning()
    {
        using var leaf = Launch();
        leaf.OpenSettings("About");
        Assert.NotNull(leaf.WaitInSettings("AboutVersion"));

        leaf.MainWindow.Close();

        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);
        Assert.NotNull(leaf.WaitInSettings("AboutVersion"));
        Assert.False(leaf.App.HasExited);
    }
```

In `tests/LeafCalendar.UITests/OnboardingTests.cs`, replace these two lines under `// Next launch skips onboarding`:
```csharp
            leaf.MainWindow.Close();
            Assert.True(Retry.WhileFalse(() => leaf.App.HasExited, TimeSpan.FromSeconds(15)).Success);
```
with:
```csharp
            // (closing the window leaves Leaf in the tray, so end this one first)
            leaf.App.Kill();
            Assert.True(Retry.WhileFalse(() => leaf.App.HasExited, TimeSpan.FromSeconds(15)).Success);
```

In `tests/LeafCalendar.UITests/Support/LeafApp.cs`, add after `IsInFront`:
```csharp
    /// <summary>The tray icon's "clicked" event (<c>NIN_SELECT</c>).</summary>
    public const uint TraySelect = 0x0400;

    /// <summary>The tray icon's "right-clicked" event (<c>WM_CONTEXTMENU</c>).</summary>
    public const uint TrayContextMenu = 0x007B;

    /// <summary>The hidden window that owns this Leaf's tray icon, or 0 when there's none.</summary>
    public nint TrayWindow()
    {
        var hwnd = nint.Zero;
        while ((hwnd = NativeMethods.FindWindowEx(nint.Zero, hwnd, "LeafCalendarTray", null)) != nint.Zero)
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == App.ProcessId)
            {
                return hwnd;
            }
        }

        return nint.Zero;
    }

    /// <summary>
    /// Sends the tray icon an event the way the shell does with <c>NOTIFYICON_VERSION_4</c>: the event in lParam's low
    /// word, the icon ID (1) in its high word, and the anchor point in wParam. The tray area itself isn't driven, since
    /// Windows may keep the icon in the overflow.
    /// </summary>
    public void PostTrayMessage(uint trayEvent, int x = 0, int y = 0)
    {
        var hwnd = nint.Zero;
        Retry.WhileTrue(() => (hwnd = TrayWindow()) == nint.Zero, TimeSpan.FromSeconds(10));
        if (hwnd == nint.Zero)
        {
            throw new InvalidOperationException("Leaf's tray icon window didn't appear.");
        }

        NativeMethods.PostMessage(hwnd, 0x8001, (nint)((y << 16) | (x & 0xFFFF)), (nint)((1 << 16) | (int)trayEvent));
    }
```

and add to its nested `NativeMethods` class:
```csharp
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`, then `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TrayTests" --filter-class "*SettingsTests"`
Expected: FAIL — `TrayWindow()` is 0 (there's no tray icon), and closing the main window ends Leaf.

- [ ] **Step 3: Declare the Win32 calls, the startup task, and efficiency mode**

Append to `src/LeafCalendar.App/NativeMethods.txt`:
```
Shell_NotifyIcon
Shell_NotifyIconGetRect
GetModuleHandle
RegisterClassEx
CreateWindowEx
DestroyWindow
DefWindowProc
RegisterWindowMessage
CreateIconFromResourceEx
DestroyIcon
GetSystemMetricsForDpi
FindWindow
SetProcessInformation
PROCESS_POWER_THROTTLING_STATE
```

`src/LeafCalendar.App/Interop/EfficiencyMode.cs`:
```csharp
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Windows efficiency mode (EcoQoS) while Leaf is only in the tray (spec 3.4 item 3): the CPU runs Leaf's work at its
/// most efficient speed. Timers, sync, and notifications keep working.
/// </summary>
internal static unsafe class EfficiencyMode
{
    // PROCESS_POWER_THROTTLING_CURRENT_VERSION and PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    const uint CurrentVersion = 1;
    const uint ExecutionSpeed = 0x1;

    /// <summary>Turns efficiency mode on (tray only) or off (a window is open).</summary>
    public static void Set(bool on)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version     = CurrentVersion,
            ControlMask = ExecutionSpeed,
            StateMask   = on ? ExecutionSpeed : 0,
        };

        _ = PInvoke.SetProcessInformation(PInvoke.GetCurrentProcess(), PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }
}
```

Replace `src/LeafCalendar.App/Package.appxmanifest` with:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap5 rescap">

  <Identity Name="LeafCalendar" Publisher="CN=Artistro08" Version="0.1.0.0" />

  <Properties>
    <DisplayName>Leaf Calendar</DisplayName>
    <PublisherDisplayName>Devin Green</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="x-generate" />
  </Resources>

  <Applications>
    <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
      <uap:VisualElements
        DisplayName="Leaf Calendar"
        Description="A fast native calendar for Google Calendar."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
        <uap:SplashScreen Image="Assets\SplashScreen.png" />
      </uap:VisualElements>

      <Extensions>
        <!-- Starts With Windows (spec 8.1), on by default; Settings › General turns it off -->
        <uap5:Extension Category="windows.startupTask">
          <uap5:StartupTask TaskId="LeafCalendarStartup" Enabled="true" DisplayName="Leaf Calendar" />
        </uap5:Extension>
      </Extensions>
    </Application>
  </Applications>

  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

- [ ] **Step 4: Write `TrayIcon`**

`src/LeafCalendar.App/Interop/TrayIcon.cs`:
```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Tray;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Leaf's notification-area icon and the hidden window that receives its messages (spec 8.1).
/// </summary>
/// <remarks>
/// <para>
/// WinUI has no tray icon, so this calls <c>Shell_NotifyIconW</c> through CsWin32 without marshaling, the way Layers'
/// tray icon does. The hidden window (class <c>LeafCalendarTray</c>, a tool window that's never shown) is an ordinary
/// top-level window rather than <c>HWND_MESSAGE</c>, because message-only windows miss broadcasts like
/// <c>TaskbarCreated</c>. It's created on the UI thread, whose message loop dispatches its messages, so every event here
/// is raised on the UI thread.
/// </para>
/// <para>
/// The icon uses <c>NOTIFYICON_VERSION_4</c>: a click or Enter arrives as <c>NIN_SELECT</c> / <c>NIN_KEYSELECT</c>,
/// and a right-click or the menu key as <c>WM_CONTEXTMENU</c> with the anchor point in wParam. <c>TaskbarCreated</c>
/// (Explorer restarted) adds the icon again, a theme or DPI change reloads it at the taskbar's size, and
/// <c>WM_HOTKEY</c> (the global shortcuts are registered on this window) is passed on. No exception may leave the
/// window procedure: it would end the process.
/// </para>
/// <para>
/// The art is a placeholder (the app logo); Milestone 6 draws light and dark tray glyphs and gives the icon a fixed
/// <c>NIF_GUID</c> identity, which only survives updates on a signed package.
/// </para>
/// </remarks>
internal sealed unsafe class TrayIcon : IDisposable
{
    const string WindowClass = "LeafCalendarTray";
    const uint IconId        = 1;

    // Messages And Values (declared here, so a name missing from the metadata can't break the build)
    const uint CallbackMessage     = 0x8000 + 1;
    const uint WmContextMenu       = 0x007B;
    const uint WmHotkey            = 0x0312;
    const uint WmSettingChange     = 0x001A;
    const uint WmDpiChanged        = 0x02E0;
    const uint NinSelect           = 0x0400;
    const uint NinKeySelect        = 0x0401;
    const uint NotifyIconVersion4  = 4;
    const uint IconResourceVersion = 0x00030000;

    static TrayIcon? s_current;

    readonly AppLog _log;
    readonly HWND _hwnd;
    readonly uint _taskbarCreated;
    HICON _icon;
    string _tooltip = "Leaf Calendar";
    bool _disposed;

    /// <summary>Creates the hidden window and adds the icon. Only one may exist.</summary>
    /// <exception cref="InvalidOperationException">A tray icon already exists.</exception>
    /// <exception cref="Win32Exception">The window couldn't be created.</exception>
    public TrayIcon(AppLog log)
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon may exist.");
        }

        _log      = log;
        s_current = this;

        // Hidden Window
        var instance = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = WindowClass)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize        = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc   = &WindowProc,
                hInstance     = (HINSTANCE)instance.DangerousGetHandle(),
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(in windowClass);

            _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, WindowClass, "Leaf Calendar tray", WINDOW_STYLE.WS_OVERLAPPED, 0, 0, 0, 0, HWND.Null, null, instance, null);
        }

        if (_hwnd.IsNull)
        {
            s_current = null;
            throw new Win32Exception();
        }

        // Explorer Restarts Are Announced With This Message
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    /// <summary>The icon was clicked, or Enter was pressed on it.</summary>
    public event EventHandler? Invoked;

    /// <summary>The icon was right-clicked, or the menu key was pressed on it; the point is in screen pixels.</summary>
    public event EventHandler<(int X, int Y)>? ContextMenuRequested;

    /// <summary>A global shortcut registered on this window was pressed; the argument is its ID.</summary>
    public event EventHandler<int>? HotkeyPressed;

    /// <summary>The hidden window (global shortcuts are registered on it).</summary>
    public nint Handle => _hwnd;

    /// <summary>Changes the tooltip (at most 127 characters are shown).</summary>
    public void SetTooltip(string text)
    {
        if (_disposed || text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>The icon's screen rectangle, or null when the shell can't say (it's in the overflow, or Explorer is restarting).</summary>
    public PixelRect? IconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId };
        return PInvoke.Shell_NotifyIconGetRect(in id, out var rect).Succeeded ? new PixelRect(rect.left, rect.top, rect.right, rect.bottom) : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data  = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        PInvoke.DestroyWindow(_hwnd);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
        }

        s_current = null;
    }

    // Adds the icon (fails while Explorer isn't up yet at sign-in; TaskbarCreated adds it then)
    void Add()
    {
        LoadIcon();
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            _log.Info("tray.add.failed");
            return;
        }

        data.Anonymous.uVersion = NotifyIconVersion4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize           = (uint)sizeof(NOTIFYICONDATAW),
            hWnd             = _hwnd,
            uID              = IconId,
            uFlags           = flags,
            uCallbackMessage = CallbackMessage,
            hIcon            = _icon,
        };
        _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127)).CopyTo(data.szTip.AsSpan());
        return data;
    }

    // Placeholder Art: the app logo PNG, turned into an icon at the taskbar's small-icon size
    void LoadIcon()
    {
        try
        {
            var png  = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"));
            var size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, TaskbarDpi());
            HICON icon;
            fixed (byte* bits = png)
            {
                icon = PInvoke.CreateIconFromResourceEx(bits, (uint)png.Length, true, IconResourceVersion, size, size, IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }

            if (icon.IsNull)
            {
                _log.Info("tray.icon.failed");
                return;
            }

            if (!_icon.IsNull)
            {
                PInvoke.DestroyIcon(_icon);
            }

            _icon = icon;
        }
        catch (IOException ex)
        {
            _log.Error("tray.icon.failed", ex);
        }
    }

    // The taskbar's own DPI, then 96
    static uint TaskbarDpi()
    {
        var taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        var dpi     = taskbar.IsNull ? 0u : PInvoke.GetDpiForWindow(taskbar);
        return dpi == 0 ? 96u : dpi;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_current is { } self && hwnd == self._hwnd && self.OnMessage(message, wParam, lParam))
            {
                return new LRESULT(0);
            }
        }
#pragma warning disable CA1031 // An exception leaving an unmanaged callback ends the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            s_current?._log.Error("tray.message.failed", ex);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // True when the message was handled here
    bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        // Icon Events (version 4: the event in lParam's low word, the anchor point in wParam)
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam.Value & 0xFFFF))
            {
                case NinSelect or NinKeySelect:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;

                case WmContextMenu:
                    ContextMenuRequested?.Invoke(this, ((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF)));
                    break;
            }

            return true;
        }

        // Global Shortcuts
        if (message == WmHotkey)
        {
            HotkeyPressed?.Invoke(this, (int)wParam.Value);
            return true;
        }

        // Explorer Restarted
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            Add();
            return true;
        }

        // Theme Or DPI: reload the icon at the new size
        if (message is WmSettingChange or WmDpiChanged)
        {
            LoadIcon();
            var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON);
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
        }

        return false;
    }
}
```

- [ ] **Step 5: Keep how the launch started in `Program`**

Replace `src/LeafCalendar.App/Program.cs` with:
```csharp
using LeafCalendar.Core.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace LeafCalendar.App;

/// <summary>A later launch handed to the running Leaf: how it was started, and its arguments when it has any.</summary>
internal sealed record Activation(ExtendedActivationKind Kind, string? Arguments);

/// <summary>
/// Entry point. Leaf runs once per profile: a second launch with the same profile hands its activation to the running
/// Leaf and exits. How this process was started (a plain launch, Windows' sign-in startup task, or a notification) is
/// kept for the App.
/// </summary>
public static class Program
{
    // How long a second launch waits for the running Leaf to take its activation before giving up and exiting
    const uint RedirectTimeoutMs = 10_000;

    // Redirected Activations: one can arrive before the app is ready for it, so it waits here until the app is
    static readonly Lock ActivationGate = new();
    static readonly List<Activation> Pending = [];
    static Action<Activation>? _onActivated;

    /// <summary>This launch's options.</summary>
    internal static LaunchOptions Options { get; private set; } = LaunchOptions.Parse([]);

    /// <summary>How Windows started this process.</summary>
    internal static ExtendedActivationKind StartKind { get; private set; } = ExtendedActivationKind.Launch;

    /// <summary>Redirects to the running Leaf for this profile, or starts the app.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // How This Launch Started
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        StartKind = activation.Kind;
        Options   = LaunchOptions.Parse(args);

        // Single Instance Per Profile (the key ignores case, like Windows' profile folders; UI tests' parallel uitest-* profiles stay independent)
        var main = AppInstance.FindOrRegisterForKey("LeafCalendar-" + Options.Profile.ToLowerInvariant());
        if (!main.IsCurrent)
        {
            RedirectTo(main, activation);
            return;
        }

        // Listen Right Away, So A Redirect That Arrives While The App Starts Isn't Lost
        main.Activated += (_, e) => OnRedirected(Read(e));

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    /// <summary>
    /// Runs <paramref name="onActivated"/> for every later launch of this profile (on the launch's thread; marshal to
    /// the UI yourself), and now for any that arrived while the app was starting.
    /// </summary>
    internal static void HandleActivations(Action<Activation> onActivated)
    {
        Activation[] pending;
        lock (ActivationGate)
        {
            _onActivated = onActivated;
            pending      = [.. Pending];
            Pending.Clear();
        }

        foreach (var activation in pending)
        {
            onActivated(activation);
        }
    }

    static void OnRedirected(Activation activation)
    {
        Action<Activation>? handler;
        lock (ActivationGate)
        {
            handler = _onActivated;
            if (handler is null)
            {
                Pending.Add(activation);
            }
        }

        handler?.Invoke(activation);
    }

    // What a redirected activation carries
    static Activation Read(AppActivationArguments args) => new(args.Kind, null);

    // Microsoft's documented pattern: redirect on a background thread while this STA thread waits with COM pumping,
    // so the redirect can't deadlock it. The wait is bounded: a running Leaf that never answers doesn't keep this
    // process around; it just exits.
    static unsafe void RedirectTo(AppInstance main, AppActivationArguments args)
    {
        // This process was just launched by the user, so it may hand the foreground to the running Leaf
        PInvoke.AllowSetForegroundWindow(main.ProcessId);

        var redirect = Task.Run(() => main.RedirectActivationToAsync(args).AsTask().Wait());
        var handle   = (HANDLE)((IAsyncResult)redirect).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle();

        uint index;
        _ = PInvoke.CoWaitForMultipleObjects((uint)CWMO_FLAGS.CWMO_DEFAULT, RedirectTimeoutMs, 1, &handle, &index);

        // The wait handle belongs to the task, so it must outlive the wait
        GC.KeepAlive(redirect);
    }
}
```

- [ ] **Step 6: Start the clock from `--now` in `LeafServices`**

In `src/LeafCalendar.App/LeafServices.cs`, change the start of the constructor to:
```csharp
        Options  = options;
        Time     = options.Now is { } now ? new ShiftedTimeProvider(TimeProvider.System, now) : TimeProvider.System;
        Paths    = new LeafPaths(localFolder, options.Profile);
```
and replace the `Time` property with:
```csharp
    /// <summary>Clock (in fake-Google mode, <c>--now</c> starts it at a chosen instant).</summary>
    public TimeProvider Time { get; }
```

- [ ] **Step 7: Let the main window use the App's view model**

In `src/LeafCalendar.App/MainWindow.xaml.cs`:

1. Change the field `CalendarViewModel? _calendar;` to `readonly CalendarViewModel _calendar;`.
2. Change the constructor's signature, summary, and first lines to:
```csharp
    /// <summary>Creates the window on the App's calendar view model (Settings shares it). <see cref="App"/> owns both.</summary>
    public MainWindow(LeafServices services, CalendarViewModel calendar)
    {
        _services = services;
        _calendar = calendar;
        InitializeComponent();
```
3. Replace the `Closed += ...` block with:
```csharp
        Closed    += (_, _) =>
        {
            // Closing doesn't navigate, so release the page's views here; the view model is the App's (Settings may
            // still be open on it, and Leaf stays in the tray)
            (ContentFrame.Content as CalendarPage)?.Detach();
            _waitingTimer.Stop();
            _calendar.LayoutChanged   -= OnCalendarLayoutChanged;
            _calendar.PropertyChanged -= OnCalendarPropertyChanged;
        };
```
4. Replace the start of `ShowCalendar()` (the whole `if (_calendar is null) { ... }` block) with:
```csharp
        // Listen While Open (the App set the view model's OpenSettings)
        _calendar.LayoutChanged   += OnCalendarLayoutChanged;
        _calendar.PropertyChanged += OnCalendarPropertyChanged;
        ApplyTheme(_calendar.Settings.Theme);
```
5. Add after `ShowCalendar()`:
```csharp
    void OnCalendarLayoutChanged(object? sender, EventArgs e)
    {
        SyncMenu();
        ApplyTheme(_calendar.Settings.Theme);
    }
```
6. Update the class summary's last sentence to: "Settings and accounts live in their own window (<see cref="SettingsWindow"/>), which shares the App's calendar view model, so its changes show here right away; it stays open when this window closes."

- [ ] **Step 8: Rewrite `App` around the tray**

Replace `src/LeafCalendar.App/App.xaml.cs` with:
```csharp
using System.ComponentModel;
using System.Text.Json;
using LeafCalendar.App.Interop;
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views.Onboarding;
using LeafCalendar.App.Views.Settings;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace LeafCalendar.App;

/// <summary>
/// Leaf Calendar application entry. Leaf lives in the tray (spec 8.1): closing the main window keeps it running for
/// the tray icon, sync, and reminders, and only Quit ends it. Started by Windows at sign-in, it stays in the tray.
/// </summary>
/// <remarks>
/// The main window and Settings share one calendar view model, which the App creates when either opens and releases
/// once neither is open. Tray-only Leaf then polls every 60 seconds, runs in Windows efficiency mode, and trims its
/// memory (spec 3.4, 5.3).
/// </remarks>
public partial class App : Application
{
    MainWindow? _window;
    OnboardingWindow? _onboarding;
    LeafServices? _services;
    CalendarViewModel? _calendar;
    SettingsWindow? _hookedSettings;
    TrayIcon? _tray;
    SyncEngine? _attachedSync;
    DispatcherQueue _dispatcher = null!;
    DispatcherQueueTimer? _minuteTimer;
    DispatcherQueueTimer? _probeTimer;
    readonly LocalZoneWatcher _zone = new();
    AppLog? _log;
    bool _trayStarted;

    /// <summary>Loads XAML resources and hooks crash logging.</summary>
    public App()
    {
        InitializeComponent();

        // Crash Logging
        UnhandledException                    += (_, e) => _log?.Error("app.unhandled", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => _log?.Error("app.task.unobserved", e.Exception);
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options     = Program.Options;
        var localFolder = ApplicationData.Current.LocalFolder.Path;

        // Log First, So A Startup Failure Is Recorded Before The Process Ends
        _log = new AppLog(new LeafPaths(localFolder, options.Profile).LogDirectory, TimeProvider.System);

        LeafServices services;
        try
        {
            services = new LeafServices(options, localFolder);
        }
        catch (Exception ex)
        {
            _log.Error("app.start.failed", ex);
            throw;
        }

        _log        = services.Log;
        _services   = services;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // First Run: onboarding shows instead of the main window until there's an OAuth client and an account; the tray starts when it's done
        if (OnboardingFlow.IsNeeded(services.Tokens.GetClientCredentials() is not null, services.HasAccount()))
        {
            _onboarding = new OnboardingWindow(services, () =>
            {
                StartTray(services);
                ShowMainWindow();
            });
            _onboarding.Closed += async (_, _) =>
            {
                _onboarding = null;

                // Left Setup Without An Account: the app is exiting, so the services go with it
                if (!_trayStarted)
                {
                    _services = null;
                    await DisposeServicesAsync(services);
                }
            };
            _onboarding.Activate();
        }
        else
        {
            StartTray(services);

            // Started By Windows At Sign-In: stay in the tray
            if (Program.StartKind == ExtendedActivationKind.StartupTask)
            {
                GoToTray();
            }
            else
            {
                ShowMainWindow();
            }
        }

        // Later Launches Of This Profile Were Redirected Here (see Program)
        var dispatcher = _dispatcher;
        Program.HandleActivations(activation => dispatcher.TryEnqueue(() => OnActivated(activation)));
    }

    // =========================================================================
    // TRAY
    // =========================================================================

    // The tray icon and the minute clock. From here on, closing the last window doesn't end Leaf; Quit does.
    void StartTray(LeafServices services)
    {
        if (_trayStarted)
        {
            return;
        }

        _trayStarted           = true;
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Tray Icon (without one Leaf still runs, and launching it again brings the window back)
        try
        {
            _tray          = new TrayIcon(services.Log);
            _tray.Invoked += (_, _) => ShowMainWindow();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            services.Log.Error("tray.create.failed", ex);
        }

        // Minute Clock (tooltip countdown, time zone)
        _minuteTimer          = _dispatcher!.CreateTimer();
        _minuteTimer.Interval = TimeSpan.FromMinutes(1);
        _minuteTimer.Tick    += (_, _) => OnMinute();
        _minuteTimer.Start();

        // Sync Changes Refresh The Tooltip (the Google services are rebuilt when the OAuth client changes)
        services.GoogleChanged += (_, _) => _dispatcher.TryEnqueue(AttachSync);
        AttachSync();
        RefreshTooltip();
    }

    void AttachSync()
    {
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _attachedSync = _services?.Google?.Sync;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged += OnSyncDataChanged;
        }
    }

    // Raised on the sync thread
    void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher?.TryEnqueue(RefreshTooltip);

    void OnMinute()
    {
        _zone.Check();
        RefreshTooltip();
    }

    // "Standup in 12 min" (spec 8.1), within the tray lookahead setting
    void RefreshTooltip()
    {
        if (_services is not { } services || _tray is null)
        {
            return;
        }

        try
        {
            using var conn = services.Database.Open();
            var settings   = SettingsStore.Load(conn);
            var now        = services.Time.GetUtcNow();
            var soon       = TrayAgenda.Load(conn, now, _zone.Zone, 2, includeAllDay: false, settings.Use24HourTime);
            _tray.SetTooltip(TrayAgenda.Tooltip(TrayAgenda.Next(soon, now, TimeSpan.FromMinutes(settings.TrayLookaheadMinutes))));
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            services.Log.Error("tray.tooltip.failed", ex);
        }
    }

    // The saved settings (the view model may not exist while Leaf is only in the tray)
    LeafSettings CurrentSettings()
    {
        if (_calendar is { } vm)
        {
            return vm.Settings;
        }

        using var conn = _services!.Database.Open();
        return SettingsStore.Load(conn);
    }

    // =========================================================================
    // WINDOWS
    // =========================================================================

    // Opens the main window, or brings the open one to the front
    void ShowMainWindow()
    {
        if (_services is not { } services)
        {
            return;
        }

        if (_window is { } open)
        {
            open.BringToFront();
            return;
        }

        var window = new MainWindow(services, AcquireCalendar());
        window.Closed += (_, _) =>
        {
            _window = null;
            _dispatcher?.TryEnqueue(ReleaseIfHidden);
        };
        _window = window;
        EfficiencyMode.Set(false);
        window.Activate();

        // Tray Probe: the memory budget test measures tray-only Leaf a few seconds after the window rendered
        if (services.Options.TrayProbe && _probeTimer is null)
        {
            _probeTimer             = _dispatcher!.CreateTimer();
            _probeTimer.Interval    = TimeSpan.FromSeconds(3);
            _probeTimer.IsRepeating = false;
            _probeTimer.Tick       += (_, _) => _window?.Close();
            _probeTimer.Start();
        }
    }

    // Brings onboarding forward while it's open; otherwise the main window
    void BringToFront()
    {
        if (_onboarding is { } onboarding)
        {
            onboarding.BringToFront();
            return;
        }

        ShowMainWindow();
    }

    void OnActivated(Activation activation)
    {
        // Windows' sign-in start while Leaf already runs changes nothing
        if (activation.Kind == ExtendedActivationKind.StartupTask)
        {
            return;
        }

        BringToFront();
    }

    // The main window and Settings share one view model, so a Settings change shows in the calendar at once
    CalendarViewModel AcquireCalendar()
    {
        if (_calendar is null)
        {
            _calendar              = new CalendarViewModel(_services!, _dispatcher!);
            _calendar.OpenSettings = OpenSettings;
        }

        return _calendar;
    }

    void OpenSettings(SettingsSection section)
    {
        if (_services is not { } services)
        {
            return;
        }

        SettingsWindow.Open(services, AcquireCalendar(), section);
        EfficiencyMode.Set(false);

        // Watch Each Settings Window Once, To Release The View Model When It And The Main Window Are Both Closed
        if (SettingsWindow.Current is { } open && !ReferenceEquals(open, _hookedSettings))
        {
            _hookedSettings = open;
            open.Closed    += (_, _) =>
            {
                _hookedSettings = null;
                _dispatcher?.TryEnqueue(ReleaseIfHidden);
            };
        }
    }

    // Tray only: no window uses the view model any more, so its caches and timers go
    void ReleaseIfHidden()
    {
        if (_window is not null || SettingsWindow.Current is not null)
        {
            return;
        }

        _calendar?.Dispose();
        _calendar = null;
        GoToTray();
    }

    // 60-second polling, efficiency mode, and a trimmed working set
    void GoToTray()
    {
        if (_services?.Google is { } google)
        {
            google.Loop.Mode = SyncMode.Tray;
        }

        EfficiencyMode.Set(true);
        MemoryTrimmer.Trim();
    }

    // Window close must not crash the process on a disposal failure, so log and carry on
    static async Task DisposeServicesAsync(LeafServices services)
    {
        try
        {
            await services.DisposeAsync();
        }
        catch (Exception ex)
        {
            services.Log.Error("app.dispose.failed", ex);
        }
    }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run:
1. `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.
2. `pwsh tools/dev-register.ps1`
3. `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TrayTests" --filter-class "*SettingsTests" --filter-class "*OnboardingTests" --filter-class "*SingleInstanceTests"`

Expected: PASS. Then hover the tray icon by hand once: the tooltip reads "Leaf Calendar" (or the next event).

- [ ] **Step 10: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → **0 IL warnings**; then the same UI test command as Step 9 → PASS; then `pwsh tools/dev-register.ps1`.

- [ ] **Step 11: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): tray icon, tray-only lifecycle, start with Windows, and a shared calendar view model"
```

---

### Task 11: Windows Notifications for Reminders and "Join Now"

**Files:**
- Create: `src/LeafCalendar.App/Notifications/Notifier.cs`, `src/LeafCalendar.App/Notifications/AlertCenter.cs`, `tests/LeafCalendar.UITests/NotificationTests.cs`
- Modify: `src/LeafCalendar.App/Package.appxmanifest`, `src/LeafCalendar.App/App.xaml.cs`, `tests/LeafCalendar.UITests/Support/LeafApp.cs`

**Interfaces:**
- Consumes: `AlertScheduler`, `Alert`, `AlertKind` (Tasks 2–4), `ToastContent.Reminder/JoinNow/When`, `ToastMessage` (Task 7), `EventStore.Get`, `EventDetailsParser.Parse`, `SettingsStore.Load`, `LocalZoneWatcher`, `SyncEngine.DataChanged`, `EventEditor.Changed`, `ConflictResolver.Changed`, `LeafServices.GoogleChanged`.
- Produces:
  - `internal sealed class Notifier(LeafServices services) : IDisposable` — `event EventHandler<string>? Invoked` (a click's activation text, raised on a background thread), `void Register()`, `void Show(ToastMessage message)`, `Task RemoveAsync(string tag, string group)`; fake mode appends `verb\tgroup\ttag\txml` lines to `notifications.txt`
  - `internal sealed class AlertCenter(LeafServices services, Notifier notifier) : IDisposable` — `void Start()`, `void OnMinute()`
  - App fields `_notifier`, `_alerts`
  - UI test helpers `LeafApp.NotificationLines(string profile)` and `LeafApp.WaitForNotification(string profile, Func<string, bool> match, int seconds = 60)`

- [ ] **Step 1: Write the failing UI tests**

`tests/LeafCalendar.UITests/NotificationTests.cs`:
```csharp
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class NotificationTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // The fixtures' Design review is Oct 1, 2-3 PM New York, with a Meet link; the primary calendar reminds 10 minutes before
    LeafApp Launch(string now) => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");

    [Fact]
    public void Reminder_AtGooglesDefaultTime_IsShownWithJoinAndSnooze()
    {
        using var leaf = Launch("2026-10-01T13:49:50-04:00");

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\treminders\t", StringComparison.Ordinal) && l.Contains("Design review", StringComparison.Ordinal));

        Assert.Contains("content=\"Join\"", line, StringComparison.Ordinal);
        Assert.Contains("arguments=\"snooze\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("scenario=", line, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinNow_AtStart_UsesTheReminderScenario()
    {
        using var leaf = Launch("2026-10-01T13:59:50-04:00");

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tjoin\t", StringComparison.Ordinal));

        Assert.Contains("scenario=\"reminder\"", line, StringComparison.Ordinal);
        Assert.Contains("Design review", line, StringComparison.Ordinal);
        Assert.Contains("activationType=\"background\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("snooze", line, StringComparison.Ordinal);
    }

    [Fact]
    public void StartedAfterTheMeeting_ShowsNothingForIt()
    {
        using var leaf = Launch("2026-10-01T15:30:00-04:00");
        leaf.WaitFor("CalendarRoot");

        Thread.Sleep(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(LeafApp.NotificationLines(_profile), l => l.Contains("Design review", StringComparison.Ordinal));
    }

    [Fact]
    public void JoinNow_AfterTheMeetingEnds_IsWithdrawn()
    {
        // Starts 30 seconds before the meeting ends, so Join now shows at once (the look-back) and goes when it ends
        using var leaf = Launch("2026-10-01T14:59:30-04:00");

        var shown = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tjoin\t", StringComparison.Ordinal));
        var tag   = shown.Split('\t')[2];

        LeafApp.WaitForNotification(_profile, l => l == $"remove\tjoin\t{tag}\t");
    }
}
```

In `tests/LeafCalendar.UITests/Support/LeafApp.cs`, add after `LaunchedLinks`:
```csharp
    /// <summary>Notifications Leaf showed or withdrew, oldest first ("show" or "remove", group, tag, toast XML; fake-Google mode records them instead of showing them).</summary>
    public static IReadOnlyList<string> NotificationLines(string profile)
    {
        var file = Path.Combine(ProfileFolder(profile), "notifications.txt");
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file) : [];
        }
        catch (IOException)
        {
            // Leaf is writing it; the next poll reads it
            return [];
        }
    }

    /// <summary>Waits for a notification line matching <paramref name="match"/>.</summary>
    public static string WaitForNotification(string profile, Func<string, bool> match, int seconds = 60) =>
        Retry.WhileNull(() => NotificationLines(profile).FirstOrDefault(match), TimeSpan.FromSeconds(seconds)).Result
        ?? throw new InvalidOperationException("Leaf didn't show the expected notification.");
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*NotificationTests"`
Expected: FAIL — "Leaf didn't show the expected notification." (nothing writes `notifications.txt` yet).

- [ ] **Step 3: Declare the notification activator**

In `src/LeafCalendar.App/Package.appxmanifest`, change the `Package` element's namespaces to:
```xml
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap5 com desktop rescap">
```
and add inside `<Extensions>`, after the startup task:
```xml
        <!-- Notifications: Windows activates this COM class when a Leaf notification (or one of its buttons) is clicked -->
        <desktop:Extension Category="windows.toastNotificationActivation">
          <desktop:ToastNotificationActivation ToastActivatorCLSID="8E1C4A57-2B9D-4F36-A0C8-5D7E91B3F264" />
        </desktop:Extension>
        <com:Extension Category="windows.comServer">
          <com:ComServer>
            <com:ExeServer Executable="LeafCalendar.exe" DisplayName="Leaf Calendar" Arguments="----AppNotificationActivated:">
              <com:Class Id="8E1C4A57-2B9D-4F36-A0C8-5D7E91B3F264" />
            </com:ExeServer>
          </com:ComServer>
        </com:Extension>
```

- [ ] **Step 4: Write `Notifier`**

`src/LeafCalendar.App/Notifications/Notifier.cs`:
```csharp
using System.Runtime.InteropServices;
using LeafCalendar.Core.Alerts;
using Microsoft.Windows.AppNotifications;

namespace LeafCalendar.App.Notifications;

/// <summary>
/// Shows Leaf's notifications as Windows App SDK app notifications (spec 8.4) and reports clicks.
/// </summary>
/// <remarks>
/// <para>
/// Leaf is packaged, so Windows activates the COM class declared in Package.appxmanifest when a notification is clicked.
/// While Leaf runs, the click arrives here as <see cref="Invoked"/>; when it doesn't, Windows starts Leaf and
/// <see cref="Program"/> hands the click to the App. Windows Do Not Disturb and Focus apply on their own; Leaf has no
/// pause switch (spec 1.4).
/// </para>
/// <para>
/// In fake-Google mode (UI tests) nothing registers with Windows: each notification is appended to
/// <c>notifications.txt</c> in the profile folder as <c>verb, group, tag, XML</c> (tab-separated, one line), where the
/// UI tests read it. A notification Windows refuses is logged by the error only, never its content.
/// </para>
/// </remarks>
internal sealed class Notifier(LeafServices services) : IDisposable
{
    readonly Lock _fileGate = new();
    bool _registered;

    /// <summary>A notification or one of its buttons was clicked; the argument is its activation text. Raised on a background thread.</summary>
    public event EventHandler<string>? Invoked;

    bool IsFake => services.Options.FakeGoogle is not null;

    /// <summary>Registers with Windows: the click handler first, then the registration, as Windows App SDK requires.</summary>
    public void Register()
    {
        if (IsFake || _registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            services.Log.Error("notifications.register.failed", ex);
        }
    }

    /// <summary>Shows a notification (replacing one with the same tag and group).</summary>
    public void Show(ToastMessage message)
    {
        if (IsFake)
        {
            Record("show", message.Group, message.Tag, message.Xml);
            return;
        }

        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Show(new AppNotification(message.Xml) { Tag = message.Tag, Group = message.Group });
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            services.Log.Error("notifications.show.failed", ex);
        }
    }

    /// <summary>Withdraws a notification from the screen and Notification Center.</summary>
    public async Task RemoveAsync(string tag, string group)
    {
        if (IsFake)
        {
            Record("remove", group, tag, "");
            return;
        }

        if (!_registered)
        {
            return;
        }

        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            services.Log.Error("notifications.remove.failed", ex);
        }
    }

    /// <summary>Unregisters, so Windows starts a new Leaf for a later click (Quit).</summary>
    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            services.Log.Error("notifications.unregister.failed", ex);
        }
    }

    void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Invoked?.Invoke(this, args.Argument);

    void Record(string verb, string group, string tag, string xml)
    {
        try
        {
            lock (_fileGate)
            {
                File.AppendAllText(Path.Combine(services.Paths.ProfileDirectory, "notifications.txt"), $"{verb}\t{group}\t{tag}\t{xml.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
        }
        catch (IOException ex)
        {
            services.Log.Error("notifications.record.failed", ex);
        }
    }
}
```

- [ ] **Step 5: Write `AlertCenter`**

`src/LeafCalendar.App/Notifications/AlertCenter.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.App.Notifications;

/// <summary>
/// Turns Leaf's alerts into Windows notifications (spec 8.4): reminders at Google's reminder times, and the persistent
/// "Join now" at start, withdrawn once its meeting ends, moves, or is declined.
/// </summary>
/// <remarks>
/// The <see cref="AlertScheduler"/> decides what's due; every local edit, conflict answer, and sync that wrote something
/// makes it plan again, and a minute before each alert it asks for a sync (spec 5.3). Settings are read for every alert,
/// so a change in Settings › Notifications applies to the next one. Logs carry the alert's kind and tag only.
/// </remarks>
internal sealed class AlertCenter : IDisposable
{
    readonly LeafServices _services;
    readonly Notifier _notifier;
    readonly AlertScheduler _scheduler;
    readonly LocalZoneWatcher _zone = new();
    SyncEngine? _sync;

    /// <summary>Wires the scheduler to the notifier and to every source of changed events.</summary>
    public AlertCenter(LeafServices services, Notifier notifier)
    {
        _services  = services;
        _notifier  = notifier;
        _scheduler = new AlertScheduler(services.Database, services.Time, () => _zone.Zone) { IsEnabled = IsEnabled };

        // Scheduler
        _scheduler.AlertDue       += OnAlertDue;
        _scheduler.AlertRetracted += OnAlertRetracted;
        _scheduler.SyncSoon       += OnSyncSoon;
        _scheduler.Failed         += OnSchedulerFailed;

        // Changed Events Re-Plan
        services.Editor.Changed    += OnDataChanged;
        services.Conflicts.Changed += OnDataChanged;
        services.GoogleChanged     += OnGoogleChanged;
        AttachSync();
    }

    /// <summary>Starts the 15-second passes.</summary>
    public void Start() => _scheduler.Start();

    /// <summary>The App's minute clock: a new PC time zone re-plans (all-day reminders count from local midnight).</summary>
    public void OnMinute()
    {
        if (_zone.Check())
        {
            _scheduler.Invalidate();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Editor.Changed    -= OnDataChanged;
        _services.Conflicts.Changed -= OnDataChanged;
        _services.GoogleChanged     -= OnGoogleChanged;
        DetachSync();
        _scheduler.Dispose();
    }

    bool IsEnabled(AlertKind kind)
    {
        var settings = Settings();
        return kind switch
        {
            AlertKind.Reminder => settings.ReminderNotifications,
            AlertKind.JoinNow  => settings.JoinNowNotifications,
            _                  => settings.InviteNotifications,
        };
    }

    LeafSettings Settings()
    {
        using var conn = _services.Database.Open();
        return SettingsStore.Load(conn);
    }

    // Raised on the scheduler's thread
    void OnAlertDue(object? sender, Alert alert)
    {
        try
        {
            using var conn = _services.Database.Open();
            var o          = alert.Occurrence;
            if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
            {
                return;
            }

            var settings = SettingsStore.Load(conn);
            var details  = EventDetailsParser.Parse(stored.RawJson);
            var when     = ToastContent.When(o, _zone.Zone, settings.Use24HourTime, _services.Time.GetUtcNow());
            var profile  = _services.Options.Profile;
            _notifier.Show(alert.Kind == AlertKind.JoinNow
                ? ToastContent.JoinNow(alert, details, when, profile, settings.NotificationSound)
                : ToastContent.Reminder(alert, details, when, profile, settings.NotificationSound));
            _services.Log.Info("alert.shown", $"kind={alert.Kind} tag={alert.Tag}");
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            _services.Log.Error("alert.show.failed", ex);
        }
    }

    void OnAlertRetracted(object? sender, string tag)
    {
        _services.Log.Info("alert.withdrawn", $"tag={tag}");
        _ = _notifier.RemoveAsync(tag, ToastContent.JoinGroup);
    }

    void OnSyncSoon(object? sender, EventArgs e) => _services.Google?.Loop.TriggerNow();

    void OnSchedulerFailed(object? sender, Exception ex) => _services.Log.Error("alert.check.failed", ex);

    void OnDataChanged(object? sender, EventArgs e) => _scheduler.Invalidate();

    void OnGoogleChanged(object? sender, EventArgs e) => AttachSync();

    void AttachSync()
    {
        DetachSync();
        _sync = _services.Google?.Sync;
        if (_sync is not null)
        {
            _sync.DataChanged += OnDataChanged;
        }
    }

    void DetachSync()
    {
        if (_sync is not null)
        {
            _sync.DataChanged -= OnDataChanged;
        }

        _sync = null;
    }
}
```

- [ ] **Step 6: Start them with the tray**

In `src/LeafCalendar.App/App.xaml.cs`:

Add `using LeafCalendar.App.Notifications;`, and the fields:
```csharp
    Notifier? _notifier;
    AlertCenter? _alerts;
```

In `StartTray`, add before the `// Minute Clock` block:
```csharp
        // Notifications (registered before any click is handled)
        _notifier = new Notifier(services);
        _notifier.Register();
        _alerts = new AlertCenter(services, _notifier);
        _alerts.Start();
```

In `OnMinute`, add after `_zone.Check();`:
```csharp
        _alerts?.OnMinute();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*NotificationTests"`
Expected: PASS. Then by hand with your real profile (Debug): create an event 12 minutes out in Google Calendar with a Meet link, click "Sync now" in Settings › Accounts, and confirm a real Windows reminder appears 10 minutes before (Join, Snooze, Dismiss) and a "Join now" at start that stays on screen.

- [ ] **Step 8: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → 0 IL warnings; the Step 7 test command → PASS; `pwsh tools/dev-register.ps1`.

- [ ] **Step 9: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): Windows notifications for reminders and the persistent Join now"
```

---

### Task 12: Tray Menu

**Files:**
- Create: `src/LeafCalendar.App/Interop/TrayScreen.cs`, `src/LeafCalendar.App/Interop/InvisibleHost.cs`, `src/LeafCalendar.App/Tray/TrayHost.xaml`, `src/LeafCalendar.App/Tray/TrayHost.xaml.cs`, `tests/LeafCalendar.UITests/TrayMenuTests.cs`
- Modify: `src/LeafCalendar.App/NativeMethods.txt`, `src/LeafCalendar.App/App.xaml.cs`, `tests/LeafCalendar.UITests/Support/LeafApp.cs`

**Interfaces:**
- Consumes: `TrayPlacement`, `PixelRect`, `TaskbarEdge` (Task 9), `JoinPicker.Find` (Task 5), `ToastContent.NoMeeting` (Task 7), `Notifier` (Task 11), `TrayIcon.ContextMenuRequested` (Task 10), `SyncEngine.SyncAllAsync(bool, CancellationToken)`.
- Produces:
  - `internal readonly record struct TrayScreenInfo(PixelRect Area, TaskbarEdge Edge, double Scale)`; `internal static class TrayScreen` — `TrayScreenInfo At(int x, int y)`, `TrayScreenInfo Primary()`
  - `internal static class InvisibleHost` — `void Apply(Window window)`, `PointInt32 ClientOrigin(Window window)`, `void TakeForeground(Window window)`
  - `public sealed partial class TrayHost : Window` — events `OpenRequested`, `NewEventRequested`, `JoinNextRequested`, `SyncRequested`, `SettingsRequested`, `QuitRequested`; `void ShowMenu(int x, int y, AppTheme theme)`; `void Shutdown()`. Automation IDs `TrayMenuOpen`, `TrayMenuNewEvent`, `TrayMenuJoin`, `TrayMenuSync`, `TrayMenuSettings`, `TrayMenuQuit`.
  - App: `_host`, `NewEvent()`, `JoinNext()`, `SyncNow()`, `Quit()`, `_quitting`
  - UI test helpers `LeafApp.WaitForPopup(string automationId)`, `LeafApp.PopupExists(string automationId)`, `LeafApp.RightClickTrayIcon()`

- [ ] **Step 1: Write the failing UI tests**

`tests/LeafCalendar.UITests/TrayMenuTests.cs`:
```csharp
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TrayMenuTests : IDisposable
{
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch(string now = "2026-10-01T08:00:00-04:00")
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    [Fact]
    public void RightClick_ShowsTheSpecItems()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();

        foreach (var id in new[] { "TrayMenuOpen", "TrayMenuNewEvent", "TrayMenuJoin", "TrayMenuSync", "TrayMenuSettings", "TrayMenuQuit" })
        {
            Assert.NotNull(leaf.WaitForPopup(id));
        }

        Assert.Equal("Settings…", leaf.WaitForPopup("TrayMenuSettings").Name);
    }

    [Fact]
    public void Quit_EndsLeaf()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuQuit").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.App.HasExited, TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public void Settings_OpensTheSettingsWindow()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuSettings").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitInSettings("ThemeComboBox"));
    }

    [Fact]
    public void Open_WhileInTheTray_ShowsTheMainWindow()
    {
        using var leaf = Launch();
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuOpen").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
    }

    [Fact]
    public void NewEvent_OpensTheEditor()
    {
        using var leaf = Launch();

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuNewEvent").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitFor("EditorTitle"));
    }

    [Fact]
    public void JoinNext_MeetingSoon_OpensMeetWithItsAccount()
    {
        using var leaf = Launch("2026-10-01T13:55:00-04:00");

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuJoin").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void JoinNext_NothingSoon_SaysSo()
    {
        using var leaf = Launch("2026-10-01T11:00:00-04:00");

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuJoin").AsMenuItem().Invoke();

        LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tnotices\t", StringComparison.Ordinal) && l.Contains("No meeting to join", StringComparison.Ordinal), seconds: 10);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }

    [Fact]
    public void SyncNow_RefreshesTheCalendarList()
    {
        using var leaf = Launch();
        var before = _google.Requests.Count(r => r.Contains("calendarList", StringComparison.Ordinal));

        leaf.RightClickTrayIcon();
        leaf.WaitForPopup("TrayMenuSync").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => _google.Requests.Count(r => r.Contains("calendarList", StringComparison.Ordinal)) > before, TimeSpan.FromSeconds(15)).Success);
    }
}
```

In `tests/LeafCalendar.UITests/Support/LeafApp.cs`, add after `PostTrayMessage`:
```csharp
    /// <summary>Right-clicks the tray icon near the bottom-right corner of the primary screen.</summary>
    public void RightClickTrayIcon() =>
        PostTrayMessage(TrayContextMenu, NativeMethods.GetSystemMetrics(NativeMethods.PrimaryScreenWidth) - 100, NativeMethods.GetSystemMetrics(NativeMethods.PrimaryScreenHeight) - 20);

    /// <summary>
    /// Waits up to 15 s for an element in any of this Leaf's top-level windows or popups (the tray menu and flyout open
    /// in popups of their own, which aren't always reported as windows).
    /// </summary>
    public AutomationElement WaitForPopup(string automationId) =>
        Retry.WhileNull(() => FindInPopups(automationId), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in any window or popup.");

    /// <summary>True when an element with this ID is currently in one of this Leaf's windows or popups.</summary>
    public bool PopupExists(string automationId) => FindInPopups(automationId) is not null;

    AutomationElement? FindInPopups(string automationId) =>
        _automation.GetDesktop()
            .FindAllChildren(cf => cf.ByProcessId(App.ProcessId))
            .Select(w => w.Properties.AutomationId.ValueOrDefault == automationId ? w : w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)))
            .FirstOrDefault(e => e is not null);
```
and to its nested `NativeMethods` class:
```csharp
        internal const int PrimaryScreenWidth  = 0;
        internal const int PrimaryScreenHeight = 1;
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TrayMenuTests"`
Expected: FAIL — "Element 'TrayMenuOpen' didn't appear in any window or popup."

- [ ] **Step 3: Declare the Win32 calls and read the screen**

Append to `src/LeafCalendar.App/NativeMethods.txt`:
```
SHAppBarMessage
MonitorFromPoint
GetMonitorInfo
GetDpiForMonitor
GetWindowLongPtr
SetWindowLongPtr
SetLayeredWindowAttributes
ClientToScreen
```

`src/LeafCalendar.App/Interop/TrayScreen.cs`:
```csharp
using LeafCalendar.Core.Tray;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;

namespace LeafCalendar.App.Interop;

/// <summary>A monitor's usable area (work area minus an auto-hidden taskbar), taskbar edge, and scale (DPI / 96).</summary>
internal readonly record struct TrayScreenInfo(PixelRect Area, TaskbarEdge Edge, double Scale);

/// <summary>Reads where the taskbar is on a monitor (spec 8.2): Windows' own answer when that taskbar is on it, else what its work area leaves out.</summary>
internal static unsafe class TrayScreen
{
    // SHAppBarMessage: the taskbar's rectangle and edge
    const uint AbmGetTaskbarPos = 5;

    /// <summary>The monitor holding a screen point (the tray icon or a click).</summary>
    public static TrayScreenInfo At(int x, int y) =>
        For(PInvoke.MonitorFromPoint(new System.Drawing.Point(x, y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    /// <summary>The primary monitor (where the tray is when the icon's place is unknown).</summary>
    public static TrayScreenInfo Primary() =>
        For(PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY));

    static TrayScreenInfo For(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(monitor, ref info);
        var bounds = Rect(info.rcMonitor);
        var work   = Rect(info.rcWork);
        var scale  = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;

        // The Taskbar (ABM_GETTASKBARPOS answers for the primary taskbar, so it's used only when that one is on this monitor)
        var edge    = TrayPlacement.DetectEdge(bounds, work);
        PixelRect? taskbar = null;
        var bar     = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        if (PInvoke.SHAppBarMessage(AbmGetTaskbarPos, ref bar) != 0)
        {
            var rect = Rect(bar.rc);
            if (rect.Left >= bounds.Left && rect.Right <= bounds.Right && rect.Top >= bounds.Top && rect.Bottom <= bounds.Bottom)
            {
                taskbar = rect;
                edge    = TrayPlacement.EdgeFromAppBar(bar.uEdge);
            }
        }

        return new TrayScreenInfo(TrayPlacement.UsableArea(work, taskbar, edge), edge, scale);
    }

    static PixelRect Rect(RECT r) => new(r.left, r.top, r.right, r.bottom);
}
```

`src/LeafCalendar.App/Interop/InvisibleHost.cs`:
```csharp
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace LeafCalendar.App.Interop;

/// <summary>
/// The invisible, topmost anchor window a stock flyout or menu opens from (Layers' <c>WindowStyles.MakeInvisibleHost</c>).
/// </summary>
/// <remarks>
/// A stock <c>MenuFlyout</c> or <c>Flyout</c> needs a XAML root. The host is borderless, topmost, hidden from the
/// taskbar and Alt+Tab, and fully transparent (layered alpha 0), since Windows clamps a 1 × 1 size up to its minimum.
/// The flyouts draw in popups of their own (<c>ShouldConstrainToRootBounds="False"</c>), so they show normally.
/// </remarks>
internal static class InvisibleHost
{
    /// <summary>Makes <paramref name="window"/> an invisible host.</summary>
    public static void Apply(Window window)
    {
        // Tiny, Borderless, Topmost, Hidden From Switchers
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Resize(new SizeInt32(1, 1));

        // Fully Transparent
        var hwnd  = Handle(window);
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) | WINDOW_EX_STYLE.WS_EX_LAYERED;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)style);
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 0, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    /// <summary>Where the window's client area (its XAML root) starts on screen, in physical pixels.</summary>
    public static PointInt32 ClientOrigin(Window window)
    {
        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(Handle(window), ref origin);
        return new PointInt32(origin.X, origin.Y);
    }

    /// <summary>Takes the foreground (light dismiss needs it; a tray click or a hotkey allows it).</summary>
    public static void TakeForeground(Window window) => PInvoke.SetForegroundWindow(Handle(window));

    static HWND Handle(Window window) => new(WindowNative.GetWindowHandle(window));
}
```

- [ ] **Step 4: Write the host with the menu**

`src/LeafCalendar.App/Tray/TrayHost.xaml`:
```xml
<Window
    x:Class="LeafCalendar.App.Tray.TrayHost"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Leaf Calendar tray">

    <!-- Invisible Anchor For The Tray Menu -->
    <Grid x:Name="Root" Background="Transparent">

        <!-- Tray Menu (spec 8.3): every item pins the narrow padding, or the first open is touch-sized -->
        <Grid x:Name="MenuAnchor">
            <FlyoutBase.AttachedFlyout>
                <MenuFlyout x:Name="Menu" ShouldConstrainToRootBounds="False" Closed="OnMenuClosed">
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Open Leaf Calendar" Click="OnOpenClick" AutomationProperties.AutomationId="TrayMenuOpen">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE787;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="New event" Click="OnNewEventClick" AutomationProperties.AutomationId="TrayMenuNewEvent">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE710;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Join next meeting" Click="OnJoinNextClick" AutomationProperties.AutomationId="TrayMenuJoin">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE714;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>

                    <MenuFlyoutSeparator />

                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Sync now" Click="OnSyncClick" AutomationProperties.AutomationId="TrayMenuSync">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE72C;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Settings…" Click="OnSettingsClick" AutomationProperties.AutomationId="TrayMenuSettings">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE713;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>

                    <MenuFlyoutSeparator />

                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Quit" Click="OnQuitClick" AutomationProperties.AutomationId="TrayMenuQuit">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE7E8;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                </MenuFlyout>
            </FlyoutBase.AttachedFlyout>
        </Grid>
        <!-- /Tray Menu -->
    </Grid>
</Window>
```

`src/LeafCalendar.App/Tray/TrayHost.xaml.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;

namespace LeafCalendar.App.Tray;

/// <summary>
/// The tray's invisible host window (Layers' <c>TrayMenuHost</c>): the stock right-click menu opens from it, placed by
/// the taskbar edge (spec 8.3). Theme, Esc, outside-click dismissal, keyboard, and screen readers come from the stock
/// control. Created once with the tray and kept hidden; only Quit closes it.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable.")]
public sealed partial class TrayHost : Window
{
    Action? _pendingOpen;
    bool _shuttingDown;

    /// <summary>Creates the hidden host.</summary>
    public TrayHost()
    {
        InitializeComponent();
        InvisibleHost.Apply(this);

        // A host shown for the first time loads its content a moment later, so the open waits for it
        Root.Loaded += (_, _) => RunPendingOpen();

        // Only Quit Really Closes It
        AppWindow.Closing += (_, e) =>
        {
            if (!_shuttingDown)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    /// <summary>Open Leaf Calendar.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>New event.</summary>
    public event EventHandler? NewEventRequested;

    /// <summary>Join next meeting.</summary>
    public event EventHandler? JoinNextRequested;

    /// <summary>Sync now.</summary>
    public event EventHandler? SyncRequested;

    /// <summary>Settings….</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Opens the menu for a right-click at a screen point (physical pixels), growing away from the taskbar.</summary>
    public void ShowMenu(int x, int y, AppTheme theme)
    {
        var screen   = TrayScreen.At(x, y);
        var (ax, ay) = TrayPlacement.MenuAnchor(x, y, screen.Area, screen.Edge, screen.Scale);
        Root.RequestedTheme = ThemeOf(theme);
        Open(ax, ay, screen.Scale, position => Menu.ShowAt(Root, new FlyoutShowOptions { Position = position, Placement = MenuPlacement(screen.Edge) }));
    }

    /// <summary>Lets the window really close (Quit).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
    }

    // Moves the host to the anchor (twice: crossing into a monitor with another scale resizes it), shows it, takes the
    // foreground (light dismiss needs it), then opens at the anchor in DIPs from the host's client origin
    void Open(int x, int y, double scale, Action<Point> open)
    {
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(true);
        Activate();
        InvisibleHost.TakeForeground(this);

        var origin   = InvisibleHost.ClientOrigin(this);
        var position = new Point((x - origin.X) / scale, (y - origin.Y) / scale);
        _pendingOpen = () => open(position);
        if (Root.IsLoaded)
        {
            RunPendingOpen();
        }
    }

    void RunPendingOpen()
    {
        var open = _pendingOpen;
        _pendingOpen = null;
        open?.Invoke();
    }

    // Grows away from the taskbar (Sony Control's tray menu)
    static FlyoutPlacementMode MenuPlacement(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedRight,
        TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
        TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        _                 => FlyoutPlacementMode.TopEdgeAlignedRight,
    };

    static ElementTheme ThemeOf(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark  => ElementTheme.Dark,
        _              => ElementTheme.Default,
    };

    // A closed window has no AppWindow to hide
    void HideHostIfIdle()
    {
        if (!_shuttingDown && !Menu.IsOpen)
        {
            AppWindow.Hide();
        }
    }

    void OnMenuClosed(object sender, object e) => HideHostIfIdle();

    void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    void OnNewEventClick(object sender, RoutedEventArgs e) => NewEventRequested?.Invoke(this, EventArgs.Empty);

    void OnJoinNextClick(object sender, RoutedEventArgs e) => JoinNextRequested?.Invoke(this, EventArgs.Empty);

    void OnSyncClick(object sender, RoutedEventArgs e) => SyncRequested?.Invoke(this, EventArgs.Empty);

    void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 5: Wire the menu's actions in `App`**

In `src/LeafCalendar.App/App.xaml.cs`:

Add usings `using LeafCalendar.App.Tray;` and `using LeafCalendar.Core.Alerts;`, and the fields:
```csharp
    TrayHost? _host;
    bool _quitting;
```

In `StartTray`, add right after the `// Tray Icon` try/catch:
```csharp
        // Tray Menu (the host is created once and kept hidden)
        _host = new TrayHost();
        _host.OpenRequested     += (_, _) => ShowMainWindow();
        _host.NewEventRequested += (_, _) => NewEvent();
        _host.JoinNextRequested += (_, _) => JoinNext();
        _host.SyncRequested     += (_, _) => SyncNow();
        _host.SettingsRequested += (_, _) => OpenSettings(SettingsSection.General);
        _host.QuitRequested     += (_, _) => Quit();
        if (_tray is not null)
        {
            _tray.ContextMenuRequested += (_, point) => _host.ShowMenu(point.X, point.Y, CurrentSettings().Theme);
        }
```

In both `window.Closed` (in `ShowMainWindow`) and `open.Closed` (in `OpenSettings`) handlers, guard the release with the quit flag — change `_dispatcher?.TryEnqueue(ReleaseIfHidden);` to:
```csharp
            if (!_quitting)
            {
                _dispatcher?.TryEnqueue(ReleaseIfHidden);
            }
```

Add a new section before `// Window close must not crash ...`:
```csharp
    // =========================================================================
    // TRAY ACTIONS
    // =========================================================================

    // New event: the main window's editor, at the next free slot
    void NewEvent()
    {
        ShowMainWindow();
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => _calendar?.BeginCreateNow());
    }

    // Join next meeting (the tray menu and the join shortcut): the join rule (spec 8.5), else "No meeting to join"
    void JoinNext()
    {
        if (_services is not { } services)
        {
            return;
        }

        try
        {
            Uri? link;
            using (var conn = services.Database.Open())
            {
                link = JoinPicker.Find(conn, services.Time.GetUtcNow(), _zone.Zone);
            }

            if (link is null)
            {
                _notifier?.Show(ToastContent.NoMeeting(CurrentSettings().NotificationSound));
                return;
            }

            _ = services.LaunchAsync(link);
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            services.Log.Error("tray.join.failed", ex);
        }
    }

    // Sync now: calendars and events, the calendar list included
    async void SyncNow()
    {
        // async void: anything that escapes here would end the process
        try
        {
            if (_services?.Google is { } google)
            {
                await Task.Run(() => google.Sync.SyncAllAsync(refreshCalendarLists: true, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _log?.Error("tray.sync.failed", ex);
        }
    }

    // Quit: the only way Leaf ends once it's in the tray
    async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            _minuteTimer?.Stop();
            _alerts?.Dispose();
            _notifier?.Dispose();
            _tray?.Dispose();
            _tray = null;
            SettingsWindow.Current?.Close();
            _window?.Close();
            _host?.Shutdown();
            _calendar?.Dispose();
            _calendar = null;
            if (_services is { } services)
            {
                _services = null;
                await DisposeServicesAsync(services);
            }
        }
        catch (Exception ex)
        {
            _log?.Error("app.quit.failed", ex);
        }
        finally
        {
            Exit();
        }
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TrayMenuTests" --filter-class "*TrayTests"`
Expected: PASS. Then right-click the real tray icon by hand with the taskbar at the bottom, and (Settings › Personalization › Taskbar behaviors) once with auto-hide on: the menu opens above the taskbar with 12 DIPs to spare, in the app theme.

- [ ] **Step 7: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → 0 IL warnings; the Step 6 test command → PASS; `pwsh tools/dev-register.ps1`.

- [ ] **Step 8: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): XAML tray menu placed by the taskbar edge"
```

---

### Task 13: Tray Flyout

**Files:**
- Create: `src/LeafCalendar.App/Tray/ActiveAcrylicBackdrop.cs`, `src/LeafCalendar.App/Tray/AgendaRows.cs`, `tests/LeafCalendar.UITests/FlyoutTests.cs`
- Modify: `src/LeafCalendar.App/Tray/TrayHost.xaml`, `src/LeafCalendar.App/Tray/TrayHost.xaml.cs`, `src/LeafCalendar.App/App.xaml.cs`, `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`, `tests/LeafCalendar.UITests/TrayTests.cs`

**Interfaces:**
- Consumes: `TrayAgenda.Load/Next/NothingNext`, `AgendaDay`, `AgendaItem`, `NextUp` (Task 6), `TrayPlacement.Flyout/Frame/FlyoutAnchor` (Task 9), `TrayScreen` (Task 12), `JoinPicker.MeetingLink/JoinLink`, `JoinTarget` (Task 5), `TrayIcon.IconRect()/Invoked` (Task 10), `EventColors.ResolveAccent`, `LeafBrushes.FromHex`, `ScrollIndicator.ShowOnHover`.
- Produces:
  - `public sealed record AgendaModel(IReadOnlyList<AgendaDay> Days, NextUp? Next, string NothingNext)`
  - `public sealed record AgendaDayRow(string Header, List<AgendaRow> Items)`; `public sealed record AgendaRow(string Title, string When, SolidColorBrush Accent, Visibility JoinVisibility, string RowId, string JoinId, Action OnOpen, Action OnJoin)` with `Open()` and `Join()`
  - `TrayHost` additions: `bool IsAgendaOpen`, `void ShowAgenda(AgendaModel model, PixelRect? icon, AppTheme theme)`, `void UpdateAgenda(AgendaModel model)`, `void HideAgenda()`, events `AgendaOpened`, `AgendaClosed`, `EventHandler<CalendarOccurrence> OpenEventRequested`, `EventHandler<CalendarOccurrence> JoinRequested`. Automation IDs `FlyoutRoot`, `FlyoutNextTitle`, `FlyoutNextWhen`, `FlyoutJoinButton`, `FlyoutNothingNext`, `FlyoutAgenda`, `FlyoutEvent_<eventId>`, `FlyoutJoin_<eventId>`, `FlyoutAgendaEmpty`, `FlyoutNewEvent`
  - `CalendarViewModel.Reveal(CalendarOccurrence occurrence)`
  - App: `ToggleAgenda()`, `BuildAgenda()`, `LoadNext(...)`, `RevealEvent(CalendarOccurrence)`, `JoinEvent(CalendarOccurrence)`, `UpdateSyncMode(bool flyoutOpened = false)`

- [ ] **Step 1: Write the failing UI tests**

`tests/LeafCalendar.UITests/FlyoutTests.cs`:
```csharp
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class FlyoutTests : IDisposable
{
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // 1:50 PM in New York on Oct 1: Design review (2 PM, Meet) is next, 10 minutes out
    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T13:50:00-04:00");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    [Fact]
    public void TrayClick_ShowsTheNextMeetingAndTheAgenda()
    {
        using var leaf = Launch();

        leaf.PostTrayMessage(LeafApp.TraySelect);

        // (the times follow this PC's time zone, so only the countdown is checked)
        Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNextTitle").Name);
        Assert.Matches(@" · in (9|10) min$", leaf.WaitForPopup("FlyoutNextWhen").Name);
        Assert.NotNull(leaf.WaitForPopup("FlyoutJoinButton"));
        Assert.NotNull(leaf.WaitForPopup("FlyoutEvent_evt-meeting"));
        Assert.NotNull(leaf.WaitForPopup("FlyoutJoin_evt-meeting"));
    }

    [Fact]
    public void JoinButton_OpensMeetWithItsAccount()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);

        leaf.WaitForPopup("FlyoutJoinButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void EventRow_WhileInTheTray_OpensTheMainWindowOnIt()
    {
        using var leaf = Launch();
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutEvent_evt-meeting").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Design review", TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public void Escape_ClosesTheFlyout()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutRoot");

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void NewEvent_OpensTheEditor()
    {
        using var leaf = Launch();
        leaf.PostTrayMessage(LeafApp.TraySelect);

        leaf.WaitForPopup("FlyoutNewEvent").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("EditorTitle"));
    }

    [Fact]
    public void OpenAndCloseThreeTimes_KeepsWorking()
    {
        using var leaf = Launch();

        for (var i = 0; i < 3; i++)
        {
            leaf.PostTrayMessage(LeafApp.TraySelect);
            Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNextTitle").Name);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
            Thread.Sleep(400);
        }

        Assert.False(leaf.App.HasExited);
    }
}
```

In `tests/LeafCalendar.UITests/TrayTests.cs`, replace `TrayIconClick_WhileInTheTray_OpensTheMainWindow` (the click now opens the flyout) with:
```csharp
    [Fact]
    public void TrayIconClick_OpensTheFlyoutAndAgainClosesIt()
    {
        using var leaf = Launch();
        leaf.WaitFor("CalendarRoot");

        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.NotNull(leaf.WaitForPopup("FlyoutRoot"));

        Thread.Sleep(400);
        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*FlyoutTests"`
Expected: FAIL — "Element 'FlyoutNextTitle' didn't appear in any window or popup."

- [ ] **Step 3: Add the backdrop and the row types**

`src/LeafCalendar.App/Tray/ActiveAcrylicBackdrop.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Tray;

/// <summary>
/// Desktop acrylic that stays see-through while its window isn't focused (design standard 2; Sony Control's and Layers'
/// <c>ActiveAcrylicBackdrop</c>).
/// </summary>
/// <remarks>
/// The stock <see cref="DesktopAcrylicBackdrop"/> turns solid whenever its window is inactive, and the flyout's host
/// never really is, so this one always reports input as active. Theme and high contrast follow the defaults.
/// See https://learn.microsoft.com/windows/apps/windows-app-sdk/system-backdrop-controller
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "The controller is disposed in OnTargetDisconnected, the backdrop's own teardown.")]
public sealed partial class ActiveAcrylicBackdrop : SystemBackdrop
{
    readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    DesktopAcrylicController? _controller;

    /// <inheritdoc />
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // Live Acrylic (releasing any controller a missed disconnect left behind)
        _controller?.Dispose();
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
        OnDefaultSystemBackdropConfigurationChanged(connectedTarget, xamlRoot);
    }

    /// <inheritdoc />
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(disconnectedTarget);

        // Close The Target Here, On The UI Thread: XAML drops a disconnected target without closing it, and its last
        // release from the GC finalizer thread fails fast (Layers found this). The flyout's content disconnects every
        // time it closes, so this runs often; FlyoutTests.OpenAndCloseThreeTimes_KeepsWorking covers it on AOT.
        if (disconnectedTarget is IDisposable closable)
        {
            closable.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // Copy theme changes, never the "inactive" state
        var defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _configuration.Theme          = defaults.Theme;
        _configuration.IsHighContrast = defaults.IsHighContrast;
        _configuration.IsInputActive  = true;
    }
}
```

`src/LeafCalendar.App/Tray/AgendaRows.cs`:
```csharp
using LeafCalendar.Core.Tray;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Tray;

/// <summary>What the flyout shows: the agenda by day, the next event (or null), and the header's empty sentence.</summary>
public sealed record AgendaModel(IReadOnlyList<AgendaDay> Days, NextUp? Next, string NothingNext);

/// <summary>One day of the flyout's agenda, as its list shows it (an App type, so no Core type reaches a WinRT list under AOT).</summary>
public sealed record AgendaDayRow(string Header, List<AgendaRow> Items);

/// <summary>
/// One flyout row: title, time, the calendar's color dot, and the Join button when there's a link. The buttons x:Bind
/// their clicks to <see cref="Open"/> and <see cref="Join"/>, so nothing is read back from a control.
/// </summary>
public sealed record AgendaRow(string Title, string When, SolidColorBrush Accent, Visibility JoinVisibility, string RowId, string JoinId, Action OnOpen, Action OnJoin)
{
    /// <summary>Row click: opens the event in the main window.</summary>
    public void Open() => OnOpen();

    /// <summary>Join click: opens the meeting.</summary>
    public void Join() => OnJoin();
}
```

- [ ] **Step 4: Add the flyout to the host**

Replace `src/LeafCalendar.App/Tray/TrayHost.xaml` with:
```xml
<Window
    x:Class="LeafCalendar.App.Tray.TrayHost"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:LeafCalendar.App.Tray"
    Title="Leaf Calendar tray">

    <!-- Invisible Anchor For The Tray Menu And The Flyout -->
    <Grid x:Name="Root" Background="Transparent">

        <!-- Tray Menu (spec 8.3): every item pins the narrow padding, or the first open is touch-sized -->
        <Grid x:Name="MenuAnchor">
            <FlyoutBase.AttachedFlyout>
                <MenuFlyout x:Name="Menu" ShouldConstrainToRootBounds="False" Closed="OnMenuClosed">
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Open Leaf Calendar" Click="OnOpenClick" AutomationProperties.AutomationId="TrayMenuOpen">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE787;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="New event" Click="OnNewEventClick" AutomationProperties.AutomationId="TrayMenuNewEvent">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE710;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Join next meeting" Click="OnJoinNextClick" AutomationProperties.AutomationId="TrayMenuJoin">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE714;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>

                    <MenuFlyoutSeparator />

                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Sync now" Click="OnSyncClick" AutomationProperties.AutomationId="TrayMenuSync">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE72C;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Settings…" Click="OnSettingsClick" AutomationProperties.AutomationId="TrayMenuSettings">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE713;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>

                    <MenuFlyoutSeparator />

                    <MenuFlyoutItem Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}" Text="Quit" Click="OnQuitClick" AutomationProperties.AutomationId="TrayMenuQuit">
                        <MenuFlyoutItem.Icon>
                            <FontIcon Glyph="&#xE7E8;" />
                        </MenuFlyoutItem.Icon>
                    </MenuFlyoutItem>
                </MenuFlyout>
            </FlyoutBase.AttachedFlyout>
        </Grid>
        <!-- /Tray Menu -->

        <!-- Agenda Flyout (spec 8.2) -->
        <Grid x:Name="AgendaAnchor">
            <FlyoutBase.AttachedFlyout>
                <Flyout
                    x:Name="Agenda"
                    ShouldConstrainToRootBounds="False"
                    AreOpenCloseAnimationsEnabled="False"
                    Opened="OnAgendaOpened"
                    Closing="OnAgendaClosing"
                    Closed="OnAgendaClosed">

                    <!-- Bare Presenter, So The Panel Below Is The Whole Visible Flyout -->
                    <Flyout.FlyoutPresenterStyle>
                        <Style TargetType="FlyoutPresenter">
                            <Setter Property="Padding" Value="0" />
                            <Setter Property="MinWidth" Value="0" />
                            <Setter Property="MinHeight" Value="0" />
                            <Setter Property="MaxWidth" Value="10000" />
                            <Setter Property="MaxHeight" Value="10000" />
                            <Setter Property="Background" Value="Transparent" />
                            <Setter Property="BorderThickness" Value="0" />
                            <Setter Property="IsDefaultShadowEnabled" Value="False" />
                        </Style>
                    </Flyout.FlyoutPresenterStyle>

                    <!-- Frame: the panel plus 12 DIPs all round (room for its shadow); its taskbar-side edge is the work
                         area's edge, where the slide is clipped, so the panel seems to come out from behind the taskbar -->
                    <Grid x:Name="AgendaFrame" Padding="12">
                        <Grid.Resources>
                            <ResourceDictionary>
                                <ResourceDictionary.ThemeDictionaries>
                                    <ResourceDictionary x:Key="Light">
                                        <SolidColorBrush x:Key="LeafFlyoutFooterBrush" Color="#0F000000" />
                                    </ResourceDictionary>
                                    <ResourceDictionary x:Key="Dark">
                                        <SolidColorBrush x:Key="LeafFlyoutFooterBrush" Color="#33000000" />
                                    </ResourceDictionary>
                                    <ResourceDictionary x:Key="HighContrast">
                                        <SolidColorBrush x:Key="LeafFlyoutFooterBrush" Color="{ThemeResource SystemColorWindowColor}" />
                                    </ResourceDictionary>
                                </ResourceDictionary.ThemeDictionaries>
                            </ResourceDictionary>
                        </Grid.Resources>

                        <!-- Panel -->
                        <Grid
                            x:Name="AgendaPanel"
                            Width="360"
                            Height="560"
                            BorderBrush="{ThemeResource SurfaceStrokeColorDefaultBrush}"
                            BorderThickness="1"
                            CornerRadius="8"
                            Translation="0,0,32"
                            AutomationProperties.Name="Leaf Calendar agenda"
                            AutomationProperties.AutomationId="FlyoutRoot">
                            <Grid.Shadow>
                                <ThemeShadow />
                            </Grid.Shadow>
                            <Grid.RenderTransform>
                                <TranslateTransform x:Name="PanelShift" />
                            </Grid.RenderTransform>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="*" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>

                            <!-- Acrylic Background (stays active, like Windows' own flyouts) -->
                            <SystemBackdropElement Grid.RowSpan="4" CornerRadius="8">
                                <SystemBackdropElement.SystemBackdrop>
                                    <local:ActiveAcrylicBackdrop />
                                </SystemBackdropElement.SystemBackdrop>
                            </SystemBackdropElement>

                            <!-- Next Up -->
                            <StackPanel Padding="20,20,20,16" Spacing="4">
                                <TextBlock Style="{StaticResource LeafSectionHeaderStyle}" Text="Next" />
                                <StackPanel x:Name="NextPanel" Spacing="4">
                                    <TextBlock
                                        x:Name="NextTitle"
                                        Style="{StaticResource SubtitleTextBlockStyle}"
                                        MaxLines="2"
                                        TextTrimming="CharacterEllipsis"
                                        TextWrapping="Wrap"
                                        AutomationProperties.AutomationId="FlyoutNextTitle" />
                                    <TextBlock x:Name="NextWhen" Style="{StaticResource LeafSecondaryTextStyle}" AutomationProperties.AutomationId="FlyoutNextWhen" />
                                    <Button
                                        x:Name="NextJoinButton"
                                        Margin="0,12,0,0"
                                        HorizontalAlignment="Stretch"
                                        Style="{StaticResource AccentButtonStyle}"
                                        Click="OnNextJoinClick"
                                        AutomationProperties.Name="Join"
                                        AutomationProperties.AutomationId="FlyoutJoinButton">
                                        <StackPanel Orientation="Horizontal" Spacing="8">
                                            <FontIcon FontSize="14" Glyph="&#xE714;" AutomationProperties.AccessibilityView="Raw" />
                                            <TextBlock Text="Join" />
                                        </StackPanel>
                                    </Button>
                                </StackPanel>
                                <TextBlock x:Name="NothingNextText" Style="{StaticResource LeafSecondaryTextStyle}" TextWrapping="Wrap" AutomationProperties.AutomationId="FlyoutNothingNext" />
                            </StackPanel>
                            <!-- /Next Up -->

                            <!-- Divider -->
                            <Rectangle Grid.Row="1" Height="1" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />

                            <!-- Agenda (only this part scrolls) -->
                            <ScrollViewer x:Name="AgendaScroll" Grid.Row="2" AutomationProperties.AutomationId="FlyoutAgenda">
                                <StackPanel Padding="8,4,8,12">
                                    <ItemsControl x:Name="AgendaDays">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate x:DataType="local:AgendaDayRow">
                                                <StackPanel Spacing="2">
                                                    <TextBlock Margin="8,12,8,4" Style="{StaticResource BodyStrongTextBlockStyle}" Text="{x:Bind Header}" />
                                                    <ItemsControl ItemsSource="{x:Bind Items}">
                                                        <ItemsControl.ItemTemplate>
                                                            <DataTemplate x:DataType="local:AgendaRow">
                                                                <Grid ColumnSpacing="8">
                                                                    <Grid.ColumnDefinitions>
                                                                        <ColumnDefinition Width="*" />
                                                                        <ColumnDefinition Width="Auto" />
                                                                    </Grid.ColumnDefinitions>

                                                                    <!-- Row: opens the event in the main window -->
                                                                    <Button
                                                                        Padding="8,6"
                                                                        HorizontalAlignment="Stretch"
                                                                        HorizontalContentAlignment="Stretch"
                                                                        Style="{StaticResource SubtleButtonStyle}"
                                                                        Click="{x:Bind Open}"
                                                                        AutomationProperties.Name="{x:Bind Title}"
                                                                        AutomationProperties.AutomationId="{x:Bind RowId}">
                                                                        <Grid ColumnSpacing="12">
                                                                            <Grid.ColumnDefinitions>
                                                                                <ColumnDefinition Width="Auto" />
                                                                                <ColumnDefinition Width="*" />
                                                                            </Grid.ColumnDefinitions>
                                                                            <Ellipse Width="8" Height="8" VerticalAlignment="Center" Fill="{x:Bind Accent}" />
                                                                            <StackPanel Grid.Column="1">
                                                                                <TextBlock Text="{x:Bind Title}" TextTrimming="CharacterEllipsis" />
                                                                                <TextBlock Style="{StaticResource LeafSecondaryTextStyle}" Text="{x:Bind When}" />
                                                                            </StackPanel>
                                                                        </Grid>
                                                                    </Button>

                                                                    <!-- Join -->
                                                                    <Button
                                                                        Grid.Column="1"
                                                                        VerticalAlignment="Center"
                                                                        Content="Join"
                                                                        Visibility="{x:Bind JoinVisibility}"
                                                                        Click="{x:Bind Join}"
                                                                        AutomationProperties.Name="Join"
                                                                        AutomationProperties.AutomationId="{x:Bind JoinId}" />
                                                                </Grid>
                                                            </DataTemplate>
                                                        </ItemsControl.ItemTemplate>
                                                    </ItemsControl>
                                                </StackPanel>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                    <TextBlock
                                        x:Name="AgendaEmpty"
                                        Margin="8,12"
                                        Style="{StaticResource LeafSecondaryTextStyle}"
                                        Text="Nothing coming up."
                                        AutomationProperties.AutomationId="FlyoutAgendaEmpty" />
                                </StackPanel>
                            </ScrollViewer>
                            <!-- /Agenda -->

                            <!-- Footer -->
                            <Border Grid.Row="3" Padding="16,12" Background="{ThemeResource LeafFlyoutFooterBrush}" CornerRadius="0,0,8,8">
                                <Button HorizontalAlignment="Left" Click="OnNewEventClick" AutomationProperties.Name="New event" AutomationProperties.AutomationId="FlyoutNewEvent">
                                    <StackPanel Orientation="Horizontal" Spacing="8">
                                        <FontIcon FontSize="14" Glyph="&#xE710;" AutomationProperties.AccessibilityView="Raw" />
                                        <TextBlock Text="New event" />
                                    </StackPanel>
                                </Button>
                            </Border>
                        </Grid>
                        <!-- /Panel -->
                    </Grid>
                </Flyout>
            </FlyoutBase.AttachedFlyout>
        </Grid>
        <!-- /Agenda Flyout -->
    </Grid>
</Window>
```

Replace `src/LeafCalendar.App/Tray/TrayHost.xaml.cs` with:
```csharp
using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace LeafCalendar.App.Tray;

/// <summary>
/// The tray's invisible host window (Layers' pattern): the right-click menu (spec 8.3) and the left-click flyout
/// (spec 8.2) open from it as stock controls in popups of their own, placed by the taskbar edge.
/// </summary>
/// <remarks>
/// <para>
/// The flyout is a 360 × 560 DIP panel of always-active desktop acrylic (design standard 2) next to the tray icon, 12
/// DIPs from the taskbar and the screen edges. It slides in from the taskbar edge with a fade (250 ms decelerate) and
/// back out (167 ms accelerate) when it closes by Esc, focus leaving, or a second click; the popup's edge on the
/// taskbar side clips the slide. Stock light dismiss, Esc, focus, and screen reader support come from the
/// <c>Flyout</c>. The host is created once and kept hidden, so the flyout opens at once; only Quit closes it.
/// </para>
/// <para>
/// The flyout shows the next event with a large Join button, the agenda by day with a Join button per meeting, and "New
/// event". Its rows are App records (AOT list rule) whose clicks are closures over their event (AOT read-back rule).
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable.")]
public sealed partial class TrayHost : Window
{
    // Fluent Motion (design standard 11)
    static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan ExitDuration  = TimeSpan.FromMilliseconds(167);

    // The icon click that closed the flyout (by taking focus) arrives just after the close, so it mustn't reopen it
    const long ReopenGuardMs = 300;

    Action? _pendingOpen;
    TaskbarEdge _edge;
    AgendaModel? _model;
    Storyboard? _motion;
    long _agendaClosedAt;
    bool _exitFinished;
    bool _shuttingDown;

    /// <summary>Creates the hidden host.</summary>
    public TrayHost()
    {
        InitializeComponent();
        InvisibleHost.Apply(this);
        ScrollIndicator.ShowOnHover(AgendaScroll);

        // A host shown for the first time loads its content a moment later, so the open waits for it
        Root.Loaded += (_, _) => RunPendingOpen();

        // Only Quit Really Closes It
        AppWindow.Closing += (_, e) =>
        {
            if (!_shuttingDown)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    /// <summary>Open Leaf Calendar.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>New event (the menu or the flyout's footer).</summary>
    public event EventHandler? NewEventRequested;

    /// <summary>Join next meeting.</summary>
    public event EventHandler? JoinNextRequested;

    /// <summary>Sync now.</summary>
    public event EventHandler? SyncRequested;

    /// <summary>Settings….</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The flyout opened.</summary>
    public event EventHandler? AgendaOpened;

    /// <summary>The flyout closed.</summary>
    public event EventHandler? AgendaClosed;

    /// <summary>A flyout row was clicked: show that event in the main window.</summary>
    public event EventHandler<CalendarOccurrence>? OpenEventRequested;

    /// <summary>A flyout Join button was clicked.</summary>
    public event EventHandler<CalendarOccurrence>? JoinRequested;

    /// <summary>True while the flyout is open.</summary>
    public bool IsAgendaOpen => Agenda.IsOpen;

    /// <summary>Opens the menu for a right-click at a screen point (physical pixels), growing away from the taskbar.</summary>
    public void ShowMenu(int x, int y, AppTheme theme)
    {
        HideAgenda();
        var screen   = TrayScreen.At(x, y);
        var (ax, ay) = TrayPlacement.MenuAnchor(x, y, screen.Area, screen.Edge, screen.Scale);
        Root.RequestedTheme = ThemeOf(theme);
        Open(ax, ay, screen.Scale, position => Menu.ShowAt(Root, new FlyoutShowOptions { Position = position, Placement = MenuPlacement(screen.Edge) }));
    }

    /// <summary>Opens the flyout next to the tray icon (or at the primary taskbar's far end when its place is unknown).</summary>
    public void ShowAgenda(AgendaModel model, PixelRect? icon, AppTheme theme)
    {
        if (Agenda.IsOpen || Environment.TickCount64 - _agendaClosedAt < ReopenGuardMs)
        {
            return;
        }

        if (Menu.IsOpen)
        {
            Menu.Hide();
        }

        // Placement: the panel next to the icon, opened from the frame's corner on the taskbar side
        var screen   = icon is { } r ? TrayScreen.At((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2) : TrayScreen.Primary();
        var panel    = TrayPlacement.Flyout(screen.Area, screen.Edge, icon, screen.Scale);
        var (ax, ay) = TrayPlacement.FlyoutAnchor(TrayPlacement.Frame(panel, screen.Scale), screen.Edge);
        _edge                      = screen.Edge;
        AgendaPanel.Width          = panel.Width / screen.Scale;
        AgendaPanel.Height         = panel.Height / screen.Scale;
        AgendaFrame.RequestedTheme = ThemeOf(theme);
        UpdateAgenda(model);

        Open(ax, ay, screen.Scale, position => Agenda.ShowAt(Root, new FlyoutShowOptions
        {
            Position  = position,
            Placement = AgendaPlacement(screen.Edge),
            ShowMode  = FlyoutShowMode.Standard,
        }));
    }

    /// <summary>Shows new content in the flyout (a sync or the minute clock while it's open).</summary>
    public void UpdateAgenda(AgendaModel model)
    {
        _model = model;

        // Next Up
        var next = model.Next;
        NextPanel.Visibility       = next is null ? Visibility.Collapsed : Visibility.Visible;
        NothingNextText.Visibility = next is null ? Visibility.Visible : Visibility.Collapsed;
        NothingNextText.Text       = model.NothingNext;
        NextTitle.Text             = next?.Item.Title ?? "";
        NextWhen.Text              = next is null ? "" : $"{next.Item.When} · {next.Countdown}";
        NextJoinButton.Visibility  = next?.Item.Link is null ? Visibility.Collapsed : Visibility.Visible;

        // Agenda
        List<AgendaDayRow> days = [.. model.Days.Select(d => new AgendaDayRow(d.Header, [.. d.Items.Select(Row)]))];
        AgendaDays.ItemsSource = days;
        AgendaEmpty.Visibility = days.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes the flyout (it slides out first).</summary>
    public void HideAgenda()
    {
        if (Agenda.IsOpen)
        {
            Agenda.Hide();
        }
    }

    /// <summary>Lets the window really close (Quit).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
    }

    // =========================================================================
    // OPENING
    // =========================================================================

    // Moves the host to the anchor (twice: crossing into a monitor with another scale resizes it), shows it, takes the
    // foreground (light dismiss needs it), then opens at the anchor in DIPs from the host's client origin
    void Open(int x, int y, double scale, Action<Point> open)
    {
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(true);
        Activate();
        InvisibleHost.TakeForeground(this);

        var origin   = InvisibleHost.ClientOrigin(this);
        var position = new Point((x - origin.X) / scale, (y - origin.Y) / scale);
        _pendingOpen = () => open(position);
        if (Root.IsLoaded)
        {
            RunPendingOpen();
        }
    }

    void RunPendingOpen()
    {
        var open = _pendingOpen;
        _pendingOpen = null;
        open?.Invoke();
    }

    // Grows away from the taskbar (Sony Control's tray menu)
    static FlyoutPlacementMode MenuPlacement(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedRight,
        TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
        TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        _                 => FlyoutPlacementMode.TopEdgeAlignedRight,
    };

    // The frame's corner on the taskbar side sits on the anchor (TrayPlacement.FlyoutAnchor)
    static FlyoutPlacementMode AgendaPlacement(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedLeft,
        TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
        TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        _                 => FlyoutPlacementMode.TopEdgeAlignedLeft,
    };

    static ElementTheme ThemeOf(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark  => ElementTheme.Dark,
        _              => ElementTheme.Default,
    };

    // =========================================================================
    // FLYOUT
    // =========================================================================

    AgendaRow Row(AgendaItem item)
    {
        var o = item.Occurrence;
        return new AgendaRow(
            item.Title,
            item.When,
            LeafBrushes.FromHex(EventColors.ResolveAccent(o.ColorId, o.CalendarColor)),
            item.Link is null ? Visibility.Collapsed : Visibility.Visible,
            $"FlyoutEvent_{o.EventId}",
            $"FlyoutJoin_{o.EventId}",
            () => Request(OpenEventRequested, o),
            () => Request(JoinRequested, o));
    }

    // Closes the flyout, then hands the event to the App
    void Request(EventHandler<CalendarOccurrence>? handler, CalendarOccurrence occurrence)
    {
        HideAgenda();
        handler?.Invoke(this, occurrence);
    }

    void OnNextJoinClick(object sender, RoutedEventArgs e)
    {
        if (_model?.Next is { } next)
        {
            Request(JoinRequested, next.Item.Occurrence);
        }
    }

    void OnAgendaOpened(object sender, object e)
    {
        _exitFinished = false;
        Slide(HiddenOffset(), new Point(0, 0), 0, 1, EnterDuration, enter: true, onDone: null);
        AgendaOpened?.Invoke(this, EventArgs.Empty);
    }

    // Esc, focus leaving, or HideAgenda: slide back behind the taskbar first, then close for real
    void OnAgendaClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_exitFinished || _shuttingDown)
        {
            return;
        }

        args.Cancel = true;
        Slide(new Point(PanelShift.X, PanelShift.Y), HiddenOffset(), AgendaPanel.Opacity, 0, ExitDuration, enter: false, onDone: () =>
        {
            _exitFinished = true;
            Agenda.Hide();
        });
    }

    void OnAgendaClosed(object sender, object e)
    {
        _agendaClosedAt = Environment.TickCount64;
        _exitFinished   = false;
        HideHostIfIdle();
        AgendaClosed?.Invoke(this, EventArgs.Empty);
    }

    // Far enough to put the whole panel past the frame's taskbar-side edge
    Point HiddenOffset()
    {
        var width  = AgendaPanel.Width + TrayPlacement.MarginDip;
        var height = AgendaPanel.Height + TrayPlacement.MarginDip;
        return _edge switch
        {
            TaskbarEdge.Top   => new Point(0, -height),
            TaskbarEdge.Left  => new Point(-width, 0),
            TaskbarEdge.Right => new Point(width, 0),
            _                 => new Point(0, height),
        };
    }

    // Slide and fade together; the storyboard is this method's own (never read back from the panel)
    void Slide(Point from, Point to, double fromOpacity, double toOpacity, TimeSpan duration, bool enter, Action? onDone)
    {
        _motion?.Stop();
        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(PanelShift, "X", from.X, to.X, duration, enter));
        storyboard.Children.Add(Animate(PanelShift, "Y", from.Y, to.Y, duration, enter));
        storyboard.Children.Add(Animate(AgendaPanel, "Opacity", fromOpacity, toOpacity, duration, enter));
        storyboard.Completed += (_, _) =>
        {
            if (_motion == storyboard)
            {
                _motion = null;
                onDone?.Invoke();
            }
        };

        _motion = storyboard;
        storyboard.Begin();
    }

    // Fluent curves: decelerate (0,0)-(0,1) in, accelerate (1,0)-(1,1) out
    static DoubleAnimationUsingKeyFrames Animate(DependencyObject target, string property, double from, double to, TimeSpan duration, bool enter)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime   = duration,
            Value     = to,
            KeySpline = enter
                ? new KeySpline { ControlPoint1 = new Point(0, 0), ControlPoint2 = new Point(0, 1) }
                : new KeySpline { ControlPoint1 = new Point(1, 0), ControlPoint2 = new Point(1, 1) },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    // =========================================================================
    // MENU
    // =========================================================================

    // A closed window has no AppWindow to hide
    void HideHostIfIdle()
    {
        if (!_shuttingDown && !Menu.IsOpen && !Agenda.IsOpen)
        {
            AppWindow.Hide();
        }
    }

    void OnMenuClosed(object sender, object e) => HideHostIfIdle();

    void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    void OnNewEventClick(object sender, RoutedEventArgs e)
    {
        HideAgenda();
        NewEventRequested?.Invoke(this, EventArgs.Empty);
    }

    void OnJoinNextClick(object sender, RoutedEventArgs e) => JoinNextRequested?.Invoke(this, EventArgs.Empty);

    void OnSyncClick(object sender, RoutedEventArgs e) => SyncRequested?.Invoke(this, EventArgs.Empty);

    void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 5: Let the view model show an event from outside**

In `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`, add after `SelectAdjacent`:
```csharp
    /// <summary>Shows an event picked outside the window (the tray flyout, a notification): jumps to its day, selects it, and scrolls to it.</summary>
    public void Reveal(CalendarOccurrence occurrence)
    {
        NavigateTo(LocalDate(occurrence.Start));
        Select(occurrence);
        ScrollToTimeRequested?.Invoke(this, occurrence.Start);
    }
```

- [ ] **Step 6: Open the flyout from the icon in `App`**

In `src/LeafCalendar.App/App.xaml.cs`:

Add `using LeafCalendar.Core.Events;`.

In `StartTray`, change the icon's click handler from `_tray.Invoked += (_, _) => ShowMainWindow();` to:
```csharp
            _tray.Invoked += (_, _) => ToggleAgenda();
```
and add after the `_host.QuitRequested` line:
```csharp
        _host.AgendaOpened       += (_, _) => UpdateSyncMode(flyoutOpened: true);
        _host.AgendaClosed       += (_, _) => UpdateSyncMode();
        _host.OpenEventRequested += (_, occurrence) => RevealEvent(occurrence);
        _host.JoinRequested      += (_, occurrence) => JoinEvent(occurrence);
```

Replace `OnSyncDataChanged` and `OnMinute` with:
```csharp
    // Raised on the sync thread
    void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher?.TryEnqueue(() =>
    {
        RefreshTooltip();
        RefreshAgenda();
    });

    void OnMinute()
    {
        _zone.Check();
        _alerts?.OnMinute();
        RefreshTooltip();
        RefreshAgenda();
    }
```

Replace `RefreshTooltip` with:
```csharp
    // "Standup in 12 min" (spec 8.1), within the tray lookahead setting
    void RefreshTooltip()
    {
        if (_services is not { } services || _tray is null)
        {
            return;
        }

        try
        {
            using var conn = services.Database.Open();
            _tray.SetTooltip(TrayAgenda.Tooltip(LoadNext(conn, SettingsStore.Load(conn), services.Time.GetUtcNow())));
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            services.Log.Error("tray.tooltip.failed", ex);
        }
    }

    // The flyout header's and tooltip's next event: timed events within the lookahead (up to 8 hours, so two days)
    NextUp? LoadNext(SqliteConnection conn, LeafSettings settings, DateTimeOffset now) =>
        TrayAgenda.Next(TrayAgenda.Load(conn, now, _zone.Zone, 2, includeAllDay: false, settings.Use24HourTime), now, TimeSpan.FromMinutes(settings.TrayLookaheadMinutes));
```

Add a new section before `// TRAY ACTIONS`:
```csharp
    // =========================================================================
    // FLYOUT
    // =========================================================================

    // Left-click or the flyout shortcut
    void ToggleAgenda()
    {
        if (_host is null)
        {
            return;
        }

        if (_host.IsAgendaOpen)
        {
            _host.HideAgenda();
            return;
        }

        if (BuildAgenda() is { } model)
        {
            _host.ShowAgenda(model, _tray?.IconRect(), CurrentSettings().Theme);
        }
    }

    void RefreshAgenda()
    {
        if (_host is { IsAgendaOpen: true } host && BuildAgenda() is { } model)
        {
            host.UpdateAgenda(model);
        }
    }

    // The agenda (days and all-day per the Tray settings) and its header
    AgendaModel? BuildAgenda()
    {
        if (_services is not { } services)
        {
            return null;
        }

        try
        {
            using var conn = services.Database.Open();
            var settings   = SettingsStore.Load(conn);
            var now        = services.Time.GetUtcNow();
            var days       = TrayAgenda.Load(conn, now, _zone.Zone, settings.FlyoutDays, settings.FlyoutAllDay, settings.Use24HourTime);
            return new AgendaModel(days, LoadNext(conn, settings, now), TrayAgenda.NothingNext(settings.TrayLookaheadMinutes));
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            services.Log.Error("tray.agenda.failed", ex);
            return null;
        }
    }

    // A flyout row or a notification: the main window on that event
    void RevealEvent(CalendarOccurrence occurrence)
    {
        ShowMainWindow();
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => _calendar?.Reveal(occurrence));
    }

    // A Join button: the event's own link, Meet with its account
    void JoinEvent(CalendarOccurrence occurrence)
    {
        if (_services is not { } services)
        {
            return;
        }

        try
        {
            Uri? link;
            using (var conn = services.Database.Open())
            {
                link = JoinPicker.MeetingLink(conn, occurrence) is { } meeting ? JoinPicker.JoinLink(conn, new JoinTarget(occurrence, meeting)) : null;
            }

            if (link is not null)
            {
                _ = services.LaunchAsync(link);
            }
        }
        catch (SqliteException ex)
        {
            services.Log.Error("tray.join.failed", ex);
        }
    }

    // 15 s while a window or the flyout is on screen, 60 s in the tray (spec 5.3); opening the flyout syncs at once
    void UpdateSyncMode(bool flyoutOpened = false)
    {
        var visible = _window is not null || _host?.IsAgendaOpen == true;
        if (_services?.Google is { } google)
        {
            google.Loop.Mode = visible ? SyncMode.Visible : SyncMode.Tray;
            if (flyoutOpened)
            {
                google.Loop.TriggerNow();
            }
        }

        EfficiencyMode.Set(!visible && SettingsWindow.Current is null);
    }
```

Replace `GoToTray` with:
```csharp
    // 60-second polling, efficiency mode, and a trimmed working set
    void GoToTray()
    {
        UpdateSyncMode();
        MemoryTrimmer.Trim();
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*FlyoutTests" --filter-class "*TrayTests" --filter-class "*TrayMenuTests"`
Expected: PASS. Then by hand: click the real tray icon with the taskbar at the bottom, at the top, and auto-hidden: the acrylic panel slides out from the taskbar edge next to the icon, stays inside the screen with 12 DIPs to spare, keeps its acrylic when focus moves, and slides back on Esc, on a click outside, and on a second icon click. Check light and dark. Take a screenshot for the milestone report.

- [ ] **Step 8: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → 0 IL warnings; the Step 7 test command → PASS (including `OpenAndCloseThreeTimes_KeepsWorking`); `pwsh tools/dev-register.ps1`.

- [ ] **Step 9: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): acrylic tray flyout with next event, agenda, and Join"
```

---

### Task 14: Notification Actions and the Other Alerts

**Files:**
- Create: `tests/LeafCalendar.UITests/ToastActionTests.cs`
- Modify: `src/LeafCalendar.Core/Hosting/LaunchOptions.cs`, `src/LeafCalendar.App/Program.cs`, `src/LeafCalendar.App/App.xaml.cs`, `src/LeafCalendar.App/MainWindow.xaml.cs`, `src/LeafCalendar.App/Notifications/AlertCenter.cs`, `tests/LeafCalendar.Tests/LaunchOptionsTests.cs`, `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`

**Interfaces:**
- Consumes: `ToastArgs`, `ToastAction`, `ToastContent.Invite/Conflicts/SignIn` (Task 7), `InviteWatcher.TakeNew`, `InviteAlert`, `OccurrenceLookup.Find`, `SyncEngine.ConflictsFound/SignInNeeded` (Task 8), `EventEditor.Respond(CalendarOccurrence, ResponseStatus, string?, bool, EditScope)`, `ConflictStore.Count`, `ConflictDialog.ReviewAsync`, `Notifier.Invoked` (Task 11), `RevealEvent`, `JoinEvent` (Task 13), `OpenSettings` (Task 10).
- Produces:
  - `LaunchOptions` gains `string? ToastAction = null` (from `--toast-action`, fake Google only)
  - `Program.StartArgument` (the notification argument when a click started Leaf); `Activation.Arguments` filled for notifications (the argument) and launches (the command line)
  - `MainWindow.ReviewConflictsAsync()` → `Task`
  - App: `HandleToast(string? argument)`, `Respond(CalendarOccurrence, ToastAction)`
  - `FakeGoogleServer.RejectRefresh` (bool)

- [ ] **Step 1: Write the failing tests**

Add to `tests/LeafCalendar.Tests/LaunchOptionsTests.cs`:
```csharp
    [Fact]
    public void Parse_ToastActionWithFakeGoogle_IsKept()
    {
        var options = LaunchOptions.Parse(["--profile", "uitest-a", "--fake-google", "http://127.0.0.1:5000/", "--toast-action", "action=ReviewConflicts;profile=uitest-a"]);

        Assert.Equal("action=ReviewConflicts;profile=uitest-a", options.ToastAction);
    }

    [Fact]
    public void Parse_ToastActionOnARealProfile_IsIgnored()
    {
        Assert.Null(LaunchOptions.Parse(["--toast-action", "action=ReviewConflicts;profile=default"]).ToastAction);
    }
```

`tests/LeafCalendar.UITests/ToastActionTests.cs`:
```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Alerts;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ToastActionTests : IDisposable
{
    const string Primary  = "leaf.tester@gmail.com";
    const string Dentist  = "Event_evt-single_202610011300";
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";
    static readonly DateTimeOffset MeetingStart = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // 8 AM in New York on Oct 1, so the fixtures' Design review (2 PM) is still ahead whenever the tests run
    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    // A notification click as UI tests make one: a second launch hands the click's arguments to the running Leaf
    void Click(ToastArgs args)
    {
        using var second = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --toast-action {args.Encode()}");
        Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);
    }

    ToastArgs Meeting(ToastAction action, string? profile = null) =>
        new(action, profile ?? _profile, SeededProfile.AccountId, Primary, "evt-meeting", MeetingStart);

    // Offline with Google changed behind Leaf's back, then a local rename, then back online: Leaf's patch gets 412
    void MakeConflict(LeafApp leaf)
    {
        leaf.WaitFor(Dentist);
        _google.Offline = true;
        _google.EditOnGoogle(Primary, "evt-single", e => e["summary"] = "Changed on Google");
        leaf.WaitFor(Dentist).Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Mine";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();
        _google.Offline = false;
        Assert.True(Retry.WhileFalse(() => leaf.Exists("ConflictsButton"), TimeSpan.FromSeconds(45)).Success);
    }

    [Fact]
    public void Join_OpensMeetWithItsAccount()
    {
        using var leaf = Launch();

        Click(Meeting(ToastAction.Join));

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Accept_SendsYesToGoogle()
    {
        using var leaf = Launch();

        Click(Meeting(ToastAction.Accept));

        _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal) && w.Body.Contains("\"accepted\"", StringComparison.Ordinal), seconds: 30);
    }

    [Fact]
    public void Open_WhileInTheTray_ShowsTheEvent()
    {
        using var leaf = Launch();
        leaf.MainWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Leaf Calendar") > 0, TimeSpan.FromSeconds(10)).Success);

        Click(Meeting(ToastAction.Open));

        Assert.True(Retry.WhileFalse(() => leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "Design review", TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public void AnotherProfilesNotification_IsIgnored()
    {
        using var leaf = Launch();

        Click(Meeting(ToastAction.Join, profile: "uitest-someone-else"));
        Thread.Sleep(TimeSpan.FromSeconds(3));

        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }

    [Fact]
    public void Conflict_ShowsTheReviewNotification_AndItsClickOpensTheDialog()
    {
        using var leaf = Launch();
        MakeConflict(leaf);

        LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tconflicts\t", StringComparison.Ordinal) && l.Contains("1 change needs your review", StringComparison.Ordinal), seconds: 30);

        Click(new ToastArgs(ToastAction.ReviewConflicts, _profile));
        Assert.Equal("Mine", leaf.WaitForAnywhere("ConflictMine_Title").Name);
    }

    [Fact]
    public void SignInStopsWorking_ShowsSignInAgain()
    {
        _google.RejectRefresh = true;
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tsignin\t", StringComparison.Ordinal), seconds: 30);

        Assert.Contains("Sign in again", line, StringComparison.Ordinal);
        Assert.Contains("leaf.tester@gmail.com", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OrganizerChangesAnInvite_ShowsTheUpdateWithYesNoMaybe()
    {
        using var leaf = Launch();

        // The first sync recorded the pending invite quietly; the organizer now moves it to another room
        _google.EditOnGoogle(Primary, "evt-meeting", e =>
        {
            e["sequence"] = 1;
            e["location"] = "Room 5";
        });

        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tinvites\t", StringComparison.Ordinal), seconds: 45);
        Assert.Contains("Updated invitation from boss@example.com", line, StringComparison.Ordinal);
        Assert.Contains("content=\"Yes\"", line, StringComparison.Ordinal);
        Assert.Contains("content=\"Maybe\"", line, StringComparison.Ordinal);
    }
}
```

In `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs`, add after `Offline`:
```csharp
    /// <summary>When true, refreshing an access token fails with <c>invalid_grant</c>, as when the user revoked Leaf's access.</summary>
    public bool RejectRefresh { get; set; }
```
and change the `/token` branch to:
```csharp
        if (method == "POST" && path == "/token")
        {
            var grant = QueryString.Parse(body).GetValueOrDefault("grant_type");
            if (RejectRefresh && grant == "refresh_token")
            {
                return (400, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", null);
            }

            return (200, Read(grant == "authorization_code" ? "token-response.json" : "token-refresh.json"), null);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*LaunchOptionsTests"` → build FAIL (`ToastAction` doesn't exist). After Step 3, run `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*ToastActionTests"` → FAIL (the second launch only brings the window forward; no conflict, sign-in, or invite notifications).

- [ ] **Step 3: Add `--toast-action`**

In `src/LeafCalendar.Core/Hosting/LaunchOptions.cs`:

Add to the remarks list:
```csharp
/// <item><c>--toast-action &lt;arguments&gt;</c> acts as if a notification with those arguments was clicked (UI tests, through the single-instance redirect). It's honored only with <c>--fake-google</c>.</item>
```

Change the record to:
```csharp
public sealed record LaunchOptions(string Profile, bool TrayProbe, Uri? FakeGoogle = null, DateOnly? StartDate = null, DateTimeOffset? Now = null, string? ToastAction = null)
```

In `Parse`, add `string? toast = null;` under `DateTimeOffset? now = null;`, the case:
```csharp
                case "--toast-action" when i + 1 < args.Count:
                    toast = args[++i];
                    break;
```
and change the return to:
```csharp
        return new LaunchOptions(profile, trayProbe, fake, fake is null ? null : date, fake is null ? null : now, fake is null ? null : toast);
```

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*LaunchOptionsTests"` → PASS.

- [ ] **Step 4: Read what activations carry in `Program`**

In `src/LeafCalendar.App/Program.cs`:

Add usings:
```csharp
using System.Runtime.InteropServices;
using LeafCalendar.Core.Alerts;
using Microsoft.Windows.AppNotifications;
using WinRT;
```

Add after `StartKind`:
```csharp
    /// <summary>The notification's argument when a notification click started this process, else null.</summary>
    internal static string? StartArgument { get; private set; }
```

Replace the `// How This Launch Started` block in `Main` with:
```csharp
        // How This Launch Started
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var started    = Read(activation);
        StartKind      = started.Kind;
        Options        = LaunchOptions.Parse(args);

        // A Notification Click That Started Leaf Belongs To The Profile It Names (Windows starts it without --profile)
        if (started.Kind == ExtendedActivationKind.AppNotification)
        {
            StartArgument = started.Arguments;
            if (ToastArgs.Parse(started.Arguments)?.Profile is { } profile && LaunchOptions.Parse(["--profile", profile]).Profile == profile)
            {
                Options = Options with { Profile = profile };
            }
        }
```

Replace `Read` with:
```csharp
    // What an activation carries: a notification's argument, or a plain launch's command line. The WinRT payload is read
    // through As<T>(), which Native AOT supports (a C# cast of a WinRT object read back isn't safe there).
    static Activation Read(AppActivationArguments args)
    {
        if (args.Data is null)
        {
            return new Activation(args.Kind, null);
        }

        try
        {
            return args.Kind switch
            {
                ExtendedActivationKind.AppNotification => new Activation(args.Kind, args.Data.As<AppNotificationActivatedEventArgs>().Argument),
                ExtendedActivationKind.Launch          => new Activation(args.Kind, args.Data.As<Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs>().Arguments),
                _                                      => new Activation(args.Kind, null),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return new Activation(args.Kind, null);
        }
    }
```

- [ ] **Step 5: Let the conflict review open from outside the window**

In `src/LeafCalendar.App/MainWindow.xaml.cs`, replace `OnConflictsClick` with:
```csharp
    /// <summary>Opens the conflict dialog (the toolbar's conflicts button, or the "needs your review" notification).</summary>
    public async Task ReviewConflictsAsync()
    {
        // A window just opened for a notification may not have laid out yet
        if (!RootGrid.IsLoaded)
        {
            void Later(object sender, RoutedEventArgs e)
            {
                RootGrid.Loaded -= Later;
                _ = ReviewConflictsAsync();
            }

            RootGrid.Loaded += Later;
            return;
        }

        try
        {
            await ConflictDialog.ReviewAsync(RootGrid.XamlRoot, _calendar, RootGrid.ActualTheme == ElementTheme.Dark);
        }
        catch (Exception ex)
        {
            _calendar.LogError("conflict.review.failed", ex);
        }
    }

    // ReviewConflictsAsync never throws
    async void OnConflictsClick(object sender, RoutedEventArgs e) => await ReviewConflictsAsync();
```

- [ ] **Step 6: Add conflict, sign-in, and invitation notifications**

Replace `src/LeafCalendar.App/Notifications/AlertCenter.cs` with:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.App.Notifications;

/// <summary>
/// Turns Leaf's alerts into Windows notifications (spec 8.4): reminders at Google's reminder times, the persistent
/// "Join now" at start (withdrawn once its meeting ends, moves, or is declined), new and updated invitations,
/// "1 change needs your review" (spec 5.5), and "Sign in again" (spec 4.2).
/// </summary>
/// <remarks>
/// The <see cref="AlertScheduler"/> decides what's due; every local edit, conflict answer, and sync that wrote something
/// makes it plan again, and a minute before each alert it asks for a sync (spec 5.3). Each sync that wrote something
/// also looks for invitations. Settings are read for every notification, so a change in Settings › Notifications
/// applies to the next one. Logs carry the kind and the tag only.
/// </remarks>
internal sealed class AlertCenter : IDisposable
{
    readonly LeafServices _services;
    readonly Notifier _notifier;
    readonly AlertScheduler _scheduler;
    readonly LocalZoneWatcher _zone = new();
    SyncEngine? _sync;

    /// <summary>Wires the scheduler to the notifier and to every source of changed events.</summary>
    public AlertCenter(LeafServices services, Notifier notifier)
    {
        _services  = services;
        _notifier  = notifier;
        _scheduler = new AlertScheduler(services.Database, services.Time, () => _zone.Zone) { IsEnabled = IsEnabled };

        // Scheduler
        _scheduler.AlertDue       += OnAlertDue;
        _scheduler.AlertRetracted += OnAlertRetracted;
        _scheduler.SyncSoon       += OnSyncSoon;
        _scheduler.Failed         += OnSchedulerFailed;

        // Changed Events Re-Plan
        services.Editor.Changed    += OnDataChanged;
        services.Conflicts.Changed += OnConflictsChanged;
        services.GoogleChanged     += OnGoogleChanged;
        AttachSync();
    }

    /// <summary>Starts the 15-second passes.</summary>
    public void Start() => _scheduler.Start();

    /// <summary>The App's minute clock: a new PC time zone re-plans (all-day reminders count from local midnight).</summary>
    public void OnMinute()
    {
        if (_zone.Check())
        {
            _scheduler.Invalidate();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Editor.Changed    -= OnDataChanged;
        _services.Conflicts.Changed -= OnConflictsChanged;
        _services.GoogleChanged     -= OnGoogleChanged;
        DetachSync();
        _scheduler.Dispose();
    }

    // =========================================================================
    // REMINDERS AND JOIN NOW
    // =========================================================================

    bool IsEnabled(AlertKind kind)
    {
        var settings = Settings();
        return kind switch
        {
            AlertKind.Reminder => settings.ReminderNotifications,
            AlertKind.JoinNow  => settings.JoinNowNotifications,
            _                  => settings.InviteNotifications,
        };
    }

    LeafSettings Settings()
    {
        using var conn = _services.Database.Open();
        return SettingsStore.Load(conn);
    }

    // Raised on the scheduler's thread
    void OnAlertDue(object? sender, Alert alert)
    {
        try
        {
            using var conn = _services.Database.Open();
            var o          = alert.Occurrence;
            if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
            {
                return;
            }

            var settings = SettingsStore.Load(conn);
            var details  = EventDetailsParser.Parse(stored.RawJson);
            var when     = ToastContent.When(o, _zone.Zone, settings.Use24HourTime, _services.Time.GetUtcNow());
            var profile  = _services.Options.Profile;
            _notifier.Show(alert.Kind == AlertKind.JoinNow
                ? ToastContent.JoinNow(alert, details, when, profile, settings.NotificationSound)
                : ToastContent.Reminder(alert, details, when, profile, settings.NotificationSound));
            _services.Log.Info("alert.shown", $"kind={alert.Kind} tag={alert.Tag}");
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            _services.Log.Error("alert.show.failed", ex);
        }
    }

    void OnAlertRetracted(object? sender, string tag)
    {
        _services.Log.Info("alert.withdrawn", $"tag={tag}");
        _ = _notifier.RemoveAsync(tag, ToastContent.JoinGroup);
    }

    void OnSyncSoon(object? sender, EventArgs e) => _services.Google?.Loop.TriggerNow();

    void OnSchedulerFailed(object? sender, Exception ex) => _services.Log.Error("alert.check.failed", ex);

    // =========================================================================
    // SYNC SIGNALS
    // =========================================================================

    void OnDataChanged(object? sender, EventArgs e) => _scheduler.Invalidate();

    // A sync wrote something: re-plan, then look for invitations (raised on the sync thread)
    void OnSyncDataChanged(object? sender, EventArgs e)
    {
        _scheduler.Invalidate();
        ShowInvites();
    }

    void ShowInvites()
    {
        try
        {
            using var conn = _services.Database.Open();
            var settings   = SettingsStore.Load(conn);
            var now        = _services.Time.GetUtcNow();
            foreach (var invite in InviteWatcher.TakeNew(conn, now, _zone.Zone))
            {
                // Recorded either way, so turning invitations back on doesn't bring old ones
                if (!settings.InviteNotifications)
                {
                    continue;
                }

                var when = ToastContent.When(invite.Occurrence, _zone.Zone, settings.Use24HourTime, now);
                _notifier.Show(ToastContent.Invite(invite.Occurrence, invite.Details, invite.IsUpdate, invite.Tag, when, _services.Options.Profile, settings.NotificationSound));
                _services.Log.Info("alert.shown", $"kind=Invite tag={invite.Tag}");
            }
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            _services.Log.Error("alert.invites.failed", ex);
        }
    }

    // New conflicts: one notification with the total, replacing any earlier one
    void OnConflictsFound(object? sender, int found)
    {
        try
        {
            using var conn = _services.Database.Open();
            var count      = ConflictStore.Count(conn);
            if (count > 0)
            {
                _notifier.Show(ToastContent.Conflicts(count, _services.Options.Profile, SettingsStore.Load(conn).NotificationSound));
                _services.Log.Info("alert.shown", $"kind=Conflicts count={count}");
            }
        }
        catch (SqliteException ex)
        {
            _services.Log.Error("alert.conflicts.failed", ex);
        }
    }

    // A conflict was answered: re-plan, and withdraw the notification once none are left
    void OnConflictsChanged(object? sender, EventArgs e)
    {
        _scheduler.Invalidate();
        try
        {
            using var conn = _services.Database.Open();
            if (ConflictStore.Count(conn) == 0)
            {
                _ = _notifier.RemoveAsync(ToastContent.ConflictTag, ToastContent.ConflictGroup);
            }
        }
        catch (SqliteException ex)
        {
            _services.Log.Error("alert.conflicts.failed", ex);
        }
    }

    void OnSignInNeeded(object? sender, string accountId)
    {
        try
        {
            using var conn = _services.Database.Open();
            if (AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == accountId) is { } account)
            {
                _notifier.Show(ToastContent.SignIn(account.Id, account.Email, _services.Options.Profile, SettingsStore.Load(conn).NotificationSound));
                _services.Log.Info("alert.shown", $"kind=SignIn account={account.Id}");
            }
        }
        catch (SqliteException ex)
        {
            _services.Log.Error("alert.signin.failed", ex);
        }
    }

    void OnGoogleChanged(object? sender, EventArgs e) => AttachSync();

    void AttachSync()
    {
        DetachSync();
        _sync = _services.Google?.Sync;
        if (_sync is not null)
        {
            _sync.DataChanged    += OnSyncDataChanged;
            _sync.ConflictsFound += OnConflictsFound;
            _sync.SignInNeeded   += OnSignInNeeded;
        }
    }

    void DetachSync()
    {
        if (_sync is not null)
        {
            _sync.DataChanged    -= OnSyncDataChanged;
            _sync.ConflictsFound -= OnConflictsFound;
            _sync.SignInNeeded   -= OnSignInNeeded;
        }

        _sync = null;
    }
}
```

- [ ] **Step 7: Act on clicks in `App`**

In `src/LeafCalendar.App/App.xaml.cs`:

Add `using LeafCalendar.Core.Editing;` (for `EditScope`; `OccurrenceLookup` and `ResponseStatus` are in `LeafCalendar.Core.Events`, imported in Task 13, and `ToastArgs` in `LeafCalendar.Core.Alerts`, imported in Task 12).

In `StartTray`, after `_notifier.Register();` add:
```csharp
        _notifier.Invoked += (_, argument) => _dispatcher.TryEnqueue(() => HandleToast(argument));
```

In `OnLaunched`, replace the `// Started By Windows At Sign-In: stay in the tray` block with:
```csharp
            // Started By Windows At Sign-In, Or By A Notification Click: the tray, plus what the click asked for
            if (Program.StartKind is ExtendedActivationKind.StartupTask or ExtendedActivationKind.AppNotification)
            {
                GoToTray();
                if (Program.StartKind == ExtendedActivationKind.AppNotification)
                {
                    HandleToast(Program.StartArgument);
                }
            }
            else
            {
                ShowMainWindow();
            }
```

Replace `OnActivated` with:
```csharp
    void OnActivated(Activation activation)
    {
        switch (activation.Kind)
        {
            // Windows' sign-in start while Leaf already runs changes nothing
            case ExtendedActivationKind.StartupTask:
                return;

            // A notification click that Windows handed to a new process, which passed it on
            case ExtendedActivationKind.AppNotification:
                HandleToast(activation.Arguments);
                return;

            // UI tests click notifications with "--toast-action" on a second launch (fake-Google profiles only)
            case ExtendedActivationKind.Launch when TestToastAction(activation.Arguments) is { } toast:
                HandleToast(toast);
                return;

            default:
                BringToFront();
                return;
        }
    }

    static string? TestToastAction(string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine) ? null : LaunchOptions.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToastAction;
```

Add a new section after `// FLYOUT`'s methods:
```csharp
    // =========================================================================
    // NOTIFICATION CLICKS
    // =========================================================================

    // A notification or one of its buttons was clicked (spec 8.4)
    void HandleToast(string? argument)
    {
        if (_services is not { } services || ToastArgs.Parse(argument) is not { } toast)
        {
            return;
        }

        // Another Profile's Notification (every profile shares Leaf's notification identity)
        if (toast.Profile != services.Options.Profile)
        {
            services.Log.Info("notification.other-profile");
            return;
        }

        try
        {
            switch (toast.Action)
            {
                case ToastAction.ReviewConflicts:
                    ShowMainWindow();
                    _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => _ = _window?.ReviewConflictsAsync());
                    return;

                case ToastAction.SignIn:
                    OpenSettings(SettingsSection.Accounts);
                    return;
            }

            // About An Event: find the instance again (it may have moved or gone since)
            CalendarOccurrence? occurrence;
            using (var conn = services.Database.Open())
            {
                occurrence = OccurrenceLookup.Find(conn, toast.AccountId!, toast.CalendarId!, toast.EventId!, toast.Start!.Value, _zone.Zone);
            }

            if (occurrence is null)
            {
                services.Log.Info("notification.event-gone");
                return;
            }

            switch (toast.Action)
            {
                case ToastAction.Open:
                    RevealEvent(occurrence);
                    break;

                case ToastAction.Join:
                    JoinEvent(occurrence);
                    break;

                default:
                    Respond(occurrence, toast.Action);
                    break;
            }
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            services.Log.Error("notification.action.failed", ex);
        }
    }

    // Yes / No / Maybe on an invitation: Google emails the organizer, like its own buttons; a repeating invitation is answered for the series
    void Respond(CalendarOccurrence occurrence, ToastAction action)
    {
        var response = action switch
        {
            ToastAction.Accept  => ResponseStatus.Accepted,
            ToastAction.Decline => ResponseStatus.Declined,
            _                   => ResponseStatus.Tentative,
        };
        var scope = occurrence.RecurringEventId == occurrence.EventId ? EditScope.All : EditScope.This;
        _services!.Editor.Respond(occurrence, response, note: null, sendUpdates: true, scope);
    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`, then `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*ToastActionTests" --filter-class "*NotificationTests" --filter-class "*OfflineConflictTests" --filter-class "*SingleInstanceTests"`
Expected: PASS.

Then by hand with your real profile (Debug): click a real reminder's body (the main window opens on the event), its Join (Meet opens with your account), and, with Leaf quit from the tray menu, click a reminder in Notification Center: Leaf starts in the tray and does what the click asked (the cold-start path, `Program.StartArgument`).

- [ ] **Step 9: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → 0 IL warnings; the Step 8 UI test command → PASS (this covers the `As<T>()` read of a redirected launch on AOT); repeat the cold-start click by hand on the AOT build; `pwsh tools/dev-register.ps1`.

- [ ] **Step 10: Commit**

```bash
git add src tests
git commit -m "feat(app): act on notification clicks; conflict, sign-in, and invitation notifications"
```

---

### Task 15: Global Shortcuts

**Files:**
- Create: `src/LeafCalendar.App/Interop/GlobalShortcuts.cs`, `tests/LeafCalendar.UITests/ShortcutTests.cs`
- Modify: `src/LeafCalendar.App/NativeMethods.txt`, `src/LeafCalendar.App/LeafServices.cs`, `src/LeafCalendar.App/App.xaml.cs`, `tests/LeafCalendar.UITests/Support/SeededProfile.cs`

**Interfaces:**
- Consumes: `Hotkey`, `HotkeyModifiers`, `LeafSettings.JoinShortcut/FlyoutShortcut` (Task 1), `TrayIcon.Handle/HotkeyPressed` (Task 10), `JoinNext()` (Task 12), `ToggleAgenda()` (Task 13).
- Produces:
  - `public enum ShortcutAction { Join = 1, Flyout = 2 }` (the values are the hotkey IDs)
  - `public sealed class GlobalShortcuts` — `event EventHandler<ShortcutAction>? Pressed`, `event EventHandler? Changed`, `void Attach(nint hwnd, LeafSettings settings)`, `void Apply(LeafSettings settings)`, `void Suspend()`, `bool IsFree(Hotkey hotkey)`, `bool IsTaken(ShortcutAction action)`, `void OnHotkey(int id)`
  - `LeafServices.Shortcuts` (`GlobalShortcuts`)
  - `SeededProfile.Create(LeafSettings? settings = null)`

- [ ] **Step 1: Write the failing UI tests**

In `tests/LeafCalendar.UITests/Support/SeededProfile.cs`, add `using LeafCalendar.Core.Settings;`, change the method to take settings, and save them with the account:
```csharp
    /// <summary>Creates the profile (with <paramref name="settings"/> saved, when given) and returns its name. Clean it up with <see cref="LeafApp.DeleteProfile"/>.</summary>
    public static string Create(LeafSettings? settings = null)
```
and in its `using (var conn = database.Open())` block, after `AccountStore.Upsert(...)`:
```csharp
            if (settings is not null)
            {
                SettingsStore.Save(conn, settings);
            }
```

`tests/LeafCalendar.UITests/ShortcutTests.cs`:
```csharp
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ShortcutTests : IDisposable
{
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    // Combinations nothing else uses, so a Leaf you have running doesn't hold them first (F12 is Windows' debugger key)
    static readonly LeafSettings Shortcuts = new() { JoinShortcut = "Ctrl+Alt+Shift+F9", FlyoutShortcut = "Ctrl+Alt+Shift+F10" };

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create(Shortcuts);

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch(string now)
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    static void Press(VirtualKeyShort key) => Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, key);

    [Fact]
    public void JoinShortcut_MeetingSoon_OpensMeetWithItsAccount()
    {
        using var leaf = Launch("2026-10-01T13:55:00-04:00");

        Press(VirtualKeyShort.F9);

        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void JoinShortcut_NothingSoon_SaysSo()
    {
        using var leaf = Launch("2026-10-01T11:00:00-04:00");

        Press(VirtualKeyShort.F9);

        LeafApp.WaitForNotification(_profile, l => l.Contains("No meeting to join", StringComparison.Ordinal), seconds: 10);
    }

    [Fact]
    public void FlyoutShortcut_ShowsAndHidesTheFlyout()
    {
        using var leaf = Launch("2026-10-01T13:50:00-04:00");

        Press(VirtualKeyShort.F10);
        Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNextTitle").Name);

        Thread.Sleep(400);
        Press(VirtualKeyShort.F10);
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*ShortcutTests"`
Expected: FAIL — nothing is registered, so the keys do nothing.

- [ ] **Step 3: Write `GlobalShortcuts`**

Append to `src/LeafCalendar.App/NativeMethods.txt`:
```
RegisterHotKey
UnregisterHotKey
```

`src/LeafCalendar.App/Interop/GlobalShortcuts.cs`:
```csharp
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace LeafCalendar.App.Interop;

/// <summary>What a global shortcut does. The values are the hotkey IDs registered with Windows.</summary>
public enum ShortcutAction
{
    /// <summary>Join the next meeting (spec 8.5), Ctrl+Alt+J by default.</summary>
    Join = 1,

    /// <summary>Show or hide the tray flyout, Ctrl+Alt+K by default.</summary>
    Flyout = 2,
}

/// <summary>
/// Leaf's global shortcuts (spec 8.6), registered with <c>RegisterHotKey</c> on the tray icon's hidden window, whose
/// <c>WM_HOTKEY</c> comes back through <see cref="OnHotkey"/>.
/// </summary>
/// <remarks>
/// A combination another app registered first can't be taken: it's remembered as taken, and Settings › Shortcuts warns
/// about it and asks for another. <c>MOD_NOREPEAT</c> keeps a held combination from firing over and over. Call on the
/// UI thread.
/// </remarks>
public sealed class GlobalShortcuts
{
    // A spare ID for checking whether a combination is free
    const int ProbeId = 0x7FFF;

    readonly HashSet<ShortcutAction> _registered = [];
    readonly HashSet<ShortcutAction> _taken = [];
    HWND _hwnd;

    /// <summary>A shortcut was pressed.</summary>
    public event EventHandler<ShortcutAction>? Pressed;

    /// <summary>Which shortcuts are registered or taken changed (Settings › Shortcuts refreshes).</summary>
    public event EventHandler? Changed;

    /// <summary>Starts listening on the tray icon's window with the saved shortcuts.</summary>
    public void Attach(nint hwnd, LeafSettings settings)
    {
        _hwnd = new HWND(hwnd);
        Apply(settings);
    }

    /// <summary>Registers the saved shortcuts again (after a change, or after the shortcut dialog).</summary>
    public void Apply(LeafSettings settings)
    {
        Suspend();
        _taken.Clear();
        Register(ShortcutAction.Join, settings.JoinShortcut);
        Register(ShortcutAction.Flyout, settings.FlyoutShortcut);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of Leaf's shortcuts (while the shortcut dialog listens for keys, and on Quit).</summary>
    public void Suspend()
    {
        foreach (var action in _registered)
        {
            PInvoke.UnregisterHotKey(_hwnd, (int)action);
        }

        _registered.Clear();
    }

    /// <summary>True when Windows would let Leaf register <paramref name="hotkey"/> now.</summary>
    public bool IsFree(Hotkey hotkey)
    {
        if (_hwnd.IsNull)
        {
            return true;
        }

        if (!PInvoke.RegisterHotKey(_hwnd, ProbeId, Modifiers(hotkey), (uint)hotkey.Key))
        {
            return false;
        }

        PInvoke.UnregisterHotKey(_hwnd, ProbeId);
        return true;
    }

    /// <summary>True when the saved combination for <paramref name="action"/> is held by another app.</summary>
    public bool IsTaken(ShortcutAction action) => _taken.Contains(action);

    /// <summary>A <c>WM_HOTKEY</c> arrived with this ID.</summary>
    public void OnHotkey(int id)
    {
        if (id is (int)ShortcutAction.Join or (int)ShortcutAction.Flyout)
        {
            Pressed?.Invoke(this, (ShortcutAction)id);
        }
    }

    // An empty (or unreadable) setting means no shortcut
    void Register(ShortcutAction action, string text)
    {
        if (_hwnd.IsNull || !Hotkey.TryParse(text, out var hotkey))
        {
            return;
        }

        if (PInvoke.RegisterHotKey(_hwnd, (int)action, Modifiers(hotkey), (uint)hotkey.Key))
        {
            _registered.Add(action);
        }
        else
        {
            _taken.Add(action);
        }
    }

    static HOT_KEY_MODIFIERS Modifiers(Hotkey hotkey) => (HOT_KEY_MODIFIERS)(uint)hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
}
```

In `src/LeafCalendar.App/LeafServices.cs`, add `using LeafCalendar.App.Interop;` and, after `Conflicts`:
```csharp
    /// <summary>Global shortcuts (registered once the tray icon exists).</summary>
    public GlobalShortcuts Shortcuts { get; } = new();
```

- [ ] **Step 4: Register them with the tray**

In `src/LeafCalendar.App/App.xaml.cs`, in `StartTray`, add after the tray menu block:
```csharp
        // Global Shortcuts (spec 8.6), on the tray icon's window
        services.Shortcuts.Pressed += (_, action) =>
        {
            if (action == ShortcutAction.Join)
            {
                JoinNext();
            }
            else
            {
                ToggleAgenda();
            }
        };
        if (_tray is not null)
        {
            _tray.HotkeyPressed += (_, id) => services.Shortcuts.OnHotkey(id);
            services.Shortcuts.Attach(_tray.Handle, CurrentSettings());
        }
```

In `Quit`, add before `_tray?.Dispose();`:
```csharp
            _services?.Shortcuts.Suspend();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*ShortcutTests"`
Expected: PASS. Then by hand with your real profile: with a meeting starting in 5 minutes on your second Google account, press Ctrl+Alt+J from another app: Meet opens in the browser signed in as that account.

- [ ] **Step 6: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → 0 IL warnings; the Step 5 test command → PASS; `pwsh tools/dev-register.ps1`.

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): global join and flyout shortcuts"
```

---

### Task 16: Settings for Notifications, Tray, Shortcuts, and Startup

**Files:**
- Create: `src/LeafCalendar.App/Views/Settings/NotificationsPage.xaml(.cs)`, `src/LeafCalendar.App/Views/Settings/TrayPage.xaml(.cs)`, `src/LeafCalendar.App/Views/Settings/ShortcutsPage.xaml(.cs)`, `src/LeafCalendar.App/Views/Settings/ShortcutDialog.cs`, `tests/LeafCalendar.UITests/TraySettingsTests.cs`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (`SettingsSection`), `src/LeafCalendar.App/Views/Settings/SettingsWindow.xaml.cs`, `src/LeafCalendar.App/Views/Settings/GeneralPage.xaml(.cs)`, `src/LeafCalendar.App/App.xaml.cs`

**Interfaces:**
- Consumes: `LeafSettings` new fields and `LookaheadChoices` (Task 1), `Hotkey`, `HotkeyModifiers` (Task 1), `GlobalShortcuts` and `ShortcutAction` (Task 15), `SettingsContext`, `SettingRow`, `CalendarViewModel.Update/Settings/LogError`, `KeyState.IsDown`, `ScrollIndicator.ShowOnHover`, `Windows.ApplicationModel.StartupTask`.
- Produces:
  - `SettingsSection.Notifications`, `SettingsSection.Tray`, `SettingsSection.Shortcuts`; nav IDs `SettingsNav_Notifications`, `SettingsNav_Tray`, `SettingsNav_Shortcuts`
  - Automation IDs: `RemindersSwitch`, `JoinNowSwitch`, `InvitesSwitch`, `SoundSwitch`; `FlyoutDaysNumberBox`, `FlyoutAllDaySwitch`, `LookaheadComboBox`; `JoinShortcutButton`, `FlyoutShortcutButton` (help text = the shortcut), `JoinShortcutWarning`, `FlyoutShortcutWarning`; dialog `ShortcutPreview`, `ShortcutProblem`; `StartupSwitch`
  - `internal static class ShortcutDialog` — `Task<Hotkey?> AskAsync(XamlRoot root, string title, Func<Hotkey, string?> problem)`

- [ ] **Step 1: Write the failing UI tests**

`tests/LeafCalendar.UITests/TraySettingsTests.cs`:
```csharp
using System.Runtime.InteropServices;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TraySettingsTests : IDisposable
{
    const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    readonly FakeGoogleServer _google = new();
    readonly List<string> _profiles = [];

    public void Dispose()
    {
        foreach (var profile in _profiles)
        {
            LeafApp.DeleteProfile(profile);
        }

        _google.Dispose();
    }

    string Profile(LeafSettings? settings = null)
    {
        var profile = SeededProfile.Create(settings);
        _profiles.Add(profile);
        return profile;
    }

    LeafApp Launch(string profile, string now = "2026-10-01T08:00:00-04:00")
    {
        var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now {now}");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    [Fact]
    public void Notifications_TurnedOff_StayOffAfterReopening()
    {
        using var leaf = Launch(Profile());
        leaf.OpenSettings("Notifications");
        foreach (var id in new[] { "RemindersSwitch", "JoinNowSwitch", "InvitesSwitch", "SoundSwitch" })
        {
            leaf.WaitInSettings(id).AsToggleButton().Toggle();
        }

        leaf.SettingsWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Settings") > 0, TimeSpan.FromSeconds(10)).Success);
        leaf.OpenSettings("Notifications");

        foreach (var id in new[] { "RemindersSwitch", "JoinNowSwitch", "InvitesSwitch", "SoundSwitch" })
        {
            Assert.Equal(ToggleState.Off, leaf.WaitInSettings(id).AsToggleButton().ToggleState);
        }
    }

    [Fact]
    public void RemindersOff_NoReminderShows()
    {
        var profile = Profile(new LeafSettings { ReminderNotifications = false });
        using var leaf = Launch(profile, "2026-10-01T13:49:50-04:00");

        Thread.Sleep(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(LeafApp.NotificationLines(profile), l => l.StartsWith("show\treminders\t", StringComparison.Ordinal));
    }

    [Fact]
    public void Tray_DaysAllDayAndLookahead_AreSaved()
    {
        using var leaf = Launch(Profile());
        leaf.OpenSettings("Tray");
        leaf.WaitInSettings("FlyoutDaysNumberBox").Patterns.RangeValue.Pattern.SetValue(7);
        leaf.WaitInSettings("FlyoutAllDaySwitch").AsToggleButton().Toggle();
        leaf.WaitInSettings("LookaheadComboBox").AsComboBox().Select("2 hours");

        leaf.SettingsWindow.Close();
        Assert.True(Retry.WhileTrue(() => leaf.WindowCount("Settings") > 0, TimeSpan.FromSeconds(10)).Success);
        leaf.OpenSettings("Tray");

        Assert.Equal(7, leaf.WaitInSettings("FlyoutDaysNumberBox").Patterns.RangeValue.Pattern.Value.Value);
        Assert.Equal(ToggleState.Off, leaf.WaitInSettings("FlyoutAllDaySwitch").AsToggleButton().ToggleState);
        Assert.Equal("2 hours", leaf.WaitInSettings("LookaheadComboBox").AsComboBox().SelectedItem.Text);
    }

    [Fact]
    public void Shortcuts_PressANewJoinShortcut_ItJoins()
    {
        var profile = Profile(new LeafSettings { JoinShortcut = "Ctrl+Alt+Shift+F9", FlyoutShortcut = "Ctrl+Alt+Shift+F10" });
        using var leaf = Launch(profile, "2026-10-01T13:55:00-04:00");
        leaf.OpenSettings("Shortcuts");

        leaf.WaitInSettings("JoinShortcutButton").AsButton().Invoke();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F7);
        Assert.True(Retry.WhileFalse(() => leaf.WaitForAnywhere("ShortcutPreview").Name == "Ctrl+Alt+Shift+F7", TimeSpan.FromSeconds(5)).Success);
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("JoinShortcutButton").Properties.HelpText.ValueOrDefault == "Ctrl+Alt+Shift+F7", TimeSpan.FromSeconds(5)).Success);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.F7);
        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(profile).Contains(MeetLink), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Shortcuts_TakenByAnotherApp_ShowTheWarning()
    {
        // This test's own thread takes Ctrl+Alt+Shift+F8 first, like another app would
        const uint ctrlAltShift = 0x2 | 0x1 | 0x4 | 0x4000;
        Assert.True(NativeMethods.RegisterHotKey(nint.Zero, 1, ctrlAltShift, 0x77), "Ctrl+Alt+Shift+F8 is already taken on this PC.");
        try
        {
            var profile = Profile(new LeafSettings { JoinShortcut = "Ctrl+Alt+Shift+F8", FlyoutShortcut = "Ctrl+Alt+Shift+F10" });
            using var leaf = Launch(profile);
            leaf.OpenSettings("Shortcuts");

            Assert.NotNull(leaf.WaitInSettings("JoinShortcutWarning"));
            Assert.Null(leaf.SettingsWindow.FindFirstDescendant(cf => cf.ByAutomationId("FlyoutShortcutWarning")));
        }
        finally
        {
            NativeMethods.UnregisterHotKey(nint.Zero, 1);
        }
    }

    [Fact]
    public void General_StartWithWindows_IsListed()
    {
        using var leaf = Launch(Profile());

        leaf.OpenSettings("General");

        Assert.NotNull(leaf.WaitInSettings("StartupSwitch"));
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint hwnd, int id);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TraySettingsTests"`
Expected: FAIL — "Settings page 'Notifications' isn't in the navigation." (and the same for Tray and Shortcuts, and no `StartupSwitch`); `RemindersOff_NoReminderShows` passes already (Task 11 reads the setting).

- [ ] **Step 3: Add the sections and pages to the Settings window**

In `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs`, add to `SettingsSection` after `TimeZones`:
```csharp
    /// <summary>Reminder, Join now, invitation, and sound switches.</summary>
    Notifications,

    /// <summary>The tray flyout's agenda and lookahead.</summary>
    Tray,

    /// <summary>Global shortcuts.</summary>
    Shortcuts,
```

In `src/LeafCalendar.App/Views/Settings/SettingsWindow.xaml.cs`, add to `_pages` after the Time zones entry:
```csharp
            (SettingsSection.Notifications, NavItem("Notifications", 0xEA8F, "SettingsNav_Notifications"), typeof(NotificationsPage)),
            (SettingsSection.Tray, NavItem("Tray", 0xE7C4, "SettingsNav_Tray"), typeof(TrayPage)),
            (SettingsSection.Shortcuts, NavItem("Shortcuts", 0xE765, "SettingsNav_Shortcuts"), typeof(ShortcutsPage)),
```
and change the class summary's page list to "General, Calendars, Time zones, Notifications, Tray, Shortcuts, Accounts, and About at the bottom of the pane". Also change its last sentence "and closes with the main window" to "and stays open when the main window closes (Leaf lives in the tray)".

- [ ] **Step 4: Write the Notifications page**

`src/LeafCalendar.App/Views/Settings/NotificationsPage.xaml`:
```xml
<Page
    x:Class="LeafCalendar.App.Views.Settings.NotificationsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:LeafCalendar.App.Controls">

    <ScrollViewer x:Name="PageScroll">
        <StackPanel MaxWidth="900" Spacing="4">
            <TextBlock Style="{StaticResource LeafSettingsPageTitleStyle}" Text="Notifications" />

            <!-- Meetings -->
            <TextBlock Style="{StaticResource LeafSettingsGroupHeaderStyle}" Margin="0,0,0,8" Text="Meetings" />
            <controls:SettingRow Glyph="&#xEA8F;" Header="Reminder notifications" Description="Show a notification at your Google reminder times, with Join, Snooze, and Dismiss.">
                <ToggleSwitch
                    x:Name="RemindersSwitch"
                    Style="{StaticResource LeafCardToggleSwitchStyle}"
                    Toggled="OnRemindersToggled"
                    AutomationProperties.Name="Reminder notifications"
                    AutomationProperties.AutomationId="RemindersSwitch" />
            </controls:SettingRow>
            <controls:SettingRow Glyph="&#xE714;" Header="Persistent “Join now” notification" Description="When a meeting with a link starts, keep a notification on screen until you join or dismiss it.">
                <ToggleSwitch
                    x:Name="JoinNowSwitch"
                    Style="{StaticResource LeafCardToggleSwitchStyle}"
                    Toggled="OnJoinNowToggled"
                    AutomationProperties.Name="Persistent “Join now” notification"
                    AutomationProperties.AutomationId="JoinNowSwitch" />
            </controls:SettingRow>
            <controls:SettingRow Glyph="&#xE715;" Header="Invite notifications" Description="Show new and updated invitations with Yes, No, and Maybe buttons.">
                <ToggleSwitch
                    x:Name="InvitesSwitch"
                    Style="{StaticResource LeafCardToggleSwitchStyle}"
                    Toggled="OnInvitesToggled"
                    AutomationProperties.Name="Invite notifications"
                    AutomationProperties.AutomationId="InvitesSwitch" />
            </controls:SettingRow>

            <!-- Sound -->
            <TextBlock Style="{StaticResource LeafSettingsGroupHeaderStyle}" Text="Sound" />
            <controls:SettingRow Glyph="&#xE767;" Header="Sound" Description="Play the Windows notification sound. Do not disturb and Focus in Windows still apply.">
                <ToggleSwitch
                    x:Name="SoundSwitch"
                    Style="{StaticResource LeafCardToggleSwitchStyle}"
                    Toggled="OnSoundToggled"
                    AutomationProperties.Name="Sound"
                    AutomationProperties.AutomationId="SoundSwitch" />
            </controls:SettingRow>
        </StackPanel>
    </ScrollViewer>
</Page>
```

`src/LeafCalendar.App/Views/Settings/NotificationsPage.xaml.cs`:
```csharp
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Notifications (spec 9): reminders, the persistent "Join now", invitations, and sound. Every change saves
/// right away; the next notification follows it. There's no pause switch: Windows Do Not Disturb and Focus do that.
/// </summary>
public sealed partial class NotificationsPage : Page
{
    SettingsContext _context = null!;

    // True while the saved values are being shown (the switches' Toggled events are ignored meanwhile)
    bool _loading = true;

    /// <summary>Creates the page.</summary>
    public NotificationsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    CalendarViewModel Calendar => _context.Calendar;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged += OnSettingsChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Window.SettingsChanged -= OnSettingsChanged;

    void OnSettingsChanged(object? sender, EventArgs e) => Load();

    void Load()
    {
        var s = Calendar.Settings;
        _loading = true;

        RemindersSwitch.IsOn = s.ReminderNotifications;
        JoinNowSwitch.IsOn   = s.JoinNowNotifications;
        InvitesSwitch.IsOn   = s.InviteNotifications;
        SoundSwitch.IsOn     = s.NotificationSound;

        _loading = false;
    }

    void OnRemindersToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = RemindersSwitch.IsOn;
            Calendar.Update(s => s with { ReminderNotifications = on });
        }
    }

    void OnJoinNowToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = JoinNowSwitch.IsOn;
            Calendar.Update(s => s with { JoinNowNotifications = on });
        }
    }

    void OnInvitesToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = InvitesSwitch.IsOn;
            Calendar.Update(s => s with { InviteNotifications = on });
        }
    }

    void OnSoundToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = SoundSwitch.IsOn;
            Calendar.Update(s => s with { NotificationSound = on });
        }
    }
}
```

- [ ] **Step 5: Write the Tray page**

`src/LeafCalendar.App/Views/Settings/TrayPage.xaml`:
```xml
<Page
    x:Class="LeafCalendar.App.Views.Settings.TrayPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:LeafCalendar.App.Controls">

    <ScrollViewer x:Name="PageScroll">
        <StackPanel MaxWidth="900" Spacing="4">
            <TextBlock Style="{StaticResource LeafSettingsPageTitleStyle}" Text="Tray" />

            <!-- Flyout -->
            <TextBlock Style="{StaticResource LeafSettingsGroupHeaderStyle}" Margin="0,0,0,8" Text="Flyout" />
            <controls:SettingRow Glyph="&#xE787;" Header="Days in the agenda" Description="How many days the tray flyout lists, starting today. From 1 to 14. It shows the calendars you show in Leaf.">
                <NumberBox
                    x:Name="DaysBox"
                    MinWidth="180"
                    Minimum="1"
                    Maximum="14"
                    SpinButtonPlacementMode="Compact"
                    ValueChanged="OnDaysChanged"
                    AutomationProperties.Name="Days in the agenda"
                    AutomationProperties.AutomationId="FlyoutDaysNumberBox" />
            </controls:SettingRow>
            <controls:SettingRow Glyph="&#xE8BF;" Header="Include all-day events" Description="List all-day events at the top of each day.">
                <ToggleSwitch
                    x:Name="AllDaySwitch"
                    Style="{StaticResource LeafCardToggleSwitchStyle}"
                    Toggled="OnAllDayToggled"
                    AutomationProperties.Name="Include all-day events"
                    AutomationProperties.AutomationId="FlyoutAllDaySwitch" />
            </controls:SettingRow>

            <!-- Next Event -->
            <TextBlock Style="{StaticResource LeafSettingsGroupHeaderStyle}" Text="Next event" />
            <controls:SettingRow Glyph="&#xE916;" Header="Next-event lookahead" Description="How far ahead the flyout header and the tray icon's tooltip look for your next event.">
                <ComboBox
                    x:Name="LookaheadBox"
                    MinWidth="180"
                    SelectionChanged="OnLookaheadChanged"
                    AutomationProperties.Name="Next-event lookahead"
                    AutomationProperties.AutomationId="LookaheadComboBox">
                    <ComboBoxItem Content="15 minutes" />
                    <ComboBoxItem Content="30 minutes" />
                    <ComboBoxItem Content="1 hour" />
                    <ComboBoxItem Content="2 hours" />
                    <ComboBoxItem Content="4 hours" />
                    <ComboBoxItem Content="8 hours" />
                </ComboBox>
            </controls:SettingRow>
        </StackPanel>
    </ScrollViewer>
</Page>
```

`src/LeafCalendar.App/Views/Settings/TrayPage.xaml.cs`:
```csharp
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Tray (spec 9): how many days the flyout lists, whether all-day events are in it, and how far ahead the
/// flyout header and the tooltip look. Changes save right away; the flyout and tooltip use them from their next refresh.
/// Which calendars appear follows the calendars shown in Leaf until the full settings page (Milestone 5).
/// </summary>
public sealed partial class TrayPage : Page
{
    SettingsContext _context = null!;

    // True while the saved values are being shown (the controls' change events are ignored meanwhile)
    bool _loading = true;

    /// <summary>Creates the page.</summary>
    public TrayPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    CalendarViewModel Calendar => _context.Calendar;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged += OnSettingsChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Window.SettingsChanged -= OnSettingsChanged;

    void OnSettingsChanged(object? sender, EventArgs e) => Load();

    void Load()
    {
        var s = Calendar.Settings;
        _loading = true;

        DaysBox.Value              = s.FlyoutDays;
        AllDaySwitch.IsOn          = s.FlyoutAllDay;
        LookaheadBox.SelectedIndex = LeafSettings.LookaheadChoices.ToList().IndexOf(s.TrayLookaheadMinutes);

        _loading = false;
    }

    void OnDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        var days = (int)Math.Clamp(args.NewValue, 1, LeafSettings.MaxFlyoutDays);
        Calendar.Update(s => s with { FlyoutDays = days });
    }

    void OnAllDayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = AllDaySwitch.IsOn;
            Calendar.Update(s => s with { FlyoutAllDay = on });
        }
    }

    void OnLookaheadChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LookaheadBox.SelectedIndex >= 0)
        {
            var minutes = LeafSettings.LookaheadChoices[LookaheadBox.SelectedIndex];
            Calendar.Update(s => s with { TrayLookaheadMinutes = minutes });
        }
    }
}
```

- [ ] **Step 6: Write the shortcut dialog and the Shortcuts page**

`src/LeafCalendar.App/Views/Settings/ShortcutDialog.cs`:
```csharp
using LeafCalendar.App.Controls;
using LeafCalendar.Core.Tray;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Asks for a new global shortcut by listening for it: "Press the keys you want to use". Save is on only for a
/// combination that works (Ctrl, Alt, or Win with a letter, number, or F-key) and that <c>problem</c> doesn't object to
/// (already used by the other shortcut, or taken by another app). Returns null when canceled.
/// </summary>
internal static class ShortcutDialog
{
    /// <summary>Shows the dialog.</summary>
    public static async Task<Hotkey?> AskAsync(XamlRoot root, string title, Func<Hotkey, string?> problem)
    {
        Hotkey? picked = null;

        // Content (this method's own references; nothing is read back through the dialog)
        var hint    = new TextBlock { Text = "Press the keys you want to use, like Ctrl+Alt+J.", TextWrapping = TextWrapping.Wrap };
        var preview = new TextBlock { Text = "…", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var error   = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        AutomationProperties.SetAutomationId(preview, "ShortcutPreview");
        AutomationProperties.SetAutomationId(error, "ShortcutProblem");

        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(hint);
        content.Children.Add(preview);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot               = root,
            Title                  = title,
            Content                = content,
            PrimaryButtonText      = "Save",
            CloseButtonText        = "Cancel",
            DefaultButton          = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        dialog.PreviewKeyDown += (_, e) =>
        {
            // Modifier keys alone wait for the real key; Esc, Enter, and Tab on their own work as usual
            if (IsModifier(e.Key))
            {
                return;
            }

            var modifiers = Modifiers();
            if (modifiers == HotkeyModifiers.None && e.Key is VirtualKey.Escape or VirtualKey.Enter or VirtualKey.Tab)
            {
                return;
            }

            e.Handled = true;
            if (!Hotkey.TryCreate(modifiers, (int)e.Key, out var hotkey))
            {
                picked        = null;
                error.Message = "Use Ctrl, Alt, or the Windows key with a letter, a number, or F1–F24 (not F12).";
                error.IsOpen  = true;
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            preview.Text = hotkey.ToString();
            var objection = problem(hotkey);
            error.Message = objection ?? "";
            error.IsOpen  = objection is not null;
            picked        = objection is null ? hotkey : null;
            dialog.IsPrimaryButtonEnabled = picked is not null;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? picked : null;
    }

    static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    static HotkeyModifiers Modifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (KeyState.IsDown(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Ctrl;
        }

        if (KeyState.IsDown(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (KeyState.IsDown(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (KeyState.IsDown(VirtualKey.LeftWindows) || KeyState.IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }
}
```

`src/LeafCalendar.App/Views/Settings/ShortcutsPage.xaml`:
```xml
<Page
    x:Class="LeafCalendar.App.Views.Settings.ShortcutsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:LeafCalendar.App.Controls">

    <ScrollViewer x:Name="PageScroll">
        <StackPanel MaxWidth="900" Spacing="4">
            <TextBlock Style="{StaticResource LeafSettingsPageTitleStyle}" Text="Shortcuts" />

            <!-- Global Shortcuts (spec 8.6) -->
            <TextBlock Style="{StaticResource LeafSettingsGroupHeaderStyle}" Margin="0,0,0,8" Text="Global shortcuts" />
            <controls:SettingRow Glyph="&#xE714;" Header="Join meeting" Description="Works anywhere in Windows. Joins the meeting that starts in the next 10 minutes, or the one in progress, with the right Google account.">
                <Button
                    x:Name="JoinShortcutButton"
                    MinWidth="180"
                    Click="OnJoinShortcutClick"
                    AutomationProperties.Name="Join meeting"
                    AutomationProperties.AutomationId="JoinShortcutButton" />
            </controls:SettingRow>
            <InfoBar
                x:Name="JoinShortcutWarning"
                Severity="Warning"
                IsClosable="False"
                AutomationProperties.AutomationId="JoinShortcutWarning" />
            <controls:SettingRow Glyph="&#xE8A7;" Header="Show or hide the tray flyout" Description="Works anywhere in Windows.">
                <Button
                    x:Name="FlyoutShortcutButton"
                    MinWidth="180"
                    Click="OnFlyoutShortcutClick"
                    AutomationProperties.Name="Show or hide the tray flyout"
                    AutomationProperties.AutomationId="FlyoutShortcutButton" />
            </controls:SettingRow>
            <InfoBar
                x:Name="FlyoutShortcutWarning"
                Severity="Warning"
                IsClosable="False"
                AutomationProperties.AutomationId="FlyoutShortcutWarning" />
        </StackPanel>
    </ScrollViewer>
</Page>
```

`src/LeafCalendar.App/Views/Settings/ShortcutsPage.xaml.cs`:
```csharp
using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Shortcuts (spec 8.6, 9): the two global shortcuts, each changed by pressing the new keys. A combination
/// another app holds shows a warning under its row and asks for another. The link to the in-app cheat sheet arrives with
/// the cheat sheet (Milestone 5).
/// </summary>
public sealed partial class ShortcutsPage : Page
{
    SettingsContext _context = null!;

    /// <summary>Creates the page.</summary>
    public ShortcutsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged    += OnChanged;
        _context.Services.Shortcuts.Changed += OnChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Window.SettingsChanged    -= OnChanged;
        _context.Services.Shortcuts.Changed -= OnChanged;
    }

    void OnChanged(object? sender, EventArgs e) => Load();

    void Load()
    {
        var s = _context.Calendar.Settings;
        Show(JoinShortcutButton, JoinShortcutWarning, s.JoinShortcut, ShortcutAction.Join);
        Show(FlyoutShortcutButton, FlyoutShortcutWarning, s.FlyoutShortcut, ShortcutAction.Flyout);
    }

    // The shortcut is the button's text and its help text (screen readers read the row's name, then the shortcut); the
    // warning is collapsed when there's nothing to say, so it takes no room between the rows
    void Show(Button button, InfoBar warning, string shortcut, ShortcutAction action)
    {
        var text  = shortcut.Length == 0 ? "None" : shortcut;
        var taken = _context.Services.Shortcuts.IsTaken(action);
        button.Content = text;
        AutomationProperties.SetHelpText(button, text);
        warning.Message    = $"Another app is using {shortcut}. Pick a different shortcut.";
        warning.IsOpen     = taken;
        warning.Visibility = taken ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnJoinShortcutClick(object sender, RoutedEventArgs e) => await ChangeAsync(ShortcutAction.Join, "Join meeting shortcut");

    async void OnFlyoutShortcutClick(object sender, RoutedEventArgs e) => await ChangeAsync(ShortcutAction.Flyout, "Tray flyout shortcut");

    // Leaf's own shortcuts let go while the dialog listens (so pressing them reaches it), and come back from the settings after
    async Task ChangeAsync(ShortcutAction action, string title)
    {
        var shortcuts = _context.Services.Shortcuts;
        var settings  = _context.Calendar.Settings;
        var other     = action == ShortcutAction.Join ? settings.FlyoutShortcut : settings.JoinShortcut;
        var otherName = action == ShortcutAction.Join ? "Show or hide the tray flyout" : "Join meeting";

        shortcuts.Suspend();
        try
        {
            var picked = await ShortcutDialog.AskAsync(XamlRoot, title, hotkey =>
                hotkey.ToString() == other ? $"“{otherName}” already uses {hotkey}."
                : !shortcuts.IsFree(hotkey) ? $"Windows or another app is using {hotkey}. Try a different one."
                : null);

            if (picked is { } chosen)
            {
                var text = chosen.ToString();
                _context.Calendar.Update(s => action == ShortcutAction.Join ? s with { JoinShortcut = text } : s with { FlyoutShortcut = text });
            }
        }
        catch (Exception ex)
        {
            // async void callers: nothing may escape
            _context.Calendar.LogError("settings.shortcut.failed", ex);
        }
        finally
        {
            shortcuts.Apply(_context.Calendar.Settings);
        }
    }
}
```

- [ ] **Step 7: Add "Start with Windows" to General**

In `src/LeafCalendar.App/Views/Settings/GeneralPage.xaml`, add at the end of the page's `StackPanel`, after the `Use 24-hour time` row:
```xml

            <!-- Startup (spec 8.1) -->
            <TextBlock Style="{StaticResource LeafSettingsGroupHeaderStyle}" Text="Startup" />
            <controls:SettingRow x:Name="StartupRow" Glyph="&#xE770;" Header="Start with Windows" Description="Leaf starts in the tray when you sign in to Windows, so reminders arrive on time.">
                <ToggleSwitch
                    x:Name="StartupSwitch"
                    Style="{StaticResource LeafCardToggleSwitchStyle}"
                    Toggled="OnStartupToggled"
                    AutomationProperties.Name="Start with Windows"
                    AutomationProperties.AutomationId="StartupSwitch" />
            </controls:SettingRow>
```

In `src/LeafCalendar.App/Views/Settings/GeneralPage.xaml.cs`:

Add usings `using System.Runtime.InteropServices;` and `using Windows.ApplicationModel;`, then add under the combo box arrays:
```csharp
    // The startup task declared in Package.appxmanifest
    const string StartupTaskId = "LeafCalendarStartup";
```
and under `bool _loading = true;`:
```csharp
    // True while the startup task's state is being shown
    bool _loadingStartup;
```

At the end of `OnNavigatedTo`, add:
```csharp
        _ = LoadStartupAsync();
```

Add at the end of the class:
```csharp
    // Windows owns the startup state: the user can turn it off in Task Manager, and then only they can turn it back on
    async Task LoadStartupAsync()
    {
        _loadingStartup = true;
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            StartupSwitch.IsOn      = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupSwitch.IsEnabled = task.State is StartupTaskState.Enabled or StartupTaskState.Disabled;
            StartupRow.Description  = task.State switch
            {
                StartupTaskState.DisabledByUser                                       => "Turned off in Task Manager › Startup apps. Turn it on there.",
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Your organization manages this setting.",
                _                                                                     => "Leaf starts in the tray when you sign in to Windows, so reminders arrive on time.",
            };
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            StartupSwitch.IsEnabled = false;
            Calendar.LogError("settings.startup.failed", ex);
        }
        finally
        {
            _loadingStartup = false;
        }
    }

    async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup)
        {
            return;
        }

        // async void: anything that escapes here would end the process
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (StartupSwitch.IsOn)
            {
                await task.RequestEnableAsync();
            }
            else
            {
                task.Disable();
            }
        }
        catch (Exception ex)
        {
            Calendar.LogError("settings.startup.failed", ex);
        }

        await LoadStartupAsync();
    }
```

- [ ] **Step 8: Refresh the tray when settings change**

In `src/LeafCalendar.App/App.xaml.cs`, in `AcquireCalendar`, add after `_calendar.OpenSettings = OpenSettings;`:
```csharp
            _calendar.LayoutChanged += (_, _) =>
            {
                RefreshTooltip();
                RefreshAgenda();
            };
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet build LeafCalendar.slnx -c Debug`, `pwsh tools/dev-register.ps1`, `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*TraySettingsTests" --filter-class "*SettingsTests"`
Expected: PASS. Then look at each new page at the Settings window's minimum size (640 × 500) in light and dark: nothing clips, rows match General's, and take screenshots for the milestone report.

- [ ] **Step 10: Check the AOT build**

Run: `pwsh tools/publish-aot.ps1 -Register` → 0 IL warnings; the Step 9 test command → PASS; `pwsh tools/dev-register.ps1`.

- [ ] **Step 11: Commit**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests
git commit -m "feat(app): Settings for notifications, tray, global shortcuts, and start with Windows"
```

---

### Task 17: Milestone Close (AOT, Memory, Security, Spec Updates, Merge)

**Files:**
- Modify: `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`, `docs/design-standard.md`, `tests/LeafCalendar.UITests/memory-budget.json` (only with the owner's go-ahead, see Step 3)
- Modify: whatever the security review finds (each fix gets a test)

**Interfaces:**
- Consumes: everything above.
- Produces: a verified Milestone 4, with its rulings and deferrals recorded in the spec and the design standard, merged into `main` and pushed.

- [ ] **Step 1: Full verification**

Run each command in order:
1. `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.
2. `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → all PASS.
3. `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` → all PASS, 1 skipped (memory). The desktop must stay unlocked.
4. `pwsh tools/publish-aot.ps1 -Register` → **0 IL warnings**. Then the whole UI suite again against the AOT build: `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` → all PASS.
5. By hand on the AOT build with your real profile:
   - Sign out of Windows and back in: Leaf starts in the tray (no window), and the tooltip names the next event.
   - Create a Meet event 12 minutes out in Google Calendar on your second account. The reminder appears 10 minutes before (Join, Snooze, Dismiss); Snooze 5 minutes brings it back; at start, "Join now" stays on screen; its Join opens Meet signed in as that account.
   - Press Ctrl+Alt+J from another app with that meeting 5 minutes out: the same.
   - Delete that meeting in Google Calendar while its "Join now" is on screen: within a minute it's withdrawn.
   - Quit from the tray menu, then click a reminder in Notification Center: Leaf starts in the tray and opens the event.
   - Put the PC to sleep across a meeting's start and end: on wake, no stale reminder for it.
   - Taskbar at the bottom, top (with ExplorerPatcher or a second monitor's taskbar if available), and auto-hidden: the flyout and menu open next to the icon inside the screen, in light and dark.
   - Settings › Shortcuts: with another app holding a combination (for example a PowerToys shortcut, if installed), set Join to it: the dialog says another app is using it and Save stays off. Then set it back to Ctrl+Alt+J.
6. `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*MemoryTests" --output Detailed` 3 times (AOT build still registered).
7. `dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --output Detailed` → all PASS, including `LiveAlertTests` (or skipped without the live account).
8. `pwsh tools/dev-register.ps1` to put Debug back.

- [ ] **Step 2: Security review**

Invoke the `security-review` skill on the Milestone 4 diff (`git diff m3-events..HEAD`). Then check each item and record the result in the task report:
1. **Toast XML is built in one place.** `git grep -n "new AppNotification(\|<toast" -- src`: `new AppNotification(` only in `Notifier.Show`; toast XML only from `ToastContent` (`XElement`). `ToastContentTests.Reminder_HostileTitle_StaysPlainText` passes.
2. **Activation arguments are untrusted input.** They're read only by `ToastArgs.Parse` (malformed, unknown, oversized, and incomplete input reads as null: `ToastContentTests.Args_*`). Another profile's click is ignored (`ToastActionTests.AnotherProfilesNotification_IsIgnored`). Every event a click names is looked up again locally (`OccurrenceLookup`), so a forged click can only act on events already in the profile, the same way a click in the app would.
3. **One launch path.** `git grep -n "Launcher\." -- src/LeafCalendar.App`: still only `LeafServices.LaunchAsync` and `OpenSignInPageAsync`. Join from the flyout, the menu, the shortcut, and notifications all goes through `LaunchAsync` (which re-checks `LinkSafety`).
4. **Test switches stay test-only.** `--now` and `--toast-action` are honored only with `--fake-google` on a `uitest-` profile (`LaunchOptionsTests`). `notifications.txt` is written only in fake-Google mode (`Notifier.IsFake`).
5. **Nothing sensitive in logs.** `git grep -n "Log\.\(Info\|Error\)(\"\(alert\|notification\|tray\|settings\)" -- src/LeafCalendar.App`: details carry only `kind=`, `tag=`, `count=`, and `account=` (an internal ID). Then use Leaf with your real account for a day (reminders, an invitation, a join) and search `LocalState\profiles\default\Logs\leaf.log` with `Select-String` for a few of the event titles, locations, and guest emails you saw. Expected: no output.
6. **Global shortcuts don't watch typing.** Keys are read only while the shortcut dialog is open and focused (`ShortcutDialog`); `RegisterHotKey` receives only the registered combinations.
7. **Notifications unregister on Quit** (`Notifier.Dispose`), so a later click starts a fresh Leaf instead of reaching a dead one.

- [ ] **Step 3: Memory budget**

From Step 1.6's three runs:
- If every run is within **120 MB** private bytes and **25 MB** working set, leave `memory-budget.json` alone and record the three measurements in the spec (Step 4).
- If a run is over, **stop and report to the owner** with the numbers and what grew (the tray host window and its hidden flyout are the new resident parts; the flyout's content is built on open). The budget is the owner's decision; don't raise it on your own.

- [ ] **Step 4: Record rulings and deferrals in the spec and the design standard**

In `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`:

Section 2 (Decision Log), add rows:
```markdown
| Tray flyout construction | An invisible host window opens a stock `Flyout` holding the acrylic panel (Layers' HUD pattern), so light dismiss, Esc, and focus are stock and Win32 stays on CsWin32 without marshaling (Milestone 4). |
| Reminder rules | Popup reminders only; all-day events remind only from their own overrides (Google's API doesn't expose all-day defaults); each alert shows once, ever, per event instance (Milestone 4). |
```

Section 8.2, append to the **Placement** bullet: "An auto-hidden taskbar is kept clear. When the icon's place is unknown (it's in the overflow), the flyout opens at the far end of the primary taskbar."

Section 8.4, append to the **Rules** list:
```markdown
- Each pass looks back one hour: an alert missed while Leaf slept or wasn't running still shows if its meeting isn't over. Per meeting, only the latest alert that came due shows (a "Join now" rather than stale reminders).
- A "Join now" is withdrawn once its meeting ends, moves, is declined, or is deleted.
- Invitations: an account's pending invites are recorded quietly when it finishes its first sync; later, a new invite or an organizer's change (Google's `sequence`) notifies once. Yes / No / Maybe email the organizer, and a repeating invite is answered for the series.
- Toast arguments carry the profile; a running Leaf ignores another profile's notification.
```

Section 8.5, append: "With several meetings in progress and none about to start, the one that started last is picked. All-day events never qualify."

Section 3.4 item 7, append: "Re-measured 2026-MM-DD at the close of Milestone 4 (tray icon, tray host window, notifications, and the alert scheduler resident): private bytes A–B MB, working set C MB (three runs)." (Fill in the date and Step 1.6's numbers.)

Section 12, under **5. Power features**, extend the Milestone 3 deferral list with:
```markdown
   - Deferred from Milestone 4:
     - Settings › Tray "Which calendars appear" (the tray follows the calendars shown in Leaf until then)
     - Settings › Shortcuts link to the in-app cheat sheet (arrives with the cheat sheet)
```

Section 12, under **6. Polish and Store prep**, add:
```markdown
   - Deferred from Milestone 4:
     - Tray icon art: monochrome light and dark glyphs (the app logo is a placeholder), and a fixed `NIF_GUID` icon identity once the package is signed
```

In `docs/design-standard.md`:

Section 2, **Every Window** table, replace the "Backdrop, light-dismiss surfaces (tray flyout)" row's value with: "Desktop acrylic that stays "active" (`DesktopAcrylicController` with `IsInputActive = true`), inside a `SystemBackdropElement` with `CornerRadius="8"`, in a stock `Flyout` opened from an invisible host window" and its source with "Sony `ActiveAcrylicBackdrop.cs`; Layers `ActiveAcrylicBackdrop.cs`, `HudHost.xaml`; Leaf `Tray/TrayHost.xaml`".

Section 15, **Known gaps**, replace item 6 with: "6. Tray art is a placeholder (the app logo) until Milestone 6." and add to **Already follows it**: "- Tray flyout: always-active acrylic, 360 × 560, 12 DIPs from the taskbar, slide from the taskbar edge with the Fluent 250/167 ms curves; stock tray menu with narrow padding and 16 DIP icons. (`Tray/TrayHost.xaml`)"

- [ ] **Step 5: Commit**

```bash
git add -A docs tests src
git commit -m "chore: close Milestone 4 with verification, security review, and recorded rulings"
```

- [ ] **Step 6: Merge and push**

The owner authorized merge and push at each milestone's end. From a clean tree:
```bash
git checkout main
git merge --no-ff m4-tray-alerts -m "Merge Milestone 4: tray and alerts"
git push origin main m4-tray-alerts
```
Expected: the merge applies cleanly (this branch started from `m3-events`, so it carries Milestone 3 if that isn't merged yet), and the push succeeds. Don't open, comment on, or change a PR.

---

## Self-Review Notes

**Spec coverage (Milestone 4 in Section 12, plus Sections 3.4, 5.3, 5.5, 8.1–8.6, 9):**

| Spec item | Where |
|---|---|
| Tray icon via `Shell_NotifyIcon` on a hidden message window (8.1) | Task 10 |
| Tooltip: next event and countdown (8.1) | Tasks 6, 10, 13 |
| Starts with Windows (MSIX `StartupTask`, on by default, a setting); closing keeps Leaf in the tray; only Quit exits (8.1) | Tasks 10, 12, 16 |
| Flyout: acrylic, icon rect + `SHAppBarMessage` edge, next to the icon, slides from the taskbar edge, inside the work area (8.2) | Tasks 9, 12, 13 |
| Flyout content: next event, countdown, large Join; agenda by day with Join; days a setting; "New event"; clicking an event opens the main window on it (8.2) | Tasks 6, 13, 16 |
| Closes on Esc or focus loss; created once and kept hidden (8.2) | Task 13 |
| XAML `MenuFlyout` with icons from a host window, taskbar-aware; the six items (8.3) | Task 12 |
| All notifications are Windows App SDK app notifications (8.4, decision log) | Tasks 7, 11, 14 |
| Reminder at the event's own reminders, else the calendar's defaults; title, time, location; Join (with a link), Snooze, Dismiss (8.4.1) | Tasks 2, 3, 7, 11 |
| Persistent "Join now": at start, with a link, not declined, `scenario="reminder"`, background-activated Join, until Join or Dismiss (8.4.2) | Tasks 3, 4, 7, 11 |
| New or updated invite with Yes / No / Maybe (8.4.3) | Tasks 7, 8, 14 |
| "Conflict needs review" (deferred from M3; 5.5) and "Sign in again" (8.4.4, 4.2) | Tasks 7, 8, 14 |
| Do Not Disturb and Focus apply; no pause feature (8.4, 1.4) | Task 11 (nothing added; Global Constraints) |
| Scheduler on `TimeProvider` (8.4) | Task 4 |
| Resume from sleep: still-upcoming or in-progress meetings remind, finished ones don't (8.4) | Task 4 (Review Focus 1) |
| Join rule: qualifies, soonest upcoming else in progress, `authuser`, "No meeting to join" (8.5) | Tasks 5, 12, 15 |
| Global shortcuts via `RegisterHotKey`, changeable, "taken" warning (8.6) | Tasks 1, 15, 16 |
| Polling 15 s visible (window or flyout), 60 s tray; sync on flyout open and a minute before each reminder (5.3) | Tasks 4, 10, 11, 13 |
| Tray-only memory: trim, EcoQoS, memory budget test (3.4) | Tasks 10, 13, 17 |
| Settings › Notifications (four switches), Tray (days, all-day, lookahead), Shortcuts (remapping with warnings), General › launch at startup (9) | Task 16 |
| Three test layers | logic tests in Tasks 1–9; UI tests in Tasks 10–16 (each also on AOT); live test in Task 3 (run in Task 17) |
| Security review, AOT publish, memory budget, spec updates | Task 17 |

**Deliberately deferred (recorded in the spec by Task 17):**
- Settings › Tray "Which calendars appear" and the Shortcuts page's cheat-sheet link (Milestone 5)
- Tray icon art and its fixed `NIF_GUID` identity (Milestone 6)

**Placeholder scan:** no TBD/TODO steps; every code step has its code; every test has its assertions. Task 17's spec note has two blanks (the date and the measured numbers) that Step 1.6 produces.

**Type consistency checked:**
- `Alert(AlertKind, CalendarOccurrence, DateTimeOffset, int, Uri?)` with `Key`/`Tag`/`TagFor` is the same in Tasks 3, 4, 7, 8, 11, 14.
- `AlertLedger.TryAdd(conn, key, kind, tag, eventEnd, now)` (Task 2) is called the same way by `AlertScheduler` (Task 4) and `InviteWatcher` (Task 8).
- `ToastArgs(ToastAction, string Profile, string? AccountId, string? CalendarId, string? EventId, DateTimeOffset? Start)` is the same in Tasks 7, 14, and the UI tests.
- `ToastContent.Invite(CalendarOccurrence, EventDetails, bool isUpdate, string tag, string when, string profile, bool sound)` (Task 7) matches its call in `AlertCenter` (Task 14), fed from `InviteAlert(Occurrence, Details, IsUpdate, Tag)` (Task 8).
- `TrayPlacement.Flyout(area, edge, icon, scale)`, `Frame(panel, scale)`, `FlyoutAnchor(frame, edge)`, `MenuAnchor(x, y, area, edge, scale)` (Task 9) match `TrayHost` (Tasks 12, 13).
- `TrayAgenda.Load(conn, now, zone, days, includeAllDay, use24Hour)`, `Next(days, now, lookahead)`, `Tooltip(next)`, `NothingNext(minutes)` (Task 6) match `App` (Tasks 10, 13).
- `JoinPicker.Find/MeetingLink/JoinLink` and `JoinTarget(Occurrence, Link)` (Task 5) match `App` (Tasks 12, 13).
- `Activation(ExtendedActivationKind Kind, string? Arguments)` (Task 10) is filled by `Program.Read` (Task 14).
- `GlobalShortcuts` and `ShortcutAction` (Task 15) match `ShortcutsPage` (Task 16).
- `SeededProfile.Create(LeafSettings? settings = null)` (Task 15) is used by Tasks 15 and 16; earlier tasks call it with no arguments.
- `LeafApp.PostTrayMessage/TraySelect/TrayContextMenu/TrayWindow` (Task 10), `RightClickTrayIcon/WaitForPopup/PopupExists` (Task 12), `NotificationLines/WaitForNotification` (Task 11) are used unchanged by later tasks.

**Review Focus pinned:** each of the five lines names tests that live in the task owning the code (Tasks 4, 7, 8, 9).
