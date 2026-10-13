# Scheduling Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standard way to plug scheduling providers into Share availability, with Cal.com as the first: booking links per proposed time, availability-fitted picks, booking detection, and one-click Approve… that books on Cal.com.

**Architecture:** Core gets a `Scheduling` folder: one small interface per provider action, a provider registry, pure fitting and matching math, a Cal.com REST client, a hub that holds connected providers, and a booking watcher that runs after each sync pass. Secrets go through `ITokenStore`, plain settings through `LeafSettings`, and saved groups gain four columns (migration 13). The App wires the hub, adds Settings › Scheduling, extends the share panel, grid and copy, and books through Approve….

**Tech Stack:** C# 14 / .NET 10, WinUI 3 (Windows App SDK), Native AOT, `Microsoft.Data.Sqlite`, `System.Text.Json` source generation, xunit v3, FlaUI.

**Spec:** `docs/superpowers/specs/2026-10-12-scheduling-providers-design.md` (read it fully before Task 1).

## Global Constraints

- Run every command from `D:\leaf-calendar` (the folder with `global.json`).
- Build: `dotnet build LeafCalendar.slnx -c Debug` must end with 0 warnings (warnings are errors).
- Logic tests: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug` (one class: `--filter-class "*ClassName"`).
- C# style: Microsoft conventions, file-scoped namespaces matching folders, `_camelCase` fields, access modifiers written, no column alignment, doc comments on every public member, CRLF line endings (edit files in place).
- Native AOT: `x:Bind` only; `ItemsSource` gets a concrete `List<T>`; JSON only through a source-generated context; no reflection; type tests on interfaces are fine.
- Time: injected `TimeProvider`, instants stored in UTC, invariant culture for storage and the API.
- Security: never log the API key, link names, booking addresses, attendee names or emails, or event content. Log provider id, counts, HTTP status codes, exception type names. Everything from a provider is untrusted.
- Secrets only through `ITokenStore`. Never in a URL (except a guest's own email in a booking link, which is the feature), never in a log.
- Copy: US English, sentence case. Every user-visible string must be in spec section 11 (owner approval pending before shipping). Quote any string you add that isn't there in your report.
- Design: stock WinUI controls and theme brushes, spacing 2/4/8/12/16/24/32, `ControlCornerRadius`/`OverlayCornerRadius`, every icon-only control has a tooltip and `AutomationProperties.Name`, every element a UI test touches has an `AutomationId`, `ScrollIndicator.ShowOnHover`, no fixed text heights. `XamlLintTests` must pass.
- Commits: one per task on branch `feat/scheduling-providers` (owner: "this work will go into a PR"). Message style: `feat(scheduling): <user-visible effect>`. Never push or open the PR before Task 21. Never comment on the PR or change its status.
- UI tests drive the installed package: `pwsh tools/sign-install.ps1` first, never two UI runs at once, run only affected classes per change. If a whole block fails at once, re-run before diagnosing. If a `Paused:` failure appears, stop and tell the owner; never delete the pause file.

## Review Focus

1. **Bookable times that straddle midnight or a DST change in the share's zone** — fitting and splitting must work on instants, not wall-clock math; a 30-minute slot at 1:30 AM on a fall-back day stays 30 minutes. Pinned in Task 1 (`Fit_DstFallBack_KeepsInstantLengths`).
2. **Cal.com answers with an empty `data` object, `null` fields, or a booking URL that isn't https** — Leaf shows no openings / drops that link, never throws into the UI. Pinned in Task 3 (`GetLinksAsync_NullAndUnsafeFields_DropsThoseLinks`, `GetOpenTimesAsync_EmptyData_ReturnsNone`).
3. **The secrets file can't be read right now when the hub loads** — Cal.com shows as not loaded (not disconnected), nothing is overwritten, and a later load succeeds. Pinned in Task 7 (`Load_SecretsUnavailable_KeepsNothingAndRetriesLater`).
4. **A guest name or link name with control, bidi or markup characters** — the copied HTML encodes it, the toast escapes it, the link query escapes it. Pinned in Task 9 (`Html_EscapesMessageAndLines`) and Task 3 (`TimeLink_EscapesGuestValues`).
5. **The open group's link was disconnected or deleted while the panel is open** — Copy falls back to plain text with the "link gone" message instead of building links to nothing. Pinned in Task 14 (view model step "link gone" + UI check in Task 18).

---

### Task 0: Spec Touch-Ups From Planning

Planning found three details the spec needs to state. Fix them before any code so the spec stays the authority.

**Files:**
- Modify: `docs/superpowers/specs/2026-10-12-scheduling-providers-design.md`

- [ ] **Step 1: Fake endpoint.** In section 10.2, replace "the UI test sets the server address to it, so no test reaches the real Cal.com. No new launch flag is added." with: "Under the existing `--fake-google <root>` flag, every provider's default server becomes `<root><provider id>/` (Cal.com: `<root>calcom/`), the way `GoogleEndpoints.ForFake` points Google at the fake. A typed server address still must be `https://`. No new launch flag is added, and nothing changes in a normal run."
- [ ] **Step 2: Setting name.** Replace every `NotifyBookings` with `BookingNotifications` (matches `InviteNotifications`). In section 11 replace the "Notifications card" row with: `| Notifications card | Booking notifications / Show a notification when someone books a time you shared. |`.
- [ ] **Step 3: Signatures.** In section 3.1 change the table rows to: `GetOpenTimesAsync(linkId, startUtc, endUtc, ct)`, `TimeLink(link, startUtc, zone, guests)`, `BlockLink(link, day, guests)`, `GetBookingsAsync(linkId, createdAfterUtc, ct)`, and `ISchedulingProvider` members `Id`, `Name` only (setup fields and creation move to `SchedulingProviderInfo`, Task 7). In section 6 change `DragMath.MoveRange(start, end, pointerOffset, pointer, zone)` to `DragMath.MoveRange(start, end, grabbedAt, pointerAt, zone)`.
- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-10-12-scheduling-providers-design.md
git commit -m "docs(scheduling): spec details settled while planning"
```

---

### Task 1: Slot Fitting Math

**Files:**
- Create: `src/LeafCalendar.Core/Scheduling/SlotFitting.cs`
- Test: `tests/LeafCalendar.Tests/SlotFittingTests.cs`

**Interfaces:**
- Consumes: `LeafCalendar.Core.Google.BusyRange(DateTimeOffset Start, DateTimeOffset End)`, `LeafCalendar.Core.People.BusyMath.Merge(IEnumerable<BusyRange>)`.
- Produces:
  - `public sealed record FitSummary(int Kept, int Minutes, int Stretched, int Removed, int NotOpenMinutes)` with `public static FitSummary None` and `public FitSummary Add(FitSummary other)`.
  - `public sealed record FitResult(IReadOnlyList<BusyRange> Ranges, FitSummary Summary)`.
  - `public static FitResult SlotFitting.Fit(BusyRange drag, IReadOnlyList<BusyRange> open)`.
  - `public static FitResult SlotFitting.FitAll(IReadOnlyList<BusyRange> ranges, IReadOnlyList<BusyRange> open)`.
  - `public static IReadOnlyList<BusyRange> SlotFitting.Split(BusyRange range, IReadOnlyList<BusyRange>? open, int minutes)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class SlotFittingTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(-5));

    private static DateTimeOffset At(int hour, int minute = 0) => Day.AddHours(hour).AddMinutes(minute);

    private static BusyRange R(int h1, int m1, int h2, int m2) => new(At(h1, m1), At(h2, m2));

    // 30-minute openings every 30 minutes from 9:00 to 11:00
    private static readonly IReadOnlyList<BusyRange> Every30 =
        [R(9, 0, 9, 30), R(9, 30, 10, 0), R(10, 0, 10, 30), R(10, 30, 11, 0)];

    [Fact]
    public void Fit_FullyCovered_KeepsEach()
    {
        var result = SlotFitting.Fit(R(9, 0, 10, 0), Every30);

        Assert.Equal([R(9, 0, 10, 0)], result.Ranges);
        Assert.Equal(new FitSummary(2, 30, 0, 0, 0), result.Summary);
    }

    [Fact]
    public void Fit_SpecExample_StretchesMoreThanHalfAndRemovesLess()
    {
        // 9:10–10:10: 9:00 covered 20 of 30 (stretched), 9:30 whole, 10:00 covered 10 of 30 (removed)
        var result = SlotFitting.Fit(R(9, 10, 10, 10), Every30);

        Assert.Equal([R(9, 0, 10, 0)], result.Ranges);
        Assert.Equal(new FitSummary(2, 30, 1, 1, 0), result.Summary);
    }

    [Fact]
    public void Fit_ExactlyHalf_IsRemoved()
    {
        var result = SlotFitting.Fit(R(9, 15, 9, 30), Every30);

        Assert.Empty(result.Ranges);
        Assert.Equal(new FitSummary(0, 0, 0, 1, 0), result.Summary);
    }

    [Fact]
    public void Fit_NoOpenTime_CountsNotOpenMinutes()
    {
        var result = SlotFitting.Fit(R(12, 0, 13, 0), Every30);

        Assert.Empty(result.Ranges);
        Assert.Equal(new FitSummary(0, 0, 0, 0, 60), result.Summary);
    }

    [Fact]
    public void Fit_PartlyOutsideOpenings_CountsTheGap()
    {
        // 10:30–11:30: 10:30 kept, 11:00–11:30 has no opening
        var result = SlotFitting.Fit(R(10, 30, 11, 30), Every30);

        Assert.Equal([R(10, 30, 11, 0)], result.Ranges);
        Assert.Equal(new FitSummary(1, 30, 0, 0, 30), result.Summary);
    }

    [Fact]
    public void Fit_IntervalShorterThanLength_DoesNotDoubleUp()
    {
        // 30-minute openings every 15 minutes: 9:00, 9:15, 9:30, 9:45, 10:00
        IReadOnlyList<BusyRange> every15 = [R(9, 0, 9, 30), R(9, 15, 9, 45), R(9, 30, 10, 0), R(9, 45, 10, 15), R(10, 0, 10, 30)];

        var result = SlotFitting.Fit(R(9, 0, 10, 0), every15);

        Assert.Equal([R(9, 0, 10, 0)], result.Ranges);
        Assert.Equal(2, result.Summary.Kept);
        Assert.Equal(0, result.Summary.Removed);
    }

    [Fact]
    public void Fit_DstFallBack_KeepsInstantLengths()
    {
        // 2026-11-01 in Chicago: 1:00–2:00 AM happens twice. Two 30-minute openings across the repeat, by instant
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var first = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        IReadOnlyList<BusyRange> open = [new(first, first.AddMinutes(30)), new(first.AddMinutes(30), first.AddMinutes(60))];

        var result = SlotFitting.Fit(new BusyRange(first, first.AddMinutes(60)), open);

        Assert.Equal([new BusyRange(first, first.AddMinutes(60))], result.Ranges);
        Assert.Equal(30, result.Summary.Minutes);
        Assert.NotNull(zone);
    }

    [Fact]
    public void FitAll_AddsSummariesAndMerges()
    {
        var result = SlotFitting.FitAll([R(9, 10, 9, 40), R(10, 0, 10, 30)], Every30);

        Assert.Equal([R(9, 0, 9, 30), R(10, 0, 10, 30)], result.Ranges);
        Assert.Equal(2, result.Summary.Kept);
        Assert.Equal(1, result.Summary.Stretched);
        Assert.Equal(1, result.Summary.Removed);
    }

    [Fact]
    public void Split_ByOpenTimes_TakesWholeOnesEarliestFirst()
    {
        var pieces = SlotFitting.Split(R(9, 0, 10, 0), Every30, 30);

        Assert.Equal([R(9, 0, 9, 30), R(9, 30, 10, 0)], pieces);
    }

    [Fact]
    public void Split_Offline_StepsByLength()
    {
        var pieces = SlotFitting.Split(R(9, 0, 10, 15), null, 30);

        Assert.Equal([R(9, 0, 9, 30), R(9, 30, 10, 0)], pieces);
    }

    [Fact]
    public void Split_CrossMidnight_UsesInstants()
    {
        var start = Day.AddHours(23).AddMinutes(30);
        var pieces = SlotFitting.Split(new BusyRange(start, start.AddHours(1)), null, 30);

        Assert.Equal([new BusyRange(start, start.AddMinutes(30)), new BusyRange(start.AddMinutes(30), start.AddHours(1))], pieces);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*SlotFittingTests"`
Expected: build error, `SlotFitting` does not exist.

- [ ] **Step 3: Implement**

```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Scheduling;

/// <summary>What fitting a pick to a link's bookable times did, for the messages over the calendar.</summary>
/// <param name="Kept">Bookable times kept.</param>
/// <param name="Minutes">Length of a kept time in minutes (0 when none was kept).</param>
/// <param name="Stretched">Times picked more than half and stretched to their full length.</param>
/// <param name="Removed">Times picked half or less and removed.</param>
/// <param name="NotOpenMinutes">Minutes of the pick with no bookable time at all.</param>
public sealed record FitSummary(int Kept, int Minutes, int Stretched, int Removed, int NotOpenMinutes)
{
    /// <summary>Nothing fitted.</summary>
    public static FitSummary None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Both summaries together (the first nonzero length wins).</summary>
    public FitSummary Add(FitSummary other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Kept + other.Kept, Minutes != 0 ? Minutes : other.Minutes, Stretched + other.Stretched, Removed + other.Removed, NotOpenMinutes + other.NotOpenMinutes);
    }
}

/// <summary>A pick fitted to bookable times: the ranges to keep (merged, in order) and what happened.</summary>
public sealed record FitResult(IReadOnlyList<BusyRange> Ranges, FitSummary Summary);

/// <summary>
/// Fits picked times to a scheduling link's bookable times (spec 3.5), and splits saved ranges into bookable times for
/// the copied links.
/// </summary>
/// <remarks>
/// All math is on instants, so DST days and midnight need no special cases. A bookable time the pick covers fully is
/// kept; one it covers more than half of (strictly) is stretched to its full length; one covered half or less is
/// removed. Bookable times are taken earliest first, and one that overlaps a kept time is skipped, so a link that offers
/// 30-minute times every 15 minutes never doubles up.
/// </remarks>
public static class SlotFitting
{
    /// <summary>Fits one dragged range to <paramref name="open"/> (the link's bookable times, any order).</summary>
    public static FitResult Fit(BusyRange drag, IReadOnlyList<BusyRange> open)
    {
        ArgumentNullException.ThrowIfNull(drag);
        ArgumentNullException.ThrowIfNull(open);

        var touching = open.Where(s => s.End > drag.Start && s.Start < drag.End).OrderBy(s => s.Start).ToList();

        // Kept: Whole Or More Than Half, Earliest First, No Overlaps
        var kept = new List<BusyRange>();
        var stretched = 0;
        foreach (var slot in touching)
        {
            if (kept.Count > 0 && slot.Start < kept[^1].End)
            {
                continue;
            }

            var covered = Covered(slot, drag);
            var length = slot.End - slot.Start;
            if (covered >= length)
            {
                kept.Add(slot);
            }
            else if (covered * 2 > length)
            {
                kept.Add(slot);
                stretched++;
            }
        }

        // Removed: Too Little Picked, Counted Once Per Stretch That No Kept Time Covers
        var removed = new List<BusyRange>();
        foreach (var slot in touching)
        {
            var covered = Covered(slot, drag);
            if (covered * 2 > slot.End - slot.Start || kept.Any(k => Overlaps(k, slot)) || removed.Any(r => Overlaps(r, slot)))
            {
                continue;
            }

            removed.Add(slot);
        }

        // Not Open: Parts Of The Pick No Bookable Time Touches
        var openCovered = BusyMath.Merge(touching).Sum(s => Covered(s, drag).TotalMinutes);
        var notOpen = (int)Math.Round((drag.End - drag.Start).TotalMinutes - openCovered);

        var minutes = kept.Count > 0 ? (int)(kept[0].End - kept[0].Start).TotalMinutes : 0;
        return new FitResult(BusyMath.Merge(kept), new FitSummary(kept.Count, minutes, stretched, removed.Count, notOpen));
    }

    /// <summary>Fits every range (a saved group's times after a link change), with one summary for all of them.</summary>
    public static FitResult FitAll(IReadOnlyList<BusyRange> ranges, IReadOnlyList<BusyRange> open)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var all = new List<BusyRange>();
        var summary = FitSummary.None;
        foreach (var range in ranges)
        {
            var fit = Fit(range, open);
            all.AddRange(fit.Ranges);
            summary = summary.Add(fit.Summary);
        }

        return new FitResult(BusyMath.Merge(all), summary);
    }

    /// <summary>
    /// The bookable times inside a saved range, earliest first without overlap: the open times that fit wholly inside it,
    /// or, with <paramref name="open"/> null (Leaf couldn't ask), steps of <paramref name="minutes"/> from its start.
    /// </summary>
    public static IReadOnlyList<BusyRange> Split(BusyRange range, IReadOnlyList<BusyRange>? open, int minutes)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);

        var pieces = new List<BusyRange>();
        if (open is null)
        {
            var step = TimeSpan.FromMinutes(minutes);
            for (var t = range.Start; t + step <= range.End; t += step)
            {
                pieces.Add(new BusyRange(t, t + step));
            }

            return pieces;
        }

        foreach (var slot in open.Where(s => s.Start >= range.Start && s.End <= range.End).OrderBy(s => s.Start))
        {
            if (pieces.Count == 0 || slot.Start >= pieces[^1].End)
            {
                pieces.Add(slot);
            }
        }

        return pieces;
    }

    private static TimeSpan Covered(BusyRange slot, BusyRange drag)
    {
        var start = slot.Start > drag.Start ? slot.Start : drag.Start;
        var end = slot.End < drag.End ? slot.End : drag.End;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private static bool Overlaps(BusyRange a, BusyRange b) => a.Start < b.End && b.Start < a.End;
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*SlotFittingTests"`
Expected: all pass. If `BusyMath.Merge` takes `IReadOnlyList<BusyRange>` instead of `IEnumerable`, pass `[.. touching]` / `[.. kept]`.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Scheduling/SlotFitting.cs tests/LeafCalendar.Tests/SlotFittingTests.cs
git commit -m "feat(scheduling): picks fit to bookable times, stretching ones picked more than half and removing the rest"
```

---

### Task 2: Fit Messages

The text over the calendar after a fit, kept in Core so it's tested. Wording is spec section 11 verbatim.

**Files:**
- Create: `src/LeafCalendar.Core/Scheduling/FitMessages.cs`
- Test: `tests/LeafCalendar.Tests/FitMessagesTests.cs`

**Interfaces:**
- Consumes: `FitSummary` (Task 1).
- Produces: `public static IReadOnlyList<string> FitMessages.For(FitSummary summary, string providerName)`; `public static string FitMessages.Duration(int minutes)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class FitMessagesTests
{
    [Fact]
    public void For_SplitOnly_OneMessage()
    {
        Assert.Equal(["Split into 4 times of 30 minutes."], FitMessages.For(new FitSummary(4, 30, 0, 0, 0), "Cal.com"));
    }

    [Fact]
    public void For_Everything_OneMessageEach_InOrder()
    {
        var messages = FitMessages.For(new FitSummary(2, 30, 1, 1, 90), "Cal.com");

        Assert.Equal(
            [
                "Split into 2 times of 30 minutes.",
                "Stretched 1 times to their full 30 minutes, since you picked more than half of each.",
                "Removed 1 times, since you picked half or less of each.",
                "1 hour 30 minutes wasn't open on Cal.com.",
            ],
            messages);
    }

    [Fact]
    public void For_NothingKept_SaysNothingOpen()
    {
        Assert.Equal(["None of that time is open on Cal.com."], FitMessages.For(new FitSummary(0, 0, 0, 0, 60), "Cal.com"));
    }

    [Fact]
    public void For_NothingKeptButSomeRemoved_SaysRemoved()
    {
        Assert.Equal(["Removed 1 times, since you picked half or less of each."], FitMessages.For(new FitSummary(0, 0, 0, 1, 0), "Cal.com"));
    }

    [Theory]
    [InlineData(45, "45 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(150, "2 hours 30 minutes")]
    public void Duration_Reads(int minutes, string expected) => Assert.Equal(expected, FitMessages.Duration(minutes));
}
```

> Note for the owner (flag in the report): "Stretched 1 times" / "Removed 1 times" read oddly at one. The approved wording uses "{n} times"; ask whether to say "1 time" at one. Do not change it without approval.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*FitMessagesTests"`
Expected: build error, `FitMessages` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Globalization;

namespace LeafCalendar.Core.Scheduling;

/// <summary>The messages shown over the calendar after a pick is fitted to a link (spec section 11), one per fact.</summary>
public static class FitMessages
{
    /// <summary>The messages for <paramref name="summary"/>, in the order they're stacked (split, stretched, removed, not open).</summary>
    public static IReadOnlyList<string> For(FitSummary summary, string providerName)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(providerName);

        var c = CultureInfo.InvariantCulture;
        var messages = new List<string>();

        // Nothing Kept And Nothing Removed: none of it was bookable
        if (summary.Kept == 0 && summary.Removed == 0)
        {
            messages.Add($"None of that time is open on {providerName}.");
            return messages;
        }

        if (summary.Kept > 0)
        {
            messages.Add(string.Create(c, $"Split into {summary.Kept} times of {summary.Minutes} minutes."));
        }

        if (summary.Stretched > 0)
        {
            messages.Add(string.Create(c, $"Stretched {summary.Stretched} times to their full {summary.Minutes} minutes, since you picked more than half of each."));
        }

        if (summary.Removed > 0)
        {
            messages.Add(string.Create(c, $"Removed {summary.Removed} times, since you picked half or less of each."));
        }

        if (summary.NotOpenMinutes > 0 && summary.Kept > 0)
        {
            messages.Add($"{Duration(summary.NotOpenMinutes)} wasn't open on {providerName}.");
        }

        return messages;
    }

    /// <summary>"45 minutes", "1 hour", "2 hours 30 minutes".</summary>
    public static string Duration(int minutes)
    {
        var c = CultureInfo.InvariantCulture;
        var hours = minutes / 60;
        var rest = minutes % 60;
        var hourText = hours == 0 ? "" : hours == 1 ? "1 hour" : string.Create(c, $"{hours} hours");
        var minuteText = rest == 0 ? "" : string.Create(c, $"{rest} minutes");
        return string.Join(" ", new[] { hourText, minuteText }.Where(s => s.Length > 0));
    }
}
```

- [ ] **Step 4: Run to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*FitMessagesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Scheduling/FitMessages.cs tests/LeafCalendar.Tests/FitMessagesTests.cs
git commit -m "feat(scheduling): messages that say what fitting a pick did"
```

---

### Task 3: Provider Contracts And The Cal.com Client

**Files:**
- Create: `src/LeafCalendar.Core/Scheduling/SchedulingModels.cs` (records and exceptions)
- Create: `src/LeafCalendar.Core/Scheduling/SchedulingActions.cs` (the interfaces)
- Create: `src/LeafCalendar.Core/Scheduling/CalComModels.cs` (wire JSON records, internal)
- Create: `src/LeafCalendar.Core/Scheduling/CalComJsonContext.cs`
- Create: `src/LeafCalendar.Core/Scheduling/CalComProvider.cs`
- Create: `tests/LeafCalendar.Tests/Fixtures/calcom-event-types.json`, `calcom-slots.json`, `calcom-bookings.json`, `calcom-booking-created.json`
- Test: `tests/LeafCalendar.Tests/CalComProviderTests.cs`, `tests/LeafCalendar.Tests/CalComLinkTests.cs`

**Interfaces:**
- Consumes: `BusyRange`, `Contact(string Name, string Email)` (`LeafCalendar.Core.People`), `DisplayText.Clean(string?, int)` (`LeafCalendar.Core.Tray`), `LinkSafety.CanLaunch(Uri)` (`LeafCalendar.Core.Events`), `TimeZoneCatalog.IanaId(TimeZoneInfo)` (`LeafCalendar.Core.Views`).
- Produces (all in namespace `LeafCalendar.Core.Scheduling`):

```csharp
public sealed record SchedulingLink(string ProviderId, string Id, string Name, int Minutes, Uri BookingUrl, bool AllowsGuests);
public sealed record SchedulingBooking(string Id, DateTimeOffset Start, DateTimeOffset Created, string AttendeeName);
public sealed class SchedulingAuthException : Exception;            // key refused (401/403)
public sealed class SchedulingUnavailableException : Exception      // offline, 5xx, 429, unexpected answer
{ public TimeSpan? RetryAfter { get; } }
public sealed class SchedulingSlotTakenException : Exception;       // the time was booked or closed meanwhile

public interface ISchedulingProvider { string Id { get; } string Name { get; } }
public interface ILinkSource { Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct); }
public interface IOpenTimesSource { Task<IReadOnlyList<BusyRange>> GetOpenTimesAsync(string linkId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct); }
public interface ITimeLinkBuilder { Uri TimeLink(SchedulingLink link, DateTimeOffset startUtc, TimeZoneInfo zone, IReadOnlyList<Contact> guests); }
public interface IBlockLinkBuilder { Uri BlockLink(SchedulingLink link, DateOnly day, IReadOnlyList<Contact> guests); }
public interface IBookingSource { Task<IReadOnlyList<SchedulingBooking>> GetBookingsAsync(string linkId, DateTimeOffset createdAfterUtc, CancellationToken ct); }
public interface IBookingCreator { Task<SchedulingBooking> BookAsync(SchedulingLink link, DateTimeOffset startUtc, IReadOnlyList<Contact> guests, TimeZoneInfo zone, CancellationToken ct); }

public sealed class CalComProvider(HttpClient http, string apiKey, Uri apiBase) : ISchedulingProvider, ILinkSource, IOpenTimesSource, ITimeLinkBuilder, IBlockLinkBuilder, IBookingSource, IBookingCreator
{ public const string ProviderId = "calcom"; public static Uri DefaultApiBase { get; } /* https://api.cal.com/ */ }
```

- [ ] **Step 1: Add fixtures** (recorded shapes from Cal.com's v2 docs; Task 19 re-checks them against a live account)

`tests/LeafCalendar.Tests/Fixtures/calcom-event-types.json`:
```json
{
  "status": "success",
  "data": [
    { "id": 101, "title": "30 min meeting", "slug": "30min", "lengthInMinutes": 30, "bookingUrl": "https://cal.com/leaf-tester/30min", "hidden": false, "disableGuests": false },
    { "id": 102, "title": "Quick chat", "slug": "15min", "lengthInMinutes": 15, "bookingUrl": "https://cal.com/leaf-tester/15min", "hidden": false, "disableGuests": true },
    { "id": 103, "title": "Secret", "slug": "secret", "lengthInMinutes": 60, "bookingUrl": "https://cal.com/leaf-tester/secret", "hidden": true, "disableGuests": false },
    { "id": 104, "title": "Plain http", "slug": "http", "lengthInMinutes": 30, "bookingUrl": "http://cal.com/leaf-tester/http", "hidden": false },
    { "id": 105, "title": null, "slug": "nulls", "lengthInMinutes": 30, "bookingUrl": null, "hidden": false }
  ]
}
```

`calcom-slots.json`:
```json
{
  "status": "success",
  "data": {
    "2026-10-13": [
      { "start": "2026-10-13T09:00:00.000-05:00", "end": "2026-10-13T09:30:00.000-05:00" },
      { "start": "2026-10-13T09:30:00.000-05:00", "end": "2026-10-13T10:00:00.000-05:00" }
    ],
    "2026-10-14": [
      { "start": "2026-10-14T14:00:00.000Z", "end": "2026-10-14T14:30:00.000Z" }
    ]
  }
}
```

`calcom-bookings.json`:
```json
{
  "status": "success",
  "data": [
    { "uid": "bk1", "start": "2026-10-13T14:15:00.000Z", "end": "2026-10-13T14:45:00.000Z", "createdAt": "2026-10-12T18:00:00.000Z", "eventTypeId": 101, "status": "accepted", "attendees": [ { "name": "Jane Doe", "email": "jane@example.com" } ] },
    { "uid": "bk2", "start": "2026-10-14T14:00:00.000Z", "end": "2026-10-14T14:30:00.000Z", "createdAt": "2026-10-12T19:00:00.000Z", "eventTypeId": 101, "status": "accepted", "attendees": [] }
  ]
}
```

`calcom-booking-created.json`:
```json
{
  "status": "success",
  "data": { "uid": "bk9", "start": "2026-10-13T14:00:00.000Z", "end": "2026-10-13T14:30:00.000Z", "createdAt": "2026-10-12T20:00:00.000Z", "eventTypeId": 101, "status": "accepted", "attendees": [ { "name": "Jane Doe", "email": "jane@example.com" } ] }
}
```

Make sure the test project copies `Fixtures\calcom-*.json` to output (check how the existing fixtures are included in `tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`; a wildcard `Fixtures\**` already covers them if present).

- [ ] **Step 2: Write the failing tests**

`tests/LeafCalendar.Tests/CalComProviderTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public class CalComProviderTests
{
    private const string Base = "https://api.cal.com/";
    private const string Key = "cal_live_test123";
    private readonly FakeHttpHandler _cal = new();

    private CalComProvider Create() => new(new HttpClient(_cal), Key, new Uri(Base));

    [Fact]
    public async Task GetLinksAsync_SendsKeyAndVersion_DropsHiddenUnsafeAndNull()
    {
        _cal.On(HttpMethod.Get, Base + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));

        var links = await Create().GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["101", "102"], links.Select(l => l.Id));
        Assert.Equal("30 min meeting", links[0].Name);
        Assert.Equal(30, links[0].Minutes);
        Assert.True(links[0].AllowsGuests);
        Assert.False(links[1].AllowsGuests);
        Assert.Equal(Key, _cal.Last.BearerToken);
        Assert.Equal("2024-06-14", _cal.Last.Header("cal-api-version"));
        Assert.DoesNotContain(Key, _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLinksAsync_NullAndUnsafeFields_DropsThoseLinks()
    {
        _cal.On(HttpMethod.Get, Base + "v2/event-types", HttpStatusCode.OK, """{"status":"success","data":[{"id":7,"title":"x","lengthInMinutes":30,"bookingUrl":"https://user@cal.com/x","hidden":false},{"id":8}]}""");

        Assert.Empty(await Create().GetLinksAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetLinksAsync_KeyRefused_ThrowsAuth(HttpStatusCode status)
    {
        _cal.On(HttpMethod.Get, Base + "v2/event-types", status, """{"status":"error"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create().GetLinksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLinksAsync_Offline_ThrowsUnavailable()
    {
        _cal.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetLinksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLinksAsync_RateLimited_ThrowsUnavailableWithRetryAfter()
    {
        _cal.On(_ => true, _ =>
        {
            var response = FakeHttpHandler.Json((HttpStatusCode)429, """{"status":"error"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });

        var error = await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetLinksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(90), error.RetryAfter);
    }

    [Fact]
    public async Task GetOpenTimesAsync_SendsRangeQuery_ParsesOffsets()
    {
        _cal.On(HttpMethod.Get, Base + "v2/slots", HttpStatusCode.OK, Fixture.Read("calcom-slots.json"));
        var start = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        var open = await Create().GetOpenTimesAsync("101", start, start.AddDays(2), TestContext.Current.CancellationToken);

        Assert.Equal(3, open.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero), open[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 14, 30, 0, TimeSpan.Zero), open[0].End);
        Assert.Equal("101", _cal.Last.Query("eventTypeId"));
        Assert.Equal("2026-10-13T00:00:00.000Z", _cal.Last.Query("start"));
        Assert.Equal("2026-10-15T00:00:00.000Z", _cal.Last.Query("end"));
        Assert.Equal("range", _cal.Last.Query("format"));
        Assert.Equal("2024-09-04", _cal.Last.Header("cal-api-version"));
    }

    [Fact]
    public async Task GetOpenTimesAsync_EmptyData_ReturnsNone()
    {
        _cal.On(HttpMethod.Get, Base + "v2/slots", HttpStatusCode.OK, """{"status":"success","data":{}}""");
        var start = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        Assert.Empty(await Create().GetOpenTimesAsync("101", start, start.AddDays(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBookingsAsync_SendsFilters_ParsesAttendee()
    {
        _cal.On(HttpMethod.Get, Base + "v2/bookings", HttpStatusCode.OK, Fixture.Read("calcom-bookings.json"));
        var after = new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);

        var bookings = await Create().GetBookingsAsync("101", after, TestContext.Current.CancellationToken);

        Assert.Equal(["bk1", "bk2"], bookings.Select(b => b.Id));
        Assert.Equal("Jane Doe", bookings[0].AttendeeName);
        Assert.Equal("", bookings[1].AttendeeName);
        Assert.Equal("101", _cal.Last.Query("eventTypeId"));
        Assert.Equal("2026-10-12T17:00:00.000Z", _cal.Last.Query("afterCreatedAt"));
        Assert.Equal("upcoming", _cal.Last.Query("status"));
        Assert.Equal("2024-08-13", _cal.Last.Header("cal-api-version"));
    }

    [Fact]
    public async Task BookAsync_SendsBookerAndGuests_WhenAllowed()
    {
        _cal.On(HttpMethod.Post, Base + "v2/bookings", HttpStatusCode.Created, Fixture.Read("calcom-booking-created.json"));
        var link = new SchedulingLink("calcom", "101", "30 min meeting", 30, new Uri("https://cal.com/leaf-tester/30min"), AllowsGuests: true);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

        var booking = await Create().BookAsync(link, new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero), [new("Jane Doe", "jane@example.com"), new("", "sam@example.com")], zone, TestContext.Current.CancellationToken);

        Assert.Equal("bk9", booking.Id);
        var body = System.Text.Json.Nodes.JsonNode.Parse(_cal.Last.Body!)!;
        Assert.Equal("2026-10-13T14:00:00.000Z", (string?)body["start"]);
        Assert.Equal(101, (int?)body["eventTypeId"]);
        Assert.Equal("Jane Doe", (string?)body["attendee"]!["name"]);
        Assert.Equal("jane@example.com", (string?)body["attendee"]!["email"]);
        Assert.Equal("America/Chicago", (string?)body["attendee"]!["timeZone"]);
        Assert.Equal(["sam@example.com"], body["guests"]!.AsArray().Select(g => (string?)g));
    }

    [Fact]
    public async Task BookAsync_NoNameNoGuestsAllowed_UsesEmailAsNameAndSendsNoGuests()
    {
        _cal.On(HttpMethod.Post, Base + "v2/bookings", HttpStatusCode.Created, Fixture.Read("calcom-booking-created.json"));
        var link = new SchedulingLink("calcom", "102", "Quick chat", 15, new Uri("https://cal.com/leaf-tester/15min"), AllowsGuests: false);

        await Create().BookAsync(link, DateTimeOffset.UtcNow, [new("", "jane@example.com"), new("", "sam@example.com")], TimeZoneInfo.Utc, TestContext.Current.CancellationToken);

        var body = System.Text.Json.Nodes.JsonNode.Parse(_cal.Last.Body!)!;
        Assert.Equal("jane@example.com", (string?)body["attendee"]!["name"]);
        Assert.Null(body["guests"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"status":"error"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"status":"error","error":{"message":"User either already has booking at this time or is not available"}}""")]
    public async Task BookAsync_Taken_ThrowsSlotTaken(HttpStatusCode status, string body)
    {
        _cal.On(HttpMethod.Post, Base + "v2/bookings", status, body);
        var link = new SchedulingLink("calcom", "101", "x", 30, new Uri("https://cal.com/x"), true);

        await Assert.ThrowsAsync<SchedulingSlotTakenException>(() => Create().BookAsync(link, DateTimeOffset.UtcNow, [new("", "a@example.com")], TimeZoneInfo.Utc, TestContext.Current.CancellationToken));
    }
}
```

`tests/LeafCalendar.Tests/CalComLinkTests.cs`:
```csharp
using LeafCalendar.Core.Http;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class CalComLinkTests
{
    private static readonly CalComProvider Provider = new(new HttpClient(), "cal_live_x", CalComProvider.DefaultApiBase);
    private static readonly SchedulingLink WithGuests = new("calcom", "101", "30 min", 30, new Uri("https://cal.com/leaf-tester/30min"), true);
    private static readonly SchedulingLink NoGuests = WithGuests with { AllowsGuests = false };
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void TimeLink_DateInShareZone_SlotInUtc()
    {
        // 03:30 UTC on the 14th is 10:30 PM on the 13th in Chicago
        var uri = Provider.TimeLink(WithGuests, new DateTimeOffset(2026, 10, 14, 3, 30, 0, TimeSpan.Zero), Chicago, []);
        var query = QueryString.Parse(uri.Query);

        Assert.Equal("https://cal.com/leaf-tester/30min", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("2026-10-13", query["date"]);
        Assert.Equal("2026-10-14T03:30:00.000Z", query["slot"]);
        Assert.False(query.ContainsKey("email"));
    }

    [Fact]
    public void TimeLink_SeveralGuests_FirstIsBookerRestAreGuests()
    {
        var uri = Provider.TimeLink(WithGuests, DateTimeOffset.UnixEpoch, Chicago, [new("Jane Doe", "jane@example.com"), new("", "sam@example.com"), new("", "lee@example.com")]);

        Assert.Contains("name=Jane%20Doe", uri.Query, StringComparison.Ordinal);
        Assert.Contains("email=jane%40example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("guests=sam%40example.com&guests=lee%40example.com", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLink_GuestsNotAllowed_OnlyBooker()
    {
        var uri = Provider.TimeLink(NoGuests, DateTimeOffset.UnixEpoch, Chicago, [new("", "jane@example.com"), new("", "sam@example.com")]);

        Assert.Contains("email=jane%40example.com", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("guests=", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("name=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLink_EscapesGuestValues()
    {
        var uri = Provider.TimeLink(WithGuests, DateTimeOffset.UnixEpoch, Chicago, [new("A&B=C <x>", "a+b@example.com")]);

        Assert.Contains("name=A%26B%3DC%20%3Cx%3E", uri.Query, StringComparison.Ordinal);
        Assert.Contains("email=a%2Bb%40example.com", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLink_BookingUrlWithQuery_AppendsWithAmpersand()
    {
        var link = WithGuests with { BookingUrl = new Uri("https://cal.com/team/x?layout=week") };

        var uri = Provider.TimeLink(link, DateTimeOffset.UnixEpoch, Chicago, []);

        Assert.StartsWith("?layout=week&date=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockLink_DateOnly()
    {
        var uri = Provider.BlockLink(WithGuests, new DateOnly(2026, 10, 13), [new("", "jane@example.com")]);

        Assert.Equal("?date=2026-10-13&email=jane%40example.com", uri.Query);
    }
}
```

`RecordedRequest` needs a `Header(name)` accessor. Add it in `tests/LeafCalendar.Tests/Support/FakeHttpHandler.cs`: a `public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();` property, `public string? Header(string name) => Headers.GetValueOrDefault(name);`, and in `SendAsync` fill it with `request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)`.

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*CalCom*"`
Expected: build errors for the missing types.

- [ ] **Step 4: Implement the models and interfaces**

`src/LeafCalendar.Core/Scheduling/SchedulingModels.cs`:
```csharp
namespace LeafCalendar.Core.Scheduling;

/// <summary>A scheduling link (Cal.com calls these event types): what it's called, how long a booking is, and where it books.</summary>
/// <param name="ProviderId">The provider it belongs to (<see cref="ISchedulingProvider.Id"/>).</param>
/// <param name="Id">The provider's ID for it.</param>
/// <param name="Name">Its name, cleaned and capped.</param>
/// <param name="Minutes">Length of a booking.</param>
/// <param name="BookingUrl">Its booking page (always https and approved by <see cref="Events.LinkSafety.CanLaunch"/>).</param>
/// <param name="AllowsGuests">Whether people besides the booker can be added (Cal.com: <c>disableGuests</c> is false).</param>
public sealed record SchedulingLink(string ProviderId, string Id, string Name, int Minutes, Uri BookingUrl, bool AllowsGuests);

/// <summary>A booking someone made on a link.</summary>
/// <param name="Id">The provider's ID for it.</param>
/// <param name="Start">When the booked time starts.</param>
/// <param name="Created">When it was booked.</param>
/// <param name="AttendeeName">The booker's name, cleaned and capped; empty when the provider gave none.</param>
public sealed record SchedulingBooking(string Id, DateTimeOffset Start, DateTimeOffset Created, string AttendeeName);

/// <summary>The provider refused the saved key (it was revoked or is wrong). The provider needs reconnecting.</summary>
public sealed class SchedulingAuthException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingAuthException()
    {
    }

    /// <summary>Creates the exception with a message (never the key).</summary>
    public SchedulingAuthException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingAuthException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The provider couldn't be reached or answered with an error that may pass (offline, 5xx, 429, a malformed answer).</summary>
public sealed class SchedulingUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SchedulingUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception for a rate limit, with how long the provider asked to wait.</summary>
    public SchedulingUnavailableException(string message, TimeSpan? retryAfter)
        : base(message) => RetryAfter = retryAfter;

    /// <summary>How long the provider asked Leaf to wait (a 429's <c>Retry-After</c>), or null.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The time asked for was booked or closed in the meantime.</summary>
public sealed class SchedulingSlotTakenException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingSlotTakenException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SchedulingSlotTakenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingSlotTakenException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
```

`src/LeafCalendar.Core/Scheduling/SchedulingActions.cs`:
```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// A scheduling provider (spec 3.1). Every provider implements this; each action it supports is one more interface
/// (<see cref="ILinkSource"/>, <see cref="IOpenTimesSource"/>, <see cref="ITimeLinkBuilder"/>, <see cref="IBlockLinkBuilder"/>,
/// <see cref="IBookingSource"/>, <see cref="IBookingCreator"/>), and Leaf shows only the UI for the actions it finds.
/// </summary>
public interface ISchedulingProvider
{
    /// <summary>A short lowercase ID, stable forever (stored in settings, secrets and saved groups), such as <c>calcom</c>.</summary>
    string Id { get; }

    /// <summary>The name shown in the UI, such as "Cal.com".</summary>
    string Name { get; }
}

/// <summary>Lists your scheduling links.</summary>
public interface ILinkSource
{
    /// <summary>Your links, in the provider's order, unsafe or incomplete ones left out.</summary>
    /// <exception cref="SchedulingAuthException">The key was refused.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed.</exception>
    Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct);
}

/// <summary>Reads a link's bookable times.</summary>
public interface IOpenTimesSource
{
    /// <summary>Each bookable start and end for the link between two instants, in order.</summary>
    /// <exception cref="SchedulingAuthException">The key was refused.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed.</exception>
    Task<IReadOnlyList<BusyRange>> GetOpenTimesAsync(string linkId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct);
}

/// <summary>Builds a link that opens one exact time, ready to book.</summary>
public interface ITimeLinkBuilder
{
    /// <summary>
    /// The booking page on <paramref name="startUtc"/>, with the first guest as the booker and, when the link allows them,
    /// the rest as guests. <paramref name="zone"/> is the share's zone (it picks the day the page opens on).
    /// </summary>
    Uri TimeLink(SchedulingLink link, DateTimeOffset startUtc, TimeZoneInfo zone, IReadOnlyList<Contact> guests);
}

/// <summary>Builds a link that opens a day, for the guest to pick a time in it.</summary>
public interface IBlockLinkBuilder
{
    /// <summary>The booking page on <paramref name="day"/>, with the guests filled in as for <see cref="ITimeLinkBuilder"/>.</summary>
    Uri BlockLink(SchedulingLink link, DateOnly day, IReadOnlyList<Contact> guests);
}

/// <summary>Reads the bookings people made on a link.</summary>
public interface IBookingSource
{
    /// <summary>Upcoming bookings on the link made after <paramref name="createdAfterUtc"/>.</summary>
    /// <exception cref="SchedulingAuthException">The key was refused.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed.</exception>
    Task<IReadOnlyList<SchedulingBooking>> GetBookingsAsync(string linkId, DateTimeOffset createdAfterUtc, CancellationToken ct);
}

/// <summary>Books a time on a link for your guests (Approve…).</summary>
public interface IBookingCreator
{
    /// <summary>
    /// Books <paramref name="startUtc"/> with the first guest as the booker and, when the link allows them, the rest as
    /// guests. The provider sends its usual confirmation emails.
    /// </summary>
    /// <exception cref="SchedulingSlotTakenException">The time isn't open anymore.</exception>
    /// <exception cref="SchedulingAuthException">The key was refused.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or failed; nothing was booked as far as Leaf knows.</exception>
    Task<SchedulingBooking> BookAsync(SchedulingLink link, DateTimeOffset startUtc, IReadOnlyList<Contact> guests, TimeZoneInfo zone, CancellationToken ct);
}
```

- [ ] **Step 5: Implement the Cal.com wire models and JSON context**

`src/LeafCalendar.Core/Scheduling/CalComModels.cs`:
```csharp
namespace LeafCalendar.Core.Scheduling;

// Cal.com v2 answers: { "status": "...", "data": ... }. Every field is nullable, since nothing from Cal.com is trusted
internal sealed record CalComEnvelope<T>(string? Status, T? Data);

internal sealed record CalComEventType(long? Id, string? Title, int? LengthInMinutes, string? BookingUrl, bool? Hidden, bool? DisableGuests);

internal sealed record CalComSlot(DateTimeOffset? Start, DateTimeOffset? End);

internal sealed record CalComAttendee(string? Name, string? Email);

internal sealed record CalComBooking(string? Uid, DateTimeOffset? Start, DateTimeOffset? CreatedAt, long? EventTypeId, List<CalComAttendee>? Attendees);

internal sealed record CalComBookingRequest(string Start, long EventTypeId, CalComBookingAttendee Attendee, List<string>? Guests);

internal sealed record CalComBookingAttendee(string Name, string Email, string TimeZone);
```

`src/LeafCalendar.Core/Scheduling/CalComJsonContext.cs`:
```csharp
using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Scheduling;

/// <summary>Source-generated JSON for Cal.com's v2 API (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CalComEnvelope<List<CalComEventType>>))]
[JsonSerializable(typeof(CalComEnvelope<Dictionary<string, List<CalComSlot>>>))]
[JsonSerializable(typeof(CalComEnvelope<List<CalComBooking>>))]
[JsonSerializable(typeof(CalComEnvelope<CalComBooking>))]
[JsonSerializable(typeof(CalComBookingRequest))]
internal sealed partial class CalComJsonContext : JsonSerializerContext;
```

- [ ] **Step 6: Implement `CalComProvider`**

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// Cal.com, through its v2 REST API: your links (event types), their bookable times, bookings made on them, booking a
/// time, and the booking page links Leaf copies.
/// </summary>
/// <remarks>
/// Every call sends the API key as <c>Authorization: Bearer</c> to <paramref name="apiBase"/> only (https; Cal.com's
/// own server unless you self-host), never in a URL. Each endpoint pins its <c>cal-api-version</c>. Names are cleaned and
/// capped, and a link whose booking page isn't https or fails <see cref="LinkSafety.CanLaunch"/> is left out.
/// </remarks>
/// <seealso href="https://cal.com/docs/api-reference/v2/introduction"/>
public sealed class CalComProvider(HttpClient http, string apiKey, Uri apiBase)
    : ISchedulingProvider, ILinkSource, IOpenTimesSource, ITimeLinkBuilder, IBlockLinkBuilder, IBookingSource, IBookingCreator
{
    /// <summary>The provider's ID.</summary>
    public const string ProviderId = "calcom";

    /// <summary>The longest link or attendee name kept.</summary>
    public const int MaxName = 100;

    private const string EventTypesVersion = "2024-06-14";
    private const string SlotsVersion = "2024-09-04";
    private const string BookingsVersion = "2024-08-13";

    /// <summary>Cal.com's hosted API.</summary>
    public static Uri DefaultApiBase { get; } = new("https://api.cal.com/");

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string Name => "Cal.com";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct)
    {
        var page = await SendAsync(HttpMethod.Get, "v2/event-types", EventTypesVersion, null, CalComJsonContext.Default.CalComEnvelopeListCalComEventType, ct);
        var links = new List<SchedulingLink>();
        foreach (var t in page.Data ?? [])
        {
            if (t.Id is not { } id || t.Hidden != false || t.LengthInMinutes is not ({ } minutes and > 0)
                || !Uri.TryCreate(t.BookingUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !LinkSafety.CanLaunch(url))
            {
                continue;
            }

            var name = DisplayText.Clean(t.Title, MaxName);
            if (name.Length == 0)
            {
                continue;
            }

            links.Add(new SchedulingLink(ProviderId, id.ToString(CultureInfo.InvariantCulture), name, minutes, url, AllowsGuests: t.DisableGuests != true));
        }

        return links;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BusyRange>> GetOpenTimesAsync(string linkId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        var query = $"v2/slots?eventTypeId={Uri.EscapeDataString(linkId)}&start={Iso(startUtc)}&end={Iso(endUtc)}&format=range";
        var page = await SendAsync(HttpMethod.Get, query, SlotsVersion, null, CalComJsonContext.Default.CalComEnvelopeDictionaryStringListCalComSlot, ct);
        return [.. (page.Data ?? [])
            .SelectMany(day => day.Value ?? [])
            .Where(s => s.Start is { } start && s.End is { } end && end > start)
            .Select(s => new BusyRange(s.Start!.Value.ToUniversalTime(), s.End!.Value.ToUniversalTime()))
            .OrderBy(s => s.Start)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchedulingBooking>> GetBookingsAsync(string linkId, DateTimeOffset createdAfterUtc, CancellationToken ct)
    {
        var query = $"v2/bookings?eventTypeId={Uri.EscapeDataString(linkId)}&afterCreatedAt={Iso(createdAfterUtc)}&status=upcoming&take=100";
        var page = await SendAsync(HttpMethod.Get, query, BookingsVersion, null, CalComJsonContext.Default.CalComEnvelopeListCalComBooking, ct);
        return [.. (page.Data ?? []).Select(ToBooking).OfType<SchedulingBooking>()];
    }

    /// <inheritdoc />
    public async Task<SchedulingBooking> BookAsync(SchedulingLink link, DateTimeOffset startUtc, IReadOnlyList<Contact> guests, TimeZoneInfo zone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(guests);
        ArgumentNullException.ThrowIfNull(zone);
        if (guests.Count == 0 || !long.TryParse(link.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var eventTypeId))
        {
            throw new ArgumentException("A booking needs a guest and a Cal.com link.", nameof(guests));
        }

        var booker = guests[0];
        var request = new CalComBookingRequest(
            Iso(startUtc),
            eventTypeId,
            new CalComBookingAttendee(booker.Name.Length > 0 ? booker.Name : booker.Email, booker.Email, TimeZoneCatalog.IanaId(zone)),
            link.AllowsGuests && guests.Count > 1 ? [.. guests.Skip(1).Select(g => g.Email)] : null);
        var body = JsonContent.Create(request, CalComJsonContext.Default.CalComBookingRequest);

        var page = await SendAsync(HttpMethod.Post, "v2/bookings", BookingsVersion, body, CalComJsonContext.Default.CalComEnvelopeCalComBooking, ct);
        return (page.Data is { } data ? ToBooking(data) : null) ?? throw new SchedulingUnavailableException("Cal.com's booking answer was incomplete.");
    }

    /// <inheritdoc />
    public Uri TimeLink(SchedulingLink link, DateTimeOffset startUtc, TimeZoneInfo zone, IReadOnlyList<Contact> guests)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(zone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startUtc, zone).DateTime);
        return Build(link, [("date", Day(day)), ("slot", Iso(startUtc))], guests);
    }

    /// <inheritdoc />
    public Uri BlockLink(SchedulingLink link, DateOnly day, IReadOnlyList<Contact> guests)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Build(link, [("date", Day(day))], guests);
    }

    // The booking page plus query values (escaped), then the booker and, when allowed, the other guests
    private static Uri Build(SchedulingLink link, List<(string Key, string Value)> values, IReadOnlyList<Contact> guests)
    {
        ArgumentNullException.ThrowIfNull(guests);
        if (guests.Count > 0)
        {
            if (guests[0].Name.Length > 0)
            {
                values.Add(("name", guests[0].Name));
            }

            values.Add(("email", guests[0].Email));
            if (link.AllowsGuests)
            {
                values.AddRange(guests.Skip(1).Select(g => ("guests", g.Email)));
            }
        }

        var query = string.Join("&", values.Select(v => v.Key + "=" + Uri.EscapeDataString(v.Value)));
        var url = link.BookingUrl.AbsoluteUri;
        return new Uri(url + (link.BookingUrl.Query.Length > 0 ? "&" : "?") + query);
    }

    private static SchedulingBooking? ToBooking(CalComBooking b) =>
        b.Uid is { Length: > 0 } uid && b.Start is { } start && b.CreatedAt is { } created
            ? new SchedulingBooking(uid, start.ToUniversalTime(), created.ToUniversalTime(), DisplayText.Clean(b.Attendees?.FirstOrDefault()?.Name, MaxName))
            : null;

    // One request: key and version headers, status mapped to Leaf's three failures, body read through the source-generated context
    private async Task<T> SendAsync<T>(HttpMethod method, string relative, string version, HttpContent? content, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(method, new Uri(apiBase, relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("cal-api-version", version);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SchedulingUnavailableException("Cal.com couldn't be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SchedulingUnavailableException("Cal.com didn't answer in time.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new SchedulingAuthException("Cal.com refused the API key.");
                case HttpStatusCode.Conflict when method == HttpMethod.Post:
                    throw new SchedulingSlotTakenException("That time isn't open on Cal.com anymore.");
                case HttpStatusCode.BadRequest when method == HttpMethod.Post && IsTakenMessage(text):
                    throw new SchedulingSlotTakenException("That time isn't open on Cal.com anymore.");
                case (HttpStatusCode)429:
                    throw new SchedulingUnavailableException("Cal.com asked Leaf to slow down.", response.Headers.RetryAfter?.Delta);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SchedulingUnavailableException(string.Create(CultureInfo.InvariantCulture, $"Cal.com answered {(int)response.StatusCode}."));
            }

            try
            {
                return JsonSerializer.Deserialize(text, type) ?? throw new SchedulingUnavailableException("Cal.com's answer was empty.");
            }
            catch (JsonException ex)
            {
                throw new SchedulingUnavailableException("Cal.com's answer couldn't be read.", ex);
            }
        }
    }

    // Cal.com answers a booking for a taken time with 400 and this wording (checked against a live account in Task 19)
    private static bool IsTakenMessage(string body) =>
        body.Contains("already has booking", StringComparison.OrdinalIgnoreCase) || body.Contains("not available", StringComparison.OrdinalIgnoreCase);

    private static string Iso(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
```

If the source generator names the generic types differently, use the property names IntelliSense shows on `CalComJsonContext.Default` (they follow `CalComEnvelope` + type arguments). Any using that ends up unused must be removed (the build fails on unused usings).

- [ ] **Step 7: Run to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*CalCom*"`
Expected: PASS. Then the full logic suite (it must stay green) and `dotnet build LeafCalendar.slnx -c Debug` with 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/LeafCalendar.Core/Scheduling tests/LeafCalendar.Tests
git commit -m "feat(scheduling): provider actions as small interfaces, and a Cal.com client for links, openings, bookings and booking links"
```

---

### Task 4: Booking Matching

**Files:**
- Modify: `src/LeafCalendar.Core/Data/ShareGroupStore.cs` (only the `ShareGroup` record here; storage lands in Task 6 — so do Task 6 before this task if executing out of order)
- Create: `src/LeafCalendar.Core/Scheduling/BookingMatch.cs`
- Test: `tests/LeafCalendar.Tests/BookingMatchTests.cs`

> Order note: this task needs `ShareGroup.ProviderId`, `LinkId` and `CreatedUtc` from Task 6. **Run Task 6 first**, then this task. (Numbering kept so Tasks 1–3 stay pure.)

**Interfaces:**
- Consumes: `ShareGroup` with `ProviderId`, `LinkId`, `CreatedUtc` (Task 6), `SchedulingBooking` (Task 3).
- Produces: `public sealed record BookingMatchResult(SchedulingBooking Booking, IReadOnlyList<long> GroupIds)`; `public static IReadOnlyList<BookingMatchResult> BookingMatch.Find(IReadOnlyList<ShareGroup> groups, string providerId, string linkId, IReadOnlyList<SchedulingBooking> bookings)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class BookingMatchTests
{
    private static readonly DateTimeOffset Saved = new(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NineCt = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

    private static ShareGroup Group(long id, string link = "101", DateTimeOffset? created = null) =>
        new(id, "", "", "America/Chicago", [new BusyRange(NineCt, NineCt.AddHours(2))], [], "calcom", link, "https://cal.com/x", 30, created ?? Saved);

    private static SchedulingBooking Booking(DateTimeOffset start, DateTimeOffset? created = null) => new("bk", start, created ?? Saved.AddHours(1), "Jane");

    [Fact]
    public void Find_StartOnARangeStart_Matches()
    {
        var result = BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt)]);

        Assert.Equal([1L], Assert.Single(result).GroupIds);
    }

    [Fact]
    public void Find_InBetweenStart_Matches()
    {
        Assert.Single(BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt.AddMinutes(45))]));
    }

    [Fact]
    public void Find_StartAtRangeEnd_DoesNotMatch()
    {
        Assert.Empty(BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt.AddHours(2))]));
    }

    [Fact]
    public void Find_OtherLink_DoesNotMatch()
    {
        Assert.Empty(BookingMatch.Find([Group(1, link: "102")], "calcom", "101", [Booking(NineCt)]));
    }

    [Fact]
    public void Find_BookedBeforeTheGroupWasSaved_DoesNotMatch()
    {
        Assert.Empty(BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt, created: Saved.AddMinutes(-1))]));
    }

    [Fact]
    public void Find_TwoGroupsSameRange_BothGo()
    {
        var result = BookingMatch.Find([Group(1), Group(2)], "calcom", "101", [Booking(NineCt.AddMinutes(30))]);

        Assert.Equal([1L, 2L], Assert.Single(result).GroupIds);
    }

    [Fact]
    public void Find_TwoBookingsOneGroup_GroupReportedOnce()
    {
        var result = BookingMatch.Find([Group(1)], "calcom", "101", [Booking(NineCt), Booking(NineCt.AddMinutes(30))]);

        Assert.Equal([1L], Assert.Single(result).GroupIds);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*BookingMatchTests"`
Expected: build error, `BookingMatch` does not exist.

- [ ] **Step 3: Implement**

```csharp
using LeafCalendar.Core.Data;

namespace LeafCalendar.Core.Scheduling;

/// <summary>A booking and the saved groups it closes.</summary>
public sealed record BookingMatchResult(SchedulingBooking Booking, IReadOnlyList<long> GroupIds);

/// <summary>Finds the saved groups a booking closes (spec 3.6).</summary>
public static class BookingMatch
{
    /// <summary>
    /// For each booking, the groups on the same provider and link, saved at or before the booking was made, with a saved
    /// range the booking starts inside (start included, end not). A group is reported for its first matching booking only.
    /// Bookings that close no group are left out.
    /// </summary>
    public static IReadOnlyList<BookingMatchResult> Find(IReadOnlyList<ShareGroup> groups, string providerId, string linkId, IReadOnlyList<SchedulingBooking> bookings)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(bookings);

        var candidates = groups.Where(g => g.ProviderId == providerId && g.LinkId == linkId).ToList();
        var taken = new HashSet<long>();
        var results = new List<BookingMatchResult>();
        foreach (var booking in bookings.OrderBy(b => b.Created))
        {
            var ids = candidates
                .Where(g => !taken.Contains(g.Id) && booking.Created >= g.CreatedUtc && g.Slots.Any(s => booking.Start >= s.Start && booking.Start < s.End))
                .Select(g => g.Id)
                .ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            taken.UnionWith(ids);
            results.Add(new BookingMatchResult(booking, ids));
        }

        return results;
    }
}
```

- [ ] **Step 4: Run to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*BookingMatchTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Scheduling/BookingMatch.cs tests/LeafCalendar.Tests/BookingMatchTests.cs
git commit -m "feat(scheduling): a booking that starts inside saved times closes their group"
```

---

### Task 5: Provider Secrets

**Files:**
- Modify: `src/LeafCalendar.Core/Auth/ITokenStore.cs`
- Modify: `src/LeafCalendar.Core/Auth/ProtectedFileTokenStore.cs` (methods, and `SecretsFile` record at the bottom)
- Modify: `src/LeafCalendar.Core/Auth/CredentialLockerTokenStore.cs`
- Modify: `tests/LeafCalendar.Tests/Support/InMemoryTokenStore.cs`
- Test: `tests/LeafCalendar.Tests/ProtectedFileTokenStoreTests.cs` (existing class; add tests)

**Interfaces:**
- Produces on `ITokenStore`:
```csharp
/// <summary>Returns a scheduling provider's secret value (such as its API key), or null.</summary>
string? GetProviderSecret(string providerId, string key);
/// <summary>Saves (replaces) a scheduling provider's secret value.</summary>
void SetProviderSecret(string providerId, string key, string value);
/// <summary>Deletes every secret of a scheduling provider (Disconnect).</summary>
void RemoveProviderSecrets(string providerId);
```

- [ ] **Step 1: Write the failing tests** (in the existing `ProtectedFileTokenStoreTests`; follow how that class creates a store in a `TempFolder`)

```csharp
[Fact]
public void ProviderSecrets_RoundTrip_AndSurviveOtherWrites()
{
    using var folder = new TempFolder();
    var store = new ProtectedFileTokenStore(Path.Combine(folder.Path, "default"));

    store.SetProviderSecret("calcom", "api_key", "cal_live_abc");
    store.SetClientCredentials(new("id", "secret"));
    store.SetRefreshToken("acct", "1//r");

    var reopened = new ProtectedFileTokenStore(Path.Combine(folder.Path, "default"));
    Assert.Equal("cal_live_abc", reopened.GetProviderSecret("calcom", "api_key"));
    Assert.Null(reopened.GetProviderSecret("calcom", "other"));
    Assert.Null(reopened.GetProviderSecret("other", "api_key"));
    Assert.Equal("1//r", reopened.GetRefreshToken("acct"));
}

[Fact]
public void RemoveProviderSecrets_RemovesOnlyThatProvider()
{
    using var folder = new TempFolder();
    var store = new ProtectedFileTokenStore(Path.Combine(folder.Path, "default"));
    store.SetProviderSecret("calcom", "api_key", "a");
    store.SetProviderSecret("other", "api_key", "b");

    store.RemoveProviderSecrets("calcom");

    Assert.Null(store.GetProviderSecret("calcom", "api_key"));
    Assert.Equal("b", store.GetProviderSecret("other", "api_key"));
}

[Fact]
public void ProviderSecrets_OldFileWithoutThem_ReadsAsNone()
{
    using var folder = new TempFolder();
    var store = new ProtectedFileTokenStore(Path.Combine(folder.Path, "default"));
    store.SetClientCredentials(new("id", "secret"));   // written before the field existed reads with Providers null

    Assert.Null(store.GetProviderSecret("calcom", "api_key"));
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*ProtectedFileTokenStoreTests"`
Expected: build error, `GetProviderSecret` not defined.

- [ ] **Step 3: Implement**

`ITokenStore.cs`: add the three members above (with the doc comments shown).

`ProtectedFileTokenStore.cs`: change the record at the bottom to

```csharp
/// <summary>A profile's secrets, as stored (encrypted) in <c>secrets.bin</c>.</summary>
/// <param name="ClientId">The OAuth client ID, or null before setup.</param>
/// <param name="ClientSecret">The OAuth client secret, or null before setup.</param>
/// <param name="Refresh">Refresh tokens by account ID (an empty dictionary when there are none).</param>
/// <param name="Providers">Scheduling provider secrets by provider ID, then key; null in files written before scheduling providers.</param>
internal sealed record SecretsFile(string? ClientId, string? ClientSecret, Dictionary<string, string> Refresh, Dictionary<string, Dictionary<string, string>>? Providers = null);
```

and add, beside `GetAccountIds`:

```csharp
/// <inheritdoc />
public string? GetProviderSecret(string providerId, string key)
{
    lock (_gate)
    {
        return Load(out _).Providers?.GetValueOrDefault(providerId)?.GetValueOrDefault(key);
    }
}

/// <inheritdoc />
public void SetProviderSecret(string providerId, string key, string value)
{
    lock (_gate)
    {
        var file = Load(out var unreadable);
        var providers = file.Providers ?? new(StringComparer.Ordinal);
        if (!providers.TryGetValue(providerId, out var values))
        {
            values = new(StringComparer.Ordinal);
            providers[providerId] = values;
        }

        values[key] = value;
        Save(file with { Providers = providers }, unreadable);
    }
}

/// <inheritdoc />
public void RemoveProviderSecrets(string providerId)
{
    lock (_gate)
    {
        var file = Load(out var unreadable);
        if (file.Providers?.Remove(providerId) == true)
        {
            Save(file, unreadable);
        }
    }
}
```

`CredentialLockerTokenStore.cs` (read only by the one-time move; Leaf uses it for a run only when the move's write failed, so a provider save should say "try later" rather than vanish):

```csharp
/// <inheritdoc />
public string? GetProviderSecret(string providerId, string key) => null;

/// <inheritdoc />
public void SetProviderSecret(string providerId, string key, string value) =>
    throw SecretsUnavailable.Create("Scheduling secrets can't be saved until the secrets file can be written.", new NotSupportedException());

/// <inheritdoc />
public void RemoveProviderSecrets(string providerId)
{
}
```

(Check `SecretsUnavailable.Create`'s exact signature in `AuthExceptions.cs`; it's used the same way in `ProtectedFileTokenStore.Load`.)

`InMemoryTokenStore.cs`: back the three members with a `Dictionary<(string, string), string>`; `RemoveProviderSecrets` removes keys whose first item matches.

- [ ] **Step 4: Run to see them pass**, then the full logic suite.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Auth tests/LeafCalendar.Tests
git commit -m "feat(scheduling): provider API keys are kept encrypted with the other secrets"
```

---

### Task 6: Settings And Saved Group Storage

**Files:**
- Modify: `src/LeafCalendar.Core/Settings/LeafSettings.cs`
- Modify: `src/LeafCalendar.Core/Data/Schema.cs` (add `V13`), `src/LeafCalendar.Core/Data/LeafDatabase.cs` (apply it)
- Modify: `src/LeafCalendar.Core/Data/ShareGroupStore.cs`
- Modify every caller of `ShareGroupStore.Insert`/`Update` and every `new ShareGroup(` (grep both; `CalendarViewModel.People.cs` and tests)
- Test: `tests/LeafCalendar.Tests/LeafDatabaseTests.cs`, `ShareGroupStoreTests.cs`, `LeafSettingsTests.cs` (existing classes; find the settings test class with `grep -l "Normalize" tests/LeafCalendar.Tests/*.cs`)

**Interfaces:**
- Produces:
```csharp
// LeafCalendar.Core.Settings
public sealed record ProviderSettings(string ProviderId, string Server, string DefaultLinkId);
// LeafSettings
public IReadOnlyList<ProviderSettings> Scheduling { get; init; } = [];
public bool BookingNotifications { get; set; } = true;
public ProviderSettings ProviderSettingsFor(string providerId);          // the saved one, or an empty one
public LeafSettings WithProvider(ProviderSettings settings);            // replaces or adds
public LeafSettings WithoutProvider(string providerId);

// LeafCalendar.Core.Data
public sealed record ShareGroup(long Id, string Title, string Message, string ZoneId, IReadOnlyList<BusyRange> Slots, IReadOnlyList<Contact> Guests,
    string ProviderId = "", string LinkId = "", string LinkUrl = "", int LinkMinutes = 0, DateTimeOffset CreatedUtc = default);
public sealed record ShareLink(string ProviderId, string LinkId, string LinkUrl, int LinkMinutes) { public static ShareLink None { get; } = new("", "", "", 0); }
ShareGroupStore.Insert(conn, title, message, zoneId, slots, now, guests, ShareLink link)
ShareGroupStore.Update(conn, id, title, message, zoneId, slots, guests, ShareLink link)
ShareGroupStore.ClearProvider(conn, providerId)   // Disconnect: that provider's groups become plain
```

- [ ] **Step 1: Write the failing tests**

In `LeafDatabaseTests`: change every `12L` to `13L`, and add:
```csharp
[Fact]
public void Migrate_Version12_AddsLinkColumnsAndKeepsGroups()
{
    // Build a version-12 database the way the version-11 test does (copy its setup: create tables through V12 by
    // running Migrate on a fresh file, then set user_version back to 12 and drop nothing), insert a group with raw SQL
    // (title 'Held', message 'm', zone 'UTC', created_utc 0, one slot), then Migrate again
    // and assert: user_version 13, the group reads back through ShareGroupStore.GetAll with ProviderId == "" and LinkMinutes == 0
}
```
Write it out fully following the exact pattern of the existing `Migrate_Version11_...` test in that file (it builds an old-version database with raw SQL). The new columns must have defaults so old rows read as plain groups.

In `ShareGroupStoreTests`:
```csharp
[Fact]
public void Insert_WithLink_RoundTrips()
{
    using var db = new TestDatabase();
    using var conn = db.Database.Open();
    var now = new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
    var slot = new BusyRange(now.AddDays(1), now.AddDays(1).AddHours(1));

    ShareGroupStore.Insert(conn, "T", "M", "UTC", [slot], now, [], new ShareLink("calcom", "101", "https://cal.com/x/30min", 30));

    var group = Assert.Single(ShareGroupStore.GetAll(conn, now));
    Assert.Equal(("calcom", "101", "https://cal.com/x/30min", 30), (group.ProviderId, group.LinkId, group.LinkUrl, group.LinkMinutes));
    Assert.Equal(now, group.CreatedUtc);
}

[Fact]
public void Update_ReplacesLink_AndKeepsCreated()
{
    using var db = new TestDatabase();
    using var conn = db.Database.Open();
    var now = new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
    var slot = new BusyRange(now.AddDays(1), now.AddDays(1).AddHours(1));
    var id = ShareGroupStore.Insert(conn, "T", "M", "UTC", [slot], now, [], ShareLink.None);

    ShareGroupStore.Update(conn, id, "T", "M", "UTC", [slot], [], new ShareLink("calcom", "102", "https://cal.com/x/15", 15));

    var group = Assert.Single(ShareGroupStore.GetAll(conn, now));
    Assert.Equal("102", group.LinkId);
    Assert.Equal(now, group.CreatedUtc);
}

[Fact]
public void ClearProvider_MakesThoseGroupsPlain()
{
    using var db = new TestDatabase();
    using var conn = db.Database.Open();
    var now = new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
    var slot = new BusyRange(now.AddDays(1), now.AddDays(1).AddHours(1));
    ShareGroupStore.Insert(conn, "T", "M", "UTC", [slot], now, [], new ShareLink("calcom", "101", "https://cal.com/x", 30));

    ShareGroupStore.ClearProvider(conn, "calcom");

    var group = Assert.Single(ShareGroupStore.GetAll(conn, now));
    Assert.Equal(("", "", "", 0), (group.ProviderId, group.LinkId, group.LinkUrl, group.LinkMinutes));
}

[Fact]
public void Insert_LongOrUnsafeLinkUrl_IsNotKept()
{
    using var db = new TestDatabase();
    using var conn = db.Database.Open();
    var now = new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
    var slot = new BusyRange(now.AddDays(1), now.AddDays(1).AddHours(1));

    ShareGroupStore.Insert(conn, "T", "M", "UTC", [slot], now, [], new ShareLink("calcom", "101", "http://cal.com/x", 30));

    Assert.Equal(("", ""), (ShareGroupStore.GetAll(conn, now)[0].ProviderId, ShareGroupStore.GetAll(conn, now)[0].LinkUrl));
}
```

In the settings tests:
```csharp
[Fact]
public void Normalize_Scheduling_DropsBlankAndRepeatedProviders_AndClips()
{
    var settings = new LeafSettings
    {
        Scheduling = [new("calcom", " https://cal.example.com ", "101"), new("calcom", "", "102"), new(" ", "", "")],
    }.Normalize(/* pass what the other Normalize tests pass */);

    var only = Assert.Single(settings.Scheduling);
    Assert.Equal(("calcom", "https://cal.example.com", "101"), (only.ProviderId, only.Server, only.DefaultLinkId));
}

[Fact]
public void BookingNotifications_DefaultsOn() => Assert.True(new LeafSettings().BookingNotifications);

[Fact]
public void WithProvider_ThenWithoutProvider_RoundTrips()
{
    var settings = new LeafSettings().WithProvider(new("calcom", "", "101"));
    Assert.Equal("101", settings.ProviderSettingsFor("calcom").DefaultLinkId);
    Assert.Empty(settings.WithoutProvider("calcom").Scheduling);
    Assert.Equal("", new LeafSettings().ProviderSettingsFor("calcom").DefaultLinkId);
}
```

Plus a JSON round trip of `Scheduling` through `LeafJsonContext.Default.LeafSettings` (follow the existing serialization test in that class).

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug`
Expected: build errors.

- [ ] **Step 3: Implement**

`Schema.cs`, after `V12`:
```csharp
/// <summary>
/// Version 13: a saved group's scheduling link (spec 2026-10-12, scheduling providers): the provider and link IDs, the
/// link's booking page and its length, so Copy &amp; Save can build booking links again offline. Empty and 0 for a plain
/// group (every group saved before). Local only.
/// </summary>
public const string V13 = """
    ALTER TABLE share_groups ADD COLUMN provider_id  TEXT    NOT NULL DEFAULT '';
    ALTER TABLE share_groups ADD COLUMN link_id      TEXT    NOT NULL DEFAULT '';
    ALTER TABLE share_groups ADD COLUMN link_url     TEXT    NOT NULL DEFAULT '';
    ALTER TABLE share_groups ADD COLUMN link_minutes INTEGER NOT NULL DEFAULT 0;
    """;
```

`LeafDatabase.cs`, after the version-12 block:
```csharp
// Version 13
if (version < 13)
{
    using var tx = conn.BeginTransaction();
    conn.Execute(tx, Schema.V13);
    conn.Execute(tx, "PRAGMA user_version = 13;");
    tx.Commit();
}
```
Update the class doc and `CLAUDE.md`'s "currently 12" to 13.

`ShareGroupStore.cs`:
- Add the `ShareLink` record (doc: "A saved group's scheduling link; <see cref="None"/> for a plain group.") and the new `ShareGroup` parameters with a doc line each.
- `GetAll`: select `id, title, message, zone_id, created_utc, provider_id, link_id, link_url, link_minutes` and pass them into `ShareGroup` (`CreatedUtc: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4))`).
- `Insert`/`Update`: take `ShareLink link` last and write the four columns. Clean it first with:
```csharp
// A link is kept only whole and safe: an https booking page Leaf may open, a provider and link ID, a length; otherwise the group is plain
private static ShareLink Clean(ShareLink link) =>
    link.ProviderId.Length is > 0 and <= 32 && link.LinkId.Length is > 0 and <= 64 && link.LinkMinutes is > 0 and <= 1440 && link.LinkUrl.Length <= 2048
        && Uri.TryCreate(link.LinkUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && Events.LinkSafety.CanLaunch(url)
        ? link
        : ShareLink.None;
```
- `ClearProvider`: `UPDATE share_groups SET provider_id = '', link_id = '', link_url = '', link_minutes = 0 WHERE provider_id = $p;`
- Update the existing callers to pass `ShareLink.None` for now (Task 14 passes the real link).

`LeafSettings.cs`:
- `ProviderSettings` record in the same file (doc comments on it and each parameter: "Server: the address typed in Settings, empty for the provider's own; DefaultLinkId: the link each new share starts with, empty for the first").
- `Scheduling` and `BookingNotifications` properties near `InviteNotifications`, with doc comments.
- In `Normalize`, add `Scheduling = CleanScheduling(Scheduling),` with:
```csharp
// Trimmed, capped, blank and repeated provider IDs dropped (the first kept)
private static IReadOnlyList<ProviderSettings> CleanScheduling(IReadOnlyList<ProviderSettings>? list) =>
    [.. (list ?? [])
        .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.ProviderId))
        .Select(p => new ProviderSettings(Clip(p.ProviderId.Trim(), 32), Clip((p.Server ?? "").Trim(), 2048), Clip((p.DefaultLinkId ?? "").Trim(), 64)))
        .DistinctBy(p => p.ProviderId, StringComparer.Ordinal)];
```
- `ProviderSettingsFor`, `WithProvider`, `WithoutProvider` as simple `with` copies.

- [ ] **Step 4: Run** the full logic suite; all pass. Build with 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core tests/LeafCalendar.Tests CLAUDE.md
git commit -m "feat(scheduling): saved times remember their booking link, and settings keep each provider's server and default link"
```

Then do Task 4.

---

### Task 7: Provider Registry And Hub

**Files:**
- Create: `src/LeafCalendar.Core/Scheduling/SchedulingProviders.cs`
- Create: `src/LeafCalendar.Core/Scheduling/SchedulingHub.cs`
- Test: `tests/LeafCalendar.Tests/SchedulingHubTests.cs`

**Interfaces:**
- Consumes: `ITokenStore` provider secrets (Task 5), `ProviderSettings` (Task 6), `CalComProvider` (Task 3), `AppLog` (`LeafCalendar.Core.Diagnostics`), `SecretsUnavailable.Is(Exception)`.
- Produces:
```csharp
public sealed record SetupField(string Key, string Label, bool Secret, bool Required, string Placeholder, Func<string, bool> IsValid, string InvalidMessage);
public sealed record SchedulingProviderInfo(string Id, string Name, string Description, IReadOnlyList<SetupField> Fields,
    Func<HttpClient, IReadOnlyDictionary<string, string>, Uri, ISchedulingProvider> Create, Uri DefaultBase);
public static class SchedulingProviders
{
    public static IReadOnlyList<SchedulingProviderInfo> All { get; }
    public static SchedulingProviderInfo? Find(string id);
}
public enum ProviderState { NotConnected, Connected, NeedsReconnect }
public sealed class SchedulingHub(HttpClient http, ITokenStore tokens, AppLog log, TimeProvider time, Uri? fakeRoot = null)
{
    public event EventHandler? Changed;                                            // raised on any thread
    public IReadOnlyList<ISchedulingProvider> Connected { get; }                   // NeedsReconnect ones excluded
    public ProviderState StateOf(string providerId);
    public ISchedulingProvider? Get(string providerId);                            // connected only
    public void Load(IReadOnlyList<ProviderSettings> settings);                    // startup and secrets retry
    public Task<IReadOnlyList<SchedulingLink>> ConnectAsync(SchedulingProviderInfo info, IReadOnlyDictionary<string, string> values, CancellationToken ct);
    public void Disconnect(string providerId);
    public Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct); // every connected provider's links, cached 5 min
    public void MarkNeedsReconnect(string providerId);
    public bool IsWaiting(string providerId);                                       // in a 429 back-off
    public void WaitFor(string providerId, TimeSpan? retryAfter);                   // back off (Retry-After, else 60 s)
    public static string? Check(SchedulingProviderInfo info, IReadOnlyDictionary<string, string> values); // the first invalid field's message, or null
}
```

Behavior (doc each member with this):
- `Load`: for each provider in `SchedulingProviders.All` with a saved secret for every required secret field, build it with `Create(http, values, baseUri)`, where values are the secrets plus the non-secret fields from `ProviderSettings` (`server`), and `baseUri` is: the `server` value if non-empty (already validated on connect), else `fakeRoot is null ? info.DefaultBase : new Uri(fakeRoot, info.Id + "/")`. A `SecretsUnavailable` read failure leaves that provider out and keeps its state unchanged (log `scheduling.load.secrets-unavailable` with the provider id); nothing is written.
- `ConnectAsync`: `Check` first (throws `ArgumentException` with the field's message if invalid; the UI calls `Check` before, so this is a guard), builds the provider, calls `ILinkSource.GetLinksAsync` to prove the key works (exceptions pass through), then saves each secret field with `SetProviderSecret`, marks Connected, caches the links, raises `Changed`, returns the links. The caller saves `ProviderSettings` (server, default link).
- `Disconnect`: `RemoveProviderSecrets`, drop the instance and cache, state NotConnected, raise `Changed`. The caller removes its `ProviderSettings` and calls `ShareGroupStore.ClearProvider`.
- `GetLinksAsync`: per connected `ILinkSource`; a provider that throws `SchedulingAuthException` is marked NeedsReconnect and contributes nothing; `SchedulingUnavailableException` contributes its cached links (or nothing). Cache per provider for 5 minutes using `time`.
- Thread safety: one `Lock` around the provider dictionary, states and caches; never hold it across an `await`.

`SchedulingProviders.All` has one entry:
```csharp
new SchedulingProviderInfo(
    CalComProvider.ProviderId,
    "Cal.com",
    "Share times as booking links from your Cal.com account.",
    [
        new SetupField("api_key", "API key", Secret: true, Required: true, "cal_live_…", v => v.Trim().StartsWith("cal_", StringComparison.Ordinal) && v.Trim().Length <= 200,
            "Cal.com didn't accept that API key. Check it in Cal.com › Settings › Developer › API keys."),
        new SetupField("server", "Server address", Secret: false, Required: false, "Leave empty for cal.com", IsServer,
            "Enter an https:// address, or leave it empty for cal.com."),
    ],
    (http, values, baseUri) => new CalComProvider(http, values["api_key"].Trim(), baseUri),
    CalComProvider.DefaultApiBase)
```
with
```csharp
// Empty, or an absolute https address with no user info, query or fragment
private static bool IsServer(string value)
{
    var text = value.Trim();
    return text.Length == 0
        || (text.Length <= 2048 && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0);
}
```
and the server value normalized to end with `/` before use (`new Uri(text.EndsWith('/') ? text : text + "/")`).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Settings;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SchedulingHubTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeHttpHandler _cal = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero));
    private readonly SchedulingProviderInfo _calcom = SchedulingProviders.Find("calcom")!;

    private SchedulingHub Create(Uri? fakeRoot = null) => new(new HttpClient(_cal), _tokens, new AppLog(_folder.Path, _time), _time, fakeRoot);

    private void LinksAnswer(string baseUrl = "https://api.cal.com/") =>
        _cal.On(HttpMethod.Get, baseUrl + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task ConnectAsync_GoodKey_SavesSecretAndConnects()
    {
        LinksAnswer();
        var hub = Create();

        var links = await hub.ConnectAsync(_calcom, new Dictionary<string, string> { ["api_key"] = " cal_live_x ", ["server"] = "" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, links.Count);
        Assert.Equal("cal_live_x", _tokens.GetProviderSecret("calcom", "api_key"));
        Assert.Equal(ProviderState.Connected, hub.StateOf("calcom"));
        Assert.NotNull(hub.Get("calcom"));
    }

    [Fact]
    public async Task ConnectAsync_BadKey_SavesNothing()
    {
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/event-types", HttpStatusCode.Unauthorized, "{}");
        var hub = Create();

        await Assert.ThrowsAsync<SchedulingAuthException>(() => hub.ConnectAsync(_calcom, new Dictionary<string, string> { ["api_key"] = "cal_live_bad" }, TestContext.Current.CancellationToken));

        Assert.Null(_tokens.GetProviderSecret("calcom", "api_key"));
        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));
    }

    [Theory]
    [InlineData("nope", "")]
    [InlineData("cal_live_x", "http://cal.example.com")]
    [InlineData("cal_live_x", "https://user@cal.example.com")]
    [InlineData("cal_live_x", "https://cal.example.com/?a=b")]
    public void Check_InvalidValues_ReturnsMessage(string key, string server) =>
        Assert.NotNull(SchedulingHub.Check(_calcom, new Dictionary<string, string> { ["api_key"] = key, ["server"] = server }));

    [Fact]
    public async Task Load_SelfHostedServer_UsesIt()
    {
        LinksAnswer("https://cal.example.com/api/");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();

        hub.Load([new ProviderSettings("calcom", "https://cal.example.com/api", "")]);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("https://cal.example.com/api/v2/event-types", _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_FakeRoot_UsesProviderFolderOfFake()
    {
        LinksAnswer("http://127.0.0.1:5000/calcom/");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create(new Uri("http://127.0.0.1:5000/"));

        hub.Load([]);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("http://127.0.0.1:5000/calcom/v2/event-types", _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SecretsUnavailable_KeepsNothingAndRetriesLater()
    {
        var tokens = new ThrowingProviderTokenStore();
        var hub = new SchedulingHub(new HttpClient(_cal), tokens, new AppLog(_folder.Path, _time), _time);

        hub.Load([]);
        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));

        tokens.Fail = false;
        tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        hub.Load([]);
        Assert.Equal(ProviderState.Connected, hub.StateOf("calcom"));
    }

    [Fact]
    public async Task GetLinksAsync_KeyRevoked_MarksNeedsReconnect()
    {
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/event-types", HttpStatusCode.Unauthorized, "{}");
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);

        Assert.Empty(await hub.GetLinksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderState.NeedsReconnect, hub.StateOf("calcom"));
        Assert.Empty(hub.Connected);
    }

    [Fact]
    public async Task GetLinksAsync_Cached_ForFiveMinutes()
    {
        LinksAnswer();
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);

        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Single(_cal.Requests);

        _time.Advance(TimeSpan.FromMinutes(6));
        await hub.GetLinksAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _cal.Requests.Count);
    }

    [Fact]
    public void Disconnect_RemovesSecretsAndState()
    {
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        var hub = Create();
        hub.Load([]);

        hub.Disconnect("calcom");

        Assert.Null(_tokens.GetProviderSecret("calcom", "api_key"));
        Assert.Equal(ProviderState.NotConnected, hub.StateOf("calcom"));
    }

    [Fact]
    public void WaitFor_RetryAfter_WaitsThatLong()
    {
        var hub = Create();

        hub.WaitFor("calcom", TimeSpan.FromSeconds(90));
        Assert.True(hub.IsWaiting("calcom"));

        _time.Advance(TimeSpan.FromSeconds(91));
        Assert.False(hub.IsWaiting("calcom"));
    }

    // A token store whose provider reads fail the way an unreadable-right-now secrets file does
    private sealed class ThrowingProviderTokenStore : Core.Auth.ITokenStore
    {
        private readonly InMemoryTokenStore _inner = new();

        public bool Fail { get; set; } = true;

        public Core.Auth.OAuthClientCredentials? GetClientCredentials() => _inner.GetClientCredentials();
        public void SetClientCredentials(Core.Auth.OAuthClientCredentials credentials) => _inner.SetClientCredentials(credentials);
        public void ClearClientCredentials() => _inner.ClearClientCredentials();
        public string? GetRefreshToken(string accountId) => _inner.GetRefreshToken(accountId);
        public void SetRefreshToken(string accountId, string refreshToken) => _inner.SetRefreshToken(accountId, refreshToken);
        public void RemoveRefreshToken(string accountId) => _inner.RemoveRefreshToken(accountId);
        public IReadOnlyList<string> GetAccountIds() => _inner.GetAccountIds();
        public string? GetProviderSecret(string providerId, string key) =>
            Fail ? throw Core.Auth.SecretsUnavailable.Create("test", new IOException()) : _inner.GetProviderSecret(providerId, key);
        public void SetProviderSecret(string providerId, string key, string value) => _inner.SetProviderSecret(providerId, key, value);
        public void RemoveProviderSecrets(string providerId) => _inner.RemoveProviderSecrets(providerId);
    }
}
```

(Adjust `AppLog`'s constructor and `SecretsUnavailable.Create` to their real signatures; see `tests/LeafCalendar.Tests/AppLogTests.cs` and `AuthExceptions.cs`.)

- [ ] **Step 2: Run to see them fail** (`--filter-class "*SchedulingHubTests"`; build errors).
- [ ] **Step 3: Implement** `SchedulingProviders.cs` and `SchedulingHub.cs` per the interface and behavior above, with class docs (`@package`-style tags are not used in this repo; follow the doc-comment style of `GoogleServices`).
- [ ] **Step 4: Run to see them pass**, then the full suite and a 0-warning build.
- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Scheduling tests/LeafCalendar.Tests/SchedulingHubTests.cs
git commit -m "feat(scheduling): a provider list and a hub that connects, loads, caches links and notices a revoked key"
```

---

### Task 8: Booking Watcher

**Files:**
- Create: `src/LeafCalendar.Core/Scheduling/BookingWatcher.cs`
- Modify: `src/LeafCalendar.Core/Hosting/GoogleServices.cs` (optional after-sync pass)
- Test: `tests/LeafCalendar.Tests/BookingWatcherTests.cs`

**Interfaces:**
- Consumes: `SchedulingHub` (Task 7), `ShareGroupStore.GetAll`/`Delete` (Task 6), `BookingMatch.Find` (Task 4), `LeafDatabase`.
- Produces:
```csharp
public sealed class BookingFoundEventArgs(SchedulingBooking booking, string providerName) : EventArgs
{ public SchedulingBooking Booking { get; } public string ProviderName { get; } }
public sealed class BookingWatcher(SchedulingHub hub, LeafDatabase database, AppLog log, TimeProvider time)
{
    public event EventHandler<BookingFoundEventArgs>? BookingFound;   // raised on the sync thread
    public event EventHandler? GroupsChanged;                           // after matched groups are deleted
    public Task CheckAsync(CancellationToken ct);                       // never throws except OperationCanceledException when ct is canceled
}
// GoogleServices constructor gains a last optional parameter:
//   Func<CancellationToken, Task>? afterSync = null
// and builds Loop = new SyncLoop(ct => SyncThenAsync(afterSync, ct), time, log)
```

`CheckAsync` steps:
1. Read groups (`GetAll(conn, now)`) where `ProviderId.Length > 0`. None → return (no network call; this is the RAM/traffic rule).
2. Group by `(ProviderId, LinkId)`. For each: skip when `hub.Get(provider)` isn't an `IBookingSource` or `hub.IsWaiting(provider)`.
3. `GetBookingsAsync(linkId, groups.Min(g => g.CreatedUtc), ct)`. `SchedulingAuthException` → `hub.MarkNeedsReconnect`; `SchedulingUnavailableException ex` → `hub.WaitFor(provider, ex.RetryAfter)` only when `ex.RetryAfter` is set, else just skip; any other exception except cancellation → log type name and skip.
4. `BookingMatch.Find`; for each result delete its groups (one connection, `ShareGroupStore.Delete` each), log `scheduling.booking.found` with `provider=… groups=N`, raise `BookingFound` once per result, then `GroupsChanged` once if anything was deleted.

`GoogleServices.SyncThenAsync`:
```csharp
// One sync pass, then the scheduling booking check (it never fails a Google pass; a failed Google pass skips it until the next)
private async Task SyncThenAsync(Func<CancellationToken, Task>? afterSync, CancellationToken ct)
{
    await Sync.SyncAllAsync(ct);
    if (afterSync is not null)
    {
        await afterSync(ct);
    }
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class BookingWatcherTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly TempFolder _logs = new();
    private readonly FakeHttpHandler _cal = new();
    private readonly InMemoryTokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero));
    private readonly SchedulingHub _hub;
    private readonly BookingWatcher _watcher;

    public BookingWatcherTests()
    {
        var log = new AppLog(_logs.Path, _time);
        _tokens.SetProviderSecret("calcom", "api_key", "cal_live_x");
        _hub = new SchedulingHub(new HttpClient(_cal), _tokens, log, _time);
        _hub.Load([]);
        _watcher = new BookingWatcher(_hub, _db.Database, log, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _logs.Dispose();
    }

    private long SaveGroup(string provider = "calcom")
    {
        using var conn = _db.Database.Open();
        var nine = new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
        var link = provider.Length > 0 ? new ShareLink(provider, "101", "https://cal.com/leaf-tester/30min", 30) : ShareLink.None;
        return ShareGroupStore.Insert(conn, "", "", "UTC", [new BusyRange(nine, nine.AddHours(2))], _time.GetUtcNow(), [], link);
    }

    [Fact]
    public async Task CheckAsync_NoProviderGroups_SendsNothing()
    {
        SaveGroup(provider: "");

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_cal.Requests);
    }

    [Fact]
    public async Task CheckAsync_MatchingBooking_DeletesGroupAndRaises()
    {
        SaveGroup();
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/bookings", HttpStatusCode.OK, Fixture.Read("calcom-bookings.json"));
        var found = new List<BookingFoundEventArgs>();
        _watcher.BookingFound += (_, e) => found.Add(e);

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jane Doe", Assert.Single(found).Booking.AttendeeName);
        using var conn = _db.Database.Open();
        Assert.Empty(ShareGroupStore.GetAll(conn, _time.GetUtcNow()));
    }

    [Fact]
    public async Task CheckAsync_KeyRevoked_MarksNeedsReconnectAndKeepsGroup()
    {
        SaveGroup();
        _cal.On(HttpMethod.Get, "https://api.cal.com/v2/bookings", HttpStatusCode.Unauthorized, "{}");

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.NeedsReconnect, _hub.StateOf("calcom"));
        using var conn = _db.Database.Open();
        Assert.Single(ShareGroupStore.GetAll(conn, _time.GetUtcNow()));
    }

    [Fact]
    public async Task CheckAsync_Offline_DoesNotThrowAndKeepsGroup()
    {
        SaveGroup();
        _cal.Throw(new HttpRequestException("offline"));

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        using var conn = _db.Database.Open();
        Assert.Single(ShareGroupStore.GetAll(conn, _time.GetUtcNow()));
    }

    [Fact]
    public async Task CheckAsync_WhileWaitingOn429_SendsNothing()
    {
        SaveGroup();
        _hub.WaitFor("calcom", TimeSpan.FromMinutes(1));

        await _watcher.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_cal.Requests);
    }
}
```

- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement** `BookingWatcher` and the `GoogleServices` change (doc the new parameter: "Runs after each sync pass; scheduling booking checks").
- [ ] **Step 4: Run to see them pass**, full suite, 0-warning build.
- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core tests/LeafCalendar.Tests/BookingWatcherTests.cs
git commit -m "feat(scheduling): after each sync Leaf checks for bookings on saved times and closes the groups they land in"
```

