# Leaf Calendar Milestone 6 (Polish and Store Prep) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish Milestone 6 from spec Section 12 item 6, plus the package-size item in Section 13:
- a visual pass and an accessibility pass over every screen, backed by automated checks
- everything the Microsoft Store listing needs
- rich description editing (bold, italic, underline, lists), deferred from Milestone 3
- Shift+drag box select, deferred from Milestone 3
- a smaller MSIX, with the Windows App SDK AI libraries (`onnxruntime.dll`, `DirectML.dll`, and friends) left out

**Architecture:** Three tracks run in parallel. They don't share files, so each one works on its own branch and they merge cleanly.
- **Track A (Package and Store):** swap the Windows App SDK metapackage for its component packages (no AI, ML, Search, or Widgets). Add a package check script and a manual CI job. Generate real logo assets. Write the Store listing, privacy policy, and certification notes. Add a Store build switch.
- **Track B (Editing):** Core gets one list-aware reading of Google's description HTML (`DescriptionFormatter` plus a new `DescriptionHtml` writer). Writing back emits only an allowlist of tags. The editor swaps its plain `TextBox` for a `RichEditBox` with a small toolbar. Box select adds a Core hit test (`BoxSelection`), a view-model call (`SelectBox`), and a Shift branch in the existing empty-space drag.
- **Track C (Accessibility and Visual):** automated checks come first. A XAML lint (names, tooltips, `x:Bind`, theme dictionaries, the spacing and radius scale, live regions) and contrast math over every code-built color run in the logic tests, so CI enforces them. A UIA audit walks each screen for Narrator names and Tab reachability. Then come high-contrast brushes for the code-built calendar, a screenshot tour, and fixes.

A last task merges the tracks and runs everything once: the UI suite on Debug and AOT, memory three times, package size, screenshots, spec updates, and the reinstall.

**Tech Stack:** .NET 10 / C# 14, Windows App SDK 2.5.1 (component packages from Task A1 on; WinUI 3, packaged MSIX, Release Native AOT), CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.12, Meziantou.Framework.Scheduling [4.1.3], xUnit v3 on Microsoft.Testing.Platform, FlaUI.UIA3 5.0.0. No new packages. The Windows App SDK component packages are the metapackage's own dependencies, at the versions it already pulls.

**Spec:** `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`. Read Sections 1.3 item 5, 3.2, 3.4, 4.5–4.7, 6, 7.2–7.3, 8 (M4 surfaces), 10.3, 11, 12 items 4–6, and 13. Also read the design standard, `docs/design-standard.md`. It's the checklist for the visual pass, and Section 14 is the accessibility baseline. The second Milestone 3 polish plan, `docs/superpowers/plans/2026-09-30-m3-polish-2.md`, shows the established task patterns. Milestones 4 and 5 land first. Their plans are `docs/superpowers/plans/*-m4-*.md` and `docs/superpowers/plans/2026-10-01-m5-power-features.md`, and only Tasks C2 and C3 need their AutomationIds.

## Tracks and Order

| Track | Tasks | Branch | Owns (no other track edits these) |
|---|---|---|---|
| A: Package and Store | A1, A2 | `m6-a` | `Directory.Packages.props`, `src/LeafCalendar.App/LeafCalendar.App.csproj`, `Package.appxmanifest`, `src/LeafCalendar.App/Assets/**`, `assets/brand/**`, `tools/**`, `.github/workflows/ci.yml`, `docs/store/**`, `src/LeafCalendar.App/ThirdPartyNotices.txt`, `Views/Settings/AboutPage.xaml(.cs)`, `tests/LeafCalendar.Tests/PackageManifestTests.cs`, `tests/LeafCalendar.UITests/StoreTests.cs` |
| B: Editing | B1, B2, B3 | `m6-b` | `Core/Events/DescriptionFormatter.cs`, `Core/Events/DescriptionHtml.cs` (new), `Core/Editing/EventJson.cs`, `Core/Editing/EventEditor.cs`, `Core/Editing/EventDraft.cs`, `Core/Views/BoxSelection.cs` (new), `App/Controls/RichDescription.cs` (new), `Views/EventEditorView.xaml(.cs)`, `ViewModels/EventEditorViewModel.cs`, `ViewModels/CalendarViewModel.cs`, `Controls/TimeGridView.cs`, `Controls/MonthGridView.cs`, and their tests (`DescriptionFormatterTests`, `DescriptionHtmlTests`, `EventJsonTests`, `EventEditorTests`, `BoxSelectionTests`, UI `RichDescriptionTests`, `BoxSelectTests`) |
| C: Accessibility and Visual | C1, C2, C3 | `m6-c` | `Core/Views/ChromeColors.cs` (new), `Core/Views/EventColors.cs`, `App/Controls/LeafBrushes.cs`, `EventBlock.cs`, `WeekRow.cs`, `DayColumn.cs`, `AllDayCanvas.cs`, `Styles/LeafTheme.xaml`, every other `.xaml(.cs)` view not listed under A or B, `docs/design-standard.md`, `tests/LeafCalendar.Tests/XamlLintTests.cs`, `ChromeColorsTests.cs`, `EventColorsTests.cs`, UI `AccessibilityTests.cs`, `ScreenshotTour.cs`, `Support/A11yAudit.cs`, `Support/LeafApp.cs` |
| Close | Task 9 | `m6-polish` | the spec, `memory-budget.json`, anything the batch run finds |

- Create `m6-polish` from `main` once Milestones 4 and 5 are merged. Branch `m6-a`, `m6-b`, and `m6-c` from it.
- Inside each track, tasks run in order (A1 then A2; B1, B2, B3; C1, C2, C3). The tracks run side by side.
- **A finding in a file another track owns is not fixed in place.** Record it in the task report. Task 9 fixes it after the merge, with a test.
- Task 9 merges `m6-a`, `m6-b`, and `m6-c` into `m6-polish`, in that order, and then runs the batch.

## Global Constraints

Every Milestone 3 constraint still holds (`.superpowers/sdd/2026-09-30-m3-events/global-constraints.md` is binding). Every Milestone 4 and 5 constraint holds too.

- `net10.0-windows10.0.22621.0`, min `10.0.22000.0`. Packaged MSIX, `WindowsAppSDKSelfContained`. Release-only Native AOT with **0 IL warnings**. `TreatWarningsAsErrors` with `AnalysisLevel` `latest-recommended`, and NuGet audit on.
- No Google client libraries, no Newtonsoft, no Entity Framework, no WebView2 use, no Ical.Net or EWSoftware.PDI. `Meziantou.Framework.Scheduling` stays pinned `[4.1.3]`. **No new packages.** The Windows App SDK component packages in Task A1 replace the metapackage at the same versions it resolves today.
- **JSON:** `System.Text.Json` source generation only (`GoogleJsonContext`, `LeafJsonContext`). Never call a serializer overload without `JsonTypeInfo`. Build event JSON with `System.Text.Json.Nodes`, and read it with `JsonDocument`.
- **XAML:** `x:Bind` only, with no `{Binding}` and no `DisplayMemberPath` (Task C1's lint enforces this from then on). WinUI classes that implement WinRT interfaces must be `partial`.
- **Native AOT traps:**
  - **Read-back rule:** never read an object back through a typed cast of a WinRT property (`(TextBlock)button.Content`, `(Foo)element.Tag`, `x.Tag is Foo`, `(TranslateTransform)el.RenderTransform`). Keep your own references in fields.
  - **List rule:** never hand a list of Core records or classes to a WinRT `ItemsSource`. Wrap each item in an App-project class first.
  - Both crash only under AOT, with 0xc000027b in `Microsoft.UI.Xaml.dll`, and Debug never shows them. That's why every task's UI tests also run on the AOT build in Task 9.
  - `RichEditBox` document calls (`ITextRange`, `ITextCharacterFormat`, `ITextParagraphFormat`) are fine. They are typed WinRT interfaces, not casts.
- **Untrusted text:** event titles, descriptions, locations, guest and contact data come from anyone.
  - Descriptions render as native text runs only.
  - Links are clickable only through `LinkSafety.IsClickableInDescription` (`https`, `mailto`).
  - Every launch goes through `LeafServices.LaunchAsync`.
  - Never set `NavigateUri` from event content.
  - **Writing a description back to Google emits only `b`, `i`, `u`, `br`, `ul`, `ol`, `li`, and `a href` with an allowlisted `https`/`mailto` target, with all text HTML-encoded** (Task B1).
  - The editor never holds a live link, so nothing in it can launch.
- **Logs:** internal IDs only, with `AppLog.Redact` as the safety net. Never log event titles, descriptions (raw or rendered), locations, guest emails, calendar names, contact data, clipboard text, or file paths under the user's profile.
- **Secrets:** OAuth client secret and refresh tokens live only in Credential Locker. Store build values (identity name, publisher) are public, not secrets.
- **US English** everywhere ("canceled", "color", "center"). Google's own value `"cancelled"` stays as Google spells it. Store listing and privacy policy text too.
- **Tests:**
  - Run from the repo root: `dotnet test --project <csproj> [--filter-class "*Name"] [--filter-method "*Name*"] [--output Detailed]`.
  - Async calls pass `TestContext.Current.CancellationToken`.
  - Test classes are `public`, and a class holding a disposable implements `IDisposable`.
- **UI tests run in one batch, in Task 9.**
  - Each task writes its UI tests and makes `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` pass, but doesn't run them.
  - Task 9 runs the whole suite on Debug (`pwsh tools/dev-register.ps1` first), then on AOT (`pwsh tools/publish-aot.ps1 -Register`).
  - The desktop must be unlocked. `Access is denied` means it's locked, so report it and don't work around it.
- **Fake Google:** only on `uitest-*` profiles, loopback only (spec 4.8). No new fake endpoints are needed. Rich descriptions use `FakeGoogleServer.AddEvent`, and writes are read with `WaitForWrite`.
- **System settings:** never change Windows settings (contrast themes, text size, display scale) from a script or test. The high-contrast and text-scaling UI tests **skip unless the owner has turned that setting on** (Task 9 asks them).
- **Code style (owner):**
  - 4-space indent, and XAML indented.
  - A Title Case comment above each logical block, and closing comments on long XAML blocks (`<!-- /Description Toolbar -->`).
  - XML doc `<summary>` on public types and members.
  - Guard clauses first.
  - Aligned `=` in related assignment groups.
  - `snake_case` only where the owner's JS/PHP rules apply (not C#).
  - PowerShell scripts start with a header comment saying what they output, what they're for, and what they need.
- **Copy:** owner-written text is verbatim. Drafted Store and privacy text is marked "draft for owner review" in its file until the owner approves it.
- **Commits:** one commit at the end of each task, on that track's branch. Never push from a track. Task 9 merges to `main` and pushes, per the owner's standing OK for milestone ends.

## Review Focus

1. **Hostile description markup reaching the editor and going back to Google.** An invite can carry `<script>`, `<style>`, `<img onerror>`, `<iframe>`, `<a href="javascript:…">`, `data:` links, unclosed and nested tags, entity tricks (`&lt;script&gt;`), bidi controls, or 40,000 characters. The editor must show it as inert text, and saving must write only the allowlisted tags, with text encoded. Tests: `DescriptionHtmlTests.Normalize_HostileMarkup_OnlyAllowlistedTagsSurvive` and `DescriptionHtmlTests.Normalize_RandomTagSoup_IsAllowlistedAndIdempotent` (Task B1).
2. **Opening and saving an event without touching its description.** Google's original HTML (tables, Google's own spans) must not be rewritten into Leaf's subset. The patch must carry no `description`. Tests: `EventJsonTests.BuildPatch_RichDescriptionUntouched_IsNotSent` (Task B1) and the UI test `RichDescriptionTests.SaveWithoutTouchingDescription_SendsNoDescription` (Task B2).
3. **Pasting from Word or a web page.** Pictures, tables, styles, and embedded objects must paste as plain text. No object placeholder (U+FFFC) or control character may reach Google. Tests: `DescriptionHtmlTests.Write_ObjectPlaceholdersAndControlCharacters_AreDropped` (Task B1) and `RichDescriptionTests.PasteRichText_InsertsPlainText` (Task B2).
4. **Box select dragged backward, across midnight, or on a daylight-saving day.** Dragging up-left, or on the fall-back Sunday, must select what's under the box in the view's zone. Tests: `BoxSelectionTests.InTimeBox_DraggedBackward_SameAsForward` and `BoxSelectionTests.InTimeBox_FallBackDay_UsesWallClockBand` (Task B3).
5. **The trimmed package missing something Leaf really uses.** Notifications (Milestone 4) live in Foundation, Mica and title bar live in WinUI and InteractiveExperiences, and text lives in DWrite. A crash would show only in the AOT package. Tests: `PackageManifestTests.AppProject_UsesComponentPackagesWithoutAi` (Task A1) and `StoreTests.TrimmedPackage_LaunchesAndShowsANotification` (Task A1, run on AOT in Task 9).

---

## File Structure

```
Directory.Packages.props                         ~ A1  component packages instead of Microsoft.WindowsAppSDK
.github/workflows/ci.yml                         ~ A1  manual "package" job (AOT publish + check)
tools/check-package.ps1                          NEW A1  fails on AI DLLs or an MSIX over budget
tools/make-assets.ps1                            NEW A2  logo PNGs at every scale from one 1024 px master
tools/make-notices.ps1                           NEW A2  ThirdPartyNotices.txt from the shipped packages' license files
tools/publish-aot.ps1                            ~ A2  -Store builds a .msixupload with the Partner Center identity
tools/sign-install.ps1                           (unchanged)
assets/brand/leaf-icon-1024.png                  NEW A2  owner-supplied master icon
docs/store/listing.md, privacy-policy.md,        NEW A2  Store text, privacy policy, certification notes,
  certification-notes.md, partner-center.json          Partner Center identity (owner values)
src/LeafCalendar.App/
    LeafCalendar.App.csproj                      ~ A1/A2  component package refs; ThirdPartyNotices.txt as content
    Package.appxmanifest                         ~ A2  every logo, SmallTile/LargeTile, description, MaxVersionTested
    Assets/*.scale-*.png, *.targetsize-*.png     NEW A2  generated
    ThirdPartyNotices.txt                        NEW A2  generated
    Views/Settings/AboutPage.xaml(.cs)           ~ A2  Privacy policy, Third-party notices (if Milestone 5 didn't add them)
    Controls/RichDescription.cs                  NEW B2  RichEditBox <-> DescriptionLine list
    Views/EventEditorView.xaml(.cs)              ~ B2  RichEditBox, format toolbar, plain paste
    ViewModels/EventEditorViewModel.cs           ~ B2  Description is HTML
    ViewModels/CalendarViewModel.cs              ~ B3  SelectBox
    Controls/TimeGridView.cs, MonthGridView.cs   ~ B3  Shift+drag box
    Controls/LeafBrushes.cs                      ~ C1/C2  colors from ChromeColors; high-contrast system colors
    Controls/EventBlock.cs, WeekRow.cs,          ~ C1/C2  EventColors.PastOpacity; CardPalette (high contrast)
      DayColumn.cs, AllDayCanvas.cs
    Styles/LeafTheme.xaml and other views        ~ C1–C3  lint and audit fixes, HighContrast dictionaries, live regions
src/LeafCalendar.Core/
    Events/DescriptionFormatter.cs               ~ B1  list markers carry ListKind; ul/ol start a new line; ol numbers
    Events/DescriptionHtml.cs                    NEW B1  Lines / Write / Normalize
    Editing/EventDraft.cs, EventJson.cs,         ~ B1  Description is Leaf-normalized HTML
      EventEditor.cs
    Views/BoxSelection.cs                        NEW B3  which timed events a time-grid box covers
    Views/ChromeColors.cs                        NEW C1  code-built chrome colors (hex), testable
    Views/EventColors.cs                         ~ C1  PastOpacity
tests/LeafCalendar.Tests/
    PackageManifestTests.cs                      NEW A1/A2
    DescriptionHtmlTests.cs                      NEW B1   (+ DescriptionFormatterTests, EventJsonTests, EventEditorTests ~)
    BoxSelectionTests.cs                         NEW B3
    XamlLintTests.cs, ChromeColorsTests.cs       NEW C1   (+ EventColorsTests ~)
tests/LeafCalendar.UITests/
    StoreTests.cs                                NEW A1/A2
    RichDescriptionTests.cs                      NEW B2
    BoxSelectTests.cs                            NEW B3
    AccessibilityTests.cs, Support/A11yAudit.cs  NEW C2
    ScreenshotTour.cs                            NEW C3
    Support/LeafApp.cs                           ~ C2  Focused(), AllWindows()
```

---

### Task A1: Leave the AI Libraries Out of the Package

Spec Section 13: "about 40 MB of that is Windows App SDK AI libraries (`onnxruntime.dll`, `DirectML.dll`). Trim them in Milestone 6."

**Found while planning:**
- The registered AOT layout is 134 MB unpacked, and `LeafCalendar.App_0.1.0.0_x64.msix` is 55.9 MB.
- Leaf uses none of these:
  - `onnxruntime.dll` 21.7 MB and `DirectML.dll` 18.7 MB (ML)
  - `Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll` 3.9 MB and `Microsoft.Windows.Search.dll` 3.5 MB (Search)
  - `Microsoft.Windows.Widgets.dll` 2.5 MB (Widgets)
  - `PerceptiveStreaming.dll` 1.8 MB, `Microsoft.Windows.AI.*` (about 4 MB), `Microsoft.Windows.Workloads*`, `NPUDetect.dll`, and `workloads*.json` (AI)
