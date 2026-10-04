# Audit Deferred Items Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five defects the 2026-10-03 audit found but deferred as too large or risky for an audit pass.

**Architecture:** Five independent tasks, each one shippable alone: an outbox retry backoff (Core, new schema version), a slower tray sync on Energy Saver or a metered network (Core cadence rule, App wiring), secrets moved from the user-wide Credential Locker into the package's own data (Core store with a one-time migration), month view heights that follow the Windows text size (Core metrics, App controls), and the tray's database reads moved off the UI thread (measured first; may close as won't-fix).

**Tech Stack:** C# 14, .NET 10, WinUI 3 (Windows App SDK 2.x), Native AOT in Release, Microsoft.Data.Sqlite, xunit v3, FlaUI UI tests.

**Spec:** `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (sections 5.3 sync cadence, 5.4 outbox, 4.x secrets, 6.x month view, 8.x tray), plus the audit notes quoted in each task.

## Global Constraints

- Build: `dotnet build LeafCalendar.slnx -c Debug` with 0 warnings. Warnings are errors, and `.editorconfig` enforces Microsoft's C# conventions (written access modifiers, `_camelCase` private fields, `s_camelCase` private static fields, no column alignment, doc comments on public members).
- Logic tests: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug` from the repo root (the folder with `global.json`). One class: add `--filter-class "*ClassName"`.
- UI tests run against the installed package. Install the optimized build with `pwsh tools/sign-install.ps1` (an update; keeps the owner's settings and sign-in). Run only the classes a task names.
- Native AOT safe: no reflection, `x:Bind` only, `System.Text.Json` only through a source-generated context, concrete `List<T>` for `ItemsSource`.
- Never log event content, tokens, client secrets, or email addresses.
- US English. Sentence case for UI text. No new user-visible wording without the owner's approval (none of these tasks needs any).
- Files are CRLF; edit in place.
- Commit per task on a branch; never push or merge without the owner.

## Review Focus

1. **A write that keeps failing with a 5xx must never be dropped or undone.** Backoff only delays it; Undo must still treat it as possibly sent (Task 1, `Undo_AfterABackedOffWrite_KeepsIt`).
2. **A Sync now, a reconnect or a resume must not wait out a backoff.** The user expects an immediate try (Task 1, `ClearBackoff_LetsEveryEntryGoNow`).
3. **Losing Energy Saver or the metered connection mid-wait must speed up right away**, not after the slow interval (Task 2, the change events call `TriggerNow`).
4. **Upgrading must not sign anyone out.** Secrets in the Credential Locker move to the new store on first start, and a failed move leaves the Locker copy in place (Task 3, `Migrate_FromLocker_MovesEverySecretOnce`, `Migrate_WriteFails_KeepsTheLockerCopy`).
5. **At 225% text size the month view must still fit its chips and day numbers without clipping** (Task 4, `MonthMetrics_At225Percent_FitsTheText`).

---

### Task 1: Outbox Retry Backoff

Spec 5.4 item 7 asks for backoff on failed writes. Today a write that gets a 5xx is retried every sync (15 s visible, 60 s tray). The audit's suggested fix of setting `not_before` is unsafe: `not_before` is the delete undo window, and `EventEditor.Undo` treats an entry with a future `NotBefore` as never sent and removes it quietly. A write that got a 5xx may already be saved on Google. So the backoff gets its own column.

**Files:**
- Modify: `src/LeafCalendar.Core/Data/Schema.cs` (add `V8`)
- Modify: `src/LeafCalendar.Core/Data/LeafDatabase.cs` (apply version 8 after version 7)
- Modify: `src/LeafCalendar.Core/Data/OutboxStore.cs` (`OutboxEntry.RetryAfter`, `Columns`, `Map`, `RecordAttempt`, new `ClearBackoff`, new `Backoff`)
- Modify: `src/LeafCalendar.Core/Sync/OutboxSender.cs` (skip entries still backing off; `Defer` passes the time)
- Modify: `src/LeafCalendar.App/LeafServices.cs` (clear the backoff on reconnect and resume)
- Modify: `src/LeafCalendar.App/App.xaml.cs` (`SyncNow` clears the backoff)
- Test: `tests/LeafCalendar.Tests/OutboxStoreTests.cs`, `tests/LeafCalendar.Tests/OutboxSenderTests.cs`, `tests/LeafCalendar.Tests/LeafDatabaseTests.cs`, `tests/LeafCalendar.Tests/EventEditorTests.cs`

**Interfaces:**
- Produces: `OutboxEntry.RetryAfter` (`DateTimeOffset?`, last positional parameter, default `null`); `OutboxStore.RecordAttempt(SqliteConnection conn, long seq, string error, DateTimeOffset now)`; `OutboxStore.ClearBackoff(SqliteConnection conn)`; `OutboxStore.Backoff(int attempts)` returns `TimeSpan`.

- [ ] **Step 1: Write the failing tests**

In `OutboxStoreTests.cs`:

```csharp
[Theory]
[InlineData(1, 30)]
[InlineData(2, 60)]
[InlineData(3, 120)]
[InlineData(6, 900)]
[InlineData(40, 900)]
public void Backoff_DoublesFromThirtySecondsUpToFifteenMinutes(int attempts, int seconds) =>
    Assert.Equal(TimeSpan.FromSeconds(seconds), OutboxStore.Backoff(attempts));

[Fact]
public void RecordAttempt_SetsRetryAfterFromTheAttemptCount()
{
    using var conn = _db.Database.Open();
    var seq = OutboxStore.Add(conn, null, Entry());
    var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    OutboxStore.RecordAttempt(conn, seq, "status 503", now);
    OutboxStore.RecordAttempt(conn, seq, "status 503", now);

    var entry = OutboxStore.Get(conn, null, seq)!;
    Assert.Equal(2, entry.Attempts);
    Assert.Equal(now.AddSeconds(60), entry.RetryAfter);
}

[Fact]
public void ClearBackoff_LetsEveryEntryGoNow()
{
    using var conn = _db.Database.Open();
    var seq = OutboxStore.Add(conn, null, Entry());
    OutboxStore.RecordAttempt(conn, seq, "network", DateTimeOffset.UtcNow);

    OutboxStore.ClearBackoff(conn);

    Assert.Null(OutboxStore.Get(conn, null, seq)!.RetryAfter);
}
```

`Entry()` is the existing helper in `OutboxStoreTests` that builds a pending patch; reuse it (or add one returning `new OutboxEntry(0, "acc", "cal", "evt", OutboxOperation.Patch, "{}", null, false, null, null)`).

In `OutboxSenderTests.cs` (it already drives a fake Google and a `FakeTimeProvider`; follow its existing `Send_*` tests for setup):

```csharp
[Fact]
public async Task Send_GoogleFailsWithA503_WaitsOutTheBackoffBeforeTryingAgain()
{
    // Google answers 503 to the patch; the entry stays, and isn't tried again until 30 s later
    var ct = TestContext.Current.CancellationToken;
    QueuePatch();
    RouteEventPatch(HttpStatusCode.ServiceUnavailable);

    await _sender.SendAsync(Account, ct);
    await _sender.SendAsync(Account, ct);
    Assert.Equal(1, PatchCalls());

    _time.Advance(TimeSpan.FromSeconds(31));
    await _sender.SendAsync(Account, ct);
    Assert.Equal(2, PatchCalls());
}
```

`QueuePatch`, `RouteEventPatch` and `PatchCalls` are three small helpers to add to the class if it doesn't have equivalents: queue one patch of the seeded event through `OutboxStore.Add`; route the event's PATCH URL on the class's fake HTTP handler to the given status; count the PATCH requests the handler saw. If the class already has helpers doing these, use those and drop these names.

In `LeafDatabaseTests.cs`:

```csharp
[Fact]
public void Migrate_Version8_AddsRetryAfter()
{
    using var db = new TestDatabase();
    using var conn = db.Database.Open();

    var columns = conn.Query(null, "PRAGMA table_info(outbox);", r => r.GetString(1));

    Assert.Contains("retry_after", columns);
}
```

In `EventEditorTests.cs`:

```csharp
[Fact]
public void Undo_AfterABackedOffWrite_KeepsIt()
{
    // A patch that got a 5xx may already be on Google: backing off must not make Undo treat it as never sent
    var receipt = DeleteTheSingleEvent();
    using (var conn = _db.Database.Open())
    {
        OutboxStore.RecordAttempt(conn, receipt.Seq, "status 503", _time.GetUtcNow());
    }

    Assert.Equal(UndoResult.Restored, _editor.Undo(receipt));
}
```

Use the delete helper `EventEditorTests` already has for its existing `Undo_*` tests; the point of the test is that a backed-off entry follows the same Undo path as before (it doesn't look like a `NotBefore` hold). If `Undo` already returns a different value for an entry with `Attempts > 0`, assert that existing value instead and keep the test as the pin.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*OutboxStoreTests"`
Expected: build errors (`Backoff`, `RetryAfter`, the 4-argument `RecordAttempt` don't exist).

- [ ] **Step 3: Implement**

`Schema.cs`, after `V7`:

```csharp
/// <summary>
/// Version 8: <c>outbox.retry_after</c>, when a write Google failed (a 5xx, the network) is tried again. Kept apart
/// from <c>not_before</c>, the delete undo window: a write that failed may already be saved on Google.
/// </summary>
public const string V8 = """
    ALTER TABLE outbox ADD COLUMN retry_after INTEGER;
    """;
```

`LeafDatabase.Migrate`, after the version 7 block:

```csharp
// Version 8
if (version < 8)
{
    using var tx = conn.BeginTransaction();
    conn.Execute(tx, Schema.V8);
    conn.Execute(tx, "PRAGMA user_version = 8;");
    tx.Commit();
}
```

`OutboxStore.cs`: add `DateTimeOffset? RetryAfter = null` as the last parameter of `OutboxEntry`; add `, retry_after` to the end of `Columns`; add `r.GetUnixMsOrNull(14)` as the last argument in `Map`. Then:

```csharp
/// <summary>
/// How long a write waits after its <paramref name="attempts"/>th failed try: 30 s, doubling, at most 15 minutes.
/// </summary>
/// <param name="attempts">Failed tries so far (1 or more).</param>
/// <returns>The wait before the next try.</returns>
public static TimeSpan Backoff(int attempts) =>
    TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Clamp(attempts, 1, 16) - 1)));

/// <summary>Counts a failed try and holds the entry back for its backoff. <paramref name="error"/> is a status or reason only, never event content.</summary>
public static void RecordAttempt(SqliteConnection conn, long seq, string error, DateTimeOffset now) =>
    conn.Execute(
        null,
        """
        UPDATE outbox
        SET attempts = attempts + 1,
            last_error = $error,
            retry_after = $now + 1000 * MIN(900, 30 * (1 << MIN(attempts, 5)))
        WHERE seq = $seq;
        """,
        ("$error", error),
        ("$now", now.ToUnixTimeMilliseconds()),
        ("$seq", seq));

/// <summary>Lets every backed-off entry go on the next sync (Sync now, a reconnect, a resume).</summary>
public static void ClearBackoff(SqliteConnection conn) =>
    conn.Execute(null, "UPDATE outbox SET retry_after = NULL WHERE retry_after IS NOT NULL;");
```

`1 << MIN(attempts, 5)` uses the attempts count before the increment, so the first failure waits 30 s, the second 60 s, and the sixth and later 900 s. `Backoff` gives the same numbers for code and tests.

`OutboxSender.cs`: in the queue loop, add the backoff to the "held" check (the event's later edits wait behind it, as they do for `NotBefore`):

```csharp
// Held, Backing Off After A Failed Try, Behind A Held Or Conflicted Edit Of The Same Event, Or Waiting For The Entry It Depends On
if (waiting.Contains(entry.EventId) || entry.NotBefore > now || entry.RetryAfter > now || (entry.DependsOn is { } dependsOn && Reload(dependsOn) is not null))
```

and in `Defer`, pass the time: `OutboxStore.RecordAttempt(conn, entry.Seq, error, time.GetUtcNow());`. Remove the `ponytail:` line above `StopsThePass`; the backoff answers it.

`LeafServices.cs`: in `OnNetworkStatusChanged` (when connected) and `OnSuspendStatusChanged` (on resume), before triggering the loop:

```csharp
using (var conn = Database.Open())
{
    OutboxStore.ClearBackoff(conn);
}
```

`App.xaml.cs`, `SyncNow`: the same three lines before `TriggerNow`.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug`
Expected: all pass.

- [ ] **Step 5: Update the spec and commit**

In the spec, section 5.4 item 7, say: a failed write waits 30 s, doubling to at most 15 minutes, and Sync now, a reconnect or a resume tries it at once. Then:

```bash
git add src/LeafCalendar.Core src/LeafCalendar.App tests/LeafCalendar.Tests docs/superpowers/specs
git commit -m "fix(outbox): a write Google failed waits 30 s, doubling to 15 minutes, before the next try"
```

---

### Task 2: Slower Tray Sync On Energy Saver Or A Metered Connection

While Leaf is only in the tray it syncs every 60 s, even on Energy Saver or a metered connection. The owner chose a 5-minute slow interval (2026-10-04). Reminders are not affected: the alert pass reads only the local database and keeps its own timing.

**Files:**
- Modify: `src/LeafCalendar.Core/Sync/SyncLoop.cs` (`SyncMode.Saver`, `IntervalFor`, new `SyncLoop.ModeFor`)
- Modify: `src/LeafCalendar.App/App.xaml.cs` (`UpdateSyncMode` uses `ModeFor`; subscribe to the power and network events)
- Test: `tests/LeafCalendar.Tests/SyncLoopTests.cs`

**Interfaces:**
- Produces: `SyncMode.Saver`; `SyncLoop.ModeFor(bool visible, bool energySaver, bool metered)` returns `SyncMode`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(true, false, false, SyncMode.Visible)]
[InlineData(true, true, true, SyncMode.Visible)]
[InlineData(false, false, false, SyncMode.Tray)]
[InlineData(false, true, false, SyncMode.Saver)]
[InlineData(false, false, true, SyncMode.Saver)]
public void ModeFor_OnScreenWinsThenSaverThenTray(bool visible, bool energySaver, bool metered, SyncMode expected) =>
    Assert.Equal(expected, SyncLoop.ModeFor(visible, energySaver, metered));

[Fact]
public void IntervalFor_Saver_IsFiveMinutes() =>
    Assert.Equal(TimeSpan.FromMinutes(5), SyncLoop.IntervalFor(SyncMode.Saver));
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*SyncLoopTests"`
Expected: build errors (`SyncMode.Saver`, `ModeFor`).

- [ ] **Step 3: Implement**

`SyncLoop.cs`:

```csharp
/// <summary>Leaf is only in the tray, and Windows is on Energy Saver or the connection is metered.</summary>
Saver,
```

```csharp
/// <summary>The wait between syncs: 15 s on screen, 60 s in the tray, 5 minutes on Energy Saver or a metered connection.</summary>
public static TimeSpan IntervalFor(SyncMode mode) => mode switch
{
    SyncMode.Visible => TimeSpan.FromSeconds(15),
    SyncMode.Saver => TimeSpan.FromMinutes(5),
    _ => TimeSpan.FromSeconds(60),
};

/// <summary>
/// The cadence for the current state: on screen always syncs at the visible pace; otherwise Energy Saver or a
/// metered connection slows the tray pace.
/// </summary>
/// <param name="visible">A Leaf window or the flyout is on screen.</param>
/// <param name="energySaver">Windows' Energy Saver is on.</param>
/// <param name="metered">The internet connection is metered (fixed or variable cost).</param>
/// <returns>The mode to run the loop in.</returns>
public static SyncMode ModeFor(bool visible, bool energySaver, bool metered) =>
    visible ? SyncMode.Visible : energySaver || metered ? SyncMode.Saver : SyncMode.Tray;
```

Update the class summary ("15 s visible, 60 s tray, 5 minutes on Energy Saver or a metered connection").

`App.xaml.cs`, `UpdateSyncMode`:

```csharp
var visible = _window is { IsMinimized: false } || _host?.IsAgendaOpen == true;
if (_services?.Google is { } google)
{
    google.Loop.Mode = SyncLoop.ModeFor(visible, IsEnergySaverOn(), IsMetered());
    if (flyoutOpened)
    {
        google.Loop.TriggerNow();
    }
}
```

```csharp
// Windows' Energy Saver (Windows 11's battery saver)
private static bool IsEnergySaverOn() =>
    Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;

// A metered internet connection (fixed or variable cost); no connection counts as unmetered (the loop is offline anyway)
private static bool IsMetered() =>
    Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost().NetworkCostType
        is Windows.Networking.Connectivity.NetworkCostType.Fixed or Windows.Networking.Connectivity.NetworkCostType.Variable;
```

Where the tray starts (`StartTray`), subscribe once; both events arrive off the UI thread:

```csharp
// Energy Saver Or A Metered Connection Changes The Tray's Sync Pace (both events arrive off the UI thread)
Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => _dispatcher?.TryEnqueue(() => UpdateSyncMode());
Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ => _dispatcher?.TryEnqueue(() => UpdateSyncMode());
```

Leaving Saver must not wait out the 5-minute delay: the `Mode` setter wakes the loop when the new interval is shorter than the old one. In `SyncLoop.cs`, replace the auto-property:

```csharp
private SyncMode _mode = SyncMode.Tray;

/// <summary>Current cadence. Defaults to <see cref="SyncMode.Tray"/>. A faster cadence starts at once.</summary>
public SyncMode Mode
{
    get => _mode;
    set
    {
        var faster = IntervalFor(value) < IntervalFor(_mode);
        _mode = value;
        if (faster)
        {
            TriggerNow();
        }
    }
}
```

and add the test to `SyncLoopTests.cs` (it uses the class's own `CreateLoop`, `NextRunAsync` and `AssertNoRunAsync`):

```csharp
[Fact]
public async Task Mode_SaverToTray_DoesNotWaitOutTheLongInterval()
{
    await using var loop = CreateLoop();
    loop.Mode = SyncMode.Saver;
    loop.Start();
    Assert.Equal(1, await NextRunAsync());

    // A minute on Saver is too soon for the next sync
    await SettleAsync();
    _time.Advance(TimeSpan.FromSeconds(61));
    await AssertNoRunAsync();

    // Back to the tray pace: the wait ends now, not 4 minutes later
    loop.Mode = SyncMode.Tray;
    Assert.Equal(2, await NextRunAsync());
}
```

Add `[InlineData(SyncMode.Saver, 300)]` to the existing `IntervalFor_Mode_ReturnsSpecCadence` theory instead of the separate `IntervalFor_Saver_IsFiveMinutes` test from Step 1.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*SyncLoopTests"`
Expected: PASS. Then the full suite.

- [ ] **Step 5: Check on the real app, update the spec, commit**

Install (`pwsh tools/sign-install.ps1`), turn on Energy Saver (Settings › System › Power & battery), close the window to the tray, and check the log (`%LOCALAPPDATA%\Packages\LeafCalendar_*\LocalState\profiles\default\Logs\leaf.log`) shows syncs 5 minutes apart. Update spec 5.3 with the third pace. Run UI classes `TrayTests` and `FlyoutTests`. Commit:

```bash
git commit -am "feat(sync): the tray syncs every 5 minutes on Energy Saver or a metered connection"
```

---

### Task 3: Secrets In The Package's Own Data

The OAuth client secret and refresh tokens live in the user-wide Credential Locker (`PasswordVault`), which uninstalling Leaf doesn't clear. Move them into the profile folder under the package's `LocalState` (removed on uninstall), encrypted with Windows DPAPI for the current user. Migrate once from the Locker. **Owner decision needed before starting:** confirm this storage; the alternative is to keep the Locker and only delete its entries on account removal (uninstall would still leave them).

**Files:**
- Create: `src/LeafCalendar.Core/Auth/ProtectedFileTokenStore.cs` (the store)
- Create: `src/LeafCalendar.Core/Auth/Dpapi.cs` (`CryptProtectData`/`CryptUnprotectData` through `LibraryImport`)
- Create: `src/LeafCalendar.Core/Auth/TokenStoreMigration.cs`
- Modify: `src/LeafCalendar.Core/LeafCalendar.Core.csproj` (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, required by `LibraryImport`'s generated code)
- Modify: `src/LeafCalendar.Core/Settings/LeafJsonContext.cs` (register the secrets file type)
- Modify: `src/LeafCalendar.App/LeafServices.cs` (use the new store, migrate first)
- Modify: `tests/LeafCalendar.UITests/Support/SeededProfile.cs`, `tests/LeafCalendar.UITests/Support/LeafApp.cs`, `tests/LeafCalendar.UITests/OnboardingTests.cs`, `tests/LeafCalendar.UITests/ConferencingAndContactsTests.cs` (seed and read secrets through the new store)
- Test: `tests/LeafCalendar.Tests/ProtectedFileTokenStoreTests.cs`, `tests/LeafCalendar.Tests/TokenStoreMigrationTests.cs`

**Interfaces:**
- Produces: `ProtectedFileTokenStore(string profileDirectory) : ITokenStore` with `DeleteAll()`; `TokenStoreMigration.Migrate(ITokenStore from, ITokenStore to)` returns `bool` (true when it moved something). The file is `{ProfileDirectory}\secrets.bin`.

- [ ] **Step 1: Write the failing tests**

`ProtectedFileTokenStoreTests.cs` mirrors `CredentialLockerTokenStoreTests` (same three tests, against a `TempFolder`), plus:

```csharp
[Fact]
public void File_IsNotPlainText()
{
    _store.SetClientCredentials(new("one.apps.googleusercontent.com", "GOCSPX-plain-secret"));

    var bytes = File.ReadAllBytes(Path.Combine(_folder.Path, "secrets.bin"));

    Assert.DoesNotContain("GOCSPX-plain-secret", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
}

[Fact]
public void CorruptFile_ReadsAsEmpty_AndTheNextWriteReplacesIt()
{
    File.WriteAllBytes(Path.Combine(_folder.Path, "secrets.bin"), [1, 2, 3]);

    Assert.Null(_store.GetClientCredentials());
    _store.SetRefreshToken("acc", "1//token");
    Assert.Equal("1//token", _store.GetRefreshToken("acc"));
}
```

`TokenStoreMigrationTests.cs` (both sides as `InMemoryTokenStore` from `tests/LeafCalendar.Tests/Support`):

```csharp
[Fact]
public void Migrate_FromLocker_MovesEverySecretOnce()
{
    var from = new InMemoryTokenStore();
    from.SetClientCredentials(new("id.apps.googleusercontent.com", "secret"));
    from.SetRefreshToken("a", "1//a");
    from.SetRefreshToken("b", "1//b");
    var to = new InMemoryTokenStore();

    Assert.True(TokenStoreMigration.Migrate(from, to));
    Assert.False(TokenStoreMigration.Migrate(from, to));

    Assert.Equal(from.GetClientCredentials(), to.GetClientCredentials());
    Assert.Equal(["a", "b"], to.GetAccountIds().Order());
    Assert.Empty(from.GetAccountIds());
    Assert.Null(from.GetClientCredentials());
}

[Fact]
public void Migrate_WriteFails_KeepsTheLockerCopy()
{
    var from = new InMemoryTokenStore();
    from.SetRefreshToken("a", "1//a");

    Assert.ThrowsAny<IOException>(() => TokenStoreMigration.Migrate(from, new ThrowingTokenStore()));
    Assert.Equal("1//a", from.GetRefreshToken("a"));
}
```

`ThrowingTokenStore` is a private test class whose `Set*` methods throw `IOException`. `InMemoryTokenStore` needs a way to forget its client credentials; if it lacks one, add `ClearClientCredentials()` to `ITokenStore` and implement it in all three stores (the migration calls it).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*TokenStore*"`
Expected: build errors.

- [ ] **Step 3: Implement**

`Dpapi.cs`:

```csharp
using System.Runtime.InteropServices;

namespace LeafCalendar.Core.Auth;

/// <summary>Windows DPAPI for the current user (CryptProtectData), so only this Windows account can read the data.</summary>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata"/>
internal static partial class Dpapi
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    /// <summary>Encrypts <paramref name="plain"/> for the current Windows user, bound to <paramref name="entropy"/>.</summary>
    public static byte[] Protect(byte[] plain, byte[] entropy) => Run(plain, entropy, protect: true);

    /// <summary>Decrypts what <see cref="Protect"/> made; throws <see cref="CryptographicException"/> on anything else.</summary>
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

    private static unsafe byte[] Run(byte[] input, byte[] entropy, bool protect)
    {
        fixed (byte* inputPtr = input)
        fixed (byte* entropyPtr = entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (nint)inputPtr };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = (nint)entropyPtr };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out outBlob);
            if (!ok)
            {
                throw new System.Security.Cryptography.CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.Data, outBlob.Size).ToArray();
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
```

`ProtectedFileTokenStore.cs`: one JSON document `{"clientId":…,"clientSecret":…,"refresh":{"accountId":"token"}}` serialized through a new `[JsonSerializable(typeof(SecretsFile))]` entry in `LeafJsonContext`, encrypted with `Dpapi.Protect(bytes, Encoding.UTF8.GetBytes("LeafCalendar/" + profileName))`, written to `secrets.bin.tmp` and moved over `secrets.bin` (`File.Move(tmp, path, overwrite: true)`) so a crash never leaves half a file. Reads catch `CryptographicException`, `JsonException` and `FileNotFoundException` and return an empty document. Every public method takes a `lock` on a private gate (the sync loop and the UI can both touch tokens). `DeleteAll()` deletes the file.

```csharp
/// <summary>A profile's secrets, as stored (encrypted) in <c>secrets.bin</c>.</summary>
internal sealed record SecretsFile(string? ClientId, string? ClientSecret, Dictionary<string, string> Refresh);
```

`TokenStoreMigration.cs`:

```csharp
/// <summary>
/// Moves secrets from the old store to the new one, once. Everything is written to the new store before anything is
/// removed from the old one, so a failure leaves the user signed in through the old copy, and the next start tries again.
/// </summary>
public static bool Migrate(ITokenStore from, ITokenStore to)
{
    var client = from.GetClientCredentials();
    var accounts = from.GetAccountIds();
    if (client is null && accounts.Count == 0)
    {
        return false;
    }

    if (client is not null)
    {
        to.SetClientCredentials(client);
    }

    foreach (var account in accounts)
    {
        if (from.GetRefreshToken(account) is { } token)
        {
            to.SetRefreshToken(account, token);
        }
    }

    foreach (var account in accounts)
    {
        from.RemoveRefreshToken(account);
    }

    from.ClearClientCredentials();
    return true;
}
```

`LeafServices.cs`:

```csharp
// Secrets Live In The Profile Folder (removed with the app); the first start after the update moves them out of the Credential Locker
var tokens = new ProtectedFileTokenStore(Paths.ProfileDirectory);
try
{
    if (TokenStoreMigration.Migrate(new CredentialLockerTokenStore(options.Profile), tokens))
    {
        Log.Info("auth.secrets.migrated");
    }
}
catch (Exception ex)
{
    // The Locker copy stays and the next start tries again; never the secret itself in the log
    Log.Info("auth.secrets.migrate.failed", $"error={ex.GetType().Name}");
}

Tokens = tokens;
```

If migration failed, `tokens` is empty and the user would look signed out until the next start. Guard that: when `Migrate` throws, set `Tokens = new CredentialLockerTokenStore(options.Profile)` for this run instead.

UI test support: `SeededProfile.Create` writes secrets with `new ProtectedFileTokenStore(LeafApp.ProfileFolder(profile))`; `LeafApp.DeleteProfile` and `OnboardingTests` call `DeleteAll()` on that store as well as on the Locker (keep the Locker call so old profiles are cleaned); `ConferencingAndContactsTests` line ~263 reads through the new store. The DPAPI entropy uses the profile name, so pass it: give `ProtectedFileTokenStore` the profile directory and take the name from `Path.GetFileName(profileDirectory)`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug`
Expected: all pass. Build Release once (`pwsh tools/publish-aot.ps1`) to confirm no AOT or trim warnings from `LibraryImport`.

- [ ] **Step 5: Upgrade check on the real install, UI tests, commit**

Install over the owner's current build with `pwsh tools/sign-install.ps1`. Leaf must open signed in, the log must show `auth.secrets.migrated` once, and a second start must not. Run UI classes `OnboardingTests`, `AccountFlowTests`, `SetupTests`, `ConferencingAndContactsTests`, `SingleInstanceTests`. Update the spec's secrets section (4.x) and its threat table. Commit:

```bash
git commit -am "feat(auth): secrets live encrypted in the profile folder, removed with the app; moved once from the Credential Locker"
```

---

### Task 4: Month View Heights Follow The Windows Text Size

Month day numbers (22 DIP button), chips and "+N more" links (18 DIP) have fixed heights, so their 12 px text clips at larger Windows text sizes (Settings › Accessibility › Text size). The design standard (section 14) requires text to follow the system text scale, so `IsTextScaleFactorEnabled = false` is not an option.

**Files:**
- Create: `src/LeafCalendar.Core/Views/MonthMetrics.cs`
- Modify: `src/LeafCalendar.App/Controls/MonthGridView.cs` (`ChipHeight`, `DayNumberHeight`, `MinRowHeight` become instance values from `MonthMetrics`; listen for text size changes)
- Modify: `src/LeafCalendar.App/Controls/WeekRow.cs` (use the grid's metrics at lines ~52, 85, 136, 184, 255, 356, 461)
- Test: `tests/LeafCalendar.Tests/MonthMetricsTests.cs`

**Interfaces:**
- Produces: `MonthMetrics.For(double textScale)` returns `MonthMetrics(double ChipHeight, double DayNumberHeight, double DayButtonHeight, double MinRowHeight)`; `MonthGridView.Metrics` (the current `MonthMetrics`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void MonthMetrics_At100Percent_AreTodaysNumbers()
{
    var m = MonthMetrics.For(1.0);

    Assert.Equal(new MonthMetrics(20, 26, 22, 96), m);
}

[Theory]
[InlineData(1.5)]
[InlineData(2.25)]
public void MonthMetrics_At225Percent_FitsTheText(double scale)
{
    // 12 px Segoe UI needs about 16 px of line height; a chip keeps 2 px around it, a day button 3 px above and below
    var m = MonthMetrics.For(scale);

    Assert.True(m.ChipHeight - 2 >= Math.Ceiling(16 * scale));
    Assert.True(m.DayButtonHeight >= Math.Ceiling(16 * scale) + 6);
    Assert.True(m.DayNumberHeight >= m.DayButtonHeight + 4);
    Assert.True(m.MinRowHeight >= m.DayNumberHeight + 3 * m.ChipHeight);
}

[Fact]
public void MonthMetrics_BelowOne_StaysAtTodaysNumbers() =>
    Assert.Equal(MonthMetrics.For(1.0), MonthMetrics.For(0.8));
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*MonthMetricsTests"`
Expected: build errors.

- [ ] **Step 3: Implement**

`MonthMetrics.cs`:

```csharp
namespace LeafCalendar.Core.Views;

/// <summary>
/// The month view's heights at a Windows text size. Today's numbers at 100%, grown with the text so 12 px labels never
/// clip (design standard section 14: text follows the system text scale).
/// </summary>
/// <param name="ChipHeight">One event chip's lane, including the 2 DIP gap below it.</param>
/// <param name="DayNumberHeight">The band at the top of a day holding its number button.</param>
/// <param name="DayButtonHeight">The day number button.</param>
/// <param name="MinRowHeight">The smallest week row.</param>
public sealed record MonthMetrics(double ChipHeight, double DayNumberHeight, double DayButtonHeight, double MinRowHeight)
{
    /// <summary>The heights for a text scale factor (Windows' UISettings.TextScaleFactor, 1 to 2.25).</summary>
    public static MonthMetrics For(double textScale)
    {
        var s = Math.Max(1, textScale);
        var chip = Math.Max(20, Math.Ceiling(16 * s) + 4);
        var button = Math.Max(22, Math.Ceiling(16 * s) + 6);
        var band = Math.Max(26, button + 4);
        return new MonthMetrics(chip, band, button, Math.Max(96, band + 3 * chip));
    }
}
```

`MonthGridView.cs`: replace the three `public const double` fields with `public MonthMetrics Metrics { get; private set; } = MonthMetrics.For(1)`, set from `new Windows.UI.ViewManagement.UISettings()` (keep the instance in a field; WinRT events stop firing if it is collected). In `Loaded`, `Metrics = MonthMetrics.For(_uiSettings.TextScaleFactor)` and subscribe; in `Unloaded`, unsubscribe:

```csharp
// The Windows Text Size Changed (arrives off the UI thread, like the contrast change)
private void OnTextScaleChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
{
    Metrics = MonthMetrics.For(sender.TextScaleFactor);
    Relayout(force: true);
});
```

`WeekRow.cs`: every `MonthGridView.ChipHeight`, `MonthGridView.DayNumberHeight` and the literal day button `Height = 22` read the owning grid's `Metrics` (pass the grid or its metrics into `WeekRow` the way it already gets its other data). Heights set once in constructors (the day button, `MonthChip`, the "+N more" link at ~255 and ~356) move to the render path so a change applies on the next `Relayout`. `MonthGridView` line ~222 uses `Metrics.MinRowHeight`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug`
Expected: all pass.

- [ ] **Step 5: Check by eye, run UI tests, commit**

Install, set Settings › Accessibility › Text size to 150% and then 225%, and look at Month view: day numbers, chips and "+N more" whole, rows taller, nothing overlapping. Run UI classes `MonthViewTests`, `MonthDragTests`, `AccessibilityTests`. The time grid has the same pattern (`AllDayCanvas` chips, `EventBlock`, `DayHeaderCell`); check them at 225% too, and if they clip, give them the same treatment in a follow-up task with its own metrics record. Commit:

```bash
git commit -am "fix(month): day numbers, chips and +N more grow with the Windows text size"
```

---

### Task 5: Tray Reads Off The UI Thread (Measure First)

Leaf's low-level mouse hook (for the flyout's click-outside) runs on the UI thread, and the minute tick and every sync run `TrayAgenda.Load` there too (`App.RefreshAgenda` → `BuildAgenda`, and `RefreshTooltip`). Windows skips a hook that takes about 300 ms or more; the audit estimated these loads at a few milliseconds. Measure before changing anything.

**Files:**
- Modify: `src/LeafCalendar.App/App.xaml.cs` (`BuildAgenda`, `RefreshAgenda`, `RefreshTooltip`)

**Interfaces:**
- Consumes: `TrayAgenda.Load(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone, int days, bool includeAllDay, bool use24Hour)`.

- [ ] **Step 1: Measure**

Wrap the body of `BuildAgenda` and `RefreshTooltip` in a `Stopwatch` and log, only when detailed logging is on:

```csharp
var watch = System.Diagnostics.Stopwatch.StartNew();
// ... existing body ...
if (services.Log.Detailed)
{
    services.Log.Info("tray.agenda.timing", $"ms={watch.Elapsed.TotalMilliseconds:F1}");
}
```

Install, turn on Settings › General › detailed logging, use the owner's real account for a day, then read the `tray.agenda.timing` lines from the log.

- [ ] **Step 2: Decide**

If the slowest reading is under 16 ms (one frame), remove the timing code, record the numbers in this plan under this task, and close the item: no change needed. Stop here.

**Measured 2026-10-04** (controller ruling: a synthetic benchmark in place of the owner's account; a scratch logic test, deleted after). One account, 20 calendars, 2,000 events within 90 days of "now" (200 repeating series started up to a year back, 200 all-day, 1,600 timed; 738 with a Meet or Zoom link), default tray settings (3 flyout days with all-day events, 60-minute lookahead). Each timing covers the whole read: open the connection, load the settings, `TrayAgenda.Load`. Median of 20 runs after 3 warm-ups, Debug build on the owner's PC:

| Read | Median | Min | Max |
| --- | --- | --- | --- |
| `RefreshTooltip` (2 days, timed only) | 31.5 ms | 25.3 ms | 40.1 ms |
| `BuildAgenda` (3 days + the next event's 2 days; 332 rows) | 54.8 ms | 52.0 ms | 65.6 ms |

With a lighter mix (60 series instead of 200), the tooltip read was 9.0 ms and the agenda read 21.6 ms (median; 115 rows). Both are over 16 ms, so Step 3 applies.

- [ ] **Step 3: If it is slower, move the reads off the UI thread**

Take every value the read needs on the UI thread first (the zone, `_zone.Zone`, is read there, so no thread question arises; `TimeZoneInfo` and `LeafSettings` are immutable), run the load on the thread pool, and apply only the newest result:

```csharp
private int _agendaGeneration;

private async void RefreshAgenda()
{
    try
    {
        if (_host is not { IsAgendaOpen: true } host || _services is not { } services)
        {
            return;
        }

        var generation = ++_agendaGeneration;
        var zone = _zone.Zone;
        var model = await Task.Run(() => BuildAgenda(services, zone));

        // An Older Refresh Finishing Late Never Replaces A Newer One
        if (generation == _agendaGeneration && model is not null && host.IsAgendaOpen)
        {
            host.UpdateAgenda(model);
        }
    }
    catch (Exception ex)
    {
        _log?.Info("tray.flyout.refresh.failed", $"error={ex.GetType().Name}");
    }
}
```

`BuildAgenda` becomes `private static AgendaModel? BuildAgenda(LeafServices services, TimeZoneInfo localZone)` and uses `localZone` in place of `_zone.Zone`. Give `RefreshTooltip` the same shape with its own generation counter. The first build when the flyout opens stays synchronous (the flyout must open with its content).

- [ ] **Step 4: Measure again and test**

Re-read the timings (now on a pool thread, so the hook never waits on them). Run UI classes `FlyoutTests`, `TrayTests`, `TrayMenuTests`, `TrayCalendarsTests`. Remove the timing code. Commit:

```bash
git commit -am "perf(tray): the agenda and tooltip read the database off the UI thread"
```

---

## Out Of Scope

- Localization (all UI text is English and the manifest's display name is fixed). Leaf is English-only by design today; making it translatable is its own project.