---

### Task 9: Linked Copy Text

**Files:**
- Modify: `src/LeafCalendar.Core/People/AvailabilityText.cs` (add `FormatTimes`, `ComposeHtml`)
- Create: `src/LeafCalendar.Core/Scheduling/ShareLinks.cs`
- Test: `tests/LeafCalendar.Tests/AvailabilityTextTests.cs` (existing), `tests/LeafCalendar.Tests/ShareLinksTests.cs`

**Interfaces:**
- Produces:
```csharp
// AvailabilityText
public static IReadOnlyList<string> FormatTimes(IReadOnlyList<BusyRange> times, TimeZoneInfo zone, bool use24Hour); // "Tue Oct 13, 10–10:30 AM CT"
public static string ComposeHtml(string? message, IReadOnlyList<(string Text, Uri Link)> lines);                   // HTML fragment
// Scheduling
public sealed record LinkedTimes(string Plain, string Html, bool GuestsDropped);
public static class ShareLinks
{
    public static LinkedTimes Build(ITimeLinkBuilder builder, SchedulingLink link, IReadOnlyList<BusyRange> ranges, IReadOnlyList<BusyRange>? open,
        IReadOnlyList<Contact> guests, string? message, TimeZoneInfo zone, bool use24Hour);
}
```

Rules:
- `FormatTimes`: one line per time, `"{ddd MMM d}, {range} {zone label}"` using the existing private `Range` and `ZoneLabel` (en-US). A time crossing midnight in the zone still reads as one line with its start day.
- `ComposeHtml`: the message is HTML-encoded (`System.Net.WebUtility.HtmlEncode`), line breaks become `<br>`, and `{times}` is replaced by the lines, each `<a href="{encoded link}">{encoded text}</a>`, joined with `<br>`. Same placeholder rules as `Compose` (no placeholder: lines after the message on a new line; blank message: only the lines).
- `ShareLinks.Build`: split every range with `SlotFitting.Split(range, open, link.Minutes)`, format lines, build `builder.TimeLink(link, piece.Start, zone, guests)` for each, plain = `Compose(message, string.Join("\r\n", "{line}: {link}"))`, html = `ComposeHtml(message, pairs)`, `GuestsDropped = !link.AllowsGuests && guests.Count > 1`.