- `Microsoft.WindowsAppSDK` 2.5.1 is a metapackage. Its nuspec depends on Base 2.0.4, Foundation 2.3.12, InteractiveExperiences 2.1.9, WinUI 2.3.9, DWrite 2.1.0, Widgets 2.0.5, AI 2.5.5, ML 2.1.94, Search 2.5.5, and Runtime [2.5.1].
- Referencing the components directly, without AI, ML, Search, and Widgets, drops the DLLs **and** their activatable-class entries in the generated `AppxManifest.xml`. A file-delete approach would leave manifest entries pointing at missing files, which certification flags.

**Files:**
- Modify: `Directory.Packages.props`, `src/LeafCalendar.App/LeafCalendar.App.csproj`
- Create: `tools/check-package.ps1`
- Modify: `.github/workflows/ci.yml`
- Test: `tests/LeafCalendar.Tests/PackageManifestTests.cs` (new), `tests/LeafCalendar.UITests/StoreTests.cs` (new)

**Interfaces:**
- Produces:
  - `tools/check-package.ps1 [-Msix <path>] [-MaxMb <int>]`, which exits non-zero on a forbidden file or an oversized package and prints `MSIX: <n> MB`
  - `PackageManifestTests.RepoRoot()`, a `public static string` that A2 reuses

- [ ] **Step 1: Write the failing manifest test**

```csharp
using System.Xml.Linq;

namespace LeafCalendar.Tests;

/// <summary>Checks on the app project and package manifest that keep the MSIX small and Store-ready.</summary>
public class PackageManifestTests
{
    /// <summary>The folder holding <c>LeafCalendar.slnx</c>.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeafCalendar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root (LeafCalendar.slnx) not found.");
    }

    static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    // Windows App SDK: Component Packages Only, None Of The AI/ML/Search/Widgets Ones (spec 13)
    [Fact]
    public void AppProject_UsesComponentPackagesWithoutAi()
    {
        var references = XDocument.Parse(Read("src", "LeafCalendar.App", "LeafCalendar.App.csproj"))
            .Descendants("PackageReference").Select(r => (string?)r.Attribute("Include")).ToList();

        Assert.DoesNotContain("Microsoft.WindowsAppSDK", references);
        Assert.Contains("Microsoft.WindowsAppSDK.WinUI", references);
        Assert.Contains("Microsoft.WindowsAppSDK.Foundation", references);
        Assert.DoesNotContain(references, r => r is "Microsoft.WindowsAppSDK.AI" or "Microsoft.WindowsAppSDK.ML" or "Microsoft.WindowsAppSDK.Search" or "Microsoft.WindowsAppSDK.Widgets");
    }
}
```

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*PackageManifestTests"`. Expected: FAIL (`Microsoft.WindowsAppSDK` is referenced).

- [ ] **Step 2: Swap the references**

`Directory.Packages.props`: replace the `Microsoft.WindowsAppSDK` line with:

```xml
    <!-- Windows App SDK 2.5.1 Components (the metapackage minus AI, ML, Search, Widgets; spec 13) -->
    <PackageVersion Include="Microsoft.WindowsAppSDK.Base" Version="2.0.4" />
    <PackageVersion Include="Microsoft.WindowsAppSDK.DWrite" Version="2.1.0" />
    <PackageVersion Include="Microsoft.WindowsAppSDK.Foundation" Version="2.3.12" />
    <PackageVersion Include="Microsoft.WindowsAppSDK.InteractiveExperiences" Version="2.1.9" />
    <PackageVersion Include="Microsoft.WindowsAppSDK.Runtime" Version="2.5.1" />
    <PackageVersion Include="Microsoft.WindowsAppSDK.WinUI" Version="2.3.9" />
```

`LeafCalendar.App.csproj`: replace `<PackageReference Include="Microsoft.WindowsAppSDK" />` with the six `PackageReference`s, one per line, under a `<!-- Windows App SDK Components (no AI libraries) -->` comment.

First run `git grep -n "Microsoft.WindowsAppSDK\"" -- "*.csproj"`. If another project (the UI tests) references the metapackage, give it only the components it uses.

Then build and run the test: `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), then Step 1's test, which should PASS.

**If the build fails** with a missing type or target from a dropped component, record which one. Put back only that component if it's not one of the four AI-bearing packages. If an AI-bearing package turns out to be required by the build targets, stop and report: don't fall back to deleting files, because of the manifest entries noted above.

- [ ] **Step 3: Write the package check script**

`tools/check-package.ps1`:

```powershell
# Prints the size of Leaf's MSIX and fails when it carries Windows App SDK AI/ML/Search/Widgets
# files or is over budget. Used after tools/publish-aot.ps1 (locally and in the manual CI job).
# Requires PowerShell 7 on Windows.
param(
    [string]$Msix,
    [int]$MaxMb = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Newest MSIX Under AppPackages Unless One Was Given
if (-not $Msix) {
    $Msix = (Get-ChildItem (Join-Path $root 'src/LeafCalendar.App/AppPackages') -Recurse -Filter '*.msix' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Msix) { throw 'No .msix found. Run tools/publish-aot.ps1 first.' }

# Forbidden Payload (Leaf has no AI features; spec 1.4 and 13)
$forbidden = @('onnxruntime.dll', 'DirectML.dll', 'NPUDetect.dll', 'PerceptiveStreaming.dll',
    'Microsoft.Windows.Widgets.dll', 'Microsoft.Windows.Search.dll',
    'Microsoft.Asg.SemanticIndex.*', 'Microsoft.Windows.AI.*', 'Microsoft.Windows.Workloads*', 'workloads*.json')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Msix)
try {
    $hits = $zip.Entries | Where-Object { $name = $_.Name; $forbidden | Where-Object { $name -like $_ } }
} finally {
    $zip.Dispose()
}

$mb = [math]::Round((Get-Item $Msix).Length / 1MB, 1)
Write-Host "MSIX: $mb MB ($Msix)"

if ($hits) {
    $hits | ForEach-Object { Write-Host "Forbidden: $($_.FullName)" }
    exit 1
}
if ($MaxMb -gt 0 -and $mb -gt $MaxMb) {
    Write-Host "Over budget: $mb MB > $MaxMb MB"
    exit 1
}
```

- [ ] **Step 4: Measure and set the budget**

1. Run `pwsh tools/publish-aot.ps1` and check for 0 IL warnings.
2. Run `pwsh tools/check-package.ps1`. Expected: no `Forbidden:` lines.
3. Record the size, then set the default `$MaxMb` in the script to that size rounded **up** to the next multiple of 5. Add a comment with the date and the measured value (`# Measured 2026-10-xx: NN.N MB`).
4. Run it again and expect exit 0.

- [ ] **Step 5: Manual CI job**

Append this job to `.github/workflows/ci.yml`. It runs only by manual trigger (Windows runners cost double minutes on private repos, spec 11). Add the trigger too:

```yaml
on:
  push:
  pull_request:
  workflow_dispatch:

jobs:
  # ... build-and-test unchanged ...

  # Package Size (manual): Release AOT MSIX, no AI libraries, under budget
  package:
    if: github.event_name == 'workflow_dispatch'
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v5

      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json

      - run: pwsh tools/publish-aot.ps1

      - run: pwsh tools/check-package.ps1
```

- [ ] **Step 6: Trimmed-package smoke UI test (runs on AOT in Task 9)**

`tests/LeafCalendar.UITests/StoreTests.cs`. It launches the registered package and checks that the main window comes up and syncs. If Milestone 4's plan has a test-mode way to fire a notification, that check goes here too: use the M4 UI test's launch argument and its toast assertion helper (search the M4 plan for `AppNotification` and `--` test arguments). Otherwise the test asserts the window and sync only, and the report says so.

```csharp
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class StoreTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // Trimmed Package: WinUI, Mica, DWrite, And Foundation Still Load (Review Focus 5)
    [Fact]
    public void TrimmedPackage_LaunchesAndShowsANotification()
    {
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
        Assert.Contains(_google.Requests, r => r.Contains("/events", StringComparison.Ordinal));
        // Milestone 4 notification check goes here (see Step 6)
    }
}
```

Build: `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` (0 warnings). Don't run it until Task 9.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/LeafCalendar.App/LeafCalendar.App.csproj tools/check-package.ps1 .github/workflows/ci.yml tests/LeafCalendar.Tests/PackageManifestTests.cs tests/LeafCalendar.UITests/StoreTests.cs
git commit -m "build: ship Windows App SDK components without the AI libraries; package size check"
```

---

### Task A2: Store Listing Requirements

**What the Store needs, and where each item comes from:**

| Requirement | Plan |
|---|---|
| Identity name, publisher CN, publisher display name from Partner Center | **Owner input.** `docs/store/partner-center.json`. The Store build applies it (Step 5). The repo manifest keeps the dev identity, so UI tests, local data, and `sign-install.ps1` don't change. |
| Logos at every scale (Square44 with target sizes and unplated, Square150, Wide310, SmallTile, LargeTile, StoreLogo, SplashScreen) | `tools/make-assets.ps1` from **owner-supplied** `assets/brand/leaf-icon-1024.png`. Today's assets are 187–626 byte placeholders. |
| Privacy policy URL (Leaf uses the internet and personal data) | Draft in `docs/store/privacy-policy.md`. **Owner hosts it** and gives the URL. The About page links it. |
| Third-party notices | `ThirdPartyNotices.txt` generated verbatim from the shipped packages' license files, shown in a dialog from About |
| `runFullTrust` restricted capability justification | Text in `docs/store/certification-notes.md` |
| Notes for certification (testers need a Google account and an OAuth client) | Draft in `docs/store/certification-notes.md`. **Owner decides** whether to give testers a throwaway account and client. |
| Listing text, category, search terms, screenshots | `docs/store/listing.md`. Screenshots come from Task C3's tour, in Task 9. |
| Store package (`.msixupload`, x64) | `tools/publish-aot.ps1 -Store` |
| Windows App Certification Kit | Task 9 Step 6 (owner runs it elevated) |

**Files:**
- Create: `tools/make-assets.ps1`, `tools/make-notices.ps1`, `docs/store/listing.md`, `docs/store/privacy-policy.md`, `docs/store/certification-notes.md`, `docs/store/partner-center.json`, `src/LeafCalendar.App/ThirdPartyNotices.txt` (generated), `src/LeafCalendar.App/Assets/*` (generated)
- Modify: `src/LeafCalendar.App/Package.appxmanifest`, `src/LeafCalendar.App/LeafCalendar.App.csproj`, `tools/publish-aot.ps1`, `src/LeafCalendar.App/Views/Settings/AboutPage.xaml(.cs)`
- Test: `tests/LeafCalendar.Tests/PackageManifestTests.cs`, `tests/LeafCalendar.UITests/StoreTests.cs`

**Interfaces:**
- Consumes: `PackageManifestTests.RepoRoot()` (A1).
- Produces: `publish-aot.ps1 -Store`, which writes `AppPackages/**/*.msixupload`. About page AutomationIds `PrivacyPolicyLink`, `ThirdPartyNoticesButton`, and `ThirdPartyNoticesText` (in the dialog).

- [ ] **Step 1: Failing manifest and asset tests**

Add to `PackageManifestTests`:

```csharp
    static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    static readonly XNamespace Uap        = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    static XDocument Manifest() => XDocument.Parse(Read("src", "LeafCalendar.App", "Package.appxmanifest"));

    // Every Logo The Manifest Names Exists At Every Scale The Store Asks For
    [Theory]
    [InlineData("Square44x44Logo")]
    [InlineData("Square150x150Logo")]
    [InlineData("Wide310x150Logo")]
    [InlineData("SmallTile")]
    [InlineData("LargeTile")]
    [InlineData("StoreLogo")]
    [InlineData("SplashScreen")]
    public void Logo_ExistsAtEveryScale(string name)
    {
        var assets = Path.Combine(RepoRoot(), "src", "LeafCalendar.App", "Assets");
        foreach (var scale in new[] { 100, 125, 150, 200, 400 })
        {
            Assert.True(File.Exists(Path.Combine(assets, $"{name}.scale-{scale}.png")), $"{name}.scale-{scale}.png is missing");
        }

        Assert.Contains(Manifest().Descendants().Attributes(), a => a.Value == $@"Assets\{name}.png");
    }

    // Taskbar And Start Icons: Plated And Unplated Target Sizes
    [Fact]
    public void AppIcon_HasTargetSizesAndUnplated()
    {
        var assets = Path.Combine(RepoRoot(), "src", "LeafCalendar.App", "Assets");
        foreach (var size in new[] { 16, 24, 32, 48, 256 })
        {
            Assert.True(File.Exists(Path.Combine(assets, $"Square44x44Logo.targetsize-{size}.png")));
            Assert.True(File.Exists(Path.Combine(assets, $"Square44x44Logo.targetsize-{size}_altform-unplated.png")));
            Assert.True(File.Exists(Path.Combine(assets, $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")));
        }
    }

    // The Repo Keeps The Dev Identity; Only The Store Build Swaps In Partner Center's (Step 5)
    [Fact]
    public void Manifest_KeepsDevIdentityAndTestedVersion()
    {
        var identity = Manifest().Root!.Element(Foundation + "Identity")!;
        Assert.Equal("LeafCalendar", (string?)identity.Attribute("Name"));
        Assert.Equal("CN=Artistro08", (string?)identity.Attribute("Publisher"));

        var family = Manifest().Descendants(Foundation + "TargetDeviceFamily").Single();
        Assert.Equal("10.0.26100.0", (string?)family.Attribute("MaxVersionTested"));
    }

    // Notices Ship In The Package
    [Fact]
    public void ThirdPartyNotices_IsPackagedContent()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "LeafCalendar.App", "ThirdPartyNotices.txt")));
        Assert.Contains("ThirdPartyNotices.txt", Read("src", "LeafCalendar.App", "LeafCalendar.App.csproj"), StringComparison.Ordinal);
    }
