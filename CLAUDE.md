# Leaf Calendar

Leaf Calendar is a Windows 11 desktop client for Google Calendar. It is a WinUI 3 app on the Windows App SDK, written in C# 14 on .NET 10, shipped as a Native AOT MSIX package. This file tells an AI agent (or a new developer) everything needed to work in this codebase the way its owner expects. Read it fully before changing anything.

These rules override any global or personal instruction that conflicts with them for this repository.

## What the App Is

- The user signs in with their **own** Google Cloud OAuth client (Desktop app type). There is no Leaf server, no telemetry, and no analytics.
- Events are cached in a local SQLite database so the app opens fast and works offline. A background sync loop pulls changes from Google; an **outbox** queues local edits and sends them in order.
- The main window has Day, Week, Month and custom 2 to 9 day views, a sidebar (mini month and calendar list), a details panel, an event editor, a command menu (Ctrl+K), a keyboard cheat sheet (?), and Settings, which shows inside the main window in place of the calendar.
- A tray icon opens an acrylic agenda flyout and a menu. Reminders, "Join now" alerts and invitations are Windows toast notifications.
- One instance runs per profile. A `leaf-calendar:` URL protocol only brings Leaf to the front.

## Repository Layout

| Path | What it holds |
| --- | --- |
| `src/LeafCalendar.Core` | All logic with no UI: auth, Google API client, SQLite stores, sync, outbox, editing, recurrence, search, alerts, tray agenda, view math. Unit-testable. |
| `src/LeafCalendar.App` | The WinUI 3 app: `App.xaml.cs` (startup, tray, alerts), `Program.cs` (single instance, activation), `LeafServices.cs` (service wiring), `MainWindow`, `Views/`, `Controls/`, `ViewModels/`, `Tray/`, `Interop/`, `Notifications/`, `Styles/`. |
| `tests/LeafCalendar.Tests` | xunit v3 logic tests for Core, plus lint tests over the App's XAML and package manifest. |
| `tests/LeafCalendar.UITests` | FlaUI UI tests that drive the **installed** package against a fake Google server. |
| `tests/LeafCalendar.LiveTests` | Tests against the real Google API with a throwaway account. They skip until set up (see that folder's README). |
| `tools/` | PowerShell build, install and packaging scripts. |
| `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` | The product spec. The binding authority for behavior. |
| `docs/design-standard.md` | The visual and interaction design standard. Binding for all UI. |
| `docs/superpowers/plans/` | Implementation plans, one per milestone or batch of work. |
| `PRIVACY.md` | What Leaf keeps, sends and how to remove it. Shipped in the package and opened from Settings › About. Keep it accurate when data handling changes. |

Core folders: `Alerts`, `Auth`, `Data`, `Diagnostics`, `Editing`, `Events`, `Google`, `Hosting`, `Http`, `People`, `Recurrence`, `Search`, `Settings`, `Sync`, `Tray`, `Views`.

## Build, Test and Install

Run everything from the repo root (the folder with `global.json`). `dotnet test --project` fails with "MSB1001: Unknown switch" from anywhere else.

```bash
dotnet build LeafCalendar.slnx -c Debug
```

```bash
dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug
```

One class: add `--filter-class "*ClassName"`. One test: `--filter-method "*Class.Method"`.

- **The build treats every warning as an error** (`TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild`, doc-comment checks, NuGet vulnerability audit). A change is not done until the build has 0 warnings.
- **Native AOT only runs in Release.** Debug is JIT. Some bugs (reflection, WinRT casts, JSON) only appear under AOT, so check AOT-sensitive changes with `pwsh tools/publish-aot.ps1` (builds the Release MSIX; add `-Register` only for a developer install).
- **Installing for the owner:** the owner runs a signed install. Use `pwsh tools/sign-install.ps1`: it publishes the Release AOT build, stamps a newer version and installs it as an **update**, so settings, the OAuth client and sign-ins are kept. Never uninstall the owner's app. `dev-register.ps1` and `publish-aot.ps1 -Register` refuse to replace a signed install on purpose.
- **Package size:** `pwsh tools/check-package.ps1` fails if the MSIX carries Windows App SDK AI/ML files or exceeds its budget (40 MB).
- **When a batch of work is finished,** install the optimized build with `sign-install.ps1` so the owner runs the real thing.

### UI Tests

- UI tests drive the **installed** package, so install the current code first (`sign-install.ps1`), then run classes: `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*ClassName"`.
- Only one installed copy exists, so never run two UI test processes at once, and never run UI tests while another agent installs.
- Per change, run only the UI classes the change can affect. Run the full UI suite once per milestone, on the optimized build, as the merge gate. Never run the full suite twice on both builds.
- Each test uses a throwaway profile (`--profile uitest-…`) and the fake Google server (`--fake-google <uri>`), so the owner's data is never touched. Other launch flags: `--start-date`, `--now`, `--toast-action`, `--tray-probe`, `--gc-stress`, `--restarted`.
- The tests find the main window by its title bar (`AppTitleBar`), not its title, because the window title is the date range on screen.
- The owner's own Leaf can be running and holding the global shortcuts (Ctrl+Alt+J, Ctrl+Alt+K). If a whole block of tests fails at once, re-run before diagnosing: the installer restarting the owner's app has caused mass false failures.
- If the owner moves the mouse (real, not injected input) during a UI run, the run pauses: the test in progress fails with `Interrupted:`, the pause file `%LOCALAPPDATA%\LeafCalendar.UITests\paused` is written, and every later test fails at once with `Paused:` until it is deleted. Only the owner resumes; never delete the pause file without the owner's say-so.
- Screenshots: set `LEAF_SCREENSHOTS` to a folder and run the `ScreenshotTour` class to capture every screen in light and dark, at the minimum and wide window, every Settings page, the tray flyout and menu, onboarding, and the command menu with queries typed and run. Review the shots after UI changes.

### CI

`.github/workflows/ci.yml` builds and runs the logic tests on every push. A manual run (`workflow_dispatch`) also builds the AOT package and runs the UI tests on a 1920 × 1080 screen in US Central time. UI tests on GitHub's runners are slower and some are timing-sensitive; treat that job as advisory and trust local runs.

## Git Workflow

- Never commit or push unless the owner asked, or a standing rule covers it. Standing rule: the owner allows merging a finished, verified milestone branch into `main` and pushing it. "Verified" means the build has 0 warnings, all logic tests pass, the affected UI tests pass, and the full UI suite passes once on the optimized build.
- Work on a branch, not `main`. Never force-push. Never skip hooks.
- Commit messages: a type prefix and a plain past-or-present-tense summary of the user-visible effect, for example `fix(outbox): a write Google failed waits 30 s, doubling to 15 minutes, before the next try`. Body explains why, in plain sentences.
- Never comment on a PR or issue, and never change a PR's status, unless told to.

## C# Coding Standard

The standard is Microsoft's .NET coding conventions and Framework Design Guidelines, enforced by `.editorconfig` and the build. If the build passes, the style is right. The key rules:

- **Namespaces match folders.** A file's namespace is its project's root namespace plus its folder path (the .NET equivalent of PHP's PSR-4). `NamespaceLayoutTests` enforces this for every file, including XAML code-behind that the IDE0130 analyzer misses. File-scoped namespaces only.
- **Naming:** PascalCase for types, methods, properties, events and constants; `I` prefix for interfaces; `T` prefix for type parameters; `_camelCase` for private instance fields; `s_camelCase` for private static fields; `PascalCase` for static readonly fields; camelCase for locals and parameters. Local constants may be PascalCase or camelCase. Test method names use `Method_Condition_Result` underscores.
- **Access modifiers are always written out.** No `this.` qualification. Language keywords for built-in types (`int`, not `Int32`).
- **Formatting is Visual Studio's default.** No column alignment of `=` or `=>`. Usings outside the namespace, `System` first, none unused. Braces on multiline blocks. Readonly fields where possible.
- **Doc comments on every public member** (the build checks them). Say what the member does and why in plain sentences; add `<param>`/`<returns>` where they help. Tests and XAML-generated code are exempt.
- **Comments say what a block is or why it exists,** never restate the code. Keep them true when the code changes.
- 4-space indentation everywhere except JSON and YAML (2). Files are UTF-8 with **CRLF** line endings; edit files in place rather than rewriting them so line endings stay intact.
- Prefer the standard library, the platform and installed packages over new code. Add a dependency only when a few lines can't do it.
- Never run `dotnet format` over the whole solution without the owner's say-so.

## Architecture Rules

- **Core owns logic, App owns UI.** Put anything testable in Core with a logic test. App code wires services, binds views and handles Windows interop.
- **Data:** SQLite through `Microsoft.Data.Sqlite`. Stores (`EventStore`, `OutboxStore`, `CalendarStore`, `AccountStore`, `ConflictStore`, `AlertLedger`, `SettingsStore`, `ShareGroupStore`) are static classes that take a connection and an optional transaction. `LeafDatabase.Open()` gives each caller its own pooled connection; the database runs in WAL mode. Every SQL statement is parameterized.
- **Schema changes** are numbered migrations in `Data/Schema.cs`, applied in order by `LeafDatabase.Migrate` and tracked in `PRAGMA user_version` (currently 12). Never edit an applied migration; add a new version, and update the tests that check `user_version`.
- **Sync:** `SyncLoop` paces syncs (15 s on screen, 60 s in the tray, 5 minutes in the tray on Energy Saver or a metered connection; a faster pace wakes the loop at once). `SyncEngine` pulls with sync tokens and handles 410 resyncs. `OutboxSender` sends queued edits in order per account.
- **Outbox safety (never lose a user's edit):** `not_before` is only the delete undo window; `retry_after` is the backoff after a failed write (30 s doubling to 15 minutes). A write that failed may already be on Google, so Undo never treats a backed-off entry as unsent. A failure that affects the whole account (network, 401/408/429, rate or quota limits) stops the pass so the order holds; a 5xx on one entry lets other events go. Sync now, a reconnect and a resume clear the backoff. Edits Google refuses for good are undone locally and reported; conflicts go to the conflict dialog.
- **Secrets:** the OAuth client and refresh tokens live in `secrets.bin` in the profile folder, encrypted with Windows DPAPI for the current user (entropy `LeafCalendar/<lowercase profile>`), written atomically (temp file then move). Access tokens stay in memory. A file with DPAPI "bad data" is moved aside to `secrets.bin.unreadable`; any other read failure throws a marked `InvalidDataException` (`SecretsUnavailable`) and must never sign anyone out or crash startup (sync retries). The one-time move from the old Windows Credential Locker store (`TokenStoreMigration`) never throws and never signs anyone out.
- **Threading:** WinRT and system events (network, power, text size, contrast) arrive off the UI thread; marshal with `DispatcherQueue.TryEnqueue` before touching UI or view models. Async event handlers (`async void`) catch every exception. Database reads that would block the UI thread for more than a frame run on the thread pool, with a generation counter so only the newest result is applied.
- **Lifetime:** subscribe to long-lived events on `Loaded` and unsubscribe on `Unloaded` or close. Keep WinRT objects whose events you handle (for example `UISettings`) in a field, or the events stop.
- **Time:** use the injected `TimeProvider`, never `DateTime.Now`. Store instants in UTC; convert to the display zone only for display. Handle DST days (23 and 25 hours), all-day events (exclusive end dates) and cross-midnight events. Format and parse with invariant culture for storage and the API.

## Native AOT and WinUI Rules

- `x:Bind` only, never `{Binding}`.
- Set `ItemsSource` to a concrete `List<T>`, never a lazy sequence.
- Never read a WinRT collection property back with a typed cast; keep your own copy.
- `System.Text.Json` only through a source-generated `JsonSerializerContext` (`LeafJsonContext`, `GoogleJsonContext`).
- No reflection: no `Activator`, `Type.GetType`, `MakeGenericType` or `Enum.GetValues(Type)`.
- P/Invoke through CsWin32 (`NativeMethods.txt`) in the App or `LibraryImport` in Core, never `DllImport`.
- The build must stay free of trim and AOT warnings.

## Security Rules

- **Never log** event content (titles, descriptions, guests, locations), tokens, codes, client secrets or email addresses. Log event names, IDs, counts and exception **type** names. `AppLog.Redact` is a second line of defense, not the first.
- **Everything from Google is untrusted** (titles, HTML descriptions, locations, attendee names, conference links). Render descriptions through `DescriptionFormatter`/`DescriptionHtml` as plain text and safe links only. Open any link only after `LinkSafety` approves it (scheme allow-list, look-alike hosts, userinfo tricks). Notifications only open known meeting hosts.
- **Escape everything** placed in toast XML, and parse toast arguments defensively.
- **The sign-in listener** binds loopback only, checks the PKCE state, answers only the expected redirect, never echoes query values into its pages, and survives connections that reset.
- **Activation input is untrusted.** The `leaf-calendar:` protocol only brings Leaf forward; never parse its path or query into actions. Test hooks (`--fake-google` and friends) exist for UI tests; don't add new ones that can redirect Google traffic or tokens in a normal run.
- **Secrets** go only through `ITokenStore`. Never write a secret anywhere else, never put one in a URL or log.
- **Least privilege:** don't add package capabilities or OAuth scopes without the owner. If scopes change, update the onboarding guide text (owner-approved) and `PRIVACY.md`.

## Design Rules

Read `docs/design-standard.md` before any UI work. It is binding. The essentials:

- Leaf should look like Windows 11 made it: stock WinUI controls, stock theme brushes, stock type ramp. Copy a real Windows surface when there is one (Settings, Quick Settings, Task Manager).
- Colors only from `{ThemeResource}` brushes in XAML; code-built visuals use `LeafBrushes` per theme and re-apply on `ActualThemeChanged`. Every overridden brush gets a HighContrast entry.
- Spacing scale 2, 4, 8, 12, 16, 24, 32 (36 for the Settings inset). Odd values only with a comment explaining the optical reason.
- Corner radius `ControlCornerRadius` (4) for controls, `OverlayCornerRadius` (8) for cards and flyouts.
- Motion: 83 ms hover fades, 167 ms fades, 250 ms glides; honor the system animation setting.
- Every icon-only control has a tooltip and `AutomationProperties.Name`; tooltips add the shortcut in parentheses. Every element a UI test touches has an `AutomationId`.
- Text follows the Windows text size (never `IsTextScaleFactorEnabled="False"`); avoid fixed heights that clip text.
- Every `ScrollViewer` uses `ScrollIndicator.ShowOnHover`.
- Settings rows follow the Windows 11 Settings pattern: one setting per card, control on the right, `ToggleSwitch` without On/Off text, every change saves immediately.
- `XamlLintTests` lint every XAML file (names, `x:Bind`, HighContrast entries, spacing, radii, live regions, fixed text heights). Add an allow-list entry only with a reason comment.

## Copy and Content Rules

- **US English** everywhere: UI, comments, docs, commits.
- **Sentence case** for all visible labels. Proper nouns keep their capitals (Google, Leaf Calendar, Windows).
- Buttons start with a verb. Items that open more UI end with "…" (the single character). Paths use "›" ("Settings › General"). Inline facts are separated with " · ". Messages end with a period.
- Error and empty text says what's wrong and what to do, never blames the user, and never shows raw exception text.
- **Never write or change user-visible wording without the owner's approval.** Quote any new or changed string in your report. Content the owner wrote is used verbatim; flag typos instead of fixing them.

## Testing Rules

- **Test-first for Core logic:** write the failing test, see it fail for the right reason, then fix. Add a regression test with every bug fix where the logic lives in Core.
- Tests must verify behavior. No tests that assert nothing; no skipped or disabled tests to make a run green.
- Run the full logic suite after every change; it is fast (about 20 seconds, 2,200+ tests).
- App-layer code has no unit-test harness; cover it with targeted UI tests and say so when something can only be checked in the app.

## Working Style

- Understand the problem fully before changing code: trace the real flow and read the callers. Fix root causes at the layer that owns the behavior.
- Keep changes as small as correctness allows. No speculative abstractions, configuration, or "for later" scaffolding.
- Never change Windows system settings (power, network, text size, display) to test something; ask the owner to check those in the app.
- Never delete or overwrite the owner's data, installs or credentials. The owner's real profile is `default`.
- Report outcomes faithfully: say which tests ran and their real results, what was not verified, and every decision you made on the owner's behalf.
- Plans live in `docs/superpowers/plans/` and track decisions; specs in `docs/superpowers/specs/`. When behavior changes, update the spec section that describes it.