- [ ] **Step 1: Write the failing tests**

In `AvailabilityTextTests`:
```csharp
[Fact]
public void FormatTimes_OneLinePerTime()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    var start = new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero); // 10 AM CDT

    var lines = AvailabilityText.FormatTimes([new(start, start.AddMinutes(30)), new(start.AddMinutes(30), start.AddMinutes(60))], zone, use24Hour: false);

    Assert.Equal(["Tue Oct 13, 10–10:30 AM CT", "Tue Oct 13, 10:30–11 AM CT"], lines);
}

[Fact]
public void Html_EscapesMessageAndLines()
{
    var html = AvailabilityText.ComposeHtml("Hi <b>&</b>\r\n{times}", [("Tue <x>", new Uri("https://cal.com/a?date=1&slot=2"))]);

    Assert.Equal("Hi &lt;b&gt;&amp;&lt;/b&gt;<br><a href=\"https://cal.com/a?date=1&amp;slot=2\">Tue &lt;x&gt;</a>", html);
}

[Fact]
public void Html_NoPlaceholder_LinesAfterMessage()
{
    var html = AvailabilityText.ComposeHtml("Hi", [("A", new Uri("https://cal.com/a")), ("B", new Uri("https://cal.com/b"))]);

    Assert.Equal("Hi<br><a href=\"https://cal.com/a\">A</a><br><a href=\"https://cal.com/b\">B</a>", html);
}
```