```

Run: `--filter-class "*PackageManifestTests"`. Expected: the new tests FAIL.

- [ ] **Step 2: Generate the logos**

The owner supplies `assets/brand/leaf-icon-1024.png` (square, transparent background). If it isn't there, stop this step and list it in the report. Every other step still goes ahead.

`tools/make-assets.ps1`:

```powershell
# Writes every MSIX logo Leaf's manifest needs (all scales, target sizes, unplated forms) into
# src/LeafCalendar.App/Assets from one square master PNG. Used for Store prep (Milestone 6).
# Requires PowerShell 7 on Windows (System.Drawing).
param([string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets/brand/leaf-icon-1024.png'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/LeafCalendar.App/Assets'
$master = [System.Drawing.Image]::FromFile($Source)

# Draws The Icon Centered On A Transparent Canvas, Taking $Fill Of The Shorter Side
function Write-Logo([string]$Name, [int]$Width, [int]$Height, [double]$Fill) {
    $bitmap   = New-Object System.Drawing.Bitmap $Width, $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $side = [int]([math]::Min($Width, $Height) * $Fill)
    $graphics.DrawImage($master, [int](($Width - $side) / 2), [int](($Height - $side) / 2), $side, $side)
    $bitmap.Save((Join-Path $assets $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

# Base Sizes (100% Scale) And How Much Of Each The Icon Fills
$logos = @{
    'Square44x44Logo'   = @(44, 44, 1.0)
    'Square150x150Logo' = @(150, 150, 0.66)
    'Wide310x150Logo'   = @(310, 150, 0.66)
    'SmallTile'         = @(71, 71, 0.66)
    'LargeTile'         = @(310, 310, 0.66)
    'StoreLogo'         = @(50, 50, 1.0)
    'SplashScreen'      = @(620, 300, 0.5)
}
foreach ($name in $logos.Keys) {
    $w, $h, $fill = $logos[$name]
    foreach ($scale in 100, 125, 150, 200, 400) {
        Write-Logo "$name.scale-$scale.png" ([int]($w * $scale / 100)) ([int]($h * $scale / 100)) $fill
    }
}

# Taskbar/Start Target Sizes: Plated, Unplated, And Light Unplated Are The Same Art
foreach ($size in 16, 24, 32, 48, 256) {
    foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') {
        Write-Logo "Square44x44Logo.targetsize-$size$suffix.png" $size $size 1.0
    }
}

$master.Dispose()
Get-ChildItem $assets -Filter '*.png' | Where-Object { $_.Name -notmatch '\.(scale|targetsize)-' } | Remove-Item
Write-Host "Logos written to $assets"
```

Run it: `pwsh tools/make-assets.ps1`. The last line removes the old unqualified placeholders, because MRT resolves `Assets\X.png` to the qualified files.

`MainWindow.xaml` and `OnboardingWindow.xaml` load `ms-appx:///Assets/Square44x44Logo.png`, and MRT resolves that to the scale files, so no change there. Confirm on Debug in Task 9's screenshots that the title bar icon shows.

- [ ] **Step 3: Manifest**

In `Package.appxmanifest`:
- `uap:VisualElements`: add `Square71x71Logo` and `Square310x310Logo` through `uap:DefaultTile` (`Square71x71Logo="Assets\SmallTile.png" Square310x310Logo="Assets\LargeTile.png"`), plus `ShortName="Leaf Calendar"`.
- Use this `Description`: "A fast native Windows calendar for Google Calendar, with reminders, one-key meeting join, and offline editing." It's drafted, so mark it in the report for owner review.
- Keep `Identity` and `MaxVersionTested="10.0.26100.0"` as they are.

In `LeafCalendar.App.csproj`, under the assets `ItemGroup`:

```xml
        <!-- Third-Party Notices (shown from Settings › About) -->
        <Content Include="ThirdPartyNotices.txt" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 4: Third-party notices, verbatim**

`tools/make-notices.ps1` writes `src/LeafCalendar.App/ThirdPartyNotices.txt`. For each package that ships in the MSIX, it writes a header line `== <id> <version> ==`, then copies the package's own license file from the NuGet cache **verbatim** (`LICENSE*`, `License*`, `*.txt` named license, or `<license>` text from the nuspec when no file exists).

The packages:
- `Microsoft.WindowsAppSDK.*` (the six components)
- `CommunityToolkit.Mvvm`
- `Microsoft.Data.Sqlite`, `Microsoft.Data.Sqlite.Core`, and the `SQLitePCLRaw.*` packages it pulls (`dotnet list src/LeafCalendar.App package --include-transitive` gives the exact ids and versions)
- `Meziantou.Framework.Scheduling`
- the .NET runtime (`microsoft.netcore.app.runtime.win-x64` LICENSE.TXT and THIRD-PARTY-NOTICES.TXT)

`Microsoft.Windows.CsWin32` is build-only and doesn't ship.

```powershell
# Writes src/LeafCalendar.App/ThirdPartyNotices.txt from the license files of every package that
# ships in Leaf's MSIX, copied verbatim from the NuGet cache. Run after package changes.
# Requires PowerShell 7 and a restored solution (dotnet restore).
```

The body loops over `dotnet list src/LeafCalendar.App/LeafCalendar.App.csproj package --include-transitive --format json`. It skips `Microsoft.Windows.CsWin32`, finds each `~/.nuget/packages/<id lowercase>/<version>/`, and appends the first license file there. A package without one fails the script with its id, so nothing is silently missing. Run it once, and commit the output.

- [ ] **Step 5: Store build switch**

Add to `tools/publish-aot.ps1` a `[switch]$Store`, and update the header comment to say what `-Store` produces. When it's set, the script:
1. Reads `docs/store/partner-center.json` (`{ "identityName": "...", "publisher": "CN=...", "publisherDisplayName": "...", "version": "1.0.0.0" }`). If the file is missing or has an empty value, it throws "Fill in docs/store/partner-center.json from Partner Center › Product identity first."
2. Copies `Package.appxmanifest` to `Package.appxmanifest.dev`, then sets `Identity/@Name`, `@Publisher`, `@Version`, and `Properties/PublisherDisplayName` in place with `[xml]`.
3. Publishes with `-p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Never -p:AppxPackageSigningEnabled=false`.
4. Restores `Package.appxmanifest` from the `.dev` copy in a `finally`, so the repo never keeps Store identity.

`# ponytail: in-place manifest swap with a finally restore; switch to an MSBuild-generated manifest if a second store target appears.`

Commit `docs/store/partner-center.json` with empty strings and `"version": "1.0.0.0"`. The owner fills it in.

- [ ] **Step 6: Store documents (drafts for owner review)**

`docs/store/listing.md`:

```markdown
# Leaf Calendar: Microsoft Store Listing

> Draft for owner review. Nothing here is final until the owner approves it.

## Product Name
Leaf Calendar

## Category
Productivity

## Short Description
A fast native Windows calendar for Google Calendar.

## Description
Leaf Calendar is a native Windows 11 calendar for your Google accounts. It's quick to open, light on memory, and lives in the tray, so your day is a click away.

- See every Google account and calendar together in day, week, month, or custom views.
- Get real Windows notifications for reminders, plus a "Join now" alert that stays until you act.
- Join your next meeting with one shortcut, signed in with the right Google account.
- Edit offline. Changes sync when you're back online, and Leaf asks before overwriting anything that changed on Google.
- Search events and run every action from the command menu.
- Share your availability as text, overlay a teammate's calendar, and find a time to meet.

Leaf talks directly to Google. There's no Leaf server and no analytics. You'll create your own Google Cloud OAuth client the first time you run it, and Leaf walks you through it.

## Features
- Google Calendar, multiple accounts
- Day, week, month, and 1–31 day views
- Tray agenda and meeting join shortcut
- Real Windows reminders
- Offline editing with conflict review
- Light, dark, and contrast themes

## Search Terms
Google Calendar, calendar, meetings, schedule, agenda, Google Meet, reminders

## Screenshots
1366 × 768 or larger, from the screenshot tour (Task C3), fake data only:
1. Week view with the details panel (light)
2. Week view (dark)
3. Event editor with a formatted description
4. Month view
5. Settings
6. Tray flyout (Milestone 4)

## System Requirements
Windows 11 (version 21H2, build 22000) or later, x64.

## Privacy Policy URL
(owner: hosted copy of docs/store/privacy-policy.md)

## Support Contact
(owner)
```

`docs/store/privacy-policy.md`: a plain-language policy, marked "Draft for owner review". It covers, one short section each, and states only what the spec says Leaf does:
- What Leaf accesses: Google Calendar, People API read-only contacts and other contacts for guest suggestions (spec 4.2 scopes).
- Where data goes: only directly between your PC and Google over HTTPS. No Leaf server, no analytics, no ads, no selling.
- What stays on your PC: the calendar cache in the app's local folder, sign-in tokens in Windows Credential Locker, and logs without event content (spec 4.3–4.6). Contact suggestions live in memory only.
- How to delete: disconnect an account in Settings › Accounts, which revokes access and deletes its local data; uninstalling removes the rest.
- Contact: the owner's support contact.

`docs/store/certification-notes.md`:

```markdown
# Notes for Certification

> Draft for owner review.

## runFullTrust
Leaf Calendar is a packaged WinUI 3 desktop app (Windows App SDK). runFullTrust is the standard capability for packaged desktop apps; Leaf uses it to run its tray icon (Shell_NotifyIcon), register global shortcuts (RegisterHotKey), start with Windows, and receive the Google sign-in redirect on a 127.0.0.1 loopback listener.

## How to Test
Leaf needs a Google account and a Google Cloud OAuth client of type "Desktop app" with the Google Calendar API and People API enabled. The first-run window explains how to create one.
(owner: either give testers a throwaway Google account plus its OAuth client ID and secret here, or say none is provided)
```

- [ ] **Step 7: About page (skip what Milestone 5 already added)**

Check `AboutPage.xaml` first. If Milestone 5 already has a third-party notices or licenses card, skip that card and say so in the report.

Add two setting-row cards (design standard Section 8, About page):
- **"Privacy policy"**: a `HyperlinkButton` with `AutomationProperties.AutomationId="PrivacyPolicyLink"`. Its handler launches a `static readonly Uri PrivacyPolicyUri` (the owner's URL, a fixed constant, `https`) through `LeafServices.LaunchAsync`, the same way `OnGitHubClick` does today. If the owner hasn't given the URL yet, keep the card out (don't ship a dead link). The report lists it, and Task 9 adds it.
- **"Third-party notices"**: a `Button` "View notices…" with `AutomationProperties.AutomationId="ThirdPartyNoticesButton"`. It opens a stock `ContentDialog` titled "Third-party notices", with `CloseButtonText="Close"`.
  - The dialog content is a `ScrollViewer` (`MaxHeight="480"`, `ScrollIndicator.ShowOnHover`) holding a selectable `TextBlock` (`IsTextSelectionEnabled="True"`, `TextWrapping="Wrap"`, `FontFamily="Consolas"`, `AutomationProperties.AutomationId="ThirdPartyNoticesText"`).
  - The text is read with `StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///ThirdPartyNotices.txt"))` and `FileIO.ReadTextAsync`.
  - Nothing launches. Wrap the read in try/catch (`FileNotFoundException`, `COMException`), log `about.notices.failed`, and show "Couldn't load the notices." in caption text instead.

UI test in `StoreTests`:

```csharp
    // Settings › About Shows The Packaged Notices In A Dialog (nothing launches)
    [Fact]
    public void About_ThirdPartyNotices_ShowsPackagedText()
    {
        using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.OpenSettingsPage("About");
        leaf.WaitInSettings("ThirdPartyNoticesButton").AsButton().Invoke();

        var text = leaf.WaitForAnywhere("ThirdPartyNoticesText");
        Assert.Contains("CommunityToolkit.Mvvm", text.Name, StringComparison.Ordinal);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }
```

`OpenSettingsPage` and `WaitInSettings` are whatever helpers `SettingsTests` already uses to reach a page (search `SettingsTests.cs` and use the same calls). If they're inline in that class, copy the two lines rather than adding a helper.

- [ ] **Step 8: Run the logic tests and build**

Run the logic tests (`--filter-class "*PackageManifestTests"` → PASS), then `dotnet build LeafCalendar.slnx -c Debug` (0 warnings). If the owner filled in `partner-center.json`, also run `pwsh tools/publish-aot.ps1 -Store`. It should produce a `.msixupload`, and afterwards `git status` should show `Package.appxmanifest` unchanged.

- [ ] **Step 9: Commit**

```bash
git add tools docs/store assets/brand src/LeafCalendar.App/Assets src/LeafCalendar.App/Package.appxmanifest src/LeafCalendar.App/LeafCalendar.App.csproj src/LeafCalendar.App/ThirdPartyNotices.txt src/LeafCalendar.App/Views/Settings/AboutPage.xaml src/LeafCalendar.App/Views/Settings/AboutPage.xaml.cs tests/LeafCalendar.Tests/PackageManifestTests.cs tests/LeafCalendar.UITests/StoreTests.cs
git commit -m "feat(store): logos at every scale, notices, privacy and listing drafts, Store build switch"
```

---

### Task B1: Description HTML Round Trip (Core)

**Found while planning:**
- `EventDraft.Description` is plain text today (`EventJson.ReadDraft` takes `EventDetails.Description`, `EventJson.cs:64`).
- `BuildCreate` and `BuildPatch` write it back through `TextToHtml` (`:137-139`, `:206-208`), and `EventEditor` compares it the same way (`EventEditor.cs:406`).
- `DescriptionFormatter.Format` already parses Google's HTML safely into `DescriptionRun`s for the details panel, but it flattens lists into `"• "` text and opens a list on the same line as the text before it (`"a<ul><li>x"` reads "a• x").

**Design:**
- One parser: `DescriptionFormatter` stays the only thing that reads description HTML. Its list-item marker runs now carry `ListKind`, `ul` and `ol` start a new line, and `ol` markers count ("1. ", "2. ").
- One writer: the new `DescriptionHtml.Write` turns lines back into HTML using only the allowlist (Global Constraints).
- `DescriptionHtml.Normalize(html) = Write(Lines(html))`. `EventDraft.Description` becomes **normalized HTML**. A patch carries `description` only when `Normalize(before) != Normalize(after)`, so an untouched description is never rewritten (Review Focus 2).

**Files:**
- Modify: `src/LeafCalendar.Core/Events/DescriptionFormatter.cs`, `src/LeafCalendar.Core/Editing/EventJson.cs`, `src/LeafCalendar.Core/Editing/EventEditor.cs:406`, `src/LeafCalendar.Core/Editing/EventDraft.cs` (doc comment)
- Create: `src/LeafCalendar.Core/Events/DescriptionHtml.cs`
- Modify: `src/LeafCalendar.App/ViewModels/EventEditorViewModel.cs:513` (compile fix only: pass `Description` through unchanged; Task B2 owns the rest)
- Test: `tests/LeafCalendar.Tests/DescriptionHtmlTests.cs` (new), `DescriptionFormatterTests.cs`, `EventJsonTests.cs`, `EventEditorTests.cs`

**Interfaces:**
- Produces (Task B2 uses these names verbatim):
  - `public enum ListKind { None, Bullet, Numbered }` (namespace `LeafCalendar.Core.Events`)
  - `public sealed record DescriptionRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, Uri? Link = null, ListKind List = ListKind.None)`. `List != None` marks a list marker run, whose text is "• " or "N. " and may start with line breaks.
  - `public sealed record DescriptionLine(IReadOnlyList<DescriptionRun> Runs, ListKind List)`
  - `public static IReadOnlyList<DescriptionLine> DescriptionHtml.Lines(string html)`
  - `public static string DescriptionHtml.Write(IReadOnlyList<DescriptionLine> lines)`
  - `public static string DescriptionHtml.Normalize(string html)`
  - `EventDraft.Description`: Leaf-normalized description HTML (empty when none)

- [ ] **Step 1: Failing tests**

`tests/LeafCalendar.Tests/DescriptionHtmlTests.cs`:

```csharp
using System.Text.RegularExpressions;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public partial class DescriptionHtmlTests
{
    // Only these tags may be written back to Google
    [GeneratedRegex(@"<(?!/?(b|i|u|br|ul|ol|li)>|a href=""(https://|mailto:)[^""<>]*"">|/a>)", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignTag();

    static DescriptionLine Line(ListKind list, params DescriptionRun[] runs) => new(runs, list);

    static string Shape(IReadOnlyList<DescriptionLine> lines) =>
        string.Join("|", lines.Select(l => $"{l.List}:{string.Concat(l.Runs.Select(r => (r.Bold ? "*" : "") + r.Text))}"));

    [Fact]
    public void Lines_ListsAndStyles_BecomeLines()
    {
        var lines = DescriptionHtml.Lines("Agenda:<ul><li><b>Budget</b></li><li>Hiring</li></ul><ol><li>One</li><li>Two</li></ol>Thanks");

        Assert.Equal("None:Agenda:|Bullet:*Budget|Bullet:Hiring|Numbered:One|Numbered:Two|None:Thanks", Shape(lines));
    }

    [Fact]
    public void Write_LinesAndStyles_UsesOnlyAllowlistedTags()
    {
        IReadOnlyList<DescriptionLine> lines =
        [
            Line(ListKind.None, new("Read "), new("this", Bold: true, Italic: true, Underline: true), new(" <now> & then")),
            Line(ListKind.None),
            Line(ListKind.Bullet, new("Docs", Link: new Uri("https://example.com/a?b=1&c=2"))),
            Line(ListKind.Numbered, new("Mail", Link: new Uri("mailto:sam@example.com"))),
        ];

        Assert.Equal(
            "Read <b><i><u>this</u></i></b> &lt;now&gt; &amp; then<br><br>"
            + "<ul><li><a href=\"https://example.com/a?b=1&amp;c=2\">Docs</a></li></ul>"
            + "<ol><li><a href=\"mailto:sam@example.com\">Mail</a></li></ol>",
            DescriptionHtml.Write(lines));
    }

    [Theory]
    [InlineData("<b>Agenda</b><br>Budget")]
    [InlineData("Agenda:<ul><li>a</li><li>b</li></ul>after")]
    [InlineData("<ol><li>x</li></ol><ul><li>y</li></ul>")]
    [InlineData("a<br><br><br><br>b")]
    [InlineData("<p>one</p><div>two</div>")]
    public void Normalize_IsIdempotentAndReadsTheSame(string html)
    {
        var once = DescriptionHtml.Normalize(html);

        Assert.Equal(once, DescriptionHtml.Normalize(once));
        Assert.Equal(Shape(DescriptionHtml.Lines(html)), Shape(DescriptionHtml.Lines(once)));
    }

    // Review Focus 1: Hostile Markup Never Survives A Round Trip
    [Theory]
    [InlineData("<script>alert(1)</script>Hi")]
    [InlineData("<img src=x onerror=alert(1)>Hi")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>Hi")]
    [InlineData("<a href=\"javascript:alert(1)\">Hi</a>")]
    [InlineData("<a href=\"data:text/html,<b>x</b>\">Hi</a>")]
    [InlineData("<a href=\"https://evil.example\" onclick=\"x()\">https://bank.example</a>")]
    [InlineData("<style>b{}</style><b onmouseover=x>Hi")]
    [InlineData("&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("<a href=\"https://ok.example/‮gnp.exe\">Hi</a>")]
    public void Normalize_HostileMarkup_OnlyAllowlistedTagsSurvive(string html)
    {
        var output = DescriptionHtml.Normalize(html);

        Assert.DoesNotMatch(ForeignTag(), output);
        Assert.DoesNotContain("javascript:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", output, StringComparison.OrdinalIgnoreCase);
    }

    // Review Focus 1: Random Tag Soup Stays Inside The Allowlist, And A Second Pass Changes Nothing
    [Fact]
    public void Normalize_RandomTagSoup_IsAllowlistedAndIdempotent()
    {
        string[] pieces = ["<b>", "</b>", "<i>", "</u>", "<ul>", "<li>", "</li>", "</ol>", "<ol>", "<br>", "<a href=\"https://x.example/\">", "</a>",
            "<a href='mailto:a@b.example?bcc=c@d.example'>", "<script>", "&amp;", "&lt;b&gt;", "text", " ", "\n", "‮", "￼", "<p>", "</div>", "<", ">", "\""];
        var random = new Random(6);

        for (var i = 0; i < 2_000; i++)
        {
            var html   = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => pieces[random.Next(pieces.Length)]));
            var output = DescriptionHtml.Normalize(html);

            Assert.DoesNotMatch(ForeignTag(), output);
            Assert.DoesNotContain("bcc", output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(output, DescriptionHtml.Normalize(output));
        }
    }

    // Review Focus 3: Pasted Objects And Control Characters Never Reach Google
    [Fact]
    public void Write_ObjectPlaceholdersAndControlCharacters_AreDropped()
    {
        var html = DescriptionHtml.Write([Line(ListKind.None, new("a￼b\u0007c\td"))]);

        Assert.Equal("abc\td", html);
    }

    [Fact]
    public void Normalize_HugeInput_IsCapped()
    {
        var output = DescriptionHtml.Normalize(string.Concat(Enumerable.Repeat("<b>word</b> ", 20_000)));

        Assert.True(output.Length < 30_000);
        Assert.EndsWith("…</b>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_BlankLineRightAfterAList_IsDropped()
    {
        // A list's closing tags read as a line break; Leaf writes lists that way, so the extra blank line isn't kept
        Assert.Equal("Bullet:x|None:y", Shape(DescriptionHtml.Lines("<ul><li>x</li></ul>y")));
    }
}
```

In `EventJsonTests`:
- Change `ReadDraft_FullEvent_ReadsEveryField` to assert `Assert.Equal("<b>Agenda</b><br>Budget", draft.Description);`.
- Change `BuildPatch_UntouchedDescriptionWithAnyLineEnding_IsNotSent` into `BuildPatch_RichDescriptionUntouched_IsNotSent`, a `[Theory]` over equivalent spellings of the same content, each of which must not produce a `description` key:

```csharp
    // Review Focus 2: An Untouched Description Is Never Rewritten
    [Theory]
    [InlineData("<b>Agenda</b><br>Budget")]
    [InlineData("<strong>Agenda</strong><br/>Budget")]
    [InlineData("<b>Agenda</b>\n<br>Budget")]
    public void BuildPatch_RichDescriptionUntouched_IsNotSent(string html)
    {
        var draft = Read();

        Assert.False(EventJson.BuildPatch(draft, draft with { Description = html }).ContainsKey("description"));
    }

    [Fact]
    public void BuildPatch_DescriptionChanged_SendsNormalizedHtml()
    {
        var draft = Read();
        var patch = EventJson.BuildPatch(draft, draft with { Description = "<b>Agenda</b><ul><li>Budget</li></ul><script>x</script>" });

        Assert.Equal("<b>Agenda</b><ul><li>Budget</li></ul>x", (string?)patch["description"]);
    }

    [Fact]
    public void ReadDraft_GoogleTable_KeepsTextAndDropsTheTable()
    {
        var draft = Read(Meeting.Replace("<b>Agenda</b><br>Budget", "<table><tr><td>Cell</td></tr></table>", StringComparison.Ordinal));

        Assert.Equal("Cell", draft.Description);
    }
```

- Replace `BuildPatch_Description_EncodesTextAsHtml` with the same idea in rich form: `Description = "Bring &lt;notes&gt;<br>and snacks"` sends exactly that string.

In `DescriptionFormatterTests`, add:

```csharp
    [Fact]
    public void Format_ListMarkers_CarryTheirKind()
    {
        var runs = DescriptionFormatter.Format("Agenda<ul><li>a</li></ul><ol><li>b</li><li>c</li></ol>");

        Assert.Equal("Agenda\n• a\n\n1. b\n2. c", Text(runs));
        Assert.Equal([ListKind.Bullet, ListKind.Numbered, ListKind.Numbered], runs.Where(r => r.List != ListKind.None).Select(r => r.List));
    }
```

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter-class "*Description*"` and `--filter-class "*EventJsonTests"`. Expected: compile errors, then FAIL.

- [ ] **Step 2: `DescriptionFormatter` list markers**

- Add `ListKind List = ListKind.None` as the last positional parameter of `DescriptionRun`, and put `ListKind` in the same file, above it.
- In `Format`, keep `var lists = new Stack<(ListKind Kind, int Count)>();`.
- `ul` or `ol` opening: push `(Bullet|Numbered, 0)` and add `new DescriptionRun("\n")`. Closing: pop (when the stack isn't empty) and add `"\n"` as today.
- `li` opening: if the stack is empty, treat it as `Bullet`. Otherwise increment the top count, and add `new DescriptionRun(kind == ListKind.Numbered ? $"{count}. " : "• ", List: kind)`. `li` closing adds `"\n"` as today.
- `Tidy` already keeps each run's other fields (`run with { Text = ... }`), so markers keep their `List`.

The details panel still renders a marker as plain text. Its look changes only in two ways: numbered lists show numbers, and a list no longer runs into the text before it. Every existing `DescriptionFormatterTests` case must still pass. If one pinned the old "a• x" joining, update its expected text and name the test in the report.

- [ ] **Step 3: `DescriptionHtml`**

`src/LeafCalendar.Core/Events/DescriptionHtml.cs`:

```csharp
using System.Net;
using System.Text;

namespace LeafCalendar.Core.Events;

/// <summary>One line of a description: its styled runs and the list it sits in.</summary>
public sealed record DescriptionLine(IReadOnlyList<DescriptionRun> Runs, ListKind List);

/// <summary>
/// Description HTML as lines for the editor, and back to HTML for Google (spec 4.5, 7.2). Reading goes through
/// <see cref="DescriptionFormatter"/>, the one parser for untrusted description HTML. Writing emits only <c>b</c>, <c>i</c>,
/// <c>u</c>, <c>br</c>, <c>ul</c>, <c>ol</c>, <c>li</c>, and <c>a href</c> for links <see cref="LinkSafety.IsClickableInDescription"/>
/// allows, with every bit of text HTML-encoded.
/// </summary>
/// <remarks>
/// Nested lists flatten to one level, and a blank line right after a list isn't kept (a list's closing tags already
/// read as a line break). <see cref="Normalize"/> is idempotent, so comparing normalized HTML tells whether a
/// description really changed.
/// </remarks>
public static class DescriptionHtml
{
    /// <summary>The description as lines (list markers removed; their kind is on the line).</summary>
    public static IReadOnlyList<DescriptionLine> Lines(string html)
    {
        var lines = new List<DescriptionLine>();
        var runs  = new List<DescriptionRun>();
        var list  = ListKind.None;

        foreach (var run in DescriptionFormatter.Format(html))
        {
            // Marker: its leading line breaks end lines, then the next line is a list item
            if (run.List != ListKind.None)
            {
                foreach (var _ in run.Text.Where(c => c == '\n'))
                {
                    EndLine();
                }

                list = run.List;
                continue;
            }

            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    EndLine();
                }

                if (parts[i].Length > 0)
                {
                    runs.Add(run with { Text = parts[i] });
                }
            }
        }

        if (runs.Count > 0 || list != ListKind.None)
        {
            EndLine();
        }

        return lines;

        // Closes the current line; an empty plain line right after a list item came from the list's closing tags
        void EndLine()
        {
            var afterList = lines.Count > 0 && lines[^1].List != ListKind.None;
            if (!(runs.Count == 0 && list == ListKind.None && afterList))
            {
                lines.Add(new DescriptionLine([.. runs], list));
            }

            runs.Clear();
            list = ListKind.None;
        }
    }

    /// <summary>Lines as Google description HTML, using only the allowlisted tags.</summary>
    public static string Write(IReadOnlyList<DescriptionLine> lines)
    {
        var html          = new StringBuilder();
        var open          = ListKind.None;
        var previousPlain = false;

        foreach (var line in lines)
        {
            // Close A List That Ends Or Changes Kind
            if (open != ListKind.None && line.List != open)
            {
                html.Append(open == ListKind.Bullet ? "</ul>" : "</ol>");
                open = ListKind.None;
            }

            // List Item
            if (line.List != ListKind.None)
            {
                if (open == ListKind.None)
                {
                    html.Append(line.List == ListKind.Bullet ? "<ul>" : "<ol>");
                    open = line.List;
                }

                html.Append("<li>");
                AppendRuns(html, line.Runs);
                html.Append("</li>");
                previousPlain = false;
                continue;
            }

            // Plain Line (a list's closing tag already breaks the line)
            if (previousPlain)
            {
                html.Append("<br>");
            }

            AppendRuns(html, line.Runs);
            previousPlain = true;
        }

        if (open != ListKind.None)
        {
            html.Append(open == ListKind.Bullet ? "</ul>" : "</ol>");
        }

        return html.ToString();
    }

    /// <summary>Google's HTML in Leaf's subset: equal results mean the same description.</summary>
    public static string Normalize(string html) => Write(Lines(html));

    static void AppendRuns(StringBuilder html, IReadOnlyList<DescriptionRun> runs)
    {
        foreach (var run in runs)
        {
            var text = Clean(run.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var link = run.Link is { } uri && LinkSafety.IsClickableInDescription(uri) ? uri : null;
            if (link is not null)
            {
                html.Append("<a href=\"").Append(WebUtility.HtmlEncode(link.AbsoluteUri)).Append("\">");
            }

            html.Append(run.Bold ? "<b>" : "").Append(run.Italic ? "<i>" : "").Append(run.Underline ? "<u>" : "");
            html.Append(WebUtility.HtmlEncode(text));
            html.Append(run.Underline ? "</u>" : "").Append(run.Italic ? "</i>" : "").Append(run.Bold ? "</b>" : "");

            if (link is not null)
            {
                html.Append("</a>");
            }
        }
    }

    // Object placeholders (pasted pictures) and control characters other than tab never reach Google
    static string Clean(string text) => new([.. text.Where(c => c != '￼' && (c == '\t' || !char.IsControl(c)))]);
}
```

If `Normalize_HugeInput_IsCapped` shows the formatter's "…" lands outside the `</b>`, adjust only the test's expected suffix to match what the code does. The cap itself is what matters.

- [ ] **Step 4: Drafts carry normalized HTML**

- `EventDraft.cs`: update the remark to say "`Description` is Leaf-normalized description HTML (`DescriptionHtml.Normalize`)", and update the property summary to match.
- `EventJson.ReadDraft`: set `Description = DescriptionHtml.Normalize(Str(root, "description") ?? "")`.
- `EventJson.BuildCreate`: if `DescriptionHtml.Normalize(draft.Description)` is not empty, set `body["description"]` to it. Normalizing again is the trust boundary, because a draft can come from anywhere.
- `EventJson.BuildPatch`: compare and send `Normalize(before.Description)` against `Normalize(after.Description)`. Replace the line-ending comment with `// Same description in Leaf's subset → nothing to send (Google's original HTML stays as it was)`.
- `EventEditor.cs:406`: `DescriptionHtml.Normalize(before.Description) == DescriptionHtml.Normalize(after.Description) ? target.Description : after.Description`.
- `EventJson.TextToHtml`: run `git grep -n "TextToHtml"`. If only tests or nothing call it now, delete it and its tests. If a Milestone 5 caller turns plain text into a description, change that caller to `DescriptionHtml.Write([new DescriptionLine([new DescriptionRun(text)], ListKind.None)])` split per line, and delete `TextToHtml`.
- `EventEditorViewModel.cs:513`: `Description = Description,`. The `\r` handling moves to the editor in B2.

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → all PASS, including `EventEditorTests`. Then `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core src/LeafCalendar.App/ViewModels/EventEditorViewModel.cs tests/LeafCalendar.Tests
git commit -m "feat(core): description HTML round trip with an allowlisted writer; drafts carry normalized HTML"
```

---

### Task B2: Rich Description Editor

**Files:**
- Create: `src/LeafCalendar.App/Controls/RichDescription.cs`
- Modify: `src/LeafCalendar.App/Views/EventEditorView.xaml:261-265`, `EventEditorView.xaml.cs`, `src/LeafCalendar.App/ViewModels/EventEditorViewModel.cs:265-267` (doc: "Description as Leaf-normalized HTML")
- Test: `tests/LeafCalendar.UITests/RichDescriptionTests.cs` (new)

**Interfaces:**
- Consumes: `DescriptionHtml.Lines` / `Write`, `DescriptionLine`, `DescriptionRun`, `ListKind` (B1).
- Produces:
  - `internal static class RichDescription` with `Load(RichEditBox, IReadOnlyList<DescriptionLine>)`, which returns `List<(string Text, Uri Link)>` anchors, and `Read(RichEditBox, IReadOnlyList<(string Text, Uri Link)>)`, which returns `IReadOnlyList<DescriptionLine>`
  - AutomationIds: `EditorDescription` (the `RichEditBox`, same id as today), `DescriptionBold`, `DescriptionItalic`, `DescriptionUnderline`, `DescriptionBullets`, `DescriptionNumbers`

**Design:**
- The editor never holds a live link. A link loads as its text, and its target goes into an anchor list. On read-back, a run whose text exactly equals an anchor's text gets that link back. Editing a link's text drops the link, and that's safe: nothing in the editor can launch (Global Constraints).
  `// ponytail: exact-text anchors; a link whose text the user edits becomes plain text. Track ranges if owners want link editing.`
- Description is read back only when the user changed it (`_descriptionTouched`, set by `TextChanged`, a toolbar click, or Ctrl+B/I/U in the box), and compared normalized. Both guards keep an untouched description from being rewritten (Review Focus 2).
- Paste is always plain text (Review Focus 3).

- [ ] **Step 1: Failing UI tests (built now, run in Task 9)**

```csharp
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class RichDescriptionTests : IDisposable
{
    const string Rich = "evt-rich";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public RichDescriptionTests() =>
        _google.AddEvent("primary", JsonNode.Parse("""
            {
              "id": "evt-rich", "status": "confirmed", "summary": "Planning",
              "description": "<table><tr><td>Kept as Google wrote it</td></tr></table><b>Bold</b> <a href=\"https://example.com/doc\">Doc</a>",
              "start": { "dateTime": "2026-10-01T10:00:00-04:00" }, "end": { "dateTime": "2026-10-01T11:00:00-04:00" }
            }
            """)!.AsObject());

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp OpenEditor()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-rich_202610011400").Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.WaitFor("EditorDescription");
        return leaf;
    }

    // Review Focus 2: Saving Another Field Leaves Google's Description Alone
    [Fact]
    public void SaveWithoutTouchingDescription_SendsNoDescription()
    {
        using var leaf = OpenEditor();
        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        title.Text = "Planning v2";
        leaf.WaitFor("EditorSave").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.Contains(Rich, StringComparison.Ordinal));
        Assert.Contains("Planning v2", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("description", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BoldButton_ThenType_SavesBoldText()
    {
        using var leaf = OpenEditor();
        var box = leaf.WaitFor("EditorDescription");
        box.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type(VirtualKeyShort.ENTER);
        leaf.WaitFor("DescriptionBold").Click();
        Keyboard.Type("Loud");
        leaf.WaitFor("EditorSave").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.Contains(Rich, StringComparison.Ordinal));
        Assert.Contains("<b>Loud</b>", write.Body, StringComparison.Ordinal);
        Assert.Contains("<a href=\\\"https://example.com/doc\\\">Doc</a>", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BulletsButton_MakesAList()
    {
        using var leaf = OpenEditor();
        leaf.WaitFor("EditorDescription").Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type(VirtualKeyShort.ENTER);
        leaf.WaitFor("DescriptionBullets").Click();
        Keyboard.Type("First");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Keyboard.Type("Second");
        leaf.WaitFor("EditorSave").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.Contains(Rich, StringComparison.Ordinal));
        Assert.Contains("<ul><li>First</li><li>Second</li></ul>", write.Body, StringComparison.Ordinal);
    }

    // Review Focus 3: Rich Clipboard Content Pastes As Plain Text
    [Fact]
    public void PasteRichText_InsertsPlainText()
    {
        using var leaf = OpenEditor();
        Clipboard.SetHtml("<b>Pasted</b><img src=\"https://tracker.example/p.gif\"><script>x</script>", plainText: "Pasted");
        leaf.WaitFor("EditorDescription").Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        leaf.WaitFor("EditorSave").AsButton().Invoke();

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.Contains(Rich, StringComparison.Ordinal));
        Assert.Contains("Pasted", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Pasted", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("img", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\uFFFC", write.Body, StringComparison.OrdinalIgnoreCase);
    }

    // Ctrl+Enter Saves From The Description, It Doesn't Add A Line
    [Fact]
    public void CtrlEnterInDescription_Saves()
    {
        using var leaf = OpenEditor();
        leaf.WaitFor("EditorDescription").Click();
        Keyboard.Type("x");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ENTER);

        Assert.NotNull(_google.WaitForWrite(w => w.Method == "PATCH" && w.Path.Contains(Rich, StringComparison.Ordinal)));
    }

    // Hostile Description: Shown As Text, Nothing Launches
    [Fact]
    public void HostileDescription_ShowsInertTextAndLaunchesNothing()
    {
        _google.EditOnGoogle("primary", Rich, e => e["description"] = "<script>alert(1)</script><a href=\"javascript:x\">Click</a>");
        using var leaf = OpenEditor();
        var box = leaf.WaitFor("EditorDescription");
        box.Click();

        Assert.Contains("Click", box.Patterns.Text.Pattern.DocumentRange.GetText(-1), StringComparison.Ordinal);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }
}
```

- The event id format `Event_<id>_<yyyyMMddHHmm UTC>` matches the existing tests. `EditorSave` is the Save button's existing AutomationId, so check `EventEditorView.xaml` and use whatever id is there.
- `Support/Clipboard.cs` already exists. If it has no `SetHtml`, add one: a CF_HTML clipboard write plus CF_UNICODETEXT through the same Win32 calls the file already uses. This is the only change to that file.
- The two `WaitForWrite` patterns match how `ConferencingAndContactsTests` reads PATCH bodies. Copy its exact predicate shape.

Build: `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`. Expected: it compiles, and the tests aren't run yet.

- [ ] **Step 2: `RichDescription`**

`src/LeafCalendar.App/Controls/RichDescription.cs`:

```csharp
using LeafCalendar.Core.Events;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Moves a description between <see cref="DescriptionLine"/>s and a <see cref="RichEditBox"/>: one paragraph per line,
/// bold, italic, underline, and bullet or numbered paragraphs. Links never become live in the box; their targets are
/// kept as anchors and put back on read when the link text is unchanged.
/// </summary>
internal static class RichDescription
{
    /// <summary>Fills the box and clears its undo history. Returns the link anchors to pass to <see cref="Read"/>.</summary>
    public static List<(string Text, Uri Link)> Load(RichEditBox box, IReadOnlyList<DescriptionLine> lines)
    {
        var document = box.Document;
        var anchors  = new List<(string Text, Uri Link)>();
        document.SetText(TextSetOptions.None, string.Join("\r", lines.Select(l => string.Concat(l.Runs.Select(r => r.Text)))));

        var position = 0;
        foreach (var line in lines)
        {
            var lineStart = position;
            foreach (var run in line.Runs)
            {
                // Character Format For This Run
                var format       = document.GetRange(position, position + run.Text.Length).CharacterFormat;
                format.Bold      = run.Bold ? FormatEffect.On : FormatEffect.Off;
                format.Italic    = run.Italic ? FormatEffect.On : FormatEffect.Off;
                format.Underline = run.Underline ? UnderlineType.Single : UnderlineType.None;

                if (run.Link is { } link)
                {
                    anchors.Add((run.Text, link));
                }

                position += run.Text.Length;
            }

            // List Paragraph
            if (line.List != ListKind.None)
            {
                var paragraph       = document.GetRange(lineStart, position).ParagraphFormat;
                paragraph.ListType  = line.List == ListKind.Bullet ? MarkerType.Bullet : MarkerType.Arabic;
                paragraph.ListStyle = MarkerStyle.Period;
            }

            position += 1;
        }

        document.ClearUndoRedoHistory();
        return anchors;
    }

    /// <summary>The box's content as lines, walking one character-format run at a time.</summary>
    public static IReadOnlyList<DescriptionLine> Read(RichEditBox box, IReadOnlyList<(string Text, Uri Link)> anchors)
    {
        var document = box.Document;
        document.GetText(TextGetOptions.None, out var all);

        // RichEdit always ends with a paragraph mark
        all = all.TrimEnd('\r');
        var lines = new List<DescriptionLine>();
        var start = 0;

        foreach (var paragraph in all.Split('\r'))
        {
            var end  = start + paragraph.Length;
            var runs = new List<DescriptionRun>();
            var at   = start;

            while (at < end)
            {
                var range = document.GetRange(at, at);
                range.Expand(TextRangeUnit.CharacterFormat);
                var runEnd = Math.Min(range.EndPosition, end);
                if (runEnd <= at)
                {
                    runEnd = end;
                }

                var text   = all[at..runEnd];
                var format = range.CharacterFormat;
                var link   = anchors.FirstOrDefault(a => a.Text == text).Link;
                runs.Add(new DescriptionRun(text, format.Bold == FormatEffect.On, format.Italic == FormatEffect.On, format.Underline != UnderlineType.None, link));
                at = runEnd;
            }

            var listType = document.GetRange(start, start).ParagraphFormat.ListType;
            var list     = listType switch
            {
                MarkerType.None or MarkerType.Undefined => ListKind.None,
                MarkerType.Bullet                        => ListKind.Bullet,
                _                                        => ListKind.Numbered,
            };

            lines.Add(new DescriptionLine(runs, list));
            start = end + 1;
        }

        return lines;
    }
}
```

`range.CharacterFormat` is read after `Expand`, so it describes that whole run.

- [ ] **Step 3: The editor view**

Replace `EventEditorView.xaml:263-265` with:

```xml
                <!-- Description -->
                <StackPanel Spacing="4">
                    <TextBlock Text="Description" />

                    <!-- Description Toolbar -->
                    <StackPanel Orientation="Horizontal" Spacing="4">
                        <ToggleButton
                            x:Name="BoldButton"
                            Style="{StaticResource LeafIconToggleButtonStyle}"
                            Click="OnBoldClick"
                            ToolTipService.ToolTip="Bold (Ctrl+B)"
                            AutomationProperties.Name="Bold"
                            AutomationProperties.AutomationId="DescriptionBold">
                            <FontIcon Glyph="&#xE8DD;" FontSize="16" />
                        </ToggleButton>
                        <!-- Italic (E8DB, Ctrl+I), Underline (E8DC, Ctrl+U): same shape, ids DescriptionItalic / DescriptionUnderline -->
                        <!-- Bulleted list (E8FD, "Bulleted list"), Numbered list ("Numbered list"): ids DescriptionBullets / DescriptionNumbers -->
                    </StackPanel>
                    <!-- /Description Toolbar -->

                    <RichEditBox
                        x:Name="DescriptionBox"
                        MinHeight="80"
                        TextWrapping="Wrap"
                        DisabledFormattingAccelerators="None"
                        ClipboardCopyFormat="PlainText"
                        AllowDrop="False"
                        AutomationProperties.Name="Description"
                        AutomationProperties.AutomationId="EditorDescription"
                        Paste="OnDescriptionPaste"
                        TextChanged="OnDescriptionTextChanged"
                        SelectionChanged="OnDescriptionSelectionChanged" />
                </StackPanel>
                <!-- /Description -->
```

- Write out all five toggle buttons in full. The comments above are only to keep this plan short, and every button has its own `Click` handler.
- Numbered list glyph: look it up in Segoe Fluent Icons. If it has none, draw a 16 DIP `PathIcon` per design standard Section 6, with a comment.
- `LeafIconToggleButtonStyle`: if `LeafTheme.xaml` has no toggle variant of `LeafIconButtonStyle`, use the stock `ToggleButton` style at `Width="32" Height="32" Padding="0"`. Track C owns `LeafTheme.xaml`.

In `EventEditorView.xaml.cs`, under a `// DESCRIPTION` section banner:

```csharp
    List<(string Text, Uri Link)> _anchors = [];
    bool _descriptionTouched;
    bool _loadingDescription;

    // Loads the draft's description into the box (call where the view binds a new Editor)
    void LoadDescription()
    {
        _loadingDescription = true;
        _anchors            = RichDescription.Load(DescriptionBox, DescriptionHtml.Lines(Editor?.Description ?? ""));
        _descriptionTouched = false;
        _loadingDescription = false;
        SyncToolbar();
    }

    // Before saving: only a description the user changed is read back
    void CommitDescription()
    {
        if (!_descriptionTouched || Editor is null)
        {
            return;
        }

        Editor.Description = DescriptionHtml.Write(RichDescription.Read(DescriptionBox, _anchors));
    }

    void OnDescriptionTextChanged(object sender, RoutedEventArgs e) => _descriptionTouched |= !_loadingDescription;

    void OnBoldClick(object sender, RoutedEventArgs e) => Format(f => f.Bold = FormatEffect.Toggle);

    void OnUnderlineClick(object sender, RoutedEventArgs e) =>
        Format(f => f.Underline = f.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None);

    void OnBulletsClick(object sender, RoutedEventArgs e) => List(MarkerType.Bullet);

    // Applies a character format to the selection, marks the description changed, and returns focus to the box
    void Format(Action<ITextCharacterFormat> change)
    {
        change(DescriptionBox.Document.Selection.CharacterFormat);
        _descriptionTouched = true;
        DescriptionBox.Focus(FocusState.Programmatic);
        SyncToolbar();
    }

    // Toggles the selected paragraphs in or out of a list of this kind
    void List(MarkerType kind)
    {
        var paragraph       = DescriptionBox.Document.Selection.ParagraphFormat;
        paragraph.ListType  = paragraph.ListType == kind ? MarkerType.None : kind;
        paragraph.ListStyle = MarkerStyle.Period;
        _descriptionTouched = true;
        DescriptionBox.Focus(FocusState.Programmatic);
        SyncToolbar();
    }

    void OnDescriptionSelectionChanged(object sender, RoutedEventArgs e) => SyncToolbar();

    // Toolbar toggles show the selection's format
    void SyncToolbar()
    {
        var format              = DescriptionBox.Document.Selection.CharacterFormat;
        var list                = DescriptionBox.Document.Selection.ParagraphFormat.ListType;
        BoldButton.IsChecked    = format.Bold == FormatEffect.On;
        ItalicButton.IsChecked  = format.Italic == FormatEffect.On;
        UnderlineButton.IsChecked = format.Underline != UnderlineType.None;
        BulletsButton.IsChecked = list == MarkerType.Bullet;
        NumbersButton.IsChecked = list is not (MarkerType.None or MarkerType.Undefined or MarkerType.Bullet);
    }

    // Paste is always plain text: no pictures, objects, or foreign formatting (Review Focus 3)
    async void OnDescriptionPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = (await content.GetTextAsync()).Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
            DescriptionBox.Document.Selection.SetText(TextSetOptions.None, text);
            DescriptionBox.Document.Selection.Collapse(false);
            _descriptionTouched = true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            _owner?.Log.Error("editor.paste.failed", ex);
        }
    }
```

- Fill in `OnItalicClick` and `OnNumbersClick` the same way (`f.Italic = FormatEffect.Toggle`, and `List(MarkerType.Arabic)`). Align the `IsChecked` assignments.
- Call `LoadDescription()` where the view binds a new `Editor`. Search for the method that runs when `Editor` changes, where the swatches and guest chips are rebuilt.
- Call `CommitDescription()` first thing in the view's `Save(bool sendUpdates)` and in the Save button handlers, before the view model builds the draft.
- Ctrl+B, Ctrl+I, and Ctrl+U change format without `TextChanged`. In `OnPreviewKeyDown`, set `_descriptionTouched = true` when focus is in `DescriptionBox` and one of those chords is pressed. The existing Ctrl+Enter branch stays first, so Ctrl+Enter saves.
- `_owner?.Log` is a stand-in: use whatever logging reference the view already uses for errors (search `Log.Error` in the file). The log carries only the event name, never clipboard text.
- Remove the `Description` `x:Bind` (the box isn't bound). In `EventEditorViewModel`, change the property doc to `/// <summary>Description as Leaf-normalized HTML (the view reads and writes it through <c>RichDescription</c>).</summary>`.

- [ ] **Step 4: Build**

Run `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), then the logic tests (all PASS). Optionally, quick-run `RichDescriptionTests` on Debug to catch obvious breaks. The batch run is Task 9.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.App/Controls/RichDescription.cs src/LeafCalendar.App/Views/EventEditorView.xaml src/LeafCalendar.App/Views/EventEditorView.xaml.cs src/LeafCalendar.App/ViewModels/EventEditorViewModel.cs tests/LeafCalendar.UITests
git commit -m "feat(app): rich description editing with bold, italic, underline, lists, and plain paste"
```

---

### Task B3: Shift+Drag Box Select

Spec 7.3: "Multi-select with `Ctrl`+click or `Shift`+drag box". Spec 8.7 Selection: "`Shift`+drag | Box select".

**Found while planning:**
- Pressing empty time calls `TimeGridView.BeginCreateDrag` (from `DayColumn.cs:53-60`), and `OnDragMoved` / `OnDragReleased` run the session (`TimeGridView.cs:712-907`).
- The month view has no empty-space drag today. Its chips start `BeginChipDrag` (`MonthGridView.cs:432`).
- Selection lives in `CalendarViewModel` (`_selection`, `ToggleSelect`, `SelectAllVisible`, `PublishSelection` at `:1451`).

**Design:**
- Time grid: Shift held when empty time is pressed starts a `Box` drag instead of `Create`. On release, `BoxSelection.InTimeBox` picks the timed events on the covered days that overlap the covered time band. A press on a card is unchanged (Shift+click toggles).
- Month view: Shift+press on an empty day cell starts a box over day cells. On release, every event on the covered days is selected.
- Ctrl held too adds to the selection, and otherwise the box replaces it. Esc cancels a box through the existing `CancelDrag`.
- The box shows while dragging, and the selection is applied on release.
  `// ponytail: selection applies on release, not live; live highlighting re-renders every move.`
- Overlapping events that share a time band are both picked, even if the box covers only one's sub-column. `// ponytail: time-band hit test, not card rectangles.`

**Files:**
- Create: `src/LeafCalendar.Core/Views/BoxSelection.cs`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (selection section), `src/LeafCalendar.App/Controls/TimeGridView.cs` (`DragKind`, `BeginCreateDrag`, `OnDragMoved`, `OnDragReleased`, `CancelDrag`), `src/LeafCalendar.App/Controls/MonthGridView.cs`
- Test: `tests/LeafCalendar.Tests/BoxSelectionTests.cs` (new), `tests/LeafCalendar.UITests/BoxSelectTests.cs` (new)

**Interfaces:**
- Consumes: `DragMath.Instant(DateOnly day, double minutes, TimeZoneInfo zone)`, `CalendarOccurrence`, `EventWindowCache.ForDay`.
- Produces:
  - `public static IReadOnlyList<CalendarOccurrence> BoxSelection.InTimeBox(IEnumerable<CalendarOccurrence> occurrences, DateOnly dayA, DateOnly dayB, double minutesA, double minutesB, TimeZoneInfo zone)`
  - `public void CalendarViewModel.SelectBox(IReadOnlyList<CalendarOccurrence> hits, bool add)`
  - `public IReadOnlyList<CalendarOccurrence> CalendarViewModel.OnDays(IEnumerable<DateOnly> days)`
  - AutomationId `SelectionBox` on the box visual (time grid and month)

- [ ] **Step 1: Failing Core tests**

```csharp
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class BoxSelectionTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateOnly Mon = new(2026, 10, 5);

    static CalendarOccurrence At(string id, DateOnly day, int startHour, int endHour, bool allDay = false) =>
        TestOccurrences.Make(id, DragMath.Instant(day, startHour * 60, NewYork), DragMath.Instant(day, endHour * 60, NewYork), allDay);

    [Fact]
    public void InTimeBox_PicksOverlappingTimedEventsOnCoveredDays()
    {
        CalendarOccurrence[] all =
        [
            At("in-mon", Mon, 13, 14),
            At("in-wed", Mon.AddDays(2), 14, 15),
            At("too-late", Mon.AddDays(1), 17, 18),
            At("other-day", Mon.AddDays(4), 13, 14),
            At("all-day", Mon, 0, 24, allDay: true),
        ];

        var hits = BoxSelection.InTimeBox(all, Mon, Mon.AddDays(2), 12 * 60, 16 * 60, NewYork);

        Assert.Equal(["in-mon", "in-wed"], hits.Select(o => o.EventId));
    }

    [Fact]
    public void InTimeBox_DraggedBackward_SameAsForward()
    {
        CalendarOccurrence[] all = [At("a", Mon, 13, 14), At("b", Mon.AddDays(1), 15, 16)];

        Assert.Equal(
            BoxSelection.InTimeBox(all, Mon, Mon.AddDays(1), 12 * 60, 16 * 60, NewYork).Select(o => o.EventId),
            BoxSelection.InTimeBox(all, Mon.AddDays(1), Mon, 16 * 60, 12 * 60, NewYork).Select(o => o.EventId));
    }

    [Fact]
    public void InTimeBox_ZeroMinuteEventInsideBand_IsPicked()
    {
        var reminder = TestOccurrences.Make("zero", DragMath.Instant(Mon, 13 * 60, NewYork), DragMath.Instant(Mon, 13 * 60, NewYork), false);

        Assert.Single(BoxSelection.InTimeBox([reminder], Mon, Mon, 12 * 60, 14 * 60, NewYork));
    }

    // Fall-back Sunday (Nov 1 2026): 1:00–2:00 happens twice; the band is wall-clock time in the view's zone
    [Fact]
    public void InTimeBox_FallBackDay_UsesWallClockBand()
    {
        var sunday = new DateOnly(2026, 11, 1);
        CalendarOccurrence[] all = [At("three-am", sunday, 3, 4), At("noon", sunday, 12, 13)];

        Assert.Equal(["three-am"], BoxSelection.InTimeBox(all, sunday, sunday, 2.5 * 60, 4 * 60, NewYork).Select(o => o.EventId));
    }

    [Fact]
    public void InTimeBox_EventCrossingMidnight_PickedFromEitherDay()
    {
        var late = TestOccurrences.Make("late", DragMath.Instant(Mon, 23 * 60, NewYork), DragMath.Instant(Mon.AddDays(1), 60, NewYork), false);

        Assert.Single(BoxSelection.InTimeBox([late], Mon.AddDays(1), Mon.AddDays(1), 0, 30, NewYork));
    }
}
```

`TestOccurrences.Make(id, start, end, isAllDay)`: if `tests/LeafCalendar.Tests` already has an occurrence builder (search `new CalendarOccurrence(` in `DayLayoutTests` or `OccurrenceOrderTests`), use it. If not, add `tests/LeafCalendar.Tests/Support/TestOccurrences.cs` with one static method filling the remaining positional fields with neutral values (`"acct"`, `"cal"`, `null`, `null`, `Title: id`, `EventKind` default, `ResponseStatus` default, `"#039BE5"`, and so on per the record's full parameter list).

Run: `--filter-class "*BoxSelectionTests"` → compile FAIL.

- [ ] **Step 2: `BoxSelection`**

```csharp
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>Which events a Shift+drag box covers (spec 7.3).</summary>
public static class BoxSelection
{
    /// <summary>
    /// Timed events on the days between <paramref name="dayA"/> and <paramref name="dayB"/> that overlap the wall-clock
    /// band between <paramref name="minutesA"/> and <paramref name="minutesB"/> after midnight in <paramref name="zone"/>.
    /// Either corner may come first. A zero-minute event counts when it sits inside the band.
    /// </summary>
    public static IReadOnlyList<CalendarOccurrence> InTimeBox(IEnumerable<CalendarOccurrence> occurrences, DateOnly dayA, DateOnly dayB, double minutesA, double minutesB, TimeZoneInfo zone)
    {
        var (firstDay, lastDay) = dayA <= dayB ? (dayA, dayB) : (dayB, dayA);
        var (from, to)          = (Math.Min(minutesA, minutesB), Math.Max(minutesA, minutesB));
        var bands               = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            bands.Add((DragMath.Instant(day, from, zone), DragMath.Instant(day, to, zone)));
        }

        return [.. occurrences
            .Where(o => !o.IsAllDay)
            .Where(o => bands.Exists(b => o.Start == o.End ? o.Start >= b.Start && o.Start <= b.End : o.Start < b.End && o.End > b.Start))
            .DistinctBy(o => o.Key)];
    }
}
```

Run: `--filter-class "*BoxSelectionTests"` → PASS. If `InTimeBox_FallBackDay_UsesWallClockBand` fails because `DragMath.Instant` places 2:30 in the first or second 1–2 AM, read the comment on `DragMath.ToInstant` and set the test band to a value outside the repeated hour. The rule under test is wall-clock bands.

- [ ] **Step 3: View model**

In `CalendarViewModel`'s SELECTION section:

```csharp
    /// <summary>
    /// Shift+drag box: selects exactly <paramref name="hits"/>, or adds them when <paramref name="add"/> (Ctrl held too).
    /// Ends any edit, like Ctrl+click does.
    /// </summary>
    public void SelectBox(IReadOnlyList<CalendarOccurrence> hits, bool add)
    {
        CancelEdit();

        if (!add)
        {
            _selection.Clear();
        }

        foreach (var hit in hits.Where(h => !_selection.Exists(s => s.Key == h.Key)))
        {
            _selection.Add(hit);
        }

        PublishSelection();
    }

    /// <summary>Every event on these days (the month view's box, and the time grid's candidates).</summary>
    public IReadOnlyList<CalendarOccurrence> OnDays(IEnumerable<DateOnly> days) => [.. days.SelectMany(Cache.ForDay).DistinctBy(o => o.Key)];
```

An empty box with `add == false` clears the selection. That's intended: it matches clicking empty space.

- [ ] **Step 4: Time grid box**

In `TimeGridView.cs`:
- Add `Box` to `enum DragKind`.
- In `BeginCreateDrag`, start with this guard:

```csharp
        // Shift: Box Select Instead Of Create (spec 7.3)
        if (KeyState.IsDown(Windows.System.VirtualKey.Shift))
        {
            var (boxDay, boxMinutes) = BodyPosition(e);
            _drag = new DragSession(DragKind.Box, e.GetCurrentPoint(this).Position) { BoxDay = boxDay, BoxMinutes = boxMinutes };
            CapturePointer(e.Pointer);
            return;
        }
```

- `DragSession` gains `public DateOnly BoxDay { get; init; }` and `public double BoxMinutes { get; init; }`.
- `OnDragMoved`: when `drag.Kind == DragKind.Box`, position `_box` from the session origin to the current point (both in `this` coordinates). Then return before the move and resize logic.
  - `_box` is a `Border`, created once in the constructor and added to the overlay layer that holds the drag ghosts. It has `BorderThickness = new Thickness(1)`, `CornerRadius = new CornerRadius(4)`, `IsHitTestVisible = false`, `Visibility = Collapsed`, and `AutomationProperties.AutomationId = "SelectionBox"`.
  - Set `BorderBrush = LeafBrushes.Accent(dark)`, `Background = LeafBrushes.Accent(dark)`, and `Opacity = 0.25` each time it shows, so theme and high-contrast changes (Task C2) apply.
- `OnDragReleased`: when `drag.Kind == DragKind.Box`, hide `_box`, take `var (day, minutes) = BodyPosition(e);`, and call:

```csharp
            var days = Enumerable.Range(0, Math.Abs(day.DayNumber - drag.BoxDay.DayNumber) + 1).Select(i => (day < drag.BoxDay ? day : drag.BoxDay).AddDays(i));
            _vm.SelectBox(BoxSelection.InTimeBox(_vm.OnDays(days), drag.BoxDay, day, drag.BoxMinutes, minutes, _vm.Zone), add: KeyState.IsDown(Windows.System.VirtualKey.Control));
```

  Then return before the create, move, and resize logic. Hidden weekends: `OnDays` reads the cache per date, and a hidden weekend's events aren't in the visible strip. Filter `days` through the strip the view shows (`_strip`) so a box spanning a hidden weekend doesn't select its events.
- `CancelDrag`: also collapse `_box`.

- [ ] **Step 5: Month box**

In `MonthGridView.cs`:
- Add a `PointerPressed` handler with `handledEventsToo: false`. When Shift is down, the left button is pressed, and the press is not on a chip, it starts a month box with the start cell `(DayAt(point))` and captures the pointer.
  - "Not on a chip": a chip's own `PointerPressed` marks the event handled (check `WeekRow.cs:237` and keep that), so an unhandled press is empty space.
  - Reuse the view's existing point-to-date helper (search `DayAt` or the helper `BeginChipDrag`'s move uses). Don't write a second one.
- Move draws the same `_box` style over the covered rectangle.
- Release builds the covered days: for each shown week between the two cells' weeks, the shown dates whose column index is between the two cells' columns. Use the same date lists the rows render, so hidden weekends are honored. Then call `_vm.SelectBox(_vm.OnDays(days), add: Ctrl)`.
- Esc goes through the same cancel path the chip drag uses.

- [ ] **Step 6: UI tests (built now, run in Task 9)**

`tests/LeafCalendar.UITests/BoxSelectTests.cs`, using the `SelectionTests` setup (same fake Google, profile, `--start-date 2026-10-01`):

```csharp
    // Week Of Oct 5: A Box From Above Monday's 1:30 PM Card To Below Friday's Selects Both
    [Fact]
    public void ShiftDrag_OverTwoEvents_SelectsBoth()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist);
        leaf.WaitFor("NextButton").AsButton().Invoke();
        var monday = leaf.WaitFor("Event_evt-weekly_202610051330");
        var friday = leaf.WaitFor("Event_evt-weekly_202610091330");
        LeafApp.WaitUntilStill(monday);

        var from = new Point(monday.BoundingRectangle.Left + 4, monday.BoundingRectangle.Top - 12);
        var to   = new Point(friday.BoundingRectangle.Right - 4, friday.BoundingRectangle.Bottom + 12);
        Keyboard.Press(VirtualKeyShort.SHIFT);
        Mouse.Drag(from, to);
        Keyboard.Release(VirtualKeyShort.SHIFT);

        Assert.Matches(@"^\d+ events selected$", leaf.WaitFor("SelectionSummary").Name);
        Assert.Empty(_google.Writes);
    }

    [Fact]
    public void PlainDrag_SamePath_StillCreatesAnEvent()
    // Same points with no Shift: the editor opens for a new event (EventEditor visible, EditorHeader "New event")

    [Fact]
    public void ShiftCtrlDrag_AddsToSelection()
    // Click the Dentist card, then Shift+Ctrl drag over Monday's card on the next week page: SelectionSummary "2 events selected"

    [Fact]
    public void ShiftDrag_EscBeforeRelease_SelectsNothing()
    // Mouse.Down at from (Shift held), move to `to`, press Esc, Mouse.Up: no SelectionSummary, SelectionBox gone

    [Fact]
    public void MonthView_ShiftDragAcrossDays_SelectsTheirEvents()
    // Press M, Shift+drag from the Oct 1 cell's empty area to the Oct 2 cell: SelectionSummary shows ≥ 2 events selected
```

Write each commented test out in full, in the first test's shape. The exact steps are given in each comment. Points for empty space come from card bounding rectangles (12 px above or below a card) or a day cell's lower-right corner (cell rectangle minus 6 px).

Run `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` and confirm 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.Core/Views/BoxSelection.cs src/LeafCalendar.App/ViewModels/CalendarViewModel.cs src/LeafCalendar.App/Controls/TimeGridView.cs src/LeafCalendar.App/Controls/MonthGridView.cs tests
git commit -m "feat: Shift+drag box select in the time grid and month view"
```

---

### Task C1: Automated Visual and Accessibility Checks (Logic Tests, CI)

These checks run with the logic tests, so CI's existing `dotnet test` step enforces them on every push without any CI change. Every rule cites `docs/design-standard.md`.

**Files:**
- Create: `tests/LeafCalendar.Tests/XamlLintTests.cs`, `tests/LeafCalendar.Tests/ChromeColorsTests.cs`, `src/LeafCalendar.Core/Views/ChromeColors.cs`
- Modify: `src/LeafCalendar.Core/Views/EventColors.cs` (add `PastOpacity`), `src/LeafCalendar.App/Controls/LeafBrushes.cs` (read hex from `ChromeColors`), `EventBlock.cs:176`, `WeekRow.cs:249` (use `EventColors.PastOpacity`), `tests/LeafCalendar.Tests/EventColorsTests.cs`
- Modify: Track C-owned XAML files, to fix what the lint finds

**Interfaces:**
- Produces:
  - `public static class ChromeColors` with `DarkSurface`, `LightSurface`, and `PrimaryText(bool dark)`, `SecondaryText`, `DimText`, `GridLine`, `HalfHourLine`, `WeekendFill`, `Hover`, `GhostFill`, `CautionBackground`, `OnAccent`. These are the same names `LeafBrushes` has today, as hex strings.
  - `public const double EventColors.PastOpacity`
  - `XamlLintTests.Allowed`, a list A and B extend when they need an exception

- [ ] **Step 1: XAML lint (failing first)**

`tests/LeafCalendar.Tests/XamlLintTests.cs`:

```csharp
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LeafCalendar.Tests;

/// <summary>
/// Reads every XAML file in the app and checks the design standard's rules a machine can check: names and tooltips on
/// icon-only controls (Section 14), x:Bind only (Section 1), HighContrast theme entries (Section 5), the spacing and
/// radius scale (Section 3), live regions on InfoBars, and no fixed heights on text (Section 14, text scaling).
/// </summary>
public partial class XamlLintTests
{
    static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    static readonly XNamespace X  = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Known exceptions: "file|rule|detail", each with the reason in a comment. Keep it short.</summary>
    public static readonly HashSet<string> Allowed =
    [
        // Example: "Views/SidebarView.xaml|spacing|1,8,0,0", // 1 DIP optical nudge so the settings glyph lines up (SidebarView header comment)
    ];

    static readonly string[] ButtonTypes  = ["Button", "ToggleButton", "HyperlinkButton", "RepeatButton", "AppBarButton", "AppBarToggleButton", "DropDownButton", "SplitButton"];
    static readonly string[] IconTypes    = ["FontIcon", "SymbolIcon", "PathIcon", "BitmapIcon", "ImageIcon", "Image", "Viewbox"];
    static readonly HashSet<double> Scale = [0, 1, 2, 4, 8, 12, 16, 24, 32, 36, 48];
    static readonly HashSet<double> Radii = [0, 2, 4, 8];

    static string AppFolder => Path.Combine(PackageManifestTests.RepoRoot(), "src", "LeafCalendar.App");

    public static TheoryData<string> Files() =>
    [
        .. Directory.GetFiles(AppFolder, "*.xaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(AppFolder, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal) && !f.StartsWith("AppPackages/", StringComparison.Ordinal)),
    ];

    static XDocument Load(string file) => XDocument.Load(Path.Combine(AppFolder, file), LoadOptions.SetLineInfo);

    static string Where(string file, XElement e) => $"{file}:{((System.Xml.IXmlLineInfo)e).LineNumber} <{e.Name.LocalName}>";

    static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    static List<string> Failures(string file, string rule, IEnumerable<(XElement Element, string Detail)> hits) =>
        [.. hits.Where(h => !Allowed.Contains($"{file}|{rule}|{h.Detail}")).Select(h => $"{Where(file, h.Element)} {rule}: {h.Detail}")];

    // Section 7 And 14: Icon-Only Buttons Have A Name And A Tooltip
    [Theory, MemberData(nameof(Files))]
    public void IconOnlyButtons_HaveNameAndTooltip(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => ButtonTypes.Contains(e.Name.LocalName) && Attr(e, "Content") is null)
            .Where(e => e.Elements().FirstOrDefault(c => !c.Name.LocalName.Contains('.', StringComparison.Ordinal)) is { } child && IconTypes.Contains(child.Name.LocalName))
            .Where(e => Attr(e, "AutomationProperties.Name") is null || Attr(e, "ToolTipService.ToolTip") is null)
            .Select(e => (e, Attr(e, "AutomationProperties.AutomationId") ?? "(no id)"));

        Assert.Empty(Failures(file, "icon-button", hits));
    }

    // Section 1: x:Bind Only (AOT)
    [Theory, MemberData(nameof(Files))]
    public void Bindings_AreXBindOnly(string file)
    {
        var hits = Load(file).Descendants()
            .SelectMany(e => e.Attributes().Select(a => (e, a)))
            .Where(p => p.a.Value.Contains("{Binding", StringComparison.Ordinal) || p.a.Name.LocalName == "DisplayMemberPath")
            .Select(p => (p.e, p.a.Name.LocalName));

        Assert.Empty(Failures(file, "binding", hits));
    }

    // Section 5: Every Light/Dark Override Has A HighContrast Entry With The Same Keys
    [Theory, MemberData(nameof(Files))]
    public void ThemeDictionaries_HaveHighContrastWithSameKeys(string file)
    {
        var hits = new List<(XElement, string)>();
        foreach (var themes in Load(file).Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
        {
            var byKey = themes.Elements().ToDictionary(d => (string?)d.Attribute(X + "Key") ?? "", d => d.Elements().Select(r => (string?)r.Attribute(X + "Key")).ToHashSet());
            var keys  = byKey.Where(p => p.Key != "HighContrast").SelectMany(p => p.Value).ToHashSet();
            if (!byKey.TryGetValue("HighContrast", out var contrast))
            {
                hits.Add((themes, "no HighContrast dictionary"));
                continue;
            }

            hits.AddRange(keys.Except(contrast).Select(k => (themes, $"HighContrast lacks {k}")));
        }

        Assert.Empty(Failures(file, "high-contrast", hits));
    }

    // Section 3: Spacing Only From The Scale (2, 4, 8, 12, 16, 24, 32, 36, plus 0, 1, 48)
    [Theory, MemberData(nameof(Files))]
    public void Spacing_UsesTheScale(string file)
    {
        string[] names = ["Margin", "Padding", "Spacing", "RowSpacing", "ColumnSpacing"];
        var hits = Load(file).Descendants()
            .SelectMany(e => e.Attributes().Where(a => names.Contains(a.Name.LocalName)).Select(a => (e, a.Value)))
            .Where(p => !p.Value.StartsWith('{') && p.Value.Split(',').Any(v => double.TryParse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) && !Scale.Contains(Math.Abs(n))))
            .Select(p => (p.e, p.Value));

        Assert.Empty(Failures(file, "spacing", hits));
    }

    // Section 3: Corner Radii 4 And 8 (Or Theme Resources); Circles Are Allowlisted
    [Theory, MemberData(nameof(Files))]
    public void CornerRadii_UseTheScale(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => Attr(e, "CornerRadius") is { } v && !v.StartsWith('{'))
            .Where(e => Attr(e, "CornerRadius")!.Split(',').Any(v => double.TryParse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) && !Radii.Contains(n)))
            .Select(e => (e, Attr(e, "CornerRadius")!));

        Assert.Empty(Failures(file, "radius", hits));
    }

    // Section 14: Narrator Hears Changes In InfoBars (undo notice, errors)
    [Theory, MemberData(nameof(Files))]
    public void InfoBars_HaveALiveSetting(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => e.Name.LocalName == "InfoBar" && Attr(e, "AutomationProperties.LiveSetting") is null)
            .Select(e => (e, Attr(e, "AutomationProperties.AutomationId") ?? "(no id)"));

        Assert.Empty(Failures(file, "live-region", hits));
    }

    // Section 14: Text Follows The System Text Size, So Text Elements Never Get A Fixed Height
    [Theory, MemberData(nameof(Files))]
    public void TextElements_HaveNoFixedHeight(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => e.Name.LocalName is "TextBlock" or "RichTextBlock" && (Attr(e, "Height") is not null || Attr(e, "MaxHeight") is not null))
            .Select(e => (e, Attr(e, "Text") ?? Attr(e, "AutomationProperties.AutomationId") ?? "(text)"));

        Assert.Empty(Failures(file, "text-height", hits));
    }
}
```

Run: `--filter-class "*XamlLintTests"`. Expected: FAILs that list current findings.

Handle each finding:
- **In a Track C-owned file:** fix it (add the name or tooltip, the `LiveSetting="Polite"` or `"Assertive"` for errors, the HighContrast dictionary with `SystemColor*` resources, or the nearest scale value). Exception: if the odd value has a code comment explaining an optical alignment, add it to `Allowed` with that reason.
- **In a Track A- or B-owned file** (`AboutPage`, `EventEditorView`): add it to `Allowed` with the comment `// TODO: Task 9 fixes (owned by Track A|B)`, and list it in the report. Task 9 removes those entries.

Don't allowlist a missing name or a `{Binding}`. Those always get fixed, in Task 9 if the file isn't yours.

- [ ] **Step 2: Contrast of code-built colors**

`ChromeColors.cs` moves the hex values out of `LeafBrushes` (`LeafBrushes.cs:37-71`) unchanged, plus the two assumed surfaces:

```csharp
namespace LeafCalendar.Core.Views;

/// <summary>
/// Colors for chrome the app draws in code (grid lines, secondary text, hover), per theme, as hex (#RRGGBB or
/// #AARRGGBB). The app turns them into brushes; tests check their contrast against the calendar surface.
/// </summary>
public static class ChromeColors
{
    /// <summary>The dark calendar island: LayerFillColorDefault (#3A3A3A at 30%) over dark Mica (#202020).</summary>
    public const string DarkSurface = "#272727";

    /// <summary>The light calendar island: LayerFillColorDefault (#FFFFFF at 50%) over light Mica (#F3F3F3).</summary>
    public const string LightSurface = "#F9F9F9";

    /// <summary>The island surface for a theme.</summary>
    public static string Surface(bool dark) => dark ? DarkSurface : LightSurface;

    /// <summary>Primary text.</summary>
    public static string PrimaryText(bool dark) => dark ? "#FFFFFFFF" : "#E4000000";

    /// <summary>Secondary text (times, zone labels).</summary>
    public static string SecondaryText(bool dark) => dark ? "#C5FFFFFF" : "#9E000000";

    /// <summary>Dimmed text (days outside the month).</summary>
    public static string DimText(bool dark) => dark ? "#5DFFFFFF" : "#5C000000";

    // GridLine, HalfHourLine, WeekendFill, Hover, GhostFill, CautionBackground, OnAccent: the LeafBrushes values, same shape
}
```

Write every member out in full. `LeafBrushes` methods become `FromHex(ChromeColors.X(dark))`.

`ChromeColorsTests.cs`:

```csharp
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ChromeColorsTests
{
    // #AARRGGBB composited onto an opaque surface
    static string Flatten(string argb, string surface) =>
        argb.Length == 9 ? EventColors.Blend("#" + argb[3..], surface, 1 - Convert.ToInt32(argb[1..3], 16) / 255.0) : argb;

    // WCAG AA: 4.5:1 For Text On The Calendar Surface, Both Themes
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_OnSurface_MeetsAA(bool dark)
    {
        var surface = ChromeColors.Surface(dark);
        foreach (var (name, color) in new[] { ("primary", ChromeColors.PrimaryText(dark)), ("secondary", ChromeColors.SecondaryText(dark)), ("dim", ChromeColors.DimText(dark)) })
        {
            var ratio = EventColors.ContrastRatio(Flatten(color, surface), surface);
            Assert.True(ratio >= 4.5, $"{name} text {color} on {surface}: {ratio:0.00}:1");
        }
    }
}
```

Add to `EventColorsTests`:

```csharp
    // Past Events Fade, But Their Text Still Meets 4.5:1 (title and time) On Every Google Color, Both Themes
    [Fact]
    public void PastCards_FadedText_MeetsAA()
    {
        foreach (var dark in new[] { true, false })
        {
            var surface = ChromeColors.Surface(dark);
            foreach (var accent in EventColors.CalendarPalette.Concat(EventColors.EventColorNames.Select(c => EventColors.ResolveAccent(c.Id, "#039BE5"))))
            {
                var palette = EventColors.Palette(accent, dark);
                var fill    = EventColors.Blend(palette.Fill, surface, 1 - EventColors.PastOpacity);
                var text    = EventColors.Blend(palette.Text, surface, 1 - EventColors.PastOpacity);
                Assert.True(EventColors.ContrastRatio(text, fill) >= 4.5, $"{accent} dark={dark}");
            }
        }
    }
```

If `EventPalette` names its members differently (`Fill`, `Text`, `SecondaryText`), use its real names, and add the same check for `SecondaryText` flattened the same way.

Run them. Then fix the colors by changing only the values:
- Raise `DimText` alpha to the smallest value that passes, in both themes.
- Add `public const double PastOpacity` to `EventColors` at the smallest value ≥ 0.55 that passes. Step by 0.05 and record the value in its doc comment ("Faded past cards, at the lowest opacity that keeps their text at 4.5:1 on every Google color").
- Use `EventColors.PastOpacity` in `EventBlock.cs:176` and `WeekRow.cs:249`.

Grid and half-hour lines are decorative structure, not text. They aren't held to a ratio here (Windows' own calendar lines are as faint). Contrast themes cover users who need stronger lines (Task C2).

- [ ] **Step 3: Run and build**

Run `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` (all PASS), then `dotnet build LeafCalendar.slnx -c Debug` (0 warnings).

- [ ] **Step 4: Commit**

```bash
git add src tests/LeafCalendar.Tests
git commit -m "test: XAML lint and contrast checks in the logic tests; dim text and past-card fade meet 4.5:1"
```

---

### Task C2: High Contrast, Narrator, and Keyboard (UIA Audit)

**Found while planning:**
- `LeafBrushes` hands out fixed hex brushes and never reads the system's contrast colors.
- Contrast-theme overrides exist only in `DetailsPanel.xaml` (design standard Section 15, gap 2).
- In a contrast theme, the code-built calendar (grid, cards, now line) keeps its normal colors today.

**Files:**
- Modify: `src/LeafCalendar.App/Controls/LeafBrushes.cs`, `DayColumn.cs`, `WeekRow.cs`, `AllDayCanvas.cs`, `EventBlock.cs` (palette through `LeafBrushes.CardPalette`), the App's theme-change hook (where views re-render on `ActualThemeChanged`)
- Create: `tests/LeafCalendar.UITests/Support/A11yAudit.cs`, `tests/LeafCalendar.UITests/AccessibilityTests.cs`
- Modify: `tests/LeafCalendar.UITests/Support/LeafApp.cs` (add `Focused()` and `AllWindows()`)
- Modify: Track C-owned views the audit flags

**Interfaces:**
- Produces:
  - `public static bool LeafBrushes.HighContrast { get; }`
  - `public static EventPalette LeafBrushes.CardPalette(string accentHex, bool dark)`
  - `public static event EventHandler? LeafBrushes.ContrastChanged`
  - `A11yAudit.Unnamed(AutomationElement root)`, which returns `IReadOnlyList<string>`
  - `A11yAudit.TabStops(LeafApp leaf, Window window, int limit = 200)`, which returns `HashSet<string>`
  - `LeafApp.Focused()` → `AutomationElement?`
  - `LeafApp.AllWindows()` → `Window[]`

- [ ] **Step 1: UI tests (built now, run in Task 9)**

`Support/A11yAudit.cs`:

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace LeafCalendar.UITests.Support;

/// <summary>Accessibility checks over a live UIA tree: names Narrator reads, and what Tab reaches.</summary>
public static class A11yAudit
{
    static readonly ControlType[] Interactive =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink, ControlType.ListItem,
        ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.Spinner, ControlType.SplitButton, ControlType.TabItem,
        ControlType.TreeItem, ControlType.Document,
    ];

    /// <summary>On-screen interactive elements whose name is empty or a type name (an object's ToString leaking out).</summary>
    public static IReadOnlyList<string> Unnamed(AutomationElement root) =>
        [.. root.FindAllDescendants()
            .Where(e => Interactive.Contains(e.Properties.ControlType.ValueOrDefault) && !e.Properties.IsOffscreen.ValueOrDefault)
            .Where(e => IsBadName(e.Properties.Name.ValueOrDefault))
            .Select(e => $"{e.Properties.ControlType.ValueOrDefault} id='{e.Properties.AutomationId.ValueOrDefault}'")];

    static bool IsBadName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || name.StartsWith("LeafCalendar.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.UI.", StringComparison.Ordinal)
        || name.StartsWith("Windows.", StringComparison.Ordinal);

    /// <summary>AutomationIds Tab reaches from the window's first stop, until focus comes back around (or the limit).</summary>
    public static HashSet<string> TabStops(LeafApp leaf, Window window, int limit = 200)
    {
        window.Focus();
        var seen  = new HashSet<string>();
        string? first = null;

        for (var i = 0; i < limit; i++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            var id = leaf.Focused()?.Properties.AutomationId.ValueOrDefault ?? "";
            if (id.Length > 0 && id == first)
            {
                break;
            }

            first ??= id.Length > 0 ? id : null;
            seen.Add(id);
        }

        return seen;
    }
}
```

In `LeafApp.cs`, add:

```csharp
    /// <summary>The element with keyboard focus, or null.</summary>
    public AutomationElement? Focused() => _automation.FocusedElement();

    /// <summary>Every top-level window of the app (main, settings, onboarding, tray flyout, dialogs).</summary>
    public Window[] AllWindows() => App.GetAllTopLevelWindows(_automation);