`ShareLinksTests`:
```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class ShareLinksTests
{
    private static readonly CalComProvider Cal = new(new HttpClient(), "cal_live_x", CalComProvider.DefaultApiBase);
    private static readonly SchedulingLink Link = new("calcom", "101", "30 min", 30, new Uri("https://cal.com/leaf-tester/30min"), AllowsGuests: false);
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly DateTimeOffset Ten = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_SplitsAndLinksEachTime()
    {
        var result = ShareLinks.Build(Cal, Link, [new BusyRange(Ten, Ten.AddHours(1))], null, [], "Pick one:\r\n{times}", Chicago, use24Hour: false);

        Assert.Equal(
            "Pick one:\r\nTue Oct 13, 10–10:30 AM CT: https://cal.com/leaf-tester/30min?date=2026-10-13&slot=2026-10-13T15:00:00.000Z\r\n"
            + "Tue Oct 13, 10:30–11 AM CT: https://cal.com/leaf-tester/30min?date=2026-10-13&slot=2026-10-13T15:30:00.000Z",
            result.Plain);
        Assert.Equal(2, result.Html.Split("<a href=", StringSplitOptions.None).Length - 1);
        Assert.False(result.GuestsDropped);
    }

    [Fact]
    public void Build_TwoGuestsOnLinkWithoutGuests_FlagsDropped()
    {
        var result = ShareLinks.Build(Cal, Link, [new BusyRange(Ten, Ten.AddMinutes(30))], null, [new("", "a@example.com"), new("", "b@example.com")], "", Chicago, false);

        Assert.True(result.GuestsDropped);
        Assert.DoesNotContain("b%40example.com", result.Plain, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement** (reuse the private `Range` and `ZoneLabel`; don't duplicate the clock formatting).
- [ ] **Step 4: Run to see them pass**, full suite.
- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core tests/LeafCalendar.Tests
git commit -m "feat(scheduling): copied times can carry a booking link each, as rich text links with a plain fallback"
```