```

`AccessibilityTests.cs`. Screens are a table, so Milestone 4 and 5 surfaces are rows, not new test methods:

```csharp
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class AccessibilityTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    /// <summary>
    /// Each screen: how to open it, and the AutomationIds Tab must reach there. Rows for Milestone 4 and 5 surfaces use
    /// the ids from their plans (see Step 2).
    /// </summary>
    public static TheoryData<string> Screens() =>
    [
        "Main", "Details", "Editor", "SettingsGeneral", "SettingsCalendars", "SettingsTimeZones", "SettingsAccounts", "SettingsAbout",
        // Milestone 4 and 5 rows (Step 2)
    ];

    static (Action<LeafApp> Open, string[] MustReach) Screen(string name) => name switch
    {
        "Main"    => (leaf => leaf.WaitFor("Event_evt-single_202610011300"), ["PaneToggleButton", "TodayButton", "PreviousButton", "NextButton", "ViewPicker", "SettingsButton"]),
        "Details" => (leaf => leaf.WaitFor("Event_evt-single_202610011300").Click(), ["DetailsEditButton", "DetailsDeleteButton"]),
        "Editor"  => (leaf => { leaf.WaitFor("Event_evt-single_202610011300").Click(); leaf.Press(VirtualKeyShort.KEY_E); }, ["EditorTitle", "EditorDescription", "DescriptionBold", "EditorSave"]),
        // Settings pages: open Settings (Ctrl+,) and pick the page the way SettingsTests does; MustReach is the page's own control ids
        _         => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // Narrator: Every Control On Every Window Has A Real Name
    [Theory, MemberData(nameof(Screens))]
    public void Screen_EveryControlHasAName(string name)
    {
        using var leaf = Launch();
        Screen(name).Open(leaf);

        var unnamed = leaf.AllWindows().SelectMany(A11yAudit.Unnamed).ToList();
        Assert.True(unnamed.Count == 0, $"{name}: " + string.Join("; ", unnamed));
    }

    // Keyboard: Tab Reaches Every Control The Screen Needs
    [Theory, MemberData(nameof(Screens))]
    public void Screen_TabReachesEveryControl(string name)
    {
        using var leaf = Launch();
        var (open, mustReach) = Screen(name);
        open(leaf);

        var window  = leaf.AllWindows().First(w => w.Properties.HasKeyboardFocus.ValueOrDefault || w.FindFirstDescendant(cf => cf.ByAutomationId(mustReach[0])) is not null);
        var reached = A11yAudit.TabStops(leaf, window);
        Assert.Empty(mustReach.Except(reached));
    }

    // Narrator: An Event Card Says Its Title, Time, And Calendar (never color alone)
    [Fact]
    public void EventCard_NameSaysTitleTimeAndCalendar()
    {
        using var leaf = Launch();
        var card = leaf.WaitFor("Event_evt-single_202610011300");

        Assert.Matches(@"Dentist appointment, .*\d.*, .+", card.Name);
    }

    // Contrast Theme (only when the owner turned one on): cards use system colors
    [Fact]
    public void HighContrast_EventCardUsesSystemColors()
    {
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            Assert.Skip("Turn on a contrast theme (Settings › Accessibility › Contrast themes) to run this.");
        }

        using var leaf = Launch();
        var card  = leaf.WaitFor("Event_evt-single_202610011300");
        var ui    = new Windows.UI.ViewManagement.UISettings();
        var fill  = Ink.PixelAt(card.BoundingRectangle.Right - 3, card.BoundingRectangle.Bottom - 3);
        var allowed = new[] { Windows.UI.ViewManagement.UIElementType.Window, Windows.UI.ViewManagement.UIElementType.Highlight, Windows.UI.ViewManagement.UIElementType.ButtonFace }
            .Select(t => ui.UIElementColor(t)).Select(c => System.Drawing.Color.FromArgb(c.R, c.G, c.B));

        Assert.Contains(allowed, c => c.ToArgb() == fill.ToArgb());
    }

    // Text Size (only when the owner set it to 150% or more): chrome text isn't clipped
    [Fact]
    public void LargeText_ChromeTextIsNotClipped()
    {
        if (new Windows.UI.ViewManagement.UISettings().TextScaleFactor < 1.5)
        {
            Assert.Skip("Set Settings › Accessibility › Text size to 150% or more to run this.");
        }

        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        var clipped = leaf.AllWindows().SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)))
            .Where(t => !t.Properties.IsOffscreen.ValueOrDefault && t.Parent is { } p && p.Properties.ControlType.ValueOrDefault != FlaUI.Core.Definitions.ControlType.Pane)
            .Where(t => !t.Parent.BoundingRectangle.Contains(Rectangle.Inflate(t.BoundingRectangle, -1, -1)))
            .Select(t => $"'{t.Name}' in {t.Parent.Properties.AutomationId.ValueOrDefault}")
            .ToList();

        Assert.True(clipped.Count == 0, string.Join("; ", clipped));
    }
}
```

- Settings rows: write each `Settings*` arm in full. Open Settings with the shortcut, pick the page the way `SettingsTests` does, and set `MustReach` to that page's control AutomationIds (read them from the page XAML).
- The event card's name format: read `EventBlock`'s `AutomationProperties.Name` today. If it lacks the time or calendar name, Step 3 adds them in that order, comma-separated.
- `Ink.PixelAt`: `Support/Ink.cs` already samples screen pixels. Use its existing method if one exists, or add `PixelAt(int x, int y)` there through the same capture it uses.
- `// ponytail: the text-clip check compares each text's box to its parent's; it misses clipping inside custom-drawn controls (screenshots cover those).`