---

### Task 10: Moving A Picked Time (Drag Math)

**Files:**
- Modify: `src/LeafCalendar.Core/Views/DragMath.cs`
- Test: `tests/LeafCalendar.Tests/DragMathTests.cs` (existing)

**Interfaces:**
- Produces: `public static (DateTimeOffset Start, DateTimeOffset End) DragMath.MoveRange(DateTimeOffset start, DateTimeOffset end, DateTimeOffset grabbedAt, DateTimeOffset pointerAt, TimeZoneInfo zone)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void MoveRange_KeepsLengthAndSnaps()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    var start = new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    var (s, e) = DragMath.MoveRange(start, start.AddMinutes(45), start.AddMinutes(10), start.AddMinutes(10 + 37), zone);

    Assert.Equal(start.AddMinutes(30), s);   // +37 minutes snaps to +30
    Assert.Equal(TimeSpan.FromMinutes(45), e - s);
}

[Fact]
public void MoveRange_ToAnotherDay()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    var start = new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    var (s, _) = DragMath.MoveRange(start, start.AddHours(1), start, start.AddDays(1), zone);

    Assert.Equal(start.AddDays(1), s);
}

[Fact]
public void MoveRange_AcrossFallBack_KeepsInstantLength()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    var start = new DateTimeOffset(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);

    var (s, e) = DragMath.MoveRange(start, start.AddHours(1), start, start.AddDays(1), zone);

    Assert.Equal(TimeSpan.FromHours(1), e - s);
}
```

- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement** next to `ResizeRange`:

```csharp
/// <summary>
/// A picked time grabbed at <paramref name="grabbedAt"/> and dropped at <paramref name="pointerAt"/>: its start moves by
/// the same amount and snaps, and it keeps its length (an instant length, so a DST day doesn't stretch it).
/// </summary>
public static (DateTimeOffset Start, DateTimeOffset End) MoveRange(DateTimeOffset start, DateTimeOffset end, DateTimeOffset grabbedAt, DateTimeOffset pointerAt, TimeZoneInfo zone)
{
    var moved = Snap(start + (pointerAt - grabbedAt), zone);
    return (moved, moved + (end - start));
}
```

- [ ] **Step 4: Run to see them pass.**
- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Views/DragMath.cs tests/LeafCalendar.Tests/DragMathTests.cs
git commit -m "feat(share): the math for moving a picked time, keeping its length"
```

---

### Task 11: Booking Notification Content

**Files:**
- Modify: `src/LeafCalendar.Core/Alerts/ToastArgs.cs` (new action `OpenDay`, needs `start`)
- Modify: `src/LeafCalendar.Core/Alerts/ToastContent.cs` (new `Booking` builder and `BookingGroup`)
- Test: `tests/LeafCalendar.Tests/ToastArgsTests.cs`, `ToastContentTests.cs` (existing; find with grep)

**Interfaces:**
- Produces: `ToastAction.OpenDay` ("Open the main window on the day of Start"); `public const string ToastContent.BookingGroup = "bookings";` `public static ToastMessage ToastContent.Booking(string bookingId, string attendeeName, string when, DateTimeOffset start, string profile, bool sound)`.

Toast text: headline `"{attendee} booked {when}"` or `"Someone booked {when}"` when the name is empty (spec 11). `when` is built by the App with `ToastContent.When`-style formatting (time in the display zone). Tag: `"booking-" + Alert.TagFor(bookingId)` (check `Alert.TagFor` hashes to a short safe tag). Launch: `new ToastArgs(ToastAction.OpenDay, profile, Start: start)`. No buttons.

- [ ] **Step 1: Write the failing tests**

```csharp
// ToastArgsTests
[Fact]
public void Parse_OpenDay_NeedsStart()
{
    var start = new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
    var text = new ToastArgs(ToastAction.OpenDay, "default", Start: start).Encode();

    Assert.Equal(start, ToastArgs.Parse(text)!.Start);
    Assert.Null(ToastArgs.Parse("action=OpenDay;profile=default"));
}