Run `dotnet build tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` and confirm 0 warnings.

- [ ] **Step 2: Milestone 4 and 5 rows**

Read the M4 plan (`docs/superpowers/plans/*-m4-*.md`) and `docs/superpowers/plans/2026-10-01-m5-power-features.md`. Add one `Screens()` row and `Screen()` arm per surface, using their AutomationIds and test-mode launch arguments:

| Milestone | Surface | Window |
|---|---|---|
| 4 | Tray flyout (agenda, Join, New event) | its own acrylic window: audit with `AllWindows()` |
| 4 | Tray menu (`MenuFlyout` in a host window) | the host window |
| 4 | Settings › Notifications, Tray, Shortcuts (hotkey conflict warning) | Settings |
| 5 | Command menu (Ctrl+K) | main |
| 5 | Shortcut cheat sheet (?) | main or its dialog |
| 5 | Share availability (S) | main |
| 5 | People overlay (P) and Meet with (F) pickers | main |
| 5 | Time travel (Z) | main |
| 5 | Settings › Appearance (interface scale, grid density) | Settings |

- If a surface can't be opened by a UI test (for example, the tray icon needs a real shell click), open it with the test-mode argument its own UI tests use.
- If there's none, the row is "manual" and goes in Task 9's screenshot list instead.
- Toasts are drawn by Windows. Check only that every toast button has text content, in the M4 toast builder's unit tests, if they don't already.