// ToastContentTests
[Fact]
public void Booking_EscapesNameAndOpensTheDay()
{
    var start = new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

    var toast = ToastContent.Booking("bk1", "Jane <script>", "Tue Oct 13, 9 AM", start, "default", sound: true);

    Assert.Contains("Jane &lt;script&gt; booked Tue Oct 13, 9 AM", toast.Xml, StringComparison.Ordinal);
    Assert.Contains("action=OpenDay", toast.Xml, StringComparison.Ordinal);
    Assert.Equal(ToastContent.BookingGroup, toast.Group);
}

[Fact]
public void Booking_NoName_SaysSomeone()
{
    var toast = ToastContent.Booking("bk1", "", "Tue Oct 13, 9 AM", DateTimeOffset.UnixEpoch, "default", sound: false);

    Assert.Contains("Someone booked Tue Oct 13, 9 AM", toast.Xml, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement.** In `ToastArgs.Parse`'s "What Each Action Needs", add `|| (action == ToastAction.OpenDay && start is null)`. Build the toast with the existing private `Build`.
- [ ] **Step 4: Run to see them pass.**
- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Alerts tests/LeafCalendar.Tests
git commit -m "feat(scheduling): a notification that says who booked, opening that day when clicked"
```

---

### Task 12: App Wiring

**Files:**
- Modify: `src/LeafCalendar.App/LeafServices.cs`
- Modify: `src/LeafCalendar.App/App.xaml.cs` (booking toast, `OpenDay` activation)
- Modify: `src/LeafCalendar.App/MainWindow.xaml.cs` if it lacks a "show this day" entry point (mirror how `RevealEvent` reaches the view model and calls `NavigateTo(DateOnly)`)

**Interfaces:**
- Consumes: `SchedulingHub`, `BookingWatcher`, `ToastContent.Booking`, `ToastAction.OpenDay`.
- Produces on `LeafServices`: `public SchedulingHub Scheduling { get; }`, `public BookingWatcher Bookings { get; }`.

Steps:

- [ ] **Step 1:** In the `LeafServices` constructor, after `_http` is created:
```csharp
// Scheduling Providers (nothing is built until one is connected; under --fake-google they talk to the fake too)
Scheduling = new SchedulingHub(_http, Tokens, Log, Time, options.FakeGoogle);
Bookings = new BookingWatcher(Scheduling, Database, Log, Time);
```
After settings are loaded (find where `SettingsStore.Load` runs at startup; if it's in the view model, call `Scheduling.Load(settings.Scheduling)` from there once settings are read), call `Scheduling.Load(...)`. Also call it in the existing secrets-retry success path (where `_secretsRetry` is cleared and Google rebuilt).
- [ ] **Step 2:** In `CreateGoogle()`, pass `afterSync: Bookings.CheckAsync` to `new GoogleServices(...)`.
- [ ] **Step 3:** In `App.xaml.cs`, subscribe once services exist: `services.Bookings.BookingFound += OnBookingFound;` and `services.Bookings.GroupsChanged += (_, _) => _dispatcher?.TryEnqueue(() => _window?.ViewModel.ReloadGroups());` (use the real path to the main view model). `OnBookingFound` (an event raised on the sync thread) marshals to the UI thread, reads settings, and when `BookingNotifications` is on, shows `ToastContent.Booking(e.Booking.Id, e.Booking.AttendeeName, when, e.Booking.Start, profile, settings.NotificationSound)` through the notifier, where `when` is formatted like the other toasts (`ToastContent.When` needs an occurrence; add a small `ToastContent.WhenAt(DateTimeOffset start, TimeZoneInfo zone, bool use24Hour, DateTimeOffset now)` in Core with a test if no instant-based helper exists). Wrap the handler body in `try/catch (Exception ex)` logging `scheduling.booking.toast.failed` with the type name.
- [ ] **Step 4:** In the toast activation switch (before the "About An Event" lookup), add:
```csharp
case ToastAction.OpenDay:
    ShowMainWindow();
    _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => _window?.ShowDay(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(toast.Start!.Value, _zone.Zone).DateTime)));
    return;
```
and add `MainWindow.ShowDay(DateOnly day)` that calls the calendar view model's `NavigateTo(day)` (doc comment: "Shows the calendar on a day (a booking notification's click)").
- [ ] **Step 5:** Build with 0 warnings; run the full logic suite (unchanged, must pass).
- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.App
git commit -m "feat(scheduling): providers load at startup, bookings are checked after each sync, and a booking shows a notification"
```

---

### Task 13: Settings Pages

**Files:**
- Create: `src/LeafCalendar.App/Views/Settings/SchedulingPage.xaml`, `SchedulingPage.xaml.cs`
- Modify: `src/LeafCalendar.App/Views/Settings/SettingsPage.xaml.cs` (nav item between Calendars and Time zones; add `SettingsSection.Scheduling` to the enum wherever it lives)
- Modify: `src/LeafCalendar.App/Views/Settings/NotificationsPage.xaml`, `.xaml.cs` (Booking notifications card)

Build it like the other Settings pages (read `CalendarsPage.xaml` and `AccountsPage.xaml` first for styles: `LeafSettingsPagePanelStyle`, `LeafSettingsPageTitleStyle`, `controls:SettingRow`, `LeafSettingExpanderStyle`, `LeafCardToggleSwitchStyle`, `ScrollViewer x:Name="PageScroll"`).

- [ ] **Step 1: Nav item.** `(SettingsSection.Scheduling, NavItem("Scheduling", 0xE787, "SettingsNav_Scheduling"), typeof(SchedulingPage))` placed after Calendars. Use glyph `0xEC92` (Calendar reply) or another stock Segoe Fluent glyph that isn't already used by a sibling; note the choice in the report.
- [ ] **Step 2: Page layout.** Title "Scheduling". For each `SchedulingProviders.All` entry, one `Expander` (`LeafSettingExpanderStyle`, `AutomationId` `SchedulingCard_{id}`) whose header `SettingRow` shows the provider name and description, and on the right:
  - NotConnected: a **Connect…** button (`SchedulingConnect_{id}`) that expands the card.
  - Connected: nothing on the right; the expander body shows the **Default link** row (`ComboBox` `SchedulingDefaultLink_{id}`, items "{name} · {minutes} min" from `Scheduling.GetLinksAsync`, a concrete `List<LinkItem>`) and a **Disconnect** button (`SchedulingDisconnect_{id}`).
  - NeedsReconnect: header description replaced by "Cal.com stopped accepting your API key. Reconnect to keep sharing booking links." and a **Reconnect…** button.
  - Connect form (inside the expander body while connecting): one row per `SetupField` — `PasswordBox` for secrets, `TextBox` otherwise, `Header` = label, `PlaceholderText` = placeholder, `AutomationId` `SchedulingField_{id}_{key}` — then **Connect** (accent) and **Cancel**, a `ProgressRing` while connecting, and an `InfoBar` (Severity Error, not closable, `AutomationProperties.LiveSetting="Assertive"`, `SchedulingError_{id}`) for problems.
- [ ] **Step 3: Behavior (code-behind).**
  - Connect click: `SchedulingHub.Check` → show its message and stop. Else disable the form, `await Scheduling.ConnectAsync(info, values, ct)`. Success: save `settings.WithProvider(new(id, server, links.FirstOrDefault()?.Id ?? ""))` through `SettingsContext.Save`, clear the boxes (the key never stays in a box), show the connected state. `SchedulingAuthException` → the field's `InvalidMessage` for `api_key`. `SchedulingUnavailableException` → "Couldn't reach Cal.com. Check your connection and try again." Any other exception → log type name, same "Couldn't reach" text.
  - Default link change: save `WithProvider(current with { DefaultLinkId = id })`.
  - Disconnect: `Scheduling.Disconnect(id)`, save `WithoutProvider(id)`, then on the thread pool `ShareGroupStore.ClearProvider(conn, id)`, then the calendar view model's `ReloadGroups()`.
  - Subscribe to `Scheduling.Changed` on `Loaded`, unsubscribe on `Unloaded`, marshal with `DispatcherQueue.TryEnqueue`.
  - All `async void` handlers catch every exception.
- [ ] **Step 4: Notifications card.** After "Invite notifications", a `SettingRow` Glyph `&#xE8BF;` Header "Booking notifications" Description "Show a notification when someone books a time you shared." with `ToggleSwitch x:Name="BookingsSwitch"` (`AutomationId` `BookingNotificationsSwitch`, `AutomationProperties.Name="Booking notifications"`), visible only while `Scheduling.Connected.Count > 0`, saving `BookingNotifications` like the invites switch does.
- [ ] **Step 5:** Build (0 warnings) and run `XamlLintTests` + `NamespaceLayoutTests` (they run with the logic suite).
- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.App/Views/Settings
git commit -m "feat(scheduling): Settings › Scheduling connects Cal.com and picks a default link, and booking notifications can be turned off"
```

---

### Task 14: Share View Model

**Files:**
- Create: `src/LeafCalendar.App/ViewModels/CalendarViewModel.Scheduling.cs` (new partial; keeps `People.cs` from growing)
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs` (hooks in `StartSharing`, `OpenGroup`, `StopSharing`, `AddShareSlot`, `UpdateShareSlot`, `ResizeGridSlot`, `SaveShare`, `CopyAndSaveShare`, `ApproveSlot`, `GridSlots`)

**State added (in the new partial, with doc comments):**
```csharp
private bool _useProvider;                       // the panel's switch
private SchedulingLink? _shareLink;              // the panel's link (null with the switch off or no provider)
private IReadOnlyList<SchedulingLink> _links = [];
private IReadOnlyList<BusyRange>? _openTimes;    // null: unknown (offline, not fetched)
private (DateTimeOffset From, DateTimeOffset To)? _openRange;
private int _openGeneration;
private readonly List<string> _shareMessages = []; // newest first, at most 4

public bool HasScheduling => _services.Scheduling.Connected.Count > 0;
public bool UseProvider { get; }                 // with SetUseProvider(bool)
public SchedulingLink? ShareLink { get; }        // with SetShareLink(SchedulingLink?)
public IReadOnlyList<SchedulingLink> ShareLinks => _links;
public IReadOnlyList<BusyRange>? OpenTimes => _openTimes;
public IReadOnlyList<string> ShareMessages => _shareMessages;
public event EventHandler? ShareMessagesChanged;
public void SayShare(string text);               // push newest first, trim to 4, raise
public void DismissShareMessage(string text);    // the page's per-card timer
```