- [ ] **Step 3: High-contrast calendar**

`LeafBrushes`:

```csharp
    // =========================================================================
    // CONTRAST THEMES
    // =========================================================================

    static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    static readonly Windows.UI.ViewManagement.UISettings System = new();

    /// <summary>True while Windows uses a contrast theme; every brush then comes from the system's contrast colors.</summary>
    public static bool HighContrast => Accessibility.HighContrast;

    /// <summary>Raised (on a background thread) when a contrast theme turns on or off.</summary>
    public static event EventHandler? ContrastChanged;

    static LeafBrushes() => Accessibility.HighContrastChanged += (_, _) =>
    {
        Cache.Clear();
        ContrastChanged?.Invoke(null, EventArgs.Empty);
    };

    // A system contrast color as a brush
    static SolidColorBrush SystemBrush(Windows.UI.ViewManagement.UIElementType type) => new(System.UIElementColor(type));

    /// <summary>Card colors: Google's in normal themes; window, text, and highlight colors in a contrast theme.</summary>
    public static EventPalette CardPalette(string accentHex, bool dark) =>
        HighContrast ? HighContrastPalette() : EventColors.Palette(accentHex, dark);
```

- Every existing brush method gets a first line `if (HighContrast) { return SystemBrush(...); }` with this mapping:

  | Brush | Contrast color |
  |---|---|
  | Grid and half-hour lines | `GrayText` |
  | Weekend fill | `Window` |
  | Primary, secondary, and dim text | `WindowText` |
  | Hover | `Highlight` |
  | Ghost fill | `Window` |
  | Caution background | `Window` |
  | Accent | `Highlight` |
  | On-accent | `HighlightText` |
  | Now line | `Highlight` |