**Behavior:**
- `StartSharing`: when `HasScheduling`, `_useProvider = true`, then `LoadLinksAsync()` (fires, sets `_links` from `Scheduling.GetLinksAsync`, picks `Settings.ProviderSettingsFor(provider).DefaultLinkId` else the first, raises `ShareChanged`, then `RefreshOpenTimesAsync()`).
- `OpenGroup`: `_useProvider = group.ProviderId.Length > 0`. Load links; when the group's link isn't among them, `_useProvider = false` and `SayShare("That link isn't on Cal.com anymore. These times will copy as plain text.")`. Otherwise `_shareLink` = that link.
- `RefreshOpenTimesAsync()`: with the switch on and a link, the range is the days on screen (`VisibleRange` or however the grid's first/last day instants are read — use the same range the people overlay uses, `OverlayRange()`). Skip if `_openRange` already covers it for this link. Generation counter; fetch on the thread pool through `IOpenTimesSource`; on `SchedulingUnavailableException`/`SchedulingAuthException` set `_openTimes = null` and `SayShare("Couldn't reach Cal.com, so times aren't limited to your openings.")` (auth also calls `Scheduling.MarkNeedsReconnect`). Raise `ShareChanged` so the grid redraws the shade. Call it after navigation (where `RefreshOverlayAsync` is called on range change) and on link change.
- `StopSharing`: clear `_openTimes`, `_openRange`, `_shareLink`, `_links`, `_useProvider` (RAM rule), but not `_shareMessages` (they fade on their own).
- **Fitting:** `AddShareSlot(start, end)`, `UpdateShareSlot`, and the resize/move paths go through one helper:
```csharp
// With a link on and its openings known, a pick is fitted to them and the messages say what happened; otherwise it's kept as dragged
private IReadOnlyList<BusyRange> FitPick(BusyRange pick)
{
    if (!_useProvider || _shareLink is null || _openTimes is null)
    {
        return [pick];
    }

    var fit = SlotFitting.Fit(pick, _openTimes);
    foreach (var message in FitMessages.For(fit.Summary, ProviderName(_shareLink)))
    {
        SayShare(message);
    }

    return fit.Ranges;
}
```
  `AddShareSlot` merges `FitPick(new(start, end))` into `_slots`. `UpdateShareSlot(index, …)` removes the old one, then merges the fitted result (nothing left = the time is gone).
- `SetShareLink(link)` and `SetUseProvider(true)`: refetch open times, then when they're known, `_slots = SlotFitting.FitAll(_slots, _openTimes)` with messages. `SetUseProvider(false)` keeps picks.
- `MoveShareSlot(GridSlot slot, DateTimeOffset start, DateTimeOffset end)`: public, used by the grid (Task 16). Same guard as `ResizeGridSlot` (the slot still matches), then `UpdateShareSlot(slot.Index, start, end)` — so plain shares merge and provider shares refit.
- **Saving:** a helper `CurrentLink()` returns `_useProvider && _shareLink is { } l ? new ShareLink(l.ProviderId, l.Id, l.BookingUrl.AbsoluteUri, l.Minutes) : ShareLink.None`, passed to every `Insert`/`Update` in `SaveShare`, `CopyAndSaveShare`, and `UpdateSavedSlot` (that last one keeps the group's own: `new ShareLink(group.ProviderId, group.LinkId, group.LinkUrl, group.LinkMinutes)`).
- **Copy:** in `CopyAndSaveShare`, when `_useProvider && _shareLink is { } link && Scheduling.Get(link.ProviderId) is ITimeLinkBuilder builder`:
```csharp
var linked = ShareLinks.Build(builder, link, BusyMath.Merge(_slots), _openTimes, _shareGuests.ToList(), _shareText, zone, Settings.Use24HourTime);
package.SetText(linked.Plain);
package.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(linked.Html));
if (linked.GuestsDropped)
{
    SayShare($"This Cal.com link doesn't allow extra guests, so only {FirstGuestLabel()} is filled in.");
}
```
  (`FirstGuestLabel` = the first guest's name, else address.) Otherwise the existing plain path. The "Cal.com" in that string comes from the provider's `Name`.
- **Approve…** is Task 17.
- **Messages and the existing notice:** `SayShare` is only for scheduling messages while sharing; "Availability copied" stays on the existing notice.

- [ ] **Step 1:** Implement the partial and hooks above. Keep every database read off the UI thread as the existing code does (`ReloadGroups` pattern).
- [ ] **Step 2:** Build (0 warnings), full logic suite.
- [ ] **Step 3:** No unit-test harness exists for the App; behavior is covered by Task 18's UI tests. Say so in the report.
- [ ] **Step 4: Commit**

```bash
git add src/LeafCalendar.App/ViewModels
git commit -m "feat(scheduling): sharing with a booking link fits picks to its openings and copies a link per time"
```

---

### Task 15: Share Panel, Message Stack, Grid Shade

**Files:**
- Modify: `src/LeafCalendar.App/Views/ShareSlotsPanel.cs` (switch and link box)
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml` (message stack above `SharingHint`), `CalendarPage.xaml.cs` or `CalendarPage.People.cs` (fill it, per-card timers)
- Modify: `src/LeafCalendar.App/Controls/DayColumn.cs` (draw the closed-time shade), `TimeGridView.cs` if the shade needs the view model's `OpenTimes`
- Modify: `src/LeafCalendar.App/Styles/LeafBrushes.cs` (or wherever code-built brushes live) for the shade brush per theme and HighContrast

- [ ] **Step 1: Panel.** In `ShareSlotsPanel`, under the hint and above Title, when `vm.HasScheduling`:
  - `ToggleSwitch` (`OnContent`/`OffContent` empty, `Header` = "Use Cal.com" — or "Use a booking link" with two connected providers — `AutomationId` `ShareProviderSwitch`), `IsOn` = `vm.UseProvider`, `Toggled` → `vm.SetUseProvider`.
  - `ComboBox` (`Header` "Link", `AutomationId` `ShareLinkBox`, `HorizontalAlignment="Stretch"`), visible only while on, `ItemsSource` a concrete `List<string>` of "{name} · {minutes} min" (keep a parallel `List<SchedulingLink>`; never read the WinRT collection back), `SelectionChanged` → `vm.SetShareLink(links[index])`.
  - Rebuild on `ShareChanged` the way the panel rebuilds other parts (read how it refreshes the guest list) without resetting focus.
- [ ] **Step 2: Message stack.** In `CalendarPage.xaml`, inside `Toasts`, before `SharingHint`:
```xml
<!-- Scheduling Messages (newest on top, each fading on its own; above the hint while sharing) -->
<StackPanel
    x:Name="ShareMessagesPanel"
    Spacing="8"
    AutomationProperties.LiveSetting="Polite"
    AutomationProperties.AutomationId="ShareMessages" />
```
  In code, on `ShareMessagesChanged` (marshal to the UI thread), rebuild children: one `InfoBar` per message with the same `CloseButtonStyle`, `BorderBrush`, `IsOpen="True"`, `IsClosable="False"`, `Severity="Informational"`, `Message` = text, `AutomationId` `ShareMessage_{i}` (top first), raised with the same float/shadow the other toasts use (`CalendarPage.Float`). Each card gets its own `DispatcherQueueTimer` for the notice's usual time (reuse the notice's interval constant), after which `vm.DismissShareMessage(text)`. Fades follow the design standard (167 ms) and honor the system animation setting the way `NoticeBar` does.
- [ ] **Step 3: Grid shade.** While `vm.IsSharing && vm.UseProvider && vm.OpenTimes is { } open`, `DayColumn` draws a rectangle (brush `LeafBrushes.ClosedTime`, per theme, a HighContrast entry, re-applied on `ActualThemeChanged`) over each stretch of the day column with no open time (compute gaps from the merged open times clipped to the column's day). Drawn behind events and picks, `IsHitTestVisible="False"`. Nothing drawn when `OpenTimes` is null.
- [ ] **Step 4:** Build (0 warnings), logic suite (XAML lint).
- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.App
git commit -m "feat(scheduling): the share panel picks a booking link, closed times are shaded, and messages stack over the calendar"
```

---

### Task 16: Moving Picked Times On The Grid

**Files:**
- Modify: `src/LeafCalendar.App/Controls/TimeGridView.cs`, `src/LeafCalendar.App/Controls/DayColumn.cs` (cursor)

- [ ] **Step 1:** Add `MoveSlot` to `DragKind` and a `GrabbedAt` use for it. In `BeginCreateDrag`, after the resize-edge check:
```csharp
// Inside A Picked Time While Sharing (new picks or an open group): dragging moves it, keeping its length
if (_vm.IsSharing && SlotAt(day, minutes) is { Slot: { Title: null } slot, Edge: SlotEdge.Inside })
{
    _drag = new DragSession(DragKind.MoveSlot, e.GetCurrentPoint(this).Position) { Slot = slot, GrabbedAt = DragMath.Instant(day, minutes, _vm.Zone) };
    return;
}
```
  (`Title: null` marks the open picks; saved groups not open have a title and keep today's behavior.)
- [ ] **Step 2:** In the target computation switch:
```csharp
case DragKind.MoveSlot:
    var (moveStart, moveEnd) = DragMath.MoveRange(drag.Slot!.Range.Start, drag.Slot.Range.End, drag.GrabbedAt, pointerAt, zone);
    return (moveStart, moveEnd, false, false);
```
- [ ] **Step 3:** On release, before the resize branch:
```csharp
// A Picked Time Moved: it lands there (merged, or fitted to the link's openings); a press that didn't move changes nothing
if (drag.Kind == DragKind.MoveSlot)
{
    if (target.Start != drag.Slot!.Range.Start)
    {
        _vm.MoveShareSlot(drag.Slot, target.Start, target.End);
    }

    return;
}
```
  Make sure the drag only counts as started after the usual drag threshold (follow how `Create` waits for `Started`), so a click is a click.
- [ ] **Step 4:** Cursor: over a picked time's inside while sharing, show the move cursor (`InputSystemCursorShape.SizeAll`), the same way `ResizableEdgeAt` drives the up-down cursor. Add `MovableAt(DateOnly day, double y)` beside `ResizableEdgeAt`.
- [ ] **Step 5:** Update `docs/superpowers/specs/2026-10-06-saved-share-groups-design.md`: in section 6 replace "Inside an open slot (not an edge) while sharing: a drag picks a new time, as today." with "Inside an open slot (not an edge) while sharing: a drag moves that time (spec 2026-10-12 scheduling providers, section 6)." and remove "Moving a slot by dragging its middle (only edges resize)." from Out of Scope.
- [ ] **Step 6:** Build (0 warnings), logic suite.
- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.App/Controls docs/superpowers/specs/2026-10-06-saved-share-groups-design.md
git commit -m "feat(share): drag a picked time to move it, on any share"
```

---

### Task 17: Approve… Books On Cal.com

**Files:**
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.Scheduling.cs`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs` (`ApproveSlot` routes to the provider path)
- Modify: `src/LeafCalendar.App/Views/ShareSlotsPanel.cs` (time-choice flyout, confirmation dialog)

**Interfaces:**
- Produces on the view model:
```csharp
public bool ApprovesThroughProvider { get; }   // _useProvider, a link, and Scheduling.Get(...) is IBookingCreator, and an open group
public Task<IReadOnlyList<BusyRange>> BookableTimesAsync(int index, CancellationToken ct); // fresh openings split inside saved time #index
public Task<BookResult> BookAsync(BusyRange time, CancellationToken ct);
public enum BookResult { Booked, Taken, Unavailable, NeedsReconnect }
```

- [ ] **Step 1: View model.**
  - `BookableTimesAsync(index)`: fetch fresh open times for that saved range through `IOpenTimesSource` (thread pool), then `SlotFitting.Split(range, open, link.Minutes)`. Unavailable/auth → empty list plus the result surfaced by the caller.
  - `BookAsync(time)`: guard: open group, a guest, the link (`IBookingCreator`). Calls `BookAsync(link, time.Start, guests, zone, ct)` on the thread pool. Booked → delete the group (`WriteGroups`), `StopSharing()`, `ShowMessage("Booked on Cal.com.")` (provider name from `Name`), return `Booked`. `SchedulingSlotTakenException` → `SayShare("That time was just taken on Cal.com. Pick another.")`, force-refresh open times (clear `_openRange` first), return `Taken`. `SchedulingUnavailableException` → `SayShare("Couldn't reach Cal.com. Nothing was booked.")`, `Unavailable`. `SchedulingAuthException` → `Scheduling.MarkNeedsReconnect`, `NeedsReconnect`. Log only `scheduling.book.*` with the result and exception type.
  - `ApproveSlot(index)`: when `ApprovesThroughProvider`, do nothing here (the panel drives the provider flow); else today's editor flow, unchanged.
- [ ] **Step 2: Panel.** When `ApprovesThroughProvider`, the row's Approve… click:
  1. `await vm.BookableTimesAsync(index)`. Empty → (the view model already said why) stop. One → step 2. Several → a `MenuFlyout` under the button with one item per time ("Tue Oct 13, 10–10:30 AM CT" via `AvailabilityText.FormatTimes`), `AutomationId` `ApproveTime_{i}`.
  2. A `ContentDialog` (`XamlRoot` set): Title "Book {time} on Cal.com?", content listing the guests (names, else addresses), `PrimaryButtonText` "Book" (`DefaultButton = Primary`), `CloseButtonText` "Cancel". `AutomationId`s `BookDialog`, and on the buttons via the dialog's template as other dialogs in the app do (see `ScopeDialog.cs`).
  3. Book: disable the panel's buttons, show a `ProgressRing` in the row, `await vm.BookAsync(time)`, re-enable when not `Booked`.
  4. All of it in an `async void` handler that catches every exception (log type name, `SayShare("Couldn't reach Cal.com. Nothing was booked.")`).
- [ ] **Step 3:** Build (0 warnings), logic suite.
- [ ] **Step 4: Commit**

```bash
git add src/LeafCalendar.App
git commit -m "feat(scheduling): Approve… books the time on Cal.com after you confirm"
```

---

### Task 18: Fake Cal.com And UI Tests

**Files:**
- Modify: `tests/LeafCalendar.UITests/Support/FakeGoogleServer.cs` (routes under `/calcom/v2/`)
- Create: `tests/LeafCalendar.UITests/SchedulingProviderTests.cs`
- Modify: `tests/LeafCalendar.UITests/SavedShareGroupTests.cs` (move tests)
- Modify: `tests/LeafCalendar.UITests/ScreenshotTour.cs` (Scheduling page, share panel with switch)

- [ ] **Step 1: Fake routes.** Add to `FakeGoogleServer`:
  - `public bool CalComKeyValid { get; set; } = true;` (checked against the bearer token `cal_live_uitest`; requires reading the `authorization` header, which `headers` already holds)
  - `public List<(DateTimeOffset Start, DateTimeOffset End)> CalComOpen { get; } = [];` (bookable times; default: 30-minute times 9 AM–5 PM Chicago on the two days after `--start-date`, set by tests)
  - `public ConcurrentQueue<string> CalComBookings { get; } = new();` (POST bodies)
  - `public void AddCalComBooking(DateTimeOffset start, string name)` (shows up in the next bookings GET, `createdAt` = now)
  - `public bool CalComTaken { get; set; }` (next POST answers 409)
  - `GET /calcom/v2/event-types` → two links (101 "30 min meeting", 30, guests allowed, `bookingUrl` `https://cal.com/leaf-tester/30min`; 102 "Quick chat", 15, `disableGuests: true`), 401 when the key is wrong.
  - `GET /calcom/v2/slots` → `CalComOpen` within `start`/`end`, grouped by day, `format=range` shape.
  - `GET /calcom/v2/bookings` → the added bookings after `afterCreatedAt` for `eventTypeId`.
  - `POST /calcom/v2/bookings` → records the body; 409 when `CalComTaken`; else the created booking.
  Add a `FakeGoogleServerTests` case for each route (that class already tests the fake itself).
- [ ] **Step 2: `SchedulingProviderTests`** (one test per behavior; follow `SavedShareGroupTests` for launching with `--fake-google`, `--start-date`, `--now`, a throwaway profile, reading the clipboard, and dragging on the grid):
  1. `NoProvider_ShowsNoSwitch` — S, no `ShareProviderSwitch`.
  2. `Connect_BadKey_ShowsError_GoodKey_Connects` — Settings › Scheduling, Connect…, type `cal_live_wrong`, Connect → `SchedulingError_calcom` shows the bad key text; type `cal_live_uitest` → connected, default link box lists two links.
  3. `DefaultLink_StartsEachShare` — pick "Quick chat" as default, S → `ShareLinkBox` shows "Quick chat · 15 min".
  4. `Drag_OverClosedTime_IsFittedAndExplained` — open 9–10 only; drag 9:10–10:40 → one pick 9:00–10:00, `ShareMessage_*` cards include "Split into 2 times of 30 minutes." and "40 minutes wasn't open on Cal.com." (compute from the setup; assert the exact strings).
  5. `Copy_PutsLinksAndPlainText` — add guest `jane@example.com`, Copy & Save → clipboard text contains `slot=` and `email=jane%40example.com`; clipboard HTML format contains `<a href=`.
  6. `SwitchOff_CopiesPlainText` — toggle off, drag, Copy → no `slot=`.
  7. `GuestsNotAllowed_SaysSo` — link 102, two guests, Copy → message "This Cal.com link doesn't allow extra guests, so only jane@example.com is filled in."
  8. `Booking_RemovesGroupAndNotifies` — Copy & Save, `AddCalComBooking(inside range, "Jane Doe")`, wait for sync → group gone from the grid; notification check per `NotificationTests` pattern (`--toast-action` or reading Leaf's log for `scheduling.booking.found`).
  9. `OpenSavedGroup_MoveAndRefit` — open a saved Cal.com group, move a time onto a partly closed stretch → fitted; change link → every time refitted; Save; restart (`--restarted`) → switch on, link kept.
  10. `Approve_ConfirmBooks_CancelDoesNot_TakenKeepsGroup` — Approve… on a one-time range → `BookDialog`; Cancel → no POST; Approve… again → Book → POST body has `attendee.email` and the group is gone, "Booked on Cal.com." shows; with `CalComTaken` → group stays and the taken message shows; a longer range → `ApproveTime_0` flyout appears.
  11. `Disconnect_HidesSwitch_GroupsBecomePlain`.
- [ ] **Step 3: Move tests** in `SavedShareGroupTests`: drag a new pick later and to another day; open a saved group, move a time, Save, reopen → moved; click inside a pick changes nothing; a closed saved group's time doesn't move.
- [ ] **Step 4:** `ScreenshotTour`: add the Scheduling page (not connected and connected) and the share panel with the switch on, light and dark.
- [ ] **Step 5:** `pwsh tools/sign-install.ps1`, then run, one at a time:
```bash
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SchedulingProviderTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SavedShareGroupTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*ShareAvailabilityTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*SettingsPagesTests"
dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*FakeGoogleServerTests"
```
  Expected: all pass. Fix the code (not the test's intent) on a failure.
- [ ] **Step 6:** Screenshots: set `LEAF_SCREENSHOTS` to a scratch folder, run `ScreenshotTour`, review the new shots against `docs/design-standard.md`.
- [ ] **Step 7: Commit**

```bash
git add tests/LeafCalendar.UITests
git commit -m "test(scheduling): UI tests against a fake Cal.com for connect, fitting, linked copy, bookings, moving and Approve…"
```

---

### Task 19: Live Cal.com Check

**Files:**
- Create: `tests/LeafCalendar.LiveTests/CalComLiveTests.cs`
- Modify: `tests/LeafCalendar.LiveTests/README.md`

- [ ] **Step 1:** Read the folder's README and an existing live test for how they skip until set up. Add a test that skips unless `LEAF_LIVE_CALCOM_KEY` is set (and optional `LEAF_LIVE_CALCOM_SERVER`), then: lists links (at least one), reads open times for the first link over the next 7 days, builds a time link for the first open time and asserts it starts with that link's booking URL. It never books (booking sends real emails).
- [ ] **Step 2:** Ask the owner for a throwaway Cal.com account key and run it. **Verify against the live answers:** the three `cal-api-version` values, `disableGuests`, `bookingUrl`, the `/v2/bookings` filter names (`eventTypeId`, `afterCreatedAt`, `status`, `take`) and the booking `attendees` shape. Fix `CalComProvider`, its fixtures, and spec 3.4 for anything that differs. Also confirm the taken-time answer on a test booking only if the owner agrees to a real booking on the throwaway account.
- [ ] **Step 3:** README: how to set the variables, what it checks, that it never books.
- [ ] **Step 4: Commit**

```bash
git add tests/LeafCalendar.LiveTests src/LeafCalendar.Core/Scheduling tests/LeafCalendar.Tests/Fixtures docs/superpowers/specs/2026-10-12-scheduling-providers-design.md
git commit -m "test(scheduling): a live Cal.com check, skipped until a test key is set"
```

---

### Task 20: Docs

**Files:**
- Modify: `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (sections 2, 7.6, 8, 9)
- Modify: `PRIVACY.md`

- [ ] **Step 1:** Section 2 decision log: add a row "Scheduling providers | One small interface per provider action; Cal.com first (2026-10-12, issue #28). No webhooks (no Leaf server); bookings are checked after each sync while saved provider times exist." Change the "Share availability" row to mention booking links.
- [ ] **Step 2:** Section 7.6: add bullets for the provider switch and link, fitting and messages, linked copy, moving picked times, booking detection, and Approve… booking on Cal.com, each pointing to `2026-10-12-scheduling-providers-design.md`.
- [ ] **Step 3:** Section 8 (notifications): the booking notification and its switch. Section 9 (Settings): the Scheduling page.
- [ ] **Step 4:** `PRIVACY.md`: add the Scheduling section exactly as spec 3.7 describes (what is sent to Cal.com and when, guest emails in copied links, key encrypted on this PC and removed on Disconnect). Show the new text to the owner in the report (it's user-facing content).
- [ ] **Step 5: Commit**

```bash
git add docs PRIVACY.md
git commit -m "docs(scheduling): main spec and privacy notes cover scheduling providers"
```

---

### Task 21: Finish — Verify, Audit, Install, PR

- [ ] **Step 1:** `dotnet build LeafCalendar.slnx -c Debug` → 0 warnings. Full logic suite → all pass (record the count).
- [ ] **Step 2:** `pwsh tools/publish-aot.ps1` → no trim/AOT warnings (new JSON context, interfaces, SQLite reads). `pwsh tools/check-package.ps1` → within budget.
- [ ] **Step 3:** `pwsh tools/sign-install.ps1`, then the full UI suite once on this optimized build (merge gate; never twice). If a block fails at once, re-run before diagnosing.
- [ ] **Step 4:** Memory budget: run `MemoryTests` three times on the installed build; record private bytes and working set in main spec 3.4 (budget ≤ 120 MB private bytes, ≤ 25 MB working set, tray-only) with today's date and "scheduling providers". Over budget → find the cause before going on.
- [ ] **Step 5: Audit.** Review every changed file against: the Security rules (no secrets or content in logs; untrusted input cleaned; links through `LinkSafety`; toast text escaped; HTML encoded), Threading (events from the sync thread marshaled; `async void` catch-all; no lock held across `await`), Lifetime (`Loaded`/`Unloaded` subscriptions), AOT rules, RAM rules (nothing built before connect; open times dropped on stop; no booking calls without provider groups), doc comments on every public member, Copy rules (every visible string is in spec section 11 or flagged). Use the `superpowers:requesting-code-review` skill for a whole-branch review. Fix findings, re-run the logic suite and the affected UI classes.
- [ ] **Step 6:** Commit any audit fixes (`fix(scheduling): …`).
- [ ] **Step 7:** `git push -u origin feat/scheduling-providers`, then open the PR with `gh pr create --base main --title "feat(scheduling): scheduling providers, with Cal.com first (#28)"` and a body that lists: what it does (in plain words), the tests run with real results, the memory numbers, the wording awaiting approval (spec section 11 and any flagged strings), and "Closes #28". Do not comment on the PR or change its status afterward.
- [ ] **Step 8:** Report to the owner: PR link, test results, what wasn't verified (anything the live test couldn't check), every decision made on their behalf (e.g. the Scheduling nav glyph, the "1 times" wording question).