- `HighContrastPalette()` builds an `EventPalette` from `Window` (fill), `WindowText` (text, secondary text, accent bar), and a 1 px `WindowText` border. A selected card uses `Highlight` and `HighlightText`, wherever selection already changes the card.
- Writing a `Color` read from `UISettings` into a new brush is not an AOT read-back, so it's fine.
- `DayColumn`, `WeekRow`, `AllDayCanvas`, and `EventBlock` call `LeafBrushes.CardPalette` instead of `EventColors.Palette`.
- Re-render on `ContrastChanged`: where the App re-renders calendar views on `ActualThemeChanged` (search `ActualThemeChanged` in `CalendarPage.xaml.cs` and `MainWindow.xaml.cs`), subscribe to `LeafBrushes.ContrastChanged` too, marshal it with `DispatcherQueue.TryEnqueue`, run the same refresh, and unsubscribe when the window closes.
- XAML: the lint from C1 already forced a `HighContrast` dictionary wherever Light/Dark overrides exist. Check `LeafTheme.xaml`'s custom templates (bare icon buttons, mini-month days, pips) for hover and focus brushes, which must come from `SystemColor*` resources in `HighContrast`.

- [ ] **Step 4: Narrator and keyboard fixes from the audit**

Run `AccessibilityTests` once on Debug for the Track C screens only (`pwsh tools/dev-register.ps1`, then `--filter-class "*AccessibilityTests"`). The full batch is Task 9. Fix what it finds in Track C files:
- **Missing names:** set `AutomationProperties.Name` (the same text as the visible label or tooltip).
- **Type-name leaks:** list items whose name is a class name get `AutomationProperties.Name` bound with `x:Bind` to their display text in the item template.
- **Unreachable controls:** fix `IsTabStop`, `TabIndex`, or `XYFocusKeyboardNavigation`, so Tab order follows reading order. The calendar's event cards aren't Tab stops. N and B (next and previous event) and arrow keys are their keyboard path (spec 8.7). Don't add 100 Tab stops to the grid.
- **Event card name:** "Title, time range, calendar name", plus ", past" or ", declined" from the existing `ItemStatus`.
- **Live regions:** the sync status icon gets `AutomationProperties.LiveSetting="Polite"`, and its name is its tooltip text, so Narrator announces "2 changes waiting".

For failures in A- or B-owned files, record them for Task 9.

- [ ] **Step 5: Build**

Run `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), then the logic tests (all PASS).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(a11y): contrast-theme calendar colors, Narrator names, Tab reachability audit"
```

---

### Task C3: Visual Pass and Screenshot Tour

**Files:**
- Create: `tests/LeafCalendar.UITests/ScreenshotTour.cs`
- Modify: Track C-owned views and `Styles/LeafTheme.xaml` for the deviations found, and `docs/design-standard.md` Section 15 (refresh "Already follows it" and "Known gaps")

**Interfaces:**
- Consumes: the `AccessibilityTests.Screens()` and `Screen()` table (C2). The tour walks the same screens, so Milestone 4 and 5 rows come along.
- Produces: `ScreenshotTour.Capture_AllScreens` writes `<LEAF_SCREENSHOTS>/<screen>-<light|dark>-<width>.png`. Task 9 uses it for the visual review and the Store screenshots.

- [ ] **Step 1: The tour (skips unless asked)**

```csharp
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ScreenshotTour : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    /// <summary>
    /// Every screen in light and dark, at 1366 × 768 (Store minimum) and at the 1086 × 540 minimum window. Set LEAF_SCREENSHOTS
    /// to a folder to run it. Fake data only, so screenshots never show anyone's real calendar.
    /// </summary>
    [Fact]
    public void Capture_AllScreens()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the tour.");
        }

        Directory.CreateDirectory(folder);
        foreach (var screen in AccessibilityTests.Screens().Select(row => (string)row.Data.Item1))
        {
            foreach (var (width, height) in new[] { (1366, 768), (1086, 540) })
            {
                foreach (var theme in new[] { "light", "dark" })
                {
                    using var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --theme {theme}");
                    leaf.MainWindow.Patterns.Transform.Pattern.Resize(width, height);
                    AccessibilityTests.Open(screen, leaf);
                    foreach (var window in leaf.AllWindows())
                    {
                        window.CaptureToFile(Path.Combine(folder, $"{screen}-{theme}-{width}-{window.Title.Replace(' ', '_')}.png"));
                    }
                }
            }
        }
    }
}
```

- Make `Screen()` in `AccessibilityTests` reachable as `public static void Open(string name, LeafApp leaf) => Screen(name).Open(leaf);`.
- If the row shape of xUnit v3's `TheoryData<string>` differs from `row.Data.Item1`, read the screen names from a `public static readonly string[] ScreenNames` that both `Screens()` and the tour use.
- `--theme light|dark`: if Leaf has no launch argument for the theme, press Ctrl+Shift+L after launch for dark instead (it toggles the theme). Don't add a launch argument for this.

- [ ] **Step 2: Capture and review against the standard**

Run the tour on Debug: `$env:LEAF_SCREENSHOTS = "<scratchpad>\screens\m6"`, then `--filter-class "*ScreenshotTour"`.

Check each image against `docs/design-standard.md`. Write one line per finding in the report, with screen, rule section, and what's off:
- Section 2: title bar 48, icon-only bare buttons, and glyphs 12 and 16
- Section 3: spacing scale, radii 4 and 8, nothing clipped at 1086 × 540, and only overflow scrolls
- Section 4: type ramp, two weights, CharacterEllipsis
- Section 5: theme brushes, and accent used once per surface
- Section 6: icon sizes and decorative icons raw
- Section 7: setting rows, toggles without On/Off text, and "…" on items that open more
- Section 12: hover fill 83 ms (spot check), empty states, and scrollbars hidden until hover
- Section 13: sentence case, verbs on buttons, and US spelling

Fix findings in Track C files. Record findings in A- or B-owned files for Task 9.

**Rule:** the visual pass fixes deviations from the standard. It doesn't redesign what the owner already approved, and the standard's "existing screens stay" notes (for example `ScopeDialog`'s "OK") stay as they are.

- [ ] **Step 3: Design standard refresh**

In `docs/design-standard.md` Section 15:
- Move closed gaps to "Already follows it": contrast themes (Task C2) and the tray flyout (if Milestone 4 built it per Sony's `FlyoutWindow`).
- Add one line to Section 14: "The logic tests lint every XAML file (`XamlLintTests`) and check code-built colors for 4.5:1 (`ChromeColorsTests`); `AccessibilityTests` audits names and Tab reachability on every screen."

- [ ] **Step 4: Commit**

```bash
git add src tests docs/design-standard.md
git commit -m "feat(ui): visual pass against the design standard; screenshot tour"
```

---

### Task 9: Merge, Batch Verification, Spec, Reinstall

**Files:**
- Modify: whatever the batch finds, each fix with a test; `XamlLintTests.Allowed` (remove the Task 9 TODO entries); `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`; `tests/LeafCalendar.UITests/memory-budget.json` (only if the measurement requires it)

- [ ] **Step 1: Merge the tracks**

Run `git checkout m6-polish`, then `git merge --no-ff m6-a`, `m6-b`, and `m6-c`, in that order. Conflicts should be none by file ownership. If one appears, resolve it toward both changes and note it in the report.

Then:
1. `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings.
2. `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj` → all PASS.

- [ ] **Step 2: Cross-track fixes**

- Fix every finding recorded for another track's files: the lint `TODO` entries in `XamlLintTests.Allowed` for `AboutPage` and `EventEditorView`, and audit failures in A- or B-owned views. Remove their `Allowed` entries.
- The editor's new toolbar must pass `IconOnlyButtons_HaveNameAndTooltip` with no allowlist entry.
- Add the About privacy policy card if the owner has given the URL by now.

Run the logic tests again → all PASS.

- [ ] **Step 3: UI batch on Debug**

Run `pwsh tools/dev-register.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`. Expected: all PASS, with skips only for memory, high contrast, text size, and the screenshot tour.

Kill leftover `LeafCalendar.exe --profile uitest-*` processes after a failure, because they hold `leaf.db`, before re-running. Fix failures with tests.

- [ ] **Step 4: UI batch on AOT**

1. `pwsh tools/publish-aot.ps1 -Register` → **0 IL warnings**.
2. The whole UI suite again → all PASS. This is where `RichEditBox` document calls, `StoreTests.TrimmedPackage_LaunchesAndShowsANotification`, and the high-contrast brush code get their AOT run.
3. `pwsh tools/check-package.ps1` → no forbidden files, under budget. Record the MSIX size.

- [ ] **Step 5: Memory, three runs**

`dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter-class "*MemoryTests" --output Detailed`, three times with AOT still registered. Budget: private bytes ≤ 120 MB, working set ≤ 25 MB.
- **If it passes**, leave the budget alone.
- **If it fails**, find what grew first. Likely suspects are the `RichEditBox` (WinUIEdit) loading at startup instead of when the editor opens, and `UISettings` and `AccessibilitySettings` instances. Load the description box only when the editor first opens, if that's the cause.

Trimming the AI DLLs should lower private bytes slightly, since fewer images map. Record the numbers either way.

- [ ] **Step 6: Owner-run checks (ask the owner, then run)**

Ask the owner to do these. They change Windows settings or need elevation, which the plan never does itself:
1. Turn on a contrast theme (Settings › Accessibility › Contrast themes), then run `--filter-method "*HighContrast*"` and the screenshot tour in that state. Then turn it off.
2. Set Settings › Accessibility › Text size to 150%, then run `--filter-method "*LargeText*"` and the tour. Then set it back.
3. In an elevated PowerShell, run the Windows App Certification Kit on the MSIX:
   `& "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" test -appxpackagepath <msix> -reportoutputpath <scratchpad>\wack.xml`
   Read the report. Failures are fixed with tests. Warnings go in the report.

If the owner declines or isn't around, list the three as open, and finish the rest.

- [ ] **Step 7: Screenshots**

1. Run the tour on the AOT build in light and dark: `$env:LEAF_SCREENSHOTS = "<scratchpad>\screens\m6"`.
2. Re-check the Task C3 findings that were fixed, and list the files in the report.
3. Copy the six Store screenshots named in `docs/store/listing.md` into `docs/store/screenshots/` (1366 × 768, fake data).

- [ ] **Step 8: Spec updates**

In `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md`:
- **Section 3.4 item 7:** append "Measured 2026-10-xx at the close of Milestone 6 (rich description editor, box select, contrast-theme brushes, AI libraries removed) at NN MB private bytes and NN MB working set (three runs), inside the unchanged budget." Use the real numbers, and the budget change and its reason if it changed.
- **Section 4.5, Descriptions:** add "Edited in place with bold, italic, underline, and bullet or numbered lists (Milestone 6). Saving writes only those tags and allowlisted links back to Google, with all text encoded; an untouched description is never rewritten. Paste is plain text."
- **Section 7.2:** after "Description with bold, italic, underline, links, and lists.", add "Links are kept but not edited in the editor (Milestone 6)."
- **Section 7.3:** after "Multi-select with `Ctrl`+click or `Shift`+drag box", add "(box select built in Milestone 6: the time grid selects timed events under the box; the month view selects every event on the covered days; Ctrl adds to the selection)."
- **Section 9, About:** add "Privacy policy" (if added) and change "Third-party licenses" to "Third-party notices (shown in a dialog)".
- **Section 10.3:** add "**Package size:** `tools/check-package.ps1` fails on Windows App SDK AI/ML/Search/Widgets files or an MSIX over budget (manual CI job)." and "**Accessibility and design lint:** `XamlLintTests` and `ChromeColorsTests` in the logic tests; `AccessibilityTests` in the UI tests."
- **Section 11:** add "Manual workflow trigger: Release AOT publish plus the package check."
- **Section 12, item 6:** append "Built in Milestone 6." to each line, and replace the "Deferred from Milestone 3" sub-list heading with "Deferred from Milestone 3 (built in Milestone 6):".
- **Section 13, Package size:** change it to "**Package size:** the self-contained MSIX was about 56 MB, about 40 MB of it Windows App SDK AI libraries. Milestone 6 references the Windows App SDK component packages without AI, ML, Search, and Widgets; the MSIX is now NN MB (`tools/check-package.ps1`)."
- **Section 1.2:** no change. Store submission itself stays an owner action.

- [ ] **Step 9: Reinstall AOT, merge, push**

1. Run `pwsh tools/publish-aot.ps1 -Register`, and leave the AOT build installed. Switching builds empties the default profile's account list. Tell the owner they'll need to add their account again; their OAuth client in Credential Locker stays.
2. Commit:

```bash
git add -A docs tests src tools
git commit -m "chore: close Milestone 6 with batch verification, spec updates, and package size measured"
```

3. Merge to `main` and push, per the owner's standing OK at milestone ends: `git checkout main`, `git merge --no-ff m6-polish`, `git push`.

---

## Owner Input Needed (Store listing)

Nothing blocks the build. These are needed only for the Store submission:
1. **App icon:** a 1024 × 1024 transparent PNG at `assets/brand/leaf-icon-1024.png`. Today's logos are tiny placeholders.
2. **Partner Center identity:** the reserved app name's Identity Name, Publisher (`CN=…`), and Publisher display name, in `docs/store/partner-center.json`.
3. **Privacy policy URL:** where the policy is hosted, after reviewing the draft.
4. **Support contact** (email or website) for the listing and the privacy policy.
5. **Certification test account:** whether to give Microsoft's testers a throwaway Google account and OAuth client, or none.
6. **Owner-set items in Partner Center:** pricing and markets, the age-rating questionnaire, and approving the listing text.
7. **Task 9 Step 6:** switch on a contrast theme and 150% text size for the two skip-gated UI tests, and run WACK elevated.

## Self-Review Notes

**Coverage:**

| Spec item | Task |
|---|---|
| 12.6 Visual pass | C1 (lint: spacing, radii, theme dictionaries), C3 (screenshot tour, review against the standard), 9 (fixes and screenshots) |
| 12.6 Accessibility pass: contrast | C1 (code-built text 4.5:1, past cards), C2 (contrast themes) |
| 12.6 Accessibility pass: keyboard, Narrator, text scaling | C2 (Tab audit, names, live regions, text-clip check), C1 (no fixed text heights) |
| 12.6 Store listing requirements | A2 (logos, manifest, notices, privacy, listing, certification notes, Store build), 9 (WACK, screenshots) |
| 12.6 Rich description editing | B1 (Core), B2 (editor) |
| 12.6 Shift+drag box select | B3 |
| 13 Package size | A1, 9 (measured) |
| M4/M5 UI in the passes | C2 Step 2 rows, C3 tour, 9 batch |

**Type consistency checked:**
- `ListKind`, `DescriptionLine`, and `DescriptionRun(..., ListKind List)` are defined in B1 and used unchanged in B2's `RichDescription`.
- `DescriptionHtml.Lines`, `Write`, and `Normalize` match between B1 and B2.
- `BoxSelection.InTimeBox(occurrences, dayA, dayB, minutesA, minutesB, zone)` matches between B3's Core and App steps.
- `CalendarViewModel.SelectBox(hits, add)` and `OnDays(days)` are used the same way in both views.
- `PackageManifestTests.RepoRoot()` is defined in A1 and used by C1's `XamlLintTests`. C1 compiles only after A1 merges, so to keep the tracks independent, Task C1 copies the same eight-line `RepoRoot()` into `XamlLintTests` if `m6-a` isn't merged yet, and Task 9 deletes the copy.
- `LeafBrushes.CardPalette` and `HighContrast` are defined in C2. B3's box uses `LeafBrushes.Accent`, which exists today and gains its contrast behavior from C2 without a signature change.

**Review Focus pinned:** each line names its test in the owning task (B1, B2, B3, A1).
