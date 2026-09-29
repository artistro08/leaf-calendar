# Leaf Calendar Milestone 1 (Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the foundation of Leaf Calendar. It's a packaged WinUI 3 app with Release-only Native AOT. You can sign in to Google accounts with your own OAuth client, and Leaf syncs every calendar and event into a local SQLite database, first in full and then incrementally, polling on a timer.

**Architecture:** `LeafCalendar.Core` holds all non-UI logic:
- Google sign-in (PKCE with a loopback listener)
- A hand-written Google REST client using `HttpClient` and source-generated `System.Text.Json`
- SQLite stores and the sync engine
- The polling loop

`LeafCalendar.App` is a thin WinUI 3 shell. It has a setup page for the OAuth client and an accounts page. Three test projects cover logic (fake Google), UI (FlaUI), and live Google.

**Tech Stack:**
- .NET 10 / C# 14, `net10.0-windows10.0.22621.0`
- Windows App SDK 2.5.1 (WinUI 3, single-project MSIX)
- `CommunityToolkit.Mvvm` 8.4.2, `Microsoft.Windows.CsWin32` 0.3.335, `Microsoft.Data.Sqlite` 10.0.12, `Meziantou.Framework.Scheduling` 4.1.3 (RRULE expansion)
- xUnit v3, `Microsoft.Extensions.TimeProvider.Testing`, FlaUI.UIA3 5.0.0

**Spec:** `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (read Sections 2 to 5, 10 to 13 before starting).

## Global Constraints

- SDK pinned by `global.json` to `10.0.401` (`rollForward: latestFeature`). C# 14. All projects target `net10.0-windows10.0.22621.0`, with `TargetPlatformMinVersion` `10.0.22000.0` (Windows 11).
- Windows App SDK `2.5.1`. The App project is single-project MSIX (`EnableMsixTooling`), self-contained, x64 only for now. Package identity name is `LeafCalendar`.
- Native AOT on Release only: `<PublishAot Condition="'$(Configuration)' == 'Release'">true</PublishAot>`. Debug stays JIT (AOT Debug deadlocks on .NET 10).
- Release AOT publish must produce **0 IL warnings**. Every project sets `IsAotCompatible` to `true`.
- **Forbidden packages:**
  - No Google client libraries (`Google.Apis.*`)
  - No `Newtonsoft.Json`
  - No Entity Framework
  - No WebView2
  - No `Ical.Net` or `EWSoftware.PDI` (both fail AOT checks)
- **Recurrence:** `Meziantou.Framework.Scheduling` pinned to exactly `4.1.3` (the library changes often). Upgrading needs the recurrence tests and a clean AOT publish first.
- **JSON:** use `System.Text.Json` only, through `GoogleJsonContext` (source generation). Never call a `JsonSerializer` overload that doesn't take a `JsonTypeInfo`.
- **MVVM:** `CommunityToolkit.Mvvm` with partial-property style only (`[ObservableProperty] public partial string X { get; set; }`). Field style triggers MVVMTK0045.
- **XAML:** prefer `x:Bind`. Every control a UI test touches has `AutomationProperties.AutomationId`.
- **Secrets:** the OAuth client secret, refresh tokens, and access tokens live only in Windows Credential Locker (refresh tokens and client credentials) or memory (access tokens).
  - They never go into SQLite, logs, settings files, or exception messages.
  - Any record holding a secret overrides `ToString()`.
- **Logs:** never contain tokens, secrets, authorization codes, event titles, descriptions, guest emails, or locations. Use internal IDs only, and `AppLog.Redact` catches the rest.
- **Build rules:**
  - `TreatWarningsAsErrors` on, with `AnalysisLevel` `latest-recommended` and NuGet audit on (so a vulnerable package fails the build).
  - If an analyzer rule not suppressed in `.editorconfig` fails the build, fix the code.
  - Only add an `.editorconfig` suppression when the fix would hurt clarity. Put a one-line justification comment above it.
- **Language:** US English everywhere (strings, comments, docs).
- **Tests:**
  - Test methods that call async APIs pass `TestContext.Current.CancellationToken`. xUnit v3 analyzer xUnit1051 is an error under warnings-as-errors.
  - Test classes are `public`.
  - Test names use `Method_Condition_Result`.
- **AOT publish PATH:** before any `dotnet publish` with AOT, run `$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"`. `tools/publish-aot.ps1` does this for you.
- **Developer Mode:** must be on for `Add-AppxPackage -Register` (it's on for this machine).
- **Commits:** the owner authorized a commit at the end of each task (2026-09-29). Never push.
- **Code style:**
  - 4-space indent in C# and XAML, 2-space in JSON and YAML.
  - A short Title Case comment above each logical block (`// Build Authorization Url`).
  - XML doc `<summary>` on every public type and member.
  - Guard clauses first.

## Review Focus

1. **Partial consent.** Google's consent screen lets you untick the calendar permission. Leaf must refuse to save that account with a clear message, not save an account that then fails every sync. Test: `SignInFlowTests.RunAsync_CalendarScopeNotGranted_FailsAndSavesNothing` (Task 10).
2. **Refresh token revoked or expired.** For example, the OAuth app is left in Google's "Testing" status, which expires tokens after 7 days. The account must be marked "needs sign-in" and keep all its local data, and other accounts must keep syncing. Test: `SyncEngineTests.SyncAccountAsync_RefreshTokenRevoked_MarksNeedsSignInAndKeepsEvents` (Task 13).
3. **Network drops in the middle of a paged sync.** Local events and the saved sync token must stay exactly as before, so the next sync resumes cleanly. Test: `SyncEngineTests.SyncAccountAsync_FailureMidPagination_KeepsPreviousDataAndToken` (Task 13).
4. **Junk traffic on the loopback port.** A favicon request, a bare `GET /`, an oversized request, or a reply with a forged `state` must be ignored or rejected without completing sign-in. Tests: `LoopbackListenerTests` (Task 2) and `SignInFlowTests.RunAsync_StateMismatch_FailsWithoutExchangingCode` (Task 10).
5. **Same Google account added twice.** This must update the existing account (one row, with the newest refresh token), not create a duplicate. Test: `SignInFlowTests.RunAsync_SameAccountTwice_KeepsOneAccountWithLatestToken` (Task 10).

---

## File Structure

```
LeafCalendar.slnx                          Solution (src + tests)
global.json                                SDK pin
Directory.Build.props                      Shared build rules (nullable, warnings as errors, analyzers, audit)
Directory.Packages.props                   Central package versions
.editorconfig                              Code style + justified analyzer suppressions
.gitignore
.github/workflows/ci.yml                   Build + logic tests on push
tools/dev-register.ps1                     Debug build + register loose layout
tools/publish-aot.ps1                      Release AOT MSIX publish (+ optional register)

src/LeafCalendar.Core/
    LeafCalendar.Core.csproj
    Auth/Pkce.cs                           PKCE verifier/challenge/state
    Auth/LoopbackListener.cs               127.0.0.1 one-shot OAuth redirect receiver
    Auth/OAuthClientCredentials.cs         Client ID/secret record + validation
    Auth/TokenSet.cs                       Access/refresh token result
    Auth/AuthExceptions.cs                 InvalidGrant / NeedsSignIn / SignIn exceptions
    Auth/GoogleOAuthClient.cs              Auth URL, code exchange, refresh, revoke, userinfo
    Auth/ITokenStore.cs                    Secret storage contract
    Auth/CredentialLockerTokenStore.cs     Windows Credential Locker implementation
    Auth/AccessTokenProvider.cs            Cached access tokens with refresh
    Auth/SignInFlow.cs                     End-to-end browser sign-in
    Http/QueryString.cs                    Query string parser
    Google/GoogleModels.cs                 JSON DTOs
    Google/GoogleJsonContext.cs            STJ source generation context
    Google/GoogleJson.cs                   Parse helpers, API error to exception
    Google/GoogleExceptions.cs             GoogleApiException, SyncTokenExpiredException
    Google/GoogleRetryHandler.cs           Backoff on 429/5xx/rate-limit 403
    Google/GoogleCalendarClient.cs         calendarList + events.list
    Data/Schema.cs                         SQL schema v1
    Data/SqliteExtensions.cs               Execute/Query helpers (parameterized only)
    Data/LeafDatabase.cs                   Open + migrate
    Data/AccountStore.cs                   accounts table
    Data/CalendarStore.cs                  calendars table
    Data/EventStore.cs                     events table
    Diagnostics/AppLog.cs                  Rolling redacted log
    Sync/SyncEngine.cs                     Full + incremental sync
    Sync/SyncLoop.cs                       Smart polling loop
    Recurrence/RecurrenceExpander.cs       RRULE (Meziantou) + EXDATE/RDATE + date window
    Hosting/LaunchOptions.cs               Command-line options (--profile, --tray-probe)
    Hosting/LeafPaths.cs                   Per-profile folders
    Hosting/GoogleServices.cs              Wires Google pieces; sign-in + disconnect

src/LeafCalendar.App/
    LeafCalendar.App.csproj
    Package.appxmanifest
    NativeMethods.json / NativeMethods.txt CsWin32 config
    Assets/*.png                           Placeholder logos (replaced in M6)
    App.xaml / App.xaml.cs                 Startup, tray probe
    MainWindow.xaml / .cs                  Tall XAML title bar, Mica, content frame
    LeafServices.cs                        App composition root
    Interop/MemoryTrimmer.cs               Working-set trim
    ViewModels/SetupViewModel.cs
    ViewModels/AccountsViewModel.cs
    Views/SetupPage.xaml / .cs
    Views/AccountsPage.xaml / .cs

tests/LeafCalendar.Tests/                  Logic tests (fake Google, fake clock)
    LeafCalendar.Tests.csproj, .editorconfig
    Fixtures/*.json                        Recorded Google responses (fake values)
    Support/Fixture.cs, FakeHttpHandler.cs, InMemoryTokenStore.cs, TestDatabase.cs, TempFolder.cs, SyncHarness.cs
    *Tests.cs                              One file per Core unit

tests/LeafCalendar.UITests/                FlaUI tests against the registered package
    LeafCalendar.UITests.csproj, .editorconfig, memory-budget.json
    Support/LeafApp.cs
    SetupTests.cs, MemoryTests.cs

tests/LeafCalendar.LiveTests/              Real throwaway Google account, on demand
    LeafCalendar.LiveTests.csproj, .editorconfig
    Support/LiveAccount.cs, LiveGoogle.cs
    LiveSignInTests.cs, LiveSyncTests.cs
```

---

### Task 1: Repository Scaffold and PKCE

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitignore`, `LeafCalendar.slnx`, `.github/workflows/ci.yml`
- Create: `src/LeafCalendar.Core/LeafCalendar.Core.csproj`, `src/LeafCalendar.Core/Auth/Pkce.cs`
- Create: `tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`, `tests/LeafCalendar.Tests/.editorconfig`, `tests/LeafCalendar.Tests/PkceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `LeafCalendar.Core.Auth.Pkce` with `static string CreateVerifier()`, `static string CreateChallenge(string verifier)`, `static string CreateState()`, and `static bool StateMatches(string expected, string? actual)`.
  - The solution layout and build rules that every later task inherits.

- [ ] **Step 1: Create root build files**

`global.json`:
```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  }
}
```

`Directory.Build.props`:
```xml
<Project>
    <!-- Shared Build Rules -->
    <PropertyGroup>
        <LangVersion>14.0</LangVersion>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <AnalysisLevel>latest-recommended</AnalysisLevel>
        <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    </PropertyGroup>

    <!-- Vulnerable Package Check -->
    <PropertyGroup>
        <NuGetAudit>true</NuGetAudit>
        <NuGetAuditMode>all</NuGetAuditMode>
        <NuGetAuditLevel>low</NuGetAuditLevel>
    </PropertyGroup>
</Project>
```

`Directory.Packages.props`:
```xml
<Project>
    <PropertyGroup>
        <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    </PropertyGroup>

    <!-- App And Core -->
    <ItemGroup>
        <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
        <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
        <PackageVersion Include="Microsoft.Windows.CsWin32" Version="0.3.335" />
        <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.12" />
    </ItemGroup>
</Project>
```

`.editorconfig`:
```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
indent_style = space
indent_size = 4
trim_trailing_whitespace = true

[*.{json,yml,yaml,props,csproj,slnx,appxmanifest}]
indent_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
csharp_prefer_braces = when_multiline:warning
dotnet_sort_system_directives_first = true

# Leaf is an app, not a reusable library: public types in app assemblies are intended.
dotnet_diagnostic.CA1515.severity = none
# UI code resumes on the UI thread on purpose; ConfigureAwait(false) everywhere would be noise.
dotnet_diagnostic.CA2007.severity = none
# Leaf exceptions carry required data; parameterless constructors would allow invalid instances.
dotnet_diagnostic.CA1032.severity = none
```

`.gitignore`:
```gitignore
bin/
obj/
.vs/
*.user
TestResults/
AppPackages/
*.msix
```

- [ ] **Step 2: Create the Core project and solution**

`src/LeafCalendar.Core/LeafCalendar.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
        <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
        <RootNamespace>LeafCalendar.Core</RootNamespace>
        <IsAotCompatible>true</IsAotCompatible>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.Data.Sqlite" />
    </ItemGroup>

    <ItemGroup>
        <InternalsVisibleTo Include="LeafCalendar.Tests" />
    </ItemGroup>
</Project>
```

Run:
```bash
dotnet new sln --name LeafCalendar --format slnx
dotnet sln LeafCalendar.slnx add src/LeafCalendar.Core/LeafCalendar.Core.csproj --solution-folder src
```

- [ ] **Step 3: Create the logic test project**

`tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
        <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
        <OutputType>Exe</OutputType>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <Using Include="Xunit" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\LeafCalendar.Core\LeafCalendar.Core.csproj" />
    </ItemGroup>

    <ItemGroup>
        <None Include="Fixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>
</Project>
```

Run these. Central package management writes the latest stable versions into `Directory.Packages.props`:
```bash
dotnet add tests/LeafCalendar.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/LeafCalendar.Tests package xunit.v3
dotnet add tests/LeafCalendar.Tests package xunit.runner.visualstudio
dotnet add tests/LeafCalendar.Tests package Microsoft.Extensions.TimeProvider.Testing
dotnet sln LeafCalendar.slnx add tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --solution-folder tests
```
Expected: four `<PackageVersion>` lines appear in `Directory.Packages.props`, and the csproj gets version-less `<PackageReference>` lines.

`tests/LeafCalendar.Tests/.editorconfig`:
```ini
[*.cs]
# Test names use Method_Condition_Result underscores by convention.
dotnet_diagnostic.CA1707.severity = none
```

- [ ] **Step 4: Write the failing PKCE tests**

`tests/LeafCalendar.Tests/PkceTests.cs`:
```csharp
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public class PkceTests
{
    [Fact]
    public void CreateChallenge_Rfc7636Vector_MatchesSpec()
    {
        var challenge = Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void CreateVerifier_Called_Returns43UrlSafeUniqueCharacters()
    {
        var first  = Pkce.CreateVerifier();
        var second = Pkce.CreateVerifier();

        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("abc", null, false)]
    public void StateMatches_Values_ComparesExactly(string expected, string? actual, bool result)
    {
        Assert.Equal(result, Pkce.StateMatches(expected, actual));
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: build FAILS with `CS0103: The name 'Pkce' does not exist` (or `CS0246`).

- [ ] **Step 6: Implement PKCE**

`src/LeafCalendar.Core/Auth/Pkce.cs`:
```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636) helpers for the Google sign-in flow.
/// </summary>
/// <remarks>
/// A fresh verifier and state are created for every sign-in. The verifier proves to Google that the
/// app redeeming the code is the one that started sign-in; the state rejects replies Leaf never asked for.
/// </remarks>
public static class Pkce
{
    /// <summary>Creates a 43-character, URL-safe, high-entropy code verifier.</summary>
    public static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Creates the S256 code challenge for <paramref name="verifier"/>.</summary>
    public static string CreateChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Creates a random state value used to reject replies Leaf did not start.</summary>
    public static string CreateState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Compares the expected and returned state in constant time.</summary>
    public static bool StateMatches(string expected, string? actual) =>
        actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: PASS, 6 tests.

- [ ] **Step 8: Add CI**

`.github/workflows/ci.yml`:
```yaml
name: CI

on:
  push:
  pull_request:

jobs:
  build-and-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v5

      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json

      # Build (Debug, JIT) - includes the NuGet vulnerability audit
      - run: dotnet build LeafCalendar.slnx -c Debug

      # Logic Tests (UI and live tests run locally or by manual trigger)
      - run: dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --no-build
```

Run: `dotnet build LeafCalendar.slnx -c Debug`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 9: Commit**

```bash
git add global.json Directory.Build.props Directory.Packages.props .editorconfig .gitignore LeafCalendar.slnx .github src tests
git commit -m "chore: scaffold solution with PKCE helper and CI"
```

---

### Task 2: Query String Parser and Loopback Listener

**Files:**
- Create: `src/LeafCalendar.Core/Http/QueryString.cs`, `src/LeafCalendar.Core/Auth/LoopbackListener.cs`
- Test: `tests/LeafCalendar.Tests/QueryStringTests.cs`, `tests/LeafCalendar.Tests/LoopbackListenerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `LeafCalendar.Core.Http.QueryString.Parse(string query) -> Dictionary<string, string>`. A leading `?` is optional, `+` decodes as a space, and on duplicate keys the first one wins.
  - `LeafCalendar.Core.Auth.LoopbackListener : IDisposable` with `Uri RedirectUri` (always `http://127.0.0.1:<port>/`) and `Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/QueryStringTests.cs`:
```csharp
using LeafCalendar.Core.Http;

namespace LeafCalendar.Tests;

public class QueryStringTests
{
    [Fact]
    public void Parse_EncodedValues_DecodesThem()
    {
        var query = QueryString.Parse("?code=4%2F0Ab&state=x+y&empty=&flag");

        Assert.Equal("4/0Ab", query["code"]);
        Assert.Equal("x y", query["state"]);
        Assert.Equal("", query["empty"]);
        Assert.Equal("", query["flag"]);
    }

    [Fact]
    public void Parse_DuplicateKeys_KeepsFirst()
    {
        var query = QueryString.Parse("state=first&state=second");

        Assert.Equal("first", query["state"]);
    }
}
```

`tests/LeafCalendar.Tests/LoopbackListenerTests.cs`:
```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public class LoopbackListenerTests
{
    static readonly HttpClient Http = new();

    [Fact]
    public void RedirectUri_Created_BindsLoopbackOnly()
    {
        using var listener = new LoopbackListener();

        Assert.Equal("127.0.0.1", listener.RedirectUri.Host);
        Assert.Equal("/", listener.RedirectUri.AbsolutePath);
        Assert.True(listener.RedirectUri.Port > 0);
    }

    [Fact]
    public async Task WaitForCallbackAsync_ValidRedirect_ReturnsQueryAndSuccessPage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(ct);

        using var response = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);
        var query = await wait;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("You're signed in", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("abc", query["code"]);
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_FaviconThenRedirect_IgnoresFavicon()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(ct);

        using var favicon = await Http.GetAsync(new Uri(listener.RedirectUri, "favicon.ico"), ct);
        using var bare = await Http.GetAsync(listener.RedirectUri, ct);
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=abc&state=xyz"), ct);

        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.Equal("abc", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_OversizedRequest_RejectsThenAcceptsValidRedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new LoopbackListener();
        var wait = listener.WaitForCallbackAsync(ct);

        using (var junk = new TcpClient())
        {
            await junk.ConnectAsync(IPAddress.Loopback, listener.RedirectUri.Port, ct);
            var stream = junk.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /?" + new string('a', 9000)), ct);
            var buffer = new byte[64];
            _ = await stream.ReadAsync(buffer, ct);
        }
        Assert.False(wait.IsCompleted);

        using var redirect = await Http.GetAsync(new Uri(listener.RedirectUri, "?code=ok&state=s"), ct);

        Assert.Equal("ok", (await wait)["code"]);
    }

    [Fact]
    public async Task WaitForCallbackAsync_Cancelled_Throws()
    {
        using var listener = new LoopbackListener();
        using var cts = new CancellationTokenSource();
        var wait = listener.WaitForCallbackAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~QueryStringTests|FullyQualifiedName~LoopbackListenerTests"`
Expected: build FAILS, `QueryString` / `LoopbackListener` not found.

- [ ] **Step 3: Implement QueryString**

`src/LeafCalendar.Core/Http/QueryString.cs`:
```csharp
namespace LeafCalendar.Core.Http;

/// <summary>
/// Parses URL query strings such as the OAuth redirect <c>?code=...&amp;state=...</c>.
/// </summary>
public static class QueryString
{
    /// <summary>
    /// Parses <paramref name="query"/> into a dictionary.
    /// </summary>
    /// <remarks>
    /// The leading <c>?</c> is optional, <c>+</c> decodes to a space, keys without a value map to an
    /// empty string, and when a key repeats the first value wins so a later duplicate can't override it.
    /// </remarks>
    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key       = Decode(separator < 0 ? pair : pair[..separator]);
            var value     = separator < 0 ? "" : Decode(pair[(separator + 1)..]);

            result.TryAdd(key, value);
        }

        return result;
    }

    static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
```

- [ ] **Step 4: Implement LoopbackListener**

`src/LeafCalendar.Core/Auth/LoopbackListener.cs`:
```csharp
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Receives Google's OAuth redirect on <c>http://127.0.0.1:&lt;random port&gt;/</c>.
/// </summary>
/// <remarks>
/// Binds the loopback interface only, so nothing else on the network can reach it. It answers
/// unrelated requests (favicon, bare <c>/</c>, oversized or malformed input) with 404 and keeps
/// waiting; the first <c>GET /?...</c> ends the wait. It never echoes tokens or codes into the page.
/// </remarks>
public sealed class LoopbackListener : IDisposable
{
    const int MaxHeaderBytes = 8192;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    const string SuccessPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Leaf Calendar</title></head>" +
        "<body style=\"font-family:'Segoe UI',sans-serif;padding:48px\">" +
        "<h1>You're signed in</h1><p>You can close this tab and go back to Leaf Calendar.</p></body></html>";

    readonly TcpListener _listener;

    /// <summary>Starts listening on a random free loopback port.</summary>
    public LoopbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 4);

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
    }

    /// <summary>The redirect URI to send to Google. Always ends with <c>/</c>.</summary>
    public Uri RedirectUri { get; }

    /// <summary>
    /// Waits for the OAuth redirect and returns its query parameters.
    /// </summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();

            // Read Request With A Per-Connection Timeout
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(ReadTimeout);

            string? target;
            try
            {
                target = await ReadTargetAsync(stream, readTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            // Ignore Anything That Isn't The Redirect
            if (target is null || !target.StartsWith("/?", StringComparison.Ordinal))
            {
                await TryWriteAsync(stream, "404 Not Found", "", ct);
                continue;
            }

            await TryWriteAsync(stream, "200 OK", SuccessPage, ct);
            return QueryString.Parse(target[2..]);
        }
    }

    /// <summary>Stops listening.</summary>
    public void Dispose() => _listener.Stop();

    // Reads the request headers (up to 8 KB) and returns the GET target, or null for anything else.
    static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                return null;
            }

            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            {
                continue;
            }

            var lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
            var parts   = Encoding.ASCII.GetString(buffer, 0, lineEnd).Split(' ');

            return parts is ["GET", var target, var version] && version.StartsWith("HTTP/", StringComparison.Ordinal)
                ? target
                : null;
        }

        return null;
    }

    static async Task TryWriteAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Connection: close\r\n\r\n";

        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bodyBytes, ct);
        }
        catch (IOException)
        {
            // Browser closed the connection early; nothing else to do.
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~QueryStringTests|FullyQualifiedName~LoopbackListenerTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Http src/LeafCalendar.Core/Auth/LoopbackListener.cs tests/LeafCalendar.Tests
git commit -m "feat(core): add loopback OAuth redirect listener"
```

---

### Task 3: Google JSON Models, Fixtures, and Test Support

**Files:**
- Create: `src/LeafCalendar.Core/Google/GoogleModels.cs`, `GoogleJsonContext.cs`, `GoogleJson.cs`, `GoogleExceptions.cs`
- Create: `tests/LeafCalendar.Tests/Fixtures/*.json` (13 files below), `tests/LeafCalendar.Tests/Support/Fixture.cs`, `tests/LeafCalendar.Tests/Support/FakeHttpHandler.cs`
- Test: `tests/LeafCalendar.Tests/GoogleJsonTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `LeafCalendar.Core.Google`):
  - DTOs (public, mutable, for STJ):
    - `TokenResponse { AccessToken, ExpiresIn, RefreshToken?, Scope?, TokenType? }`
    - `OAuthErrorResponse { Error?, ErrorDescription? }`
    - `GoogleUserInfo { Sub, Email, Name?, Picture? }`
    - `ApiErrorEnvelope { ApiError? Error }`, `ApiError { Code, Message?, List<ApiErrorItem>? Errors }`, `ApiErrorItem { Reason?, Message? }`
    - `CalendarListPage { List<CalendarListEntry> Items, NextPageToken? }`
    - `CalendarListEntry { Id, Summary, SummaryOverride?, TimeZone?, BackgroundColor?, ForegroundColor?, AccessRole, Primary, Hidden, Deleted, List<ReminderOverride>? DefaultReminders }`
    - `ReminderOverride { Method, Minutes }`
    - `EventsPage { List<JsonElement> Items, NextPageToken?, NextSyncToken?, TimeZone? }`
    - `GoogleEvent { Id, Status?, Etag?, ICalUid?, Start?, End?, Recurrence?, RecurringEventId?, OriginalStartTime?, Updated? }`
    - `EventDateTime { DateOnly? Date, DateTimeOffset? DateTime, string? TimeZone }`
  - `internal sealed partial class GoogleJsonContext : JsonSerializerContext` (`GoogleJsonContext.Default.<Type>`, with `ListReminderOverride` for `List<ReminderOverride>`).
  - `internal static class GoogleJson` with `T? TryParse<T>(string body, JsonTypeInfo<T> info) where T : class` and `Task<GoogleApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)`.
  - `GoogleApiException(HttpStatusCode status, string? reason, string message)` with `Status` and `Reason`, plus `SyncTokenExpiredException()`.
- Test support (namespace `LeafCalendar.Tests.Support`):
  - `Fixture.Read(string name) -> string`
  - `FakeHttpHandler` with `On(Func<RecordedRequest,bool>, Func<RecordedRequest,HttpResponseMessage>, bool once = false)`, `On(HttpMethod, string urlPrefix, HttpStatusCode, string body, bool once = false)`, `List<RecordedRequest> Requests`, and `static HttpResponseMessage Json(HttpStatusCode, string)`.
  - `RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? Body)` with `Query(string key)` and `Form(string key)`.

- [ ] **Step 1: Create fixtures (all values are fake)**

`tests/LeafCalendar.Tests/Fixtures/token-response.json`:
```json
{
  "access_token": "ya29.test-access-token",
  "expires_in": 3599,
  "refresh_token": "1//test-refresh-token",
  "scope": "openid https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/calendar https://www.googleapis.com/auth/contacts.readonly https://www.googleapis.com/auth/contacts.other.readonly https://www.googleapis.com/auth/directory.readonly",
  "token_type": "Bearer",
  "id_token": "eyJ0ZXN0.header.signature"
}
```

`tests/LeafCalendar.Tests/Fixtures/token-response-no-calendar.json`:
```json
{
  "access_token": "ya29.test-access-token",
  "expires_in": 3599,
  "refresh_token": "1//test-refresh-token",
  "scope": "openid https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile",
  "token_type": "Bearer"
}
```

`tests/LeafCalendar.Tests/Fixtures/token-refresh.json`:
```json
{
  "access_token": "ya29.test-refreshed-token",
  "expires_in": 3599,
  "scope": "openid https://www.googleapis.com/auth/calendar",
  "token_type": "Bearer"
}
```

`tests/LeafCalendar.Tests/Fixtures/token-refresh-rotated.json`:
```json
{
  "access_token": "ya29.test-refreshed-token",
  "expires_in": 3599,
  "refresh_token": "1//test-rotated-refresh-token",
  "scope": "openid https://www.googleapis.com/auth/calendar",
  "token_type": "Bearer"
}
```

`tests/LeafCalendar.Tests/Fixtures/error-invalid-grant.json`:
```json
{
  "error": "invalid_grant",
  "error_description": "Token has been expired or revoked."
}
```

`tests/LeafCalendar.Tests/Fixtures/userinfo.json`:
```json
{
  "sub": "109876543210",
  "email": "leaf.tester@gmail.com",
  "email_verified": true,
  "name": "Leaf Tester",
  "picture": "https://lh3.googleusercontent.com/a/test-picture"
}
```

`tests/LeafCalendar.Tests/Fixtures/calendar-list.json`:
```json
{
  "kind": "calendar#calendarList",
  "items": [
    {
      "id": "leaf.tester@gmail.com",
      "summary": "leaf.tester@gmail.com",
      "timeZone": "America/New_York",
      "backgroundColor": "#9fe1e7",
      "foregroundColor": "#000000",
      "accessRole": "owner",
      "primary": true,
      "defaultReminders": [ { "method": "popup", "minutes": 10 } ]
    },
    {
      "id": "family123@group.calendar.google.com",
      "summary": "Family",
      "timeZone": "America/New_York",
      "backgroundColor": "#f83a22",
      "foregroundColor": "#000000",
      "accessRole": "writer",
      "defaultReminders": []
    }
  ]
}
```

`tests/LeafCalendar.Tests/Fixtures/calendar-list-primary-only.json`:
```json
{
  "kind": "calendar#calendarList",
  "items": [
    {
      "id": "leaf.tester@gmail.com",
      "summary": "leaf.tester@gmail.com",
      "timeZone": "America/New_York",
      "accessRole": "owner",
      "primary": true
    }
  ]
}
```

`tests/LeafCalendar.Tests/Fixtures/events-page1.json`:
```json
{
  "kind": "calendar#events",
  "timeZone": "America/New_York",
  "nextPageToken": "page-2",
  "items": [
    {
      "id": "evt-single",
      "status": "confirmed",
      "etag": "\"3181161784712000\"",
      "iCalUID": "evt-single@google.com",
      "summary": "Dentist appointment",
      "start": { "dateTime": "2026-10-01T09:00:00-04:00", "timeZone": "America/New_York" },
      "end": { "dateTime": "2026-10-01T10:00:00-04:00", "timeZone": "America/New_York" },
      "updated": "2026-09-20T12:00:00.000Z"
    },
    {
      "id": "evt-allday",
      "status": "confirmed",
      "etag": "\"3181161784712001\"",
      "iCalUID": "evt-allday@google.com",
      "summary": "Company holiday",
      "start": { "date": "2026-10-12" },
      "end": { "date": "2026-10-13" },
      "updated": "2026-09-20T12:00:00.000Z"
    }
  ]
}
```

`tests/LeafCalendar.Tests/Fixtures/events-page2.json`:
```json
{
  "kind": "calendar#events",
  "timeZone": "America/New_York",
  "nextSyncToken": "sync-token-1",
  "items": [
    {
      "id": "evt-weekly",
      "status": "confirmed",
      "etag": "\"3181161784712002\"",
      "iCalUID": "evt-weekly@google.com",
      "summary": "Team standup",
      "start": { "dateTime": "2026-10-05T09:30:00-04:00", "timeZone": "America/New_York" },
      "end": { "dateTime": "2026-10-05T10:00:00-04:00", "timeZone": "America/New_York" },
      "recurrence": [ "RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR" ],
      "updated": "2026-09-20T12:00:00.000Z"
    },
    {
      "id": "evt-weekly_20261007T133000Z",
      "status": "cancelled",
      "recurringEventId": "evt-weekly",
      "originalStartTime": { "dateTime": "2026-10-07T09:30:00-04:00", "timeZone": "America/New_York" }
    },
    {
      "id": "evt-deleted",
      "status": "cancelled"
    }
  ]
}
```

`tests/LeafCalendar.Tests/Fixtures/events-incremental.json`:
```json
{
  "kind": "calendar#events",
  "nextSyncToken": "sync-token-2",
  "items": [
    {
      "id": "evt-single",
      "status": "confirmed",
      "etag": "\"3181161784712010\"",
      "iCalUID": "evt-single@google.com",
      "summary": "Dentist appointment (moved)",
      "start": { "dateTime": "2026-10-01T11:00:00-04:00", "timeZone": "America/New_York" },
      "end": { "dateTime": "2026-10-01T12:00:00-04:00", "timeZone": "America/New_York" },
      "updated": "2026-09-28T12:00:00.000Z"
    },
    {
      "id": "evt-allday",
      "status": "cancelled"
    },
    {
      "id": "evt-new",
      "status": "confirmed",
      "etag": "\"3181161784712011\"",
      "iCalUID": "evt-new@google.com",
      "summary": "Lunch with Sam",
      "start": { "dateTime": "2026-10-02T12:00:00-04:00", "timeZone": "America/New_York" },
      "end": { "dateTime": "2026-10-02T13:00:00-04:00", "timeZone": "America/New_York" },
      "updated": "2026-09-28T12:00:00.000Z"
    }
  ]
}
```

`tests/LeafCalendar.Tests/Fixtures/events-empty.json`:
```json
{
  "kind": "calendar#events",
  "nextSyncToken": "sync-token-empty",
  "items": []
}
```

`tests/LeafCalendar.Tests/Fixtures/error-410.json`:
```json
{
  "error": {
    "code": 410,
    "message": "Sync token is no longer valid, a full sync is required.",
    "errors": [ { "domain": "calendar", "reason": "fullSyncRequired", "message": "Sync token is no longer valid, a full sync is required." } ]
  }
}
```

`tests/LeafCalendar.Tests/Fixtures/error-rate-limit.json`:
```json
{
  "error": {
    "code": 403,
    "message": "Rate Limit Exceeded",
    "errors": [ { "domain": "usageLimits", "reason": "rateLimitExceeded", "message": "Rate Limit Exceeded" } ]
  }
}
```

`tests/LeafCalendar.Tests/Fixtures/error-forbidden.json`:
```json
{
  "error": {
    "code": 403,
    "message": "Forbidden",
    "errors": [ { "domain": "global", "reason": "forbidden", "message": "Forbidden" } ]
  }
}
```

- [ ] **Step 2: Create test support**

`tests/LeafCalendar.Tests/Support/Fixture.cs`:
```csharp
namespace LeafCalendar.Tests.Support;

/// <summary>Reads recorded Google responses from the Fixtures folder.</summary>
public static class Fixture
{
    /// <summary>Returns the text of <paramref name="name"/> from the test output's Fixtures folder.</summary>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
```

`tests/LeafCalendar.Tests/Support/FakeHttpHandler.cs`:
```csharp
using System.Net;
using System.Text;
using LeafCalendar.Core.Http;

namespace LeafCalendar.Tests.Support;

/// <summary>A request the fake saw, with its body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? Body)
{
    /// <summary>Returns a query string value, or null.</summary>
    public string? Query(string key) => QueryString.Parse(Uri.Query).GetValueOrDefault(key);

    /// <summary>Returns a form body value, or null.</summary>
    public string? Form(string key) => Body is null ? null : QueryString.Parse(Body).GetValueOrDefault(key);
}

/// <summary>
/// Fake Google. Routes are checked in the order they were added and the first match wins.
/// A route added with <c>once: true</c> is removed after it answers, so add one-shot routes before
/// permanent ones for the same URL. Unmatched requests get a 404.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    readonly List<Route> _routes = [];

    /// <summary>Every request sent through the fake, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Adds a route with a custom matcher and responder.</summary>
    public FakeHttpHandler On(Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> respond, bool once = false)
    {
        _routes.Add(new Route(match, respond, once));
        return this;
    }

    /// <summary>Adds a route matching a method and absolute URL prefix, answering with JSON.</summary>
    public FakeHttpHandler On(HttpMethod method, string urlPrefix, HttpStatusCode status, string body, bool once = false) =>
        On(r => r.Method == method && r.Uri.AbsoluteUri.StartsWith(urlPrefix, StringComparison.Ordinal), _ => Json(status, body), once);

    /// <summary>Creates a JSON response.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body     = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter, body);
        Requests.Add(recorded);

        var route = _routes.FirstOrDefault(r => r.Match(recorded));
        if (route is null)
        {
            return Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"No fake route"}}""");
        }

        if (route.Once)
        {
            _routes.Remove(route);
        }

        return route.Respond(recorded);
    }

    sealed record Route(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Respond, bool Once);
}
```

- [ ] **Step 3: Write the failing JSON tests**

`tests/LeafCalendar.Tests/GoogleJsonTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public class GoogleJsonTests
{
    [Fact]
    public void Deserialize_EventsPage_ReadsItemsAndTokens()
    {
        var page = JsonSerializer.Deserialize(Fixture.Read("events-page1.json"), GoogleJsonContext.Default.EventsPage)!;

        Assert.Equal("page-2", page.NextPageToken);
        Assert.Null(page.NextSyncToken);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public void Deserialize_TimedAndAllDayEvents_ReadsDates()
    {
        var page   = JsonSerializer.Deserialize(Fixture.Read("events-page1.json"), GoogleJsonContext.Default.EventsPage)!;
        var timed  = JsonSerializer.Deserialize(page.Items[0], GoogleJsonContext.Default.GoogleEvent)!;
        var allDay = JsonSerializer.Deserialize(page.Items[1], GoogleJsonContext.Default.GoogleEvent)!;

        Assert.Equal("evt-single", timed.Id);
        Assert.Equal("evt-single@google.com", timed.ICalUid);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), timed.Start!.DateTime!.Value.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 10, 12), allDay.Start!.Date);
        Assert.Null(allDay.Start.DateTime);
    }

    [Fact]
    public void Deserialize_CancelledException_ReadsRecurringFields()
    {
        var page      = JsonSerializer.Deserialize(Fixture.Read("events-page2.json"), GoogleJsonContext.Default.EventsPage)!;
        var master    = JsonSerializer.Deserialize(page.Items[0], GoogleJsonContext.Default.GoogleEvent)!;
        var exception = JsonSerializer.Deserialize(page.Items[1], GoogleJsonContext.Default.GoogleEvent)!;

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR"], master.Recurrence);
        Assert.Equal("cancelled", exception.Status);
        Assert.Equal("evt-weekly", exception.RecurringEventId);
        Assert.NotNull(exception.OriginalStartTime?.DateTime);
    }

    [Fact]
    public void Deserialize_CalendarList_ReadsFlagsAndReminders()
    {
        var list = JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!;

        Assert.True(list.Items[0].Primary);
        Assert.Equal("owner", list.Items[0].AccessRole);
        Assert.Equal(10, list.Items[0].DefaultReminders![0].Minutes);
        Assert.False(list.Items[1].Primary);
    }

    [Fact]
    public void Deserialize_TokenResponse_ReadsSnakeCase()
    {
        var token = JsonSerializer.Deserialize(Fixture.Read("token-response.json"), GoogleJsonContext.Default.TokenResponse)!;

        Assert.Equal("ya29.test-access-token", token.AccessToken);
        Assert.Equal("1//test-refresh-token", token.RefreshToken);
        Assert.Equal(3599, token.ExpiresIn);
    }

    [Fact]
    public async Task ToExceptionAsync_ApiError_ReadsReason()
    {
        using var response = FakeHttpHandler.Json(HttpStatusCode.Forbidden, Fixture.Read("error-rate-limit.json"));

        var exception = await GoogleJson.ToExceptionAsync(response, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, exception.Status);
        Assert.Equal("rateLimitExceeded", exception.Reason);
    }

    [Fact]
    public void TryParse_NotJson_ReturnsNull()
    {
        Assert.Null(GoogleJson.TryParse("<html>oops</html>", GoogleJsonContext.Default.ApiErrorEnvelope));
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleJsonTests"`
Expected: build FAILS, `GoogleJsonContext` not found.

- [ ] **Step 5: Implement the models, context, helpers, and exceptions**

`src/LeafCalendar.Core/Google/GoogleModels.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Google;

// =========================================================================
// OAUTH
// =========================================================================

/// <summary>Google token endpoint response.</summary>
public sealed class TokenResponse
{
    /// <summary>Short-lived access token.</summary>
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";

    /// <summary>Seconds until the access token expires.</summary>
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

    /// <summary>Long-lived refresh token; only present on first consent or rotation.</summary>
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

    /// <summary>Space-separated scopes actually granted.</summary>
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    /// <summary>Always "Bearer".</summary>
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
}

/// <summary>Google OAuth error body, e.g. <c>{"error":"invalid_grant"}</c>.</summary>
public sealed class OAuthErrorResponse
{
    /// <summary>OAuth error code.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>OpenID Connect user info.</summary>
public sealed class GoogleUserInfo
{
    /// <summary>Stable Google account ID; Leaf's account ID.</summary>
    public string Sub { get; set; } = "";

    /// <summary>Account email; used for Meet <c>authuser</c>.</summary>
    public string Email { get; set; } = "";

    /// <summary>Display name.</summary>
    public string? Name { get; set; }

    /// <summary>Avatar image address.</summary>
    public string? Picture { get; set; }
}

// =========================================================================
// API ERRORS
// =========================================================================

/// <summary>Google API error envelope: <c>{"error":{...}}</c>.</summary>
public sealed class ApiErrorEnvelope
{
    /// <summary>The error.</summary>
    public ApiError? Error { get; set; }
}

/// <summary>Google API error details.</summary>
public sealed class ApiError
{
    /// <summary>HTTP status code.</summary>
    public int Code { get; set; }

    /// <summary>Message from Google.</summary>
    public string? Message { get; set; }

    /// <summary>Individual errors with machine-readable reasons.</summary>
    public List<ApiErrorItem>? Errors { get; set; }
}

/// <summary>One Google API error item.</summary>
public sealed class ApiErrorItem
{
    /// <summary>Reason such as <c>rateLimitExceeded</c> or <c>fullSyncRequired</c>.</summary>
    public string? Reason { get; set; }

    /// <summary>Message from Google.</summary>
    public string? Message { get; set; }
}

// =========================================================================
// CALENDAR LIST
// =========================================================================

/// <summary>One page of <c>users/me/calendarList</c>.</summary>
public sealed class CalendarListPage
{
    /// <summary>Calendars on this page.</summary>
    public List<CalendarListEntry> Items { get; set; } = [];

    /// <summary>Token for the next page, if any.</summary>
    public string? NextPageToken { get; set; }
}

/// <summary>A calendar in the user's calendar list.</summary>
public sealed class CalendarListEntry
{
    /// <summary>Calendar ID (often an email address).</summary>
    public string Id { get; set; } = "";

    /// <summary>Calendar name.</summary>
    public string Summary { get; set; } = "";

    /// <summary>User's own name for the calendar.</summary>
    public string? SummaryOverride { get; set; }

    /// <summary>IANA time zone.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Hex background color.</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Hex foreground color.</summary>
    public string? ForegroundColor { get; set; }

    /// <summary><c>owner</c>, <c>writer</c>, <c>reader</c>, or <c>freeBusyReader</c>.</summary>
    public string AccessRole { get; set; } = "reader";

    /// <summary>True for the account's primary calendar.</summary>
    public bool Primary { get; set; }

    /// <summary>True when hidden in Google Calendar's list.</summary>
    public bool Hidden { get; set; }

    /// <summary>True when removed from the list.</summary>
    public bool Deleted { get; set; }

    /// <summary>Default reminders for events on this calendar.</summary>
    public List<ReminderOverride>? DefaultReminders { get; set; }
}

/// <summary>A reminder: method and minutes before start.</summary>
public sealed class ReminderOverride
{
    /// <summary><c>popup</c> or <c>email</c>.</summary>
    public string Method { get; set; } = "popup";

    /// <summary>Minutes before the event starts.</summary>
    public int Minutes { get; set; }
}

// =========================================================================
// EVENTS
// =========================================================================

/// <summary>One page of <c>calendars/{id}/events</c>. Items stay raw so Leaf stores Google's full JSON.</summary>
public sealed class EventsPage
{
    /// <summary>Raw event objects.</summary>
    public List<JsonElement> Items { get; set; } = [];

    /// <summary>Token for the next page, if any.</summary>
    public string? NextPageToken { get; set; }

    /// <summary>Token for the next incremental sync; only on the last page.</summary>
    public string? NextSyncToken { get; set; }

    /// <summary>Calendar time zone.</summary>
    public string? TimeZone { get; set; }
}

/// <summary>The event fields Leaf indexes. The rest stays in the raw JSON.</summary>
public sealed class GoogleEvent
{
    /// <summary>Event ID.</summary>
    public string Id { get; set; } = "";

    /// <summary><c>confirmed</c>, <c>tentative</c>, or <c>cancelled</c>.</summary>
    public string? Status { get; set; }

    /// <summary>Version tag for conflict detection.</summary>
    public string? Etag { get; set; }

    /// <summary>iCalendar UID shared by copies of the same event.</summary>
    [JsonPropertyName("iCalUID")] public string? ICalUid { get; set; }

    /// <summary>Start time or date.</summary>
    public EventDateTime? Start { get; set; }

    /// <summary>End time or date (exclusive).</summary>
    public EventDateTime? End { get; set; }

    /// <summary>RRULE/EXRULE/RDATE/EXDATE lines for a recurring master.</summary>
    public List<string>? Recurrence { get; set; }

    /// <summary>Master event ID when this is a single occurrence.</summary>
    public string? RecurringEventId { get; set; }

    /// <summary>The occurrence's original start when this is a single occurrence.</summary>
    public EventDateTime? OriginalStartTime { get; set; }

    /// <summary>Last modification time.</summary>
    public DateTimeOffset? Updated { get; set; }
}

/// <summary>Either an all-day <see cref="Date"/> or a timed <see cref="DateTime"/>.</summary>
public sealed class EventDateTime
{
    /// <summary>All-day date.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>Timed start/end with offset.</summary>
    public DateTimeOffset? DateTime { get; set; }

    /// <summary>IANA time zone the event was created in.</summary>
    public string? TimeZone { get; set; }
}
```

`src/LeafCalendar.Core/Google/GoogleJsonContext.cs`:
```csharp
using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Google;

/// <summary>Source-generated JSON metadata for every Google type Leaf reads or writes (AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(GoogleUserInfo))]
[JsonSerializable(typeof(ApiErrorEnvelope))]
[JsonSerializable(typeof(CalendarListPage))]
[JsonSerializable(typeof(EventsPage))]
[JsonSerializable(typeof(GoogleEvent))]
[JsonSerializable(typeof(List<ReminderOverride>))]
internal sealed partial class GoogleJsonContext : JsonSerializerContext
{
}
```

`src/LeafCalendar.Core/Google/GoogleExceptions.cs`:
```csharp
using System.Net;

namespace LeafCalendar.Core.Google;

/// <summary>
/// A Google API call failed. The message never contains response bodies, tokens, or event data.
/// </summary>
public sealed class GoogleApiException(HttpStatusCode status, string? reason, string message) : Exception(message)
{
    /// <summary>HTTP status Google returned.</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>Google's machine-readable reason, e.g. <c>forbidden</c>.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>Google returned 410: the stored sync token is too old and a full sync is required.</summary>
public sealed class SyncTokenExpiredException() : Exception("Google reported the sync token is no longer valid.");
```

`src/LeafCalendar.Core/Google/GoogleJson.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LeafCalendar.Core.Google;

/// <summary>Shared JSON helpers for Google responses.</summary>
internal static class GoogleJson
{
    /// <summary>Parses <paramref name="body"/>, returning null when it isn't valid JSON for <typeparamref name="T"/>.</summary>
    public static T? TryParse<T>(string body, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds a <see cref="GoogleApiException"/> from a failed response, keeping only status and reason.</summary>
    public static async Task<GoogleApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body   = await response.Content.ReadAsStringAsync(ct);
        var reason = TryParse(body, GoogleJsonContext.Default.ApiErrorEnvelope)?.Error?.Errors?.FirstOrDefault()?.Reason;

        return new GoogleApiException(response.StatusCode, reason, $"Google API request failed with status {(int)response.StatusCode}.");
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleJsonTests"`
Expected: PASS, 7 tests.

- [ ] **Step 7: Commit**

```bash
git add src/LeafCalendar.Core/Google tests/LeafCalendar.Tests
git commit -m "feat(core): add source-generated Google JSON models and fake Google"
```

---

### Task 4: Google OAuth Client

**Files:**
- Create: `src/LeafCalendar.Core/Auth/OAuthClientCredentials.cs`, `TokenSet.cs`, `AuthExceptions.cs`, `GoogleOAuthClient.cs`
- Test: `tests/LeafCalendar.Tests/GoogleOAuthClientTests.cs`, `tests/LeafCalendar.Tests/OAuthClientCredentialsTests.cs`

**Interfaces:**
- Consumes: `QueryString` (Task 2); `GoogleJsonContext`, `GoogleJson`, `GoogleApiException`, `GoogleUserInfo` (Task 3); `FakeHttpHandler`, `Fixture` (Task 3).
- Produces (namespace `LeafCalendar.Core.Auth`):
  - `sealed record OAuthClientCredentials(string ClientId, string ClientSecret)` with `static string? Validate(string clientId, string clientSecret)` (returns an error message or null), and `ToString()` that omits the secret.
  - `sealed record TokenSet(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string Scope)` with `bool HasScope(string scope)`, and `ToString()` that omits the tokens.
  - `InvalidGrantException()`, `AccountNeedsSignInException(string accountId)` with `AccountId`, and `SignInException(string message)`.
  - `sealed class GoogleOAuthClient(HttpClient http, OAuthClientCredentials credentials, TimeProvider time)`:
    - `const string CalendarScope`
    - `static IReadOnlyList<string> Scopes`
    - `Uri BuildAuthorizationUrl(Uri redirectUri, string state, string codeChallenge, string? loginHint = null)`
    - `Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)`
    - `Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct)`, which throws `InvalidGrantException`
    - `Task RevokeAsync(string token, CancellationToken ct)`
    - `Task<GoogleUserInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/OAuthClientCredentialsTests.cs`:
```csharp
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public class OAuthClientCredentialsTests
{
    [Theory]
    [InlineData("123-abc.apps.googleusercontent.com", "GOCSPX-secret", null)]
    [InlineData("  123-abc.apps.googleusercontent.com  ", "GOCSPX-secret", null)]
    [InlineData("not-a-client-id", "GOCSPX-secret", "Client ID should end with .apps.googleusercontent.com.")]
    [InlineData("123-abc.apps.googleusercontent.com", "   ", "Enter the client secret.")]
    public void Validate_Input_ReturnsExpectedError(string clientId, string secret, string? error)
    {
        Assert.Equal(error, OAuthClientCredentials.Validate(clientId, secret));
    }

    [Fact]
    public void ToString_Called_OmitsSecret()
    {
        var text = new OAuthClientCredentials("id.apps.googleusercontent.com", "GOCSPX-secret").ToString();

        Assert.DoesNotContain("GOCSPX", text, StringComparison.Ordinal);
    }
}
```

`tests/LeafCalendar.Tests/GoogleOAuthClientTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class GoogleOAuthClientTests
{
    const string TokenUrl    = "https://oauth2.googleapis.com/token";
    const string RevokeUrl   = "https://oauth2.googleapis.com/revoke";
    const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    static readonly OAuthClientCredentials Credentials = new("123-abc.apps.googleusercontent.com", "GOCSPX-test-secret");

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    GoogleOAuthClient CreateClient() => new(new HttpClient(_google), Credentials, _time);

    [Fact]
    public void BuildAuthorizationUrl_Called_IncludesPkceOfflineAndScopes()
    {
        var url   = CreateClient().BuildAuthorizationUrl(new Uri("http://127.0.0.1:5000/"), "state-1", "challenge-1", "me@example.com");
        var query = QueryString.Parse(url.Query);

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(Credentials.ClientId, query["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("me@example.com", query["login_hint"]);
        Assert.Contains(GoogleOAuthClient.CalendarScope, query["scope"].Split(' '));
        Assert.DoesNotContain(Credentials.ClientSecret, url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeCodeAsync_Success_ReturnsTokensWithExpiry()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-response.json"));

        var tokens = await CreateClient().ExchangeCodeAsync("4/code", "verifier-1", new Uri("http://127.0.0.1:5000/"), TestContext.Current.CancellationToken);

        var request = Assert.Single(_google.Requests);
        Assert.Equal("authorization_code", request.Form("grant_type"));
        Assert.Equal("4/code", request.Form("code"));
        Assert.Equal("verifier-1", request.Form("code_verifier"));
        Assert.Equal("http://127.0.0.1:5000/", request.Form("redirect_uri"));
        Assert.Equal(Credentials.ClientSecret, request.Form("client_secret"));
        Assert.Equal("ya29.test-access-token", tokens.AccessToken);
        Assert.Equal("1//test-refresh-token", tokens.RefreshToken);
        Assert.Equal(_time.GetUtcNow().AddSeconds(3599), tokens.ExpiresAt);
        Assert.True(tokens.HasScope(GoogleOAuthClient.CalendarScope));
    }

    [Fact]
    public async Task RefreshAsync_InvalidGrant_ThrowsInvalidGrantException()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json"));

        await Assert.ThrowsAsync<InvalidGrantException>(() => CreateClient().RefreshAsync("1//old", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_Success_SendsRefreshGrant()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));

        var tokens = await CreateClient().RefreshAsync("1//test-refresh-token", TestContext.Current.CancellationToken);

        Assert.Equal("refresh_token", _google.Requests[0].Form("grant_type"));
        Assert.Equal("1//test-refresh-token", _google.Requests[0].Form("refresh_token"));
        Assert.Equal("ya29.test-refreshed-token", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
    }

    [Fact]
    public async Task RevokeAsync_AlreadyInvalid_DoesNotThrow()
    {
        _google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.BadRequest, """{"error":"invalid_token"}""");

        await CreateClient().RevokeAsync("1//gone", TestContext.Current.CancellationToken);

        Assert.Equal("1//gone", _google.Requests[0].Form("token"));
    }

    [Fact]
    public async Task GetUserInfoAsync_Success_SendsBearerAndReadsUser()
    {
        _google.On(HttpMethod.Get, UserInfoUrl, HttpStatusCode.OK, Fixture.Read("userinfo.json"));

        var user = await CreateClient().GetUserInfoAsync("ya29.test-access-token", TestContext.Current.CancellationToken);

        Assert.Equal("ya29.test-access-token", _google.Requests[0].BearerToken);
        Assert.Equal("109876543210", user.Sub);
        Assert.Equal("leaf.tester@gmail.com", user.Email);
    }

    [Fact]
    public void TokenSet_ToString_OmitsTokens()
    {
        var text = new TokenSet("ya29.secret", _time.GetUtcNow(), "1//secret", "").ToString();

        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleOAuthClientTests|FullyQualifiedName~OAuthClientCredentialsTests"`
Expected: build FAILS, `GoogleOAuthClient` not found.

- [ ] **Step 3: Implement the records and exceptions**

`src/LeafCalendar.Core/Auth/OAuthClientCredentials.cs`:
```csharp
namespace LeafCalendar.Core.Auth;

/// <summary>
/// The user's own Google OAuth "Desktop app" client. Stored only in Credential Locker.
/// </summary>
public sealed record OAuthClientCredentials(string ClientId, string ClientSecret)
{
    /// <summary>
    /// Checks pasted values before saving. Returns a message for the user, or null when valid.
    /// </summary>
    public static string? Validate(string clientId, string clientSecret)
    {
        if (!clientId.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
        {
            return "Client ID should end with .apps.googleusercontent.com.";
        }

        if (clientSecret.Trim().Length == 0)
        {
            return "Enter the client secret.";
        }

        return null;
    }

    /// <summary>Omits the secret so logging a record can't leak it.</summary>
    public override string ToString() => $"OAuthClientCredentials({ClientId})";
}
```

`src/LeafCalendar.Core/Auth/TokenSet.cs`:
```csharp
namespace LeafCalendar.Core.Auth;

/// <summary>
/// Tokens returned by Google's token endpoint. <see cref="RefreshToken"/> is null on most refreshes.
/// </summary>
public sealed record TokenSet(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string Scope)
{
    /// <summary>True when Google granted <paramref name="scope"/>.</summary>
    public bool HasScope(string scope) => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    /// <summary>Omits the tokens so logging a record can't leak them.</summary>
    public override string ToString() => $"TokenSet(expires {ExpiresAt:O})";
}
```

`src/LeafCalendar.Core/Auth/AuthExceptions.cs`:
```csharp
namespace LeafCalendar.Core.Auth;

/// <summary>Google rejected a refresh token (revoked, expired, or password changed).</summary>
public sealed class InvalidGrantException() : Exception("Google rejected the refresh token.");

/// <summary>An account can't get an access token until the user signs in again.</summary>
public sealed class AccountNeedsSignInException(string accountId) : Exception("The account needs to sign in again.")
{
    /// <summary>The Google account ID (<c>sub</c>).</summary>
    public string AccountId { get; } = accountId;
}

/// <summary>Sign-in failed. <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class SignInException(string message) : Exception(message);
```

- [ ] **Step 4: Implement GoogleOAuthClient**

`src/LeafCalendar.Core/Auth/GoogleOAuthClient.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Google OAuth 2.0 calls for an installed (desktop) app.
/// </summary>
/// <remarks>
/// Uses the Authorization Code flow with PKCE and a loopback redirect. The client secret goes only
/// to Google's token endpoint over HTTPS; it never appears in URLs or logs.
/// </remarks>
/// <seealso href="https://developers.google.com/identity/protocols/oauth2/native-app"/>
public sealed class GoogleOAuthClient(HttpClient http, OAuthClientCredentials credentials, TimeProvider time)
{
    /// <summary>Full read/write calendar access.</summary>
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar";

    /// <summary>Every scope Leaf requests.</summary>
    public static readonly IReadOnlyList<string> Scopes =
    [
        "openid",
        "email",
        "profile",
        CalendarScope,
        "https://www.googleapis.com/auth/contacts.readonly",
        "https://www.googleapis.com/auth/contacts.other.readonly",
        "https://www.googleapis.com/auth/directory.readonly",
    ];

    static readonly Uri AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");
    static readonly Uri TokenEndpoint         = new("https://oauth2.googleapis.com/token");
    static readonly Uri RevokeEndpoint        = new("https://oauth2.googleapis.com/revoke");
    static readonly Uri UserInfoEndpoint      = new("https://openidconnect.googleapis.com/v1/userinfo");

    /// <summary>Builds the consent page address to open in the user's browser.</summary>
    public Uri BuildAuthorizationUrl(Uri redirectUri, string state, string codeChallenge, string? loginHint = null)
    {
        // Build Query
        List<KeyValuePair<string, string>> query =
        [
            new("client_id", credentials.ClientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("response_type", "code"),
            new("scope", string.Join(' ', Scopes)),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("access_type", "offline"),
            new("prompt", "consent"),
        ];

        if (!string.IsNullOrEmpty(loginHint))
        {
            query.Add(new("login_hint", loginHint));
        }

        var encoded = string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{AuthorizationEndpoint.AbsoluteUri}?{encoded}");
    }

    /// <summary>Exchanges an authorization code for tokens.</summary>
    public Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) =>
        RequestTokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri.AbsoluteUri),
            ],
            ct);

    /// <summary>Gets a new access token.</summary>
    /// <exception cref="InvalidGrantException">The refresh token was revoked or expired.</exception>
    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RequestTokenAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
            ],
            ct);

    /// <summary>Revokes a token. A token Google already considers invalid counts as revoked.</summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var content  = new FormUrlEncodedContent([new KeyValuePair<string?, string?>("token", token)]);
        using var response = await http.PostAsync(RevokeEndpoint, content, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
        {
            throw new GoogleApiException(response.StatusCode, null, "Google token revoke failed.");
        }
    }

    /// <summary>Reads the signed-in user's ID, email, name, and picture.</summary>
    public async Task<GoogleUserInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new GoogleApiException(response.StatusCode, null, "Google user info request failed.");
        }

        return await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.GoogleUserInfo, ct)
            ?? throw new InvalidDataException("Google returned empty user info.");
    }

    async Task<TokenSet> RequestTokenAsync(List<KeyValuePair<string?, string?>> form, CancellationToken ct)
    {
        // Add Client Credentials
        form.Add(new("client_id", credentials.ClientId));
        form.Add(new("client_secret", credentials.ClientSecret));

        using var content  = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // Map Errors
        if (!response.IsSuccessStatusCode)
        {
            var error = GoogleJson.TryParse(body, GoogleJsonContext.Default.OAuthErrorResponse);
            if (error?.Error == "invalid_grant")
            {
                throw new InvalidGrantException();
            }

            throw new GoogleApiException(response.StatusCode, error?.Error, "Google token request failed.");
        }

        var token = JsonSerializer.Deserialize(body, GoogleJsonContext.Default.TokenResponse)
            ?? throw new InvalidDataException("Google returned an empty token response.");

        return new TokenSet(token.AccessToken, time.GetUtcNow().AddSeconds(token.ExpiresIn), token.RefreshToken, token.Scope ?? "");
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleOAuthClientTests|FullyQualifiedName~OAuthClientCredentialsTests"`
Expected: PASS, 12 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Auth tests/LeafCalendar.Tests
git commit -m "feat(core): add Google OAuth client with PKCE exchange, refresh, revoke"
```

---

### Task 5: Token Store (Credential Locker)

**Files:**
- Create: `src/LeafCalendar.Core/Auth/ITokenStore.cs`, `src/LeafCalendar.Core/Auth/CredentialLockerTokenStore.cs`
- Create: `tests/LeafCalendar.Tests/Support/InMemoryTokenStore.cs`
- Test: `tests/LeafCalendar.Tests/CredentialLockerTokenStoreTests.cs`

**Interfaces:**
- Consumes: `OAuthClientCredentials` (Task 4).
- Produces:
  - `interface ITokenStore`, with these members:
    - `OAuthClientCredentials? GetClientCredentials()`
    - `void SetClientCredentials(OAuthClientCredentials credentials)`
    - `string? GetRefreshToken(string accountId)`
    - `void SetRefreshToken(string accountId, string refreshToken)`
    - `void RemoveRefreshToken(string accountId)`
    - `IReadOnlyList<string> GetAccountIds()`
  - `sealed class CredentialLockerTokenStore(string profile) : ITokenStore`, plus `void DeleteAll()`.
  - Test double `LeafCalendar.Tests.Support.InMemoryTokenStore : ITokenStore`.

- [ ] **Step 1: Write the failing integration test (real Credential Locker, unique profile, cleaned up)**

`tests/LeafCalendar.Tests/CredentialLockerTokenStoreTests.cs`:
```csharp
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public sealed class CredentialLockerTokenStoreTests : IDisposable
{
    readonly CredentialLockerTokenStore _store = new("test-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() => _store.DeleteAll();

    [Fact]
    public void GetClientCredentials_Empty_ReturnsNull()
    {
        Assert.Null(_store.GetClientCredentials());
        Assert.Null(_store.GetRefreshToken("missing"));
        Assert.Empty(_store.GetAccountIds());
    }

    [Fact]
    public void SetClientCredentials_Twice_KeepsLatest()
    {
        _store.SetClientCredentials(new("one.apps.googleusercontent.com", "secret-1"));
        _store.SetClientCredentials(new("two.apps.googleusercontent.com", "secret-2"));

        Assert.Equal(new OAuthClientCredentials("two.apps.googleusercontent.com", "secret-2"), _store.GetClientCredentials());
    }

    [Fact]
    public void RefreshTokens_SetReplaceRemove_RoundTrip()
    {
        _store.SetRefreshToken("acct-1", "1//a");
        _store.SetRefreshToken("acct-2", "1//b");
        _store.SetRefreshToken("acct-1", "1//a2");

        Assert.Equal("1//a2", _store.GetRefreshToken("acct-1"));
        Assert.Equal(["acct-1", "acct-2"], _store.GetAccountIds().Order(StringComparer.Ordinal));

        _store.RemoveRefreshToken("acct-1");

        Assert.Null(_store.GetRefreshToken("acct-1"));
        Assert.Equal(["acct-2"], _store.GetAccountIds());
    }

    [Fact]
    public void Profiles_Different_AreIsolated()
    {
        var other = new CredentialLockerTokenStore("test-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            _store.SetRefreshToken("acct-1", "1//mine");

            Assert.Null(other.GetRefreshToken("acct-1"));
        }
        finally
        {
            other.DeleteAll();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~CredentialLockerTokenStoreTests"`
Expected: build FAILS, `CredentialLockerTokenStore` not found.

- [ ] **Step 3: Implement the interface, locker store, and in-memory double**

`src/LeafCalendar.Core/Auth/ITokenStore.cs`:
```csharp
namespace LeafCalendar.Core.Auth;

/// <summary>
/// Secret storage for the OAuth client and each account's refresh token.
/// </summary>
/// <remarks>
/// Production uses <see cref="CredentialLockerTokenStore"/>; tests use an in-memory double.
/// Access tokens are never stored here: they live in memory in <see cref="AccessTokenProvider"/>.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Returns the saved OAuth client, or null before setup.</summary>
    OAuthClientCredentials? GetClientCredentials();

    /// <summary>Saves (replaces) the OAuth client.</summary>
    void SetClientCredentials(OAuthClientCredentials credentials);

    /// <summary>Returns an account's refresh token, or null.</summary>
    string? GetRefreshToken(string accountId);

    /// <summary>Saves (replaces) an account's refresh token.</summary>
    void SetRefreshToken(string accountId, string refreshToken);

    /// <summary>Deletes an account's refresh token.</summary>
    void RemoveRefreshToken(string accountId);

    /// <summary>IDs of accounts that have a refresh token.</summary>
    IReadOnlyList<string> GetAccountIds();
}
```

`src/LeafCalendar.Core/Auth/CredentialLockerTokenStore.cs`:
```csharp
using Windows.Security.Credentials;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Stores Leaf's secrets in Windows Credential Locker (<see cref="PasswordVault"/>).
/// </summary>
/// <remarks>
/// Entries are encrypted with the current Windows user's login and namespaced by profile:
/// <c>LeafCalendar/{profile}/client</c> holds the client ID and secret, and
/// <c>LeafCalendar/{profile}/refresh</c> holds one entry per account, keyed by account ID.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/apps/develop/security/credential-locker"/>
public sealed class CredentialLockerTokenStore(string profile) : ITokenStore
{
    const string ClientIdUser     = "client-id";
    const string ClientSecretUser = "client-secret";
    const int ElementNotFound     = unchecked((int)0x80070490);

    readonly PasswordVault _vault = new();

    string ClientResource  => $"LeafCalendar/{profile}/client";
    string RefreshResource => $"LeafCalendar/{profile}/refresh";

    /// <inheritdoc />
    public OAuthClientCredentials? GetClientCredentials()
    {
        var id     = Read(ClientResource, ClientIdUser);
        var secret = Read(ClientResource, ClientSecretUser);

        return id is null || secret is null ? null : new OAuthClientCredentials(id, secret);
    }

    /// <inheritdoc />
    public void SetClientCredentials(OAuthClientCredentials credentials)
    {
        Write(ClientResource, ClientIdUser, credentials.ClientId);
        Write(ClientResource, ClientSecretUser, credentials.ClientSecret);
    }

    /// <inheritdoc />
    public string? GetRefreshToken(string accountId) => Read(RefreshResource, accountId);

    /// <inheritdoc />
    public void SetRefreshToken(string accountId, string refreshToken) => Write(RefreshResource, accountId, refreshToken);

    /// <inheritdoc />
    public void RemoveRefreshToken(string accountId) => Remove(RefreshResource, accountId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAccountIds() => [.. FindAll(RefreshResource).Select(c => c.UserName)];

    /// <summary>Deletes every secret for this profile (tests and profile resets).</summary>
    public void DeleteAll()
    {
        foreach (var credential in FindAll(ClientResource).Concat(FindAll(RefreshResource)))
        {
            _vault.Remove(credential);
        }
    }

    string? Read(string resource, string user)
    {
        var credential = FindAll(resource).FirstOrDefault(c => c.UserName == user);
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
    }

    void Write(string resource, string user, string value)
    {
        Remove(resource, user);
        _vault.Add(new PasswordCredential(resource, user, value));
    }

    void Remove(string resource, string user)
    {
        var credential = FindAll(resource).FirstOrDefault(c => c.UserName == user);
        if (credential is not null)
        {
            _vault.Remove(credential);
        }
    }

    // FindAllByResource throws "Element not found" (0x80070490) when a resource has no entries.
    List<PasswordCredential> FindAll(string resource)
    {
        try
        {
            return [.. _vault.FindAllByResource(resource)];
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            return [];
        }
    }
}
```

`tests/LeafCalendar.Tests/Support/InMemoryTokenStore.cs`:
```csharp
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests.Support;

/// <summary>In-memory <see cref="ITokenStore"/> for logic tests.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    readonly Dictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
    OAuthClientCredentials? _client;

    /// <inheritdoc />
    public OAuthClientCredentials? GetClientCredentials() => _client;

    /// <inheritdoc />
    public void SetClientCredentials(OAuthClientCredentials credentials) => _client = credentials;

    /// <inheritdoc />
    public string? GetRefreshToken(string accountId) => _refreshTokens.GetValueOrDefault(accountId);

    /// <inheritdoc />
    public void SetRefreshToken(string accountId, string refreshToken) => _refreshTokens[accountId] = refreshToken;

    /// <inheritdoc />
    public void RemoveRefreshToken(string accountId) => _refreshTokens.Remove(accountId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAccountIds() => [.. _refreshTokens.Keys];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~CredentialLockerTokenStoreTests"`
Expected: PASS, 4 tests. If `FindAllByResource` throws a different HRESULT on this machine, the test output shows it. Stop and report that instead of widening the catch.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Auth tests/LeafCalendar.Tests
git commit -m "feat(core): store OAuth client and refresh tokens in Credential Locker"
```

---

### Task 6: Access Token Provider

**Files:**
- Create: `src/LeafCalendar.Core/Auth/AccessTokenProvider.cs`
- Test: `tests/LeafCalendar.Tests/AccessTokenProviderTests.cs`

**Interfaces:**
- Consumes: `GoogleOAuthClient`, `TokenSet`, `InvalidGrantException`, `AccountNeedsSignInException` (Task 4); `ITokenStore`, `InMemoryTokenStore` (Task 5).
- Produces: `sealed class AccessTokenProvider(GoogleOAuthClient oauth, ITokenStore store, TimeProvider time)`, with these members:
  - `ValueTask<string> GetAccessTokenAsync(string accountId, CancellationToken ct)`, which throws `AccountNeedsSignInException`.
  - `void Seed(string accountId, TokenSet tokens)`
  - `void Forget(string accountId)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/AccessTokenProviderTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class AccessTokenProviderTests
{
    const string TokenUrl = "https://oauth2.googleapis.com/token";

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly InMemoryTokenStore _store = new();

    AccessTokenProvider CreateProvider() =>
        new(new GoogleOAuthClient(new HttpClient(_google), new("id.apps.googleusercontent.com", "secret"), _time), _store, _time);

    [Fact]
    public async Task GetAccessTokenAsync_FreshToken_ReusesCache()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        var provider = CreateProvider();

        var first  = await provider.GetAccessTokenAsync("acct", ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.GetAccessTokenAsync("acct", ct);

        Assert.Equal("ya29.test-refreshed-token", first);
        Assert.Equal(first, second);
        Assert.Single(_google.Requests);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithinOneMinuteOfExpiry_Refreshes()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        var provider = CreateProvider();
        provider.Seed("acct", new TokenSet("ya29.seeded", _time.GetUtcNow().AddSeconds(50), null, ""));

        var token = await provider.GetAccessTokenAsync("acct", ct);

        Assert.Equal("ya29.test-refreshed-token", token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_GoogleRotatesRefreshToken_SavesNewOne()
    {
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh-rotated.json"));

        await CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken);

        Assert.Equal("1//test-rotated-refresh-token", _store.GetRefreshToken("acct"));
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidGrant_ThrowsNeedsSignIn()
    {
        _store.SetRefreshToken("acct", "1//revoked");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json"));

        var error = await Assert.ThrowsAsync<AccountNeedsSignInException>(
            () => CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("acct", error.AccountId);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoRefreshToken_ThrowsNeedsSignIn()
    {
        await Assert.ThrowsAsync<AccountNeedsSignInException>(
            () => CreateProvider().GetAccessTokenAsync("acct", TestContext.Current.CancellationToken).AsTask());

        Assert.Empty(_google.Requests);
    }

    [Fact]
    public async Task Forget_AfterSeed_ForcesRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        _store.SetRefreshToken("acct", "1//test-refresh-token");
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        var provider = CreateProvider();
        provider.Seed("acct", new TokenSet("ya29.seeded", _time.GetUtcNow().AddHours(1), null, ""));

        provider.Forget("acct");
        var token = await provider.GetAccessTokenAsync("acct", ct);

        Assert.Equal("ya29.test-refreshed-token", token);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~AccessTokenProviderTests"`
Expected: build FAILS, `AccessTokenProvider` not found.

- [ ] **Step 3: Implement AccessTokenProvider**

`src/LeafCalendar.Core/Auth/AccessTokenProvider.cs`:
```csharp
using System.Collections.Concurrent;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Hands out access tokens per account, refreshing them shortly before they expire.
/// </summary>
/// <remarks>
/// Access tokens live only in memory. When Google rotates the refresh token, the new one is saved.
/// A missing or rejected refresh token becomes <see cref="AccountNeedsSignInException"/> so callers
/// can mark the account instead of retrying.
/// </remarks>
public sealed class AccessTokenProvider(GoogleOAuthClient oauth, ITokenStore store, TimeProvider time)
{
    static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    readonly ConcurrentDictionary<string, TokenSet> _cache = new(StringComparer.Ordinal);

    // ponytail: one lock for all accounts; switch to per-account locks if refreshes ever contend.
    readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Returns a valid access token for <paramref name="accountId"/>.</summary>
    /// <exception cref="AccountNeedsSignInException">No usable refresh token.</exception>
    public async ValueTask<string> GetAccessTokenAsync(string accountId, CancellationToken ct)
    {
        if (TryGetFresh(accountId, out var cached))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited
            if (TryGetFresh(accountId, out cached))
            {
                return cached;
            }

            var refreshToken = store.GetRefreshToken(accountId) ?? throw new AccountNeedsSignInException(accountId);

            TokenSet tokens;
            try
            {
                tokens = await oauth.RefreshAsync(refreshToken, ct);
            }
            catch (InvalidGrantException)
            {
                throw new AccountNeedsSignInException(accountId);
            }

            // Save Rotated Refresh Token
            if (tokens.RefreshToken is { } rotated)
            {
                store.SetRefreshToken(accountId, rotated);
            }

            _cache[accountId] = tokens;
            return tokens.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Caches tokens obtained elsewhere (right after sign-in).</summary>
    public void Seed(string accountId, TokenSet tokens) => _cache[accountId] = tokens;

    /// <summary>Drops the cached token, e.g. after Google answers 401.</summary>
    public void Forget(string accountId) => _cache.TryRemove(accountId, out _);

    bool TryGetFresh(string accountId, out string token)
    {
        if (_cache.TryGetValue(accountId, out var tokens) && tokens.ExpiresAt - time.GetUtcNow() > RefreshMargin)
        {
            token = tokens.AccessToken;
            return true;
        }

        token = "";
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~AccessTokenProviderTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Auth/AccessTokenProvider.cs tests/LeafCalendar.Tests/AccessTokenProviderTests.cs
git commit -m "feat(core): cache and refresh access tokens per account"
```

---

### Task 7: Redacting App Log

**Files:**
- Create: `src/LeafCalendar.Core/Diagnostics/AppLog.cs`, `tests/LeafCalendar.Tests/Support/TempFolder.cs`
- Test: `tests/LeafCalendar.Tests/AppLogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed partial class AppLog(string directory, TimeProvider time)`, with these members:
    - `string FilePath`
    - `void Info(string eventName, string? detail = null)`
    - `void Error(string eventName, Exception exception)`
    - `static string Redact(string text)`
  - Test support `TempFolder : IDisposable` with a `Path` property.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/Support/TempFolder.cs`:
```csharp
namespace LeafCalendar.Tests.Support;

/// <summary>A unique temp folder deleted on dispose.</summary>
public sealed class TempFolder : IDisposable
{
    /// <summary>The folder path (created).</summary>
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "leaf-tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file is still open (e.g. SQLite pool); the OS temp cleaner will remove it.
        }
    }
}
```

`tests/LeafCalendar.Tests/AppLogTests.cs`:
```csharp
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class AppLogTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("token ya29.a0AfB_byC-xyz leaked", "token [token] leaked")]
    [InlineData("refresh 1//0gAbc-def_ghi leaked", "refresh [token] leaked")]
    [InlineData("code 4/0AeanS0b-xyz leaked", "code [token] leaked")]
    [InlineData("jwt eyJhbGciOi.eyJzdWIi.sig leaked", "jwt [token] leaked")]
    [InlineData("secret GOCSPX-abc_DEF-123 leaked", "secret [token] leaked")]
    [InlineData("calendar family123@group.calendar.google.com synced", "calendar [email] synced")]
    [InlineData("account=109876543210 ok", "account=109876543210 ok")]
    public void Redact_SensitiveText_MasksIt(string input, string expected)
    {
        Assert.Equal(expected, AppLog.Redact(input));
    }

    [Fact]
    public void Info_Detail_WritesTimestampedRedactedLine()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Info("sync.calendar.done", "calendar=me@example.com changes=3");

        Assert.Equal(
            "2026-09-29T12:00:00.0000000+00:00 INFO sync.calendar.done calendar=[email] changes=3",
            File.ReadAllLines(log.FilePath).Single());
    }

    [Fact]
    public void Error_Exception_WritesTypeAndRedactedMessage()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Error("sync.failed", new InvalidOperationException("bad token ya29.abc"));

        Assert.EndsWith("ERROR sync.failed InvalidOperationException: bad token [token]", File.ReadAllLines(log.FilePath).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Info_OverOneMegabyte_RollsToBackupFile()
    {
        var log = new AppLog(_folder.Path, _time);
        File.WriteAllText(log.FilePath, new string('x', 1_000_001));

        log.Info("after.roll");

        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.Single(File.ReadAllLines(log.FilePath));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~AppLogTests"`
Expected: build FAILS, `AppLog` not found.

- [ ] **Step 3: Implement AppLog**

`src/LeafCalendar.Core/Diagnostics/AppLog.cs`:
```csharp
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Diagnostics;

/// <summary>
/// Small rolling log file for troubleshooting.
/// </summary>
/// <remarks>
/// Callers log an event name plus internal IDs, never event titles, descriptions, guests, or
/// locations. As a second line of defense, every detail passes through <see cref="Redact"/>,
/// which masks Google tokens, auth codes, client secrets, JWTs, and email addresses. The file
/// rolls to <c>leaf.log.1</c> after 1 MB, so at most about 2 MB is kept.
/// </remarks>
public sealed partial class AppLog(string directory, TimeProvider time)
{
    const long MaxBytes = 1_000_000;

    readonly Lock _gate = new();

    /// <summary>Current log file.</summary>
    public string FilePath => Path.Combine(directory, "leaf.log");

    /// <summary>Writes an informational line.</summary>
    public void Info(string eventName, string? detail = null) => Write("INFO", eventName, detail);

    /// <summary>Writes an error line with the exception type and redacted message (no stack trace).</summary>
    public void Error(string eventName, Exception exception) =>
        Write("ERROR", eventName, $"{exception.GetType().Name}: {exception.Message}");

    /// <summary>Masks secrets and email addresses in <paramref name="text"/>.</summary>
    public static string Redact(string text) =>
        EmailPattern().Replace(TokenPattern().Replace(text, "[token]"), "[email]");

    void Write(string level, string eventName, string? detail)
    {
        var line = detail is null
            ? $"{time.GetUtcNow():O} {level} {eventName}"
            : $"{time.GetUtcNow():O} {level} {eventName} {Redact(detail)}";

        lock (_gate)
        {
            Directory.CreateDirectory(directory);

            // Roll At 1 MB
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > MaxBytes)
            {
                File.Move(FilePath, FilePath + ".1", overwrite: true);
            }

            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
    }

    [GeneratedRegex(@"(ya29\.[\w\-.]+|1//[\w\-.]+|4/[\w\-.]+|eyJ[\w\-.]+|GOCSPX-[\w\-]+)")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(\.[\w\-]+)+")]
    private static partial Regex EmailPattern();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~AppLogTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Diagnostics tests/LeafCalendar.Tests
git commit -m "feat(core): add rolling redacting log"
```

---

### Task 8: Database, Accounts, and Calendars

**Files:**
- Create: `src/LeafCalendar.Core/Data/Schema.cs`, `SqliteExtensions.cs`, `LeafDatabase.cs`, `AccountStore.cs`, `CalendarStore.cs`
- Create: `tests/LeafCalendar.Tests/Support/TestDatabase.cs`
- Test: `tests/LeafCalendar.Tests/LeafDatabaseTests.cs`, `tests/LeafCalendar.Tests/AccountStoreTests.cs`, `tests/LeafCalendar.Tests/CalendarStoreTests.cs`

**Interfaces:**
- Consumes: `CalendarListEntry`, `ReminderOverride`, `GoogleJsonContext` (Task 3); `TempFolder` (Task 7).
- Produces (namespace `LeafCalendar.Core.Data`):
  - `sealed class LeafDatabase(string path)` with `SqliteConnection Open()` (foreign keys on) and `void Migrate()` (WAL, `user_version` 1).
  - Internal `SqliteExtensions`:
    - `int Execute(this SqliteConnection, SqliteTransaction?, string sql, params (string Name, object? Value)[] parameters)`
    - `List<T> Query<T>(this SqliteConnection, SqliteTransaction?, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)`
    - `DateTimeOffset? GetUnixMsOrNull(this SqliteDataReader, int ordinal)`
  - `enum AccountStatus { Ok, NeedsSignIn }` and `sealed record Account(string Id, string Email, string? DisplayName, string? Picture, AccountStatus Status)`.
  - `static class AccountStore`:
    - `Upsert(SqliteConnection, Account)`
    - `IReadOnlyList<Account> GetAll(SqliteConnection)`
    - `SetStatus(SqliteConnection, string id, AccountStatus)`
    - `Delete(SqliteConnection, string id)`
  - `sealed record CalendarInfo(string AccountId, string Id, string Summary, string? BackgroundColor, string AccessRole, bool IsPrimary, bool Hidden, string? SyncToken)`.
  - `static class CalendarStore`:
    - `ReplaceForAccount(SqliteConnection, string accountId, IReadOnlyList<CalendarListEntry> entries)`
    - `IReadOnlyList<CalendarInfo> GetForAccount(SqliteConnection, string accountId)`
    - `SetSyncToken(SqliteConnection, SqliteTransaction?, string accountId, string calendarId, string? syncToken)`
  - Test support `TestDatabase : IDisposable` with a `LeafDatabase Database` property, plus `static Account SampleAccount`.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/Support/TestDatabase.cs`:
```csharp
using LeafCalendar.Core.Data;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Tests.Support;

/// <summary>A migrated SQLite database in a temp folder, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    readonly TempFolder _folder = new();

    /// <summary>Creates and migrates the database.</summary>
    public TestDatabase()
    {
        Database = new LeafDatabase(Path.Combine(_folder.Path, "leaf.db"));
        Database.Migrate();
    }

    /// <summary>The database under test.</summary>
    public LeafDatabase Database { get; }

    /// <summary>The account used by fixtures (matches userinfo.json).</summary>
    public static Account SampleAccount { get; } =
        new("109876543210", "leaf.tester@gmail.com", "Leaf Tester", null, AccountStatus.Ok);

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }
}
```

`tests/LeafCalendar.Tests/LeafDatabaseTests.cs`:
```csharp
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class LeafDatabaseTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Migrate_Twice_IsIdempotentAndUsesWal()
    {
        _db.Database.Migrate();

        using var conn = _db.Database.Open();
        using var mode = conn.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        using var version = conn.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        using var foreignKeys = conn.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";

        Assert.Equal("wal", (string)mode.ExecuteScalar()!);
        Assert.Equal(1L, (long)version.ExecuteScalar()!);
        Assert.Equal(1L, (long)foreignKeys.ExecuteScalar()!);
    }
}
```

`tests/LeafCalendar.Tests/AccountStoreTests.cs`:
```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AccountStoreTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Upsert_SameIdTwice_UpdatesSingleRow()
    {
        using var conn = _db.Database.Open();

        AccountStore.Upsert(conn, TestDatabase.SampleAccount with { Status = AccountStatus.NeedsSignIn });
        AccountStore.Upsert(conn, TestDatabase.SampleAccount with { DisplayName = "Renamed" });

        var account = Assert.Single(AccountStore.GetAll(conn));
        Assert.Equal("Renamed", account.DisplayName);
        Assert.Equal(AccountStatus.Ok, account.Status);
    }

    [Fact]
    public void SetStatus_NeedsSignIn_Persists()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        AccountStore.SetStatus(conn, TestDatabase.SampleAccount.Id, AccountStatus.NeedsSignIn);

        Assert.Equal(AccountStatus.NeedsSignIn, AccountStore.GetAll(conn).Single().Status);
    }

    [Fact]
    public void Delete_Existing_RemovesRow()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        AccountStore.Delete(conn, TestDatabase.SampleAccount.Id);

        Assert.Empty(AccountStore.GetAll(conn));
    }
}
```

`tests/LeafCalendar.Tests/CalendarStoreTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class CalendarStoreTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    static List<CalendarListEntry> Entries(string fixture) =>
        JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.CalendarListPage)!.Items;

    [Fact]
    public void ReplaceForAccount_NewList_StoresInOrder()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        CalendarStore.ReplaceForAccount(conn, TestDatabase.SampleAccount.Id, Entries("calendar-list.json"));

        var calendars = CalendarStore.GetForAccount(conn, TestDatabase.SampleAccount.Id);
        Assert.Equal(["leaf.tester@gmail.com", "family123@group.calendar.google.com"], calendars.Select(c => c.Id));
        Assert.True(calendars[0].IsPrimary);
        Assert.Equal("Family", calendars[1].Summary);
        Assert.Equal("writer", calendars[1].AccessRole);
    }

    [Fact]
    public void ReplaceForAccount_Again_KeepsSyncTokenAndDropsMissingCalendars()
    {
        using var conn = _db.Database.Open();
        var accountId = TestDatabase.SampleAccount.Id;
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list.json"));
        CalendarStore.SetSyncToken(conn, null, accountId, "leaf.tester@gmail.com", "sync-token-1");

        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list-primary-only.json"));

        var calendar = Assert.Single(CalendarStore.GetForAccount(conn, accountId));
        Assert.Equal("sync-token-1", calendar.SyncToken);
    }

    [Fact]
    public void DeleteAccount_WithCalendars_CascadesToCalendars()
    {
        using var conn = _db.Database.Open();
        var accountId = TestDatabase.SampleAccount.Id;
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list.json"));

        AccountStore.Delete(conn, accountId);

        Assert.Empty(CalendarStore.GetForAccount(conn, accountId));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~LeafDatabaseTests|FullyQualifiedName~AccountStoreTests|FullyQualifiedName~CalendarStoreTests"`
Expected: build FAILS, `LeafDatabase` not found.

- [ ] **Step 3: Implement schema, helpers, database, and stores**

`src/LeafCalendar.Core/Data/Schema.cs`:
```csharp
namespace LeafCalendar.Core.Data;

/// <summary>SQL schema migrations, applied in order by <see cref="LeafDatabase.Migrate"/>.</summary>
internal static class Schema
{
    /// <summary>Version 1: accounts, calendars, and events. Outbox and conflicts arrive in Milestone 3.</summary>
    public const string V1 = """
        CREATE TABLE accounts (
            id           TEXT PRIMARY KEY,
            email        TEXT NOT NULL,
            display_name TEXT,
            picture      TEXT,
            status       TEXT NOT NULL DEFAULT 'ok'
        );

        CREATE TABLE calendars (
            account_id        TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            id                TEXT NOT NULL,
            summary           TEXT NOT NULL,
            summary_override  TEXT,
            time_zone         TEXT,
            background_color  TEXT,
            foreground_color  TEXT,
            access_role       TEXT NOT NULL,
            is_primary        INTEGER NOT NULL DEFAULT 0,
            hidden            INTEGER NOT NULL DEFAULT 0,
            sort_order        INTEGER NOT NULL DEFAULT 0,
            default_reminders TEXT,
            sync_token        TEXT,
            PRIMARY KEY (account_id, id)
        );

        CREATE TABLE events (
            account_id          TEXT NOT NULL,
            calendar_id         TEXT NOT NULL,
            id                  TEXT NOT NULL,
            ical_uid            TEXT,
            etag                TEXT,
            status              TEXT NOT NULL,
            start_utc           INTEGER,
            end_utc             INTEGER,
            is_all_day          INTEGER NOT NULL DEFAULT 0,
            start_time_zone     TEXT,
            is_recurring_master INTEGER NOT NULL DEFAULT 0,
            recurring_event_id  TEXT,
            original_start_utc  INTEGER,
            updated_utc         INTEGER,
            raw_json            TEXT NOT NULL,
            PRIMARY KEY (account_id, calendar_id, id),
            FOREIGN KEY (account_id, calendar_id) REFERENCES calendars(account_id, id) ON DELETE CASCADE
        );

        CREATE INDEX ix_events_range  ON events (account_id, calendar_id, start_utc, end_utc);
        CREATE INDEX ix_events_master ON events (account_id, calendar_id, recurring_event_id);
        """;
}
```

`src/LeafCalendar.Core/Data/SqliteExtensions.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>Parameterized SQL helpers. SQL text is always a constant; values always go through parameters.</summary>
internal static class SqliteExtensions
{
    /// <summary>Runs a non-query statement.</summary>
    public static int Execute(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(conn, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query and maps each row.</summary>
    public static List<T> Query<T>(this SqliteConnection conn, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(conn, tx, sql, parameters);
        using var reader  = command.ExecuteReader();

        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Reads a Unix-milliseconds column as UTC, or null.</summary>
    public static DateTimeOffset? GetUnixMsOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    /// <summary>Reads a nullable text column.</summary>
    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    [SuppressMessage("Security", "CA2100", Justification = "Callers pass constant SQL from the Data folder; values are always parameters.")]
    static SqliteCommand Create(SqliteConnection conn, SqliteTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
```

`src/LeafCalendar.Core/Data/LeafDatabase.cs`:
```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// Leaf's local SQLite database (one file per profile).
/// </summary>
/// <remarks>
/// Uses WAL mode so the UI can read while sync writes. Every connection turns on foreign keys,
/// so deleting an account cascades to its calendars and events. Schema changes are numbered
/// migrations tracked in <c>PRAGMA user_version</c>.
/// </remarks>
public sealed class LeafDatabase(string path)
{
    /// <summary>Database file path.</summary>
    public string Path { get; } = path;

    /// <summary>Opens a pooled connection with foreign keys enabled. Dispose it after use.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = true }.ToString());
        connection.Open();
        connection.Execute(null, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    /// <summary>Creates the file if needed and applies pending migrations.</summary>
    public void Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        using var conn = Open();
        conn.Execute(null, "PRAGMA journal_mode = WAL;");

        var version = Convert.ToInt32(conn.Query(null, "PRAGMA user_version;", r => r.GetInt64(0)).Single(), CultureInfo.InvariantCulture);

        // Version 1
        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V1);
            conn.Execute(tx, "PRAGMA user_version = 1;");
            tx.Commit();
        }
    }
}
```

`src/LeafCalendar.Core/Data/AccountStore.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>Whether an account can sync.</summary>
public enum AccountStatus
{
    /// <summary>Tokens work.</summary>
    Ok,

    /// <summary>Refresh token missing or rejected; user must sign in again.</summary>
    NeedsSignIn,
}

/// <summary>A connected Google account. <see cref="Id"/> is Google's stable <c>sub</c>.</summary>
public sealed record Account(string Id, string Email, string? DisplayName, string? Picture, AccountStatus Status);

/// <summary>Reads and writes the <c>accounts</c> table.</summary>
public static class AccountStore
{
    /// <summary>Inserts or updates an account (same Google account never duplicates).</summary>
    public static void Upsert(SqliteConnection conn, Account account) =>
        conn.Execute(
            null,
            """
            INSERT INTO accounts (id, email, display_name, picture, status)
            VALUES ($id, $email, $name, $picture, $status)
            ON CONFLICT (id) DO UPDATE SET
                email        = excluded.email,
                display_name = excluded.display_name,
                picture      = excluded.picture,
                status       = excluded.status;
            """,
            ("$id", account.Id),
            ("$email", account.Email),
            ("$name", account.DisplayName),
            ("$picture", account.Picture),
            ("$status", ToText(account.Status)));

    /// <summary>All accounts, ordered by email.</summary>
    public static IReadOnlyList<Account> GetAll(SqliteConnection conn) =>
        conn.Query(
            null,
            "SELECT id, email, display_name, picture, status FROM accounts ORDER BY email;",
            r => new Account(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), FromText(r.GetString(4))));

    /// <summary>Updates an account's status.</summary>
    public static void SetStatus(SqliteConnection conn, string id, AccountStatus status) =>
        conn.Execute(null, "UPDATE accounts SET status = $status WHERE id = $id;", ("$status", ToText(status)), ("$id", id));

    /// <summary>Deletes an account and (by cascade) its calendars and events.</summary>
    public static void Delete(SqliteConnection conn, string id) =>
        conn.Execute(null, "DELETE FROM accounts WHERE id = $id;", ("$id", id));

    static string ToText(AccountStatus status) => status == AccountStatus.NeedsSignIn ? "needs-sign-in" : "ok";

    static AccountStatus FromText(string text) => text == "needs-sign-in" ? AccountStatus.NeedsSignIn : AccountStatus.Ok;
}
```

`src/LeafCalendar.Core/Data/CalendarStore.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>A stored calendar. <see cref="SyncToken"/> is null until the first full sync finishes.</summary>
public sealed record CalendarInfo(
    string AccountId,
    string Id,
    string Summary,
    string? BackgroundColor,
    string AccessRole,
    bool IsPrimary,
    bool Hidden,
    string? SyncToken);

/// <summary>Reads and writes the <c>calendars</c> table.</summary>
public static class CalendarStore
{
    /// <summary>
    /// Makes the account's calendars match Google's list.
    /// </summary>
    /// <remarks>
    /// Calendars missing from the list (or marked deleted) are removed along with their events.
    /// Existing calendars keep their sync token, so their next sync stays incremental.
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
        for (var i = 0; i < incoming.Count; i++)
        {
            var entry = incoming[i];
            conn.Execute(
                tx,
                """
                INSERT INTO calendars (account_id, id, summary, summary_override, time_zone, background_color, foreground_color,
                                       access_role, is_primary, hidden, sort_order, default_reminders)
                VALUES ($account, $id, $summary, $override, $zone, $background, $foreground, $role, $primary, $hidden, $order, $reminders)
                ON CONFLICT (account_id, id) DO UPDATE SET
                    summary           = excluded.summary,
                    summary_override  = excluded.summary_override,
                    time_zone         = excluded.time_zone,
                    background_color  = excluded.background_color,
                    foreground_color  = excluded.foreground_color,
                    access_role       = excluded.access_role,
                    is_primary        = excluded.is_primary,
                    hidden            = excluded.hidden,
                    default_reminders = excluded.default_reminders;
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
                ("$order", i),
                ("$reminders", JsonSerializer.Serialize(entry.DefaultReminders ?? [], GoogleJsonContext.Default.ListReminderOverride)));
        }

        tx.Commit();
    }

    /// <summary>An account's calendars in list order.</summary>
    public static IReadOnlyList<CalendarInfo> GetForAccount(SqliteConnection conn, string accountId) =>
        conn.Query(
            null,
            """
            SELECT account_id, id, COALESCE(summary_override, summary), background_color, access_role, is_primary, hidden, sync_token
            FROM calendars WHERE account_id = $account ORDER BY sort_order;
            """,
            r => new CalendarInfo(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetStringOrNull(3),
                r.GetString(4),
                r.GetBoolean(5),
                r.GetBoolean(6),
                r.GetStringOrNull(7)),
            ("$account", accountId));

    /// <summary>Saves the token for the calendar's next incremental sync.</summary>
    public static void SetSyncToken(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string? syncToken) =>
        conn.Execute(
            tx,
            "UPDATE calendars SET sync_token = $token WHERE account_id = $account AND id = $id;",
            ("$token", syncToken),
            ("$account", accountId),
            ("$id", calendarId));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~LeafDatabaseTests|FullyQualifiedName~AccountStoreTests|FullyQualifiedName~CalendarStoreTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Data tests/LeafCalendar.Tests
git commit -m "feat(core): add SQLite database with account and calendar stores"
```

---

### Task 9: Event Store

**Files:**
- Create: `src/LeafCalendar.Core/Data/EventStore.cs`
- Test: `tests/LeafCalendar.Tests/EventStoreTests.cs`

**Interfaces:**
- Consumes: `SqliteExtensions`, `CalendarStore`, `AccountStore` (Task 8); `GoogleEvent`, `EventDateTime`, `GoogleJsonContext` (Task 3); `TestDatabase` (Task 8).
- Produces:
  - `sealed record StoredEvent(string Id, string Status, DateTimeOffset? Start, DateTimeOffset? End, bool IsAllDay, string? RecurringEventId, DateTimeOffset? OriginalStart, string? Etag, string RawJson)`.
  - `static class EventStore`:
    - `void Apply(SqliteConnection, SqliteTransaction?, string accountId, string calendarId, JsonElement item)`
    - `void DeleteAllForCalendar(SqliteConnection, SqliteTransaction?, string accountId, string calendarId)`
    - `StoredEvent? Get(SqliteConnection, string accountId, string calendarId, string id)`
    - `int Count(SqliteConnection, string accountId, string calendarId)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/EventStoreTests.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class EventStoreTests : IDisposable
{
    const string Calendar = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    readonly TestDatabase _db = new();

    public EventStoreTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(
            conn,
            Account,
            JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
    }

    public void Dispose() => _db.Dispose();

    static List<JsonElement> Items(string fixture) =>
        JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items;

    void ApplyAll(string fixture)
    {
        using var conn = _db.Database.Open();
        foreach (var item in Items(fixture))
        {
            EventStore.Apply(conn, null, Account, Calendar, item);
        }
    }

    [Fact]
    public void Apply_TimedEvent_StoresUtcRangeAndRawJson()
    {
        ApplyAll("events-page1.json");

        using var conn = _db.Database.Open();
        var stored = EventStore.Get(conn, Account, Calendar, "evt-single")!;

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), stored.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), stored.End);
        Assert.False(stored.IsAllDay);
        Assert.Equal("\"3181161784712000\"", stored.Etag);
        Assert.Contains("Dentist appointment", stored.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AllDayEvent_StoresMidnightUtcDates()
    {
        ApplyAll("events-page1.json");

        using var conn = _db.Database.Open();
        var stored = EventStore.Get(conn, Account, Calendar, "evt-allday")!;

        Assert.True(stored.IsAllDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), stored.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero), stored.End);
    }

    [Fact]
    public void Apply_CancelledStandalone_IsNotStored()
    {
        ApplyAll("events-page2.json");

        using var conn = _db.Database.Open();

        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-deleted"));
    }

    [Fact]
    public void Apply_CancelledOccurrence_KeptWithOriginalStart()
    {
        ApplyAll("events-page2.json");

        using var conn = _db.Database.Open();
        var occurrence = EventStore.Get(conn, Account, Calendar, "evt-weekly_20261007T133000Z")!;

        Assert.Equal("cancelled", occurrence.Status);
        Assert.Equal("evt-weekly", occurrence.RecurringEventId);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 13, 30, 0, TimeSpan.Zero), occurrence.OriginalStart);
        Assert.Null(occurrence.Start);
    }

    [Fact]
    public void Apply_CancelledMaster_RemovesMasterAndItsOccurrences()
    {
        ApplyAll("events-page2.json");

        using (var conn = _db.Database.Open())
        {
            using var doc = JsonDocument.Parse("""{"id":"evt-weekly","status":"cancelled"}""");
            EventStore.Apply(conn, null, Account, Calendar, doc.RootElement);
        }

        using var check = _db.Database.Open();
        Assert.Null(EventStore.Get(check, Account, Calendar, "evt-weekly"));
        Assert.Null(EventStore.Get(check, Account, Calendar, "evt-weekly_20261007T133000Z"));
    }

    [Fact]
    public void Apply_SameIdTwice_Updates()
    {
        ApplyAll("events-page1.json");
        ApplyAll("events-incremental.json");

        using var conn = _db.Database.Open();

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), EventStore.Get(conn, Account, Calendar, "evt-single")!.Start);
        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-allday"));
        Assert.Equal(2, EventStore.Count(conn, Account, Calendar));
    }

    [Fact]
    public void DeleteAllForCalendar_WithEvents_EmptiesOnlyThatCalendar()
    {
        ApplyAll("events-page1.json");
        using var conn = _db.Database.Open();

        EventStore.DeleteAllForCalendar(conn, null, Account, Calendar);

        Assert.Equal(0, EventStore.Count(conn, Account, Calendar));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~EventStoreTests"`
Expected: build FAILS, `EventStore` not found.

- [ ] **Step 3: Implement EventStore**

`src/LeafCalendar.Core/Data/EventStore.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// A stored event row. Times are UTC. All-day dates are stored as midnight UTC with
/// <see cref="IsAllDay"/> set, since all-day events float across time zones.
/// </summary>
public sealed record StoredEvent(
    string Id,
    string Status,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool IsAllDay,
    string? RecurringEventId,
    DateTimeOffset? OriginalStart,
    string? Etag,
    string RawJson);

/// <summary>Reads and writes the <c>events</c> table.</summary>
public static class EventStore
{
    /// <summary>
    /// Applies one event from Google.
    /// </summary>
    /// <remarks>
    /// A cancelled event without <c>recurringEventId</c> is a deletion: the row and any of its
    /// occurrence exceptions are removed. A cancelled occurrence of a repeating event is kept,
    /// since it hides that one date when the series is expanded. Everything else is upserted with
    /// Google's full JSON kept in <c>raw_json</c>.
    /// </remarks>
    public static void Apply(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, JsonElement item)
    {
        var ev = JsonSerializer.Deserialize(item, GoogleJsonContext.Default.GoogleEvent)
            ?? throw new InvalidDataException("Google returned an empty event.");

        // Deleted Event Or Series
        if (ev.Status == "cancelled" && ev.RecurringEventId is null)
        {
            conn.Execute(
                tx,
                "DELETE FROM events WHERE account_id = $account AND calendar_id = $calendar AND (id = $id OR recurring_event_id = $id);",
                ("$account", accountId),
                ("$calendar", calendarId),
                ("$id", ev.Id));
            return;
        }

        // Upsert
        conn.Execute(
            tx,
            """
            INSERT INTO events (account_id, calendar_id, id, ical_uid, etag, status, start_utc, end_utc, is_all_day, start_time_zone,
                                is_recurring_master, recurring_event_id, original_start_utc, updated_utc, raw_json)
            VALUES ($account, $calendar, $id, $uid, $etag, $status, $start, $end, $allDay, $zone,
                    $master, $recurringId, $originalStart, $updated, $raw)
            ON CONFLICT (account_id, calendar_id, id) DO UPDATE SET
                ical_uid            = excluded.ical_uid,
                etag                = excluded.etag,
                status              = excluded.status,
                start_utc           = excluded.start_utc,
                end_utc             = excluded.end_utc,
                is_all_day          = excluded.is_all_day,
                start_time_zone     = excluded.start_time_zone,
                is_recurring_master = excluded.is_recurring_master,
                recurring_event_id  = excluded.recurring_event_id,
                original_start_utc  = excluded.original_start_utc,
                updated_utc         = excluded.updated_utc,
                raw_json            = excluded.raw_json;
            """,
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", ev.Id),
            ("$uid", ev.ICalUid),
            ("$etag", ev.Etag),
            ("$status", ev.Status ?? "confirmed"),
            ("$start", ToUnixMs(ev.Start)),
            ("$end", ToUnixMs(ev.End)),
            ("$allDay", ev.Start?.Date is not null),
            ("$zone", ev.Start?.TimeZone),
            ("$master", ev.Recurrence is { Count: > 0 }),
            ("$recurringId", ev.RecurringEventId),
            ("$originalStart", ToUnixMs(ev.OriginalStartTime)),
            ("$updated", ev.Updated?.ToUnixTimeMilliseconds()),
            ("$raw", item.GetRawText()));
    }

    /// <summary>Removes every event of a calendar (before a full resync).</summary>
    public static void DeleteAllForCalendar(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId) =>
        conn.Execute(tx, "DELETE FROM events WHERE account_id = $account AND calendar_id = $calendar;", ("$account", accountId), ("$calendar", calendarId));

    /// <summary>Returns one event, or null.</summary>
    public static StoredEvent? Get(SqliteConnection conn, string accountId, string calendarId, string id) =>
        conn.Query(
            null,
            """
            SELECT id, status, start_utc, end_utc, is_all_day, recurring_event_id, original_start_utc, etag, raw_json
            FROM events WHERE account_id = $account AND calendar_id = $calendar AND id = $id;
            """,
            r => new StoredEvent(
                r.GetString(0),
                r.GetString(1),
                r.GetUnixMsOrNull(2),
                r.GetUnixMsOrNull(3),
                r.GetBoolean(4),
                r.GetStringOrNull(5),
                r.GetUnixMsOrNull(6),
                r.GetStringOrNull(7),
                r.GetString(8)),
            ("$account", accountId),
            ("$calendar", calendarId),
            ("$id", id)).SingleOrDefault();

    /// <summary>Number of stored rows for a calendar (including cancelled occurrences).</summary>
    public static int Count(SqliteConnection conn, string accountId, string calendarId) =>
        conn.Query(
            null,
            "SELECT COUNT(*) FROM events WHERE account_id = $account AND calendar_id = $calendar;",
            r => r.GetInt32(0),
            ("$account", accountId),
            ("$calendar", calendarId)).Single();

    static long? ToUnixMs(EventDateTime? value) => value switch
    {
        { DateTime: { } dateTime } => dateTime.ToUnixTimeMilliseconds(),
        { Date: { } date } => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds(),
        _ => null,
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~EventStoreTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Data/EventStore.cs tests/LeafCalendar.Tests/EventStoreTests.cs
git commit -m "feat(core): store Google events with UTC index and raw JSON"
```

---

### Task 10: Sign-In Flow

**Files:**
- Create: `src/LeafCalendar.Core/Auth/SignInFlow.cs`
- Test: `tests/LeafCalendar.Tests/SignInFlowTests.cs`

**Interfaces:**
- Consumes:
  - `Pkce` (Task 1) and `LoopbackListener`, `QueryString` (Task 2)
  - `GoogleOAuthClient`, `SignInException` (Task 4) and `ITokenStore`, `InMemoryTokenStore` (Task 5)
  - `AccessTokenProvider` (Task 6), `AppLog`, `TempFolder` (Task 7)
  - `LeafDatabase`, `AccountStore`, `Account`, `TestDatabase` (Task 8)
- Produces: `sealed class SignInFlow(GoogleOAuthClient oauth, ITokenStore tokenStore, AccessTokenProvider accessTokens, LeafDatabase database, Func<Uri, Task> openBrowser, TimeProvider time, AppLog log)`, with:
  - `static readonly TimeSpan Timeout` (5 minutes)
  - `Task<Account> RunAsync(string? loginHint, CancellationToken ct)`, which throws `SignInException` with a user-facing message.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/SignInFlowTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SignInFlowTests : IDisposable
{
    const string TokenUrl    = "https://oauth2.googleapis.com/token";
    const string RevokeUrl   = "https://oauth2.googleapis.com/revoke";
    const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    static readonly HttpClient Browser = new();

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly InMemoryTokenStore _store = new();
    readonly TestDatabase _db = new();
    readonly TempFolder _logs = new();

    public void Dispose()
    {
        _db.Dispose();
        _logs.Dispose();
    }

    SignInFlow CreateFlow(Func<Uri, Task> openBrowser)
    {
        var oauth = new GoogleOAuthClient(new HttpClient(_google), new("id.apps.googleusercontent.com", "GOCSPX-test"), _time);
        return new SignInFlow(oauth, _store, new AccessTokenProvider(oauth, _store, _time), _db.Database, openBrowser, _time, new AppLog(_logs.Path, _time));
    }

    // Acts like Google + the browser: reads the consent URL and hits the loopback redirect.
    static Func<Uri, Task> GoogleRedirects(Func<IReadOnlyDictionary<string, string>, string> replyQuery) => consentUrl =>
    {
        var query    = QueryString.Parse(consentUrl.Query);
        var redirect = new Uri(query["redirect_uri"]);
        _ = Task.Run(() => Browser.GetAsync(new Uri(redirect, "?" + replyQuery(query))));
        return Task.CompletedTask;
    };

    static string Approve(IReadOnlyDictionary<string, string> q) => $"code=4%2Fauth-code&state={Uri.EscapeDataString(q["state"])}";

    void GoogleAccepts(string tokenFixture = "token-response.json")
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read(tokenFixture));
        _google.On(HttpMethod.Get, UserInfoUrl, HttpStatusCode.OK, Fixture.Read("userinfo.json"));
        _google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.OK, "{}");
    }

    [Fact]
    public async Task RunAsync_UserApproves_SavesAccountAndRefreshToken()
    {
        GoogleAccepts();
        Uri? consentUrl = null;
        var redirect = GoogleRedirects(Approve);

        var account = await CreateFlow(url => { consentUrl = url; return redirect(url); }).RunAsync(null, TestContext.Current.CancellationToken);

        // Code exchanged with the matching PKCE verifier and redirect
        var consent  = QueryString.Parse(consentUrl!.Query);
        var exchange = _google.Requests.Single(r => r.Uri.AbsoluteUri == TokenUrl);
        Assert.Equal("4/auth-code", exchange.Form("code"));
        Assert.Equal(consent["code_challenge"], Pkce.CreateChallenge(exchange.Form("code_verifier")!));
        Assert.Equal(consent["redirect_uri"], exchange.Form("redirect_uri"));

        // Account saved
        Assert.Equal("109876543210", account.Id);
        Assert.Equal("leaf.tester@gmail.com", account.Email);
        Assert.Equal("1//test-refresh-token", _store.GetRefreshToken("109876543210"));
        using var conn = _db.Database.Open();
        Assert.Equal(account, AccountStore.GetAll(conn).Single());
    }

    [Fact]
    public async Task RunAsync_StateMismatch_FailsWithoutExchangingCode()
    {
        GoogleAccepts();

        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(_ => "code=4%2Fstolen&state=forged")).RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("didn't match", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_google.Requests, r => r.Uri.AbsoluteUri == TokenUrl);
    }

    [Fact]
    public async Task RunAsync_UserDenies_FailsAsCancelled()
    {
        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(q => $"error=access_denied&state={Uri.EscapeDataString(q["state"])}"))
                .RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("Sign-in was cancelled.", error.Message);
    }

    [Fact]
    public async Task RunAsync_CalendarScopeNotGranted_FailsAndSavesNothing()
    {
        GoogleAccepts("token-response-no-calendar.json");

        var error = await Assert.ThrowsAsync<SignInException>(
            () => CreateFlow(GoogleRedirects(Approve)).RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("calendar access", error.Message, StringComparison.Ordinal);
        Assert.Empty(_store.GetAccountIds());
        Assert.Contains(_google.Requests, r => r.Uri.AbsoluteUri == RevokeUrl);
        using var conn = _db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
    }

    [Fact]
    public async Task RunAsync_SameAccountTwice_KeepsOneAccountWithLatestToken()
    {
        var ct = TestContext.Current.CancellationToken;
        GoogleAccepts();
        await CreateFlow(GoogleRedirects(Approve)).RunAsync(null, ct);
        _store.SetRefreshToken("109876543210", "1//older");

        await CreateFlow(GoogleRedirects(Approve)).RunAsync(null, ct);

        using var conn = _db.Database.Open();
        Assert.Single(AccountStore.GetAll(conn));
        Assert.Equal("1//test-refresh-token", _store.GetRefreshToken("109876543210"));
    }

    [Fact]
    public async Task RunAsync_NoReplyWithinFiveMinutes_TimesOut()
    {
        var run = CreateFlow(_ => Task.CompletedTask).RunAsync(null, TestContext.Current.CancellationToken);

        _time.Advance(SignInFlow.Timeout);

        var error = await Assert.ThrowsAsync<SignInException>(() => run);
        Assert.Equal("Sign-in timed out. Try again.", error.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~SignInFlowTests"`
Expected: build FAILS, `SignInFlow` not found.

- [ ] **Step 3: Implement SignInFlow**

`src/LeafCalendar.Core/Auth/SignInFlow.cs`:
```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Signs a Google account in through the system browser.
/// </summary>
/// <remarks>
/// The steps are:
/// <list type="number">
/// <item>Start a loopback listener, create a PKCE verifier and state, and open Google's consent page.</item>
/// <item>Wait up to <see cref="Timeout"/> for the redirect, then check the state before trusting anything in it.</item>
/// <item>Exchange the code, and require calendar access plus a refresh token.</item>
/// <item>Read the user's ID and email, save the refresh token to <see cref="ITokenStore"/>, and upsert the account.</item>
/// </list>
/// Every failure throws <see cref="SignInException"/> with a message meant for the user.
/// </remarks>
public sealed class SignInFlow(
    GoogleOAuthClient oauth,
    ITokenStore tokenStore,
    AccessTokenProvider accessTokens,
    LeafDatabase database,
    Func<Uri, Task> openBrowser,
    TimeProvider time,
    AppLog log)
{
    /// <summary>How long Leaf waits for the browser to come back.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs sign-in and returns the saved account.</summary>
    /// <exception cref="SignInException">Sign-in failed, was cancelled, or timed out.</exception>
    public async Task<Account> RunAsync(string? loginHint, CancellationToken ct)
    {
        using var listener = new LoopbackListener();
        var verifier = Pkce.CreateVerifier();
        var state    = Pkce.CreateState();

        // Open Consent Page
        log.Info("signin.started");
        await openBrowser(oauth.BuildAuthorizationUrl(listener.RedirectUri, state, Pkce.CreateChallenge(verifier), loginHint));

        // Wait For Redirect
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        IReadOnlyDictionary<string, string> reply;
        try
        {
            reply = await listener.WaitForCallbackAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Fail("timeout", "Sign-in timed out. Try again.");
        }

        // Check Reply
        if (!Pkce.StateMatches(state, reply.GetValueOrDefault("state")))
        {
            throw Fail("state-mismatch", "The sign-in reply didn't match this request. Try again.");
        }

        if (reply.TryGetValue("error", out var error))
        {
            throw Fail("google-error", error == "access_denied" ? "Sign-in was cancelled." : "Google couldn't complete sign-in. Try again.");
        }

        if (!reply.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw Fail("missing-code", "Google didn't return a sign-in code. Try again.");
        }

        // Exchange Code
        var tokens = await oauth.ExchangeCodeAsync(code, verifier, listener.RedirectUri, ct);

        if (!tokens.HasScope(GoogleOAuthClient.CalendarScope))
        {
            await TryRevokeAsync(tokens, ct);
            throw Fail("calendar-scope-missing", "Leaf needs calendar access. Sign in again and allow calendar access.");
        }

        if (tokens.RefreshToken is null)
        {
            throw Fail("refresh-token-missing", "Google didn't allow offline access. Try again.");
        }

        // Save Account
        var user = await oauth.GetUserInfoAsync(tokens.AccessToken, ct);
        tokenStore.SetRefreshToken(user.Sub, tokens.RefreshToken);
        accessTokens.Seed(user.Sub, tokens);

        var account = new Account(user.Sub, user.Email, user.Name, user.Picture, AccountStatus.Ok);
        using (var conn = database.Open())
        {
            AccountStore.Upsert(conn, account);
        }

        log.Info("signin.completed", $"account={user.Sub}");
        return account;
    }

    async Task TryRevokeAsync(TokenSet tokens, CancellationToken ct)
    {
        try
        {
            await oauth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or GoogleApiException)
        {
            log.Error("signin.revoke-failed", ex);
        }
    }

    SignInException Fail(string reason, string message)
    {
        log.Info("signin.failed", $"reason={reason}");
        return new SignInException(message);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~SignInFlowTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Auth/SignInFlow.cs tests/LeafCalendar.Tests/SignInFlowTests.cs
git commit -m "feat(core): add browser sign-in flow with state, scope, and timeout checks"
```

---

### Task 11: Retry Handler

**Files:**
- Create: `src/LeafCalendar.Core/Google/GoogleRetryHandler.cs`
- Test: `tests/LeafCalendar.Tests/GoogleRetryHandlerTests.cs`

**Interfaces:**
- Consumes: `GoogleJson`, `GoogleJsonContext` (Task 3) and `FakeHttpHandler` (Task 3).
- Produces: `sealed class GoogleRetryHandler(TimeProvider time) : DelegatingHandler` with `const int MaxAttempts = 5`. It retries 429, 5xx, and 403 responses whose reason is `rateLimitExceeded` or `userRateLimitExceeded`. It honors `Retry-After` (capped at 60 s); otherwise it waits 1, 2, 4, 8 s (capped at 32 s) plus up to 1 s of random jitter.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/GoogleRetryHandlerTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class GoogleRetryHandlerTests
{
    const string Url = "https://www.googleapis.com/calendar/v3/users/me/calendarList";

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    HttpClient CreateClient() => new(new GoogleRetryHandler(_time) { InnerHandler = _google });

    // Drives fake time forward until the request finishes (bounded so a bug can't hang the run).
    async Task<HttpResponseMessage> SendWithTimeAsync(Task<HttpResponseMessage> send)
    {
        for (var i = 0; i < 100 && !send.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(40));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return await send;
    }

    [Fact]
    public async Task SendAsync_429Then503ThenOk_RetriesUntilSuccess()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.TooManyRequests, "{}", once: true);
        _google.On(HttpMethod.Get, Url, HttpStatusCode.ServiceUnavailable, "{}", once: true);
        _google.On(HttpMethod.Get, Url, HttpStatusCode.OK, """{"items":[]}""");

        using var response = await SendWithTimeAsync(CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, _google.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_RateLimit403_Retries()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.Forbidden, Fixture.Read("error-rate-limit.json"), once: true);
        _google.On(HttpMethod.Get, Url, HttpStatusCode.OK, """{"items":[]}""");

        using var response = await SendWithTimeAsync(CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_Forbidden403_ReturnsWithoutRetryAndBodyStillReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        _google.On(HttpMethod.Get, Url, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));

        using var response = await CreateClient().GetAsync(new Uri(Url), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(_google.Requests);
        Assert.Contains("forbidden", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_AlwaysFailing_StopsAtMaxAttempts()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.InternalServerError, "{}");

        using var response = await SendWithTimeAsync(CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(GoogleRetryHandler.MaxAttempts, _google.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_NotFound_DoesNotRetry()
    {
        _google.On(HttpMethod.Get, Url, HttpStatusCode.NotFound, "{}");

        using var response = await CreateClient().GetAsync(new Uri(Url), TestContext.Current.CancellationToken);

        Assert.Single(_google.Requests);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleRetryHandlerTests"`
Expected: build FAILS, `GoogleRetryHandler` not found.

- [ ] **Step 3: Implement GoogleRetryHandler**

`src/LeafCalendar.Core/Google/GoogleRetryHandler.cs`:
```csharp
using System.Net;
using System.Security.Cryptography;

namespace LeafCalendar.Core.Google;

/// <summary>
/// Retries Google calls that failed for temporary reasons, using exponential backoff with jitter.
/// </summary>
/// <remarks>
/// Retries 429, any 5xx, and 403 when Google's reason is <c>rateLimitExceeded</c> or
/// <c>userRateLimitExceeded</c>. Honors <c>Retry-After</c> up to 60 seconds. Other failures
/// return right away with their body still readable.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/errors"/>
public sealed class GoogleRetryHandler(TimeProvider time) : DelegatingHandler
{
    /// <summary>Total tries, including the first.</summary>
    public const int MaxAttempts = 5;

    static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (attempt == MaxAttempts || !await IsRetryableAsync(response, cancellationToken))
            {
                return response;
            }

            var delay = GetDelay(attempt, response);
            response.Dispose();
            await Task.Delay(delay, time, cancellationToken);
        }
    }

    static async Task<bool> IsRetryableAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || status is >= 500 and <= 599)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        // Buffer So The Caller Can Still Read The Body
        await response.Content.LoadIntoBufferAsync(ct);
        var error = GoogleJson.TryParse(await response.Content.ReadAsStringAsync(ct), GoogleJsonContext.Default.ApiErrorEnvelope);

        return error?.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") == true;
    }

    static TimeSpan GetDelay(int attempt, HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter;
        }

        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 32));
        return backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 1000));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleRetryHandlerTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Google/GoogleRetryHandler.cs tests/LeafCalendar.Tests/GoogleRetryHandlerTests.cs
git commit -m "feat(core): retry rate-limited and failed Google calls with backoff"
```

---

### Task 12: Google Calendar Client

**Files:**
- Create: `src/LeafCalendar.Core/Google/GoogleCalendarClient.cs`
- Test: `tests/LeafCalendar.Tests/GoogleCalendarClientTests.cs`

**Interfaces:**
- Consumes: the Task 3 JSON types, `AccessTokenProvider` (Task 6), `InMemoryTokenStore` (Task 5), and `FakeHttpHandler` (Task 3).
- Produces: `sealed class GoogleCalendarClient(HttpClient http, AccessTokenProvider tokens)`:
  - `Task<IReadOnlyList<CalendarListEntry>> ListCalendarsAsync(string accountId, CancellationToken ct)` follows every page.
  - `Task<EventsPage> ListEventsAsync(string accountId, string calendarId, string? syncToken, string? pageToken, CancellationToken ct)` sends `maxResults=2500&showDeleted=true&singleEvents=false`.
    - Throws `SyncTokenExpiredException` on 410.
    - Throws `GoogleApiException` on other failures.
    - Retries once after `Forget` on a 401.

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/GoogleCalendarClientTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class GoogleCalendarClientTests
{
    const string Account     = "109876543210";
    const string TokenUrl    = "https://oauth2.googleapis.com/token";
    const string ListUrl     = "https://www.googleapis.com/calendar/v3/users/me/calendarList";
    const string EventsUrl   = "https://www.googleapis.com/calendar/v3/calendars/leaf.tester%40gmail.com/events";

    readonly FakeHttpHandler _google = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    GoogleCalendarClient CreateClient()
    {
        var store = new InMemoryTokenStore();
        store.SetRefreshToken(Account, "1//test-refresh-token");
        var http  = new HttpClient(_google);
        var oauth = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "secret"), _time);
        return new GoogleCalendarClient(http, new AccessTokenProvider(oauth, store, _time));
    }

    [Fact]
    public async Task ListCalendarsAsync_TwoPages_ReturnsAll()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(r => r.Uri.AbsoluteUri.StartsWith(ListUrl, StringComparison.Ordinal) && r.Query("pageToken") is null,
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"a","summary":"A","accessRole":"owner"}],"nextPageToken":"p2"}"""));
        _google.On(r => r.Uri.AbsoluteUri.StartsWith(ListUrl, StringComparison.Ordinal) && r.Query("pageToken") == "p2",
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"b","summary":"B","accessRole":"reader"}]}"""));

        var calendars = await CreateClient().ListCalendarsAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], calendars.Select(c => c.Id));
        Assert.All(_google.Requests.Where(r => r.Method == HttpMethod.Get), r => Assert.Equal("ya29.test-refreshed-token", r.BearerToken));
    }

    [Fact]
    public async Task ListEventsAsync_WithSyncToken_SendsExpectedQuery()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.OK, Fixture.Read("events-incremental.json"));

        var page = await CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", "sync-token-1", "pg", TestContext.Current.CancellationToken);

        var request = _google.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal("sync-token-1", request.Query("syncToken"));
        Assert.Equal("pg", request.Query("pageToken"));
        Assert.Equal("true", request.Query("showDeleted"));
        Assert.Equal("false", request.Query("singleEvents"));
        Assert.Equal("2500", request.Query("maxResults"));
        Assert.Equal("sync-token-2", page.NextSyncToken);
        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task ListEventsAsync_Gone_ThrowsSyncTokenExpired()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.Gone, Fixture.Read("error-410.json"));

        await Assert.ThrowsAsync<SyncTokenExpiredException>(
            () => CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", "old", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListEventsAsync_Forbidden_ThrowsGoogleApiExceptionWithReason()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));

        var error = await Assert.ThrowsAsync<GoogleApiException>(
            () => CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", null, null, TestContext.Current.CancellationToken));

        Assert.Equal("forbidden", error.Reason);
    }

    [Fact]
    public async Task ListEventsAsync_Unauthorized_RefreshesAndRetriesOnce()
    {
        _google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.Unauthorized, "{}", once: true);
        _google.On(HttpMethod.Get, EventsUrl, HttpStatusCode.OK, Fixture.Read("events-empty.json"));

        var page = await CreateClient().ListEventsAsync(Account, "leaf.tester@gmail.com", null, null, TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-empty", page.NextSyncToken);
        Assert.Equal(2, _google.Requests.Count(r => r.Uri.AbsoluteUri == TokenUrl));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleCalendarClientTests"`
Expected: build FAILS, `GoogleCalendarClient` not found.

- [ ] **Step 3: Implement GoogleCalendarClient**

`src/LeafCalendar.Core/Google/GoogleCalendarClient.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Core.Google;

/// <summary>
/// Google Calendar REST API v3 calls Leaf needs for sync.
/// </summary>
/// <remarks>
/// Events are listed with <c>singleEvents=false</c> so repeating events arrive as one master plus
/// exceptions, and with <c>showDeleted=true</c> so deletions arrive during incremental sync. A 401
/// drops the cached access token and retries once; a 410 means the sync token expired.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/events/list"/>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/sync"/>
public sealed class GoogleCalendarClient(HttpClient http, AccessTokenProvider tokens)
{
    static readonly Uri BaseUri = new("https://www.googleapis.com/calendar/v3/");

    /// <summary>Returns every calendar in the account's list (all pages).</summary>
    public async Task<IReadOnlyList<CalendarListEntry>> ListCalendarsAsync(string accountId, CancellationToken ct)
    {
        var calendars = new List<CalendarListEntry>();
        string? pageToken = null;

        do
        {
            var path = "users/me/calendarList?maxResults=250&showHidden=true";
            if (pageToken is not null)
            {
                path += "&pageToken=" + Uri.EscapeDataString(pageToken);
            }

            var page = await GetAsync(accountId, path, GoogleJsonContext.Default.CalendarListPage, ct);
            calendars.AddRange(page.Items);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return calendars;
    }

    /// <summary>Returns one page of events (full sync when <paramref name="syncToken"/> is null).</summary>
    /// <exception cref="SyncTokenExpiredException">Google needs a full sync.</exception>
    public Task<EventsPage> ListEventsAsync(string accountId, string calendarId, string? syncToken, string? pageToken, CancellationToken ct)
    {
        var path = new StringBuilder($"calendars/{Uri.EscapeDataString(calendarId)}/events?maxResults=2500&showDeleted=true&singleEvents=false");

        if (syncToken is not null)
        {
            path.Append("&syncToken=").Append(Uri.EscapeDataString(syncToken));
        }

        if (pageToken is not null)
        {
            path.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        return GetAsync(accountId, path.ToString(), GoogleJsonContext.Default.EventsPage, ct);
    }

    async Task<T> GetAsync<T>(string accountId, string relativePath, JsonTypeInfo<T> info, CancellationToken ct)
    {
        var uri = new Uri(BaseUri, relativePath);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(accountId, ct));

            using var response = await http.SendAsync(request, ct);

            // Expired Access Token: Refresh Once
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                tokens.Forget(accountId);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Gone)
            {
                throw new SyncTokenExpiredException();
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await GoogleJson.ToExceptionAsync(response, ct);
            }

            return await response.Content.ReadFromJsonAsync(info, ct)
                ?? throw new InvalidDataException("Google returned an empty response.");
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~GoogleCalendarClientTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Google/GoogleCalendarClient.cs tests/LeafCalendar.Tests/GoogleCalendarClientTests.cs
git commit -m "feat(core): add Google Calendar list and events client"
```

---

### Task 13: Sync Engine

**Files:**
- Create: `src/LeafCalendar.Core/Sync/SyncEngine.cs`, `tests/LeafCalendar.Tests/Support/SyncHarness.cs`
- Test: `tests/LeafCalendar.Tests/SyncEngineTests.cs`

**Interfaces:**
- Consumes: `GoogleCalendarClient` (Task 12), `AccountStore`, `CalendarStore` (Task 8), `EventStore` (Task 9), `AppLog` (Task 7), `AccountNeedsSignInException` (Task 4), and `SyncTokenExpiredException`, `GoogleApiException` (Task 3).
- Produces: `sealed class SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log)`:
  - `Task SyncAllAsync(CancellationToken ct)` covers accounts whose status is `Ok`.
  - `Task SyncAccountAsync(string accountId, CancellationToken ct)` never throws for Google/network failures. A failing calendar is logged and skipped; a failing refresh marks the account `NeedsSignIn`.
- Test support: `SyncHarness : IDisposable` with these members:
  - Properties: `FakeHttpHandler Google`, `InMemoryTokenStore Tokens`, `TestDatabase Db`, `AppLog Log`, `string LogPath`, `SyncEngine Engine`, `FakeTimeProvider Time`
  - `const string AccountId`, `PrimaryEventsUrl`, `FamilyEventsUrl`
  - `void RouteStandardGoogle()`
  - `SyncEngine NewEngine()`, which builds a fresh token cache

- [ ] **Step 1: Write the harness and failing tests**

`tests/LeafCalendar.Tests/Support/SyncHarness.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Sync;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests.Support;

/// <summary>A sync engine wired to fake Google, a temp database, and a temp log.</summary>
public sealed class SyncHarness : IDisposable
{
    /// <summary>Fixture account ID (userinfo.json <c>sub</c>).</summary>
    public const string AccountId = "109876543210";

    /// <summary>Token endpoint.</summary>
    public const string TokenUrl = "https://oauth2.googleapis.com/token";

    /// <summary>Calendar list endpoint.</summary>
    public const string ListUrl = "https://www.googleapis.com/calendar/v3/users/me/calendarList";

    /// <summary>Primary calendar events endpoint.</summary>
    public const string PrimaryEventsUrl = "https://www.googleapis.com/calendar/v3/calendars/leaf.tester%40gmail.com/events";

    /// <summary>Family calendar events endpoint.</summary>
    public const string FamilyEventsUrl = "https://www.googleapis.com/calendar/v3/calendars/family123%40group.calendar.google.com/events";

    readonly TempFolder _logs = new();

    /// <summary>Creates the harness with one signed-in account.</summary>
    public SyncHarness()
    {
        Log = new AppLog(_logs.Path, Time);
        Tokens.SetRefreshToken(AccountId, "1//test-refresh-token");
        using (var conn = Db.Database.Open())
        {
            AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        }

        Engine = NewEngine();
    }

    /// <summary>Fake Google.</summary>
    public FakeHttpHandler Google { get; } = new();

    /// <summary>Fake clock.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Secret store.</summary>
    public InMemoryTokenStore Tokens { get; } = new();

    /// <summary>Database.</summary>
    public TestDatabase Db { get; } = new();

    /// <summary>Log.</summary>
    public AppLog Log { get; }

    /// <summary>Log file path.</summary>
    public string LogPath => Log.FilePath;

    /// <summary>Engine under test.</summary>
    public SyncEngine Engine { get; private set; }

    /// <summary>Builds a new engine with an empty access-token cache.</summary>
    public SyncEngine NewEngine()
    {
        var http  = new HttpClient(Google);
        var oauth = new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "GOCSPX-test"), Time);
        Engine = new SyncEngine(new GoogleCalendarClient(http, new AccessTokenProvider(oauth, Tokens, Time)), Db.Database, Log);
        return Engine;
    }

    /// <summary>
    /// Token refresh OK. Calendar list has primary and family. The primary full sync is two pages
    /// (sync-token-1), the primary incremental from sync-token-1 gives sync-token-2, and family is empty.
    /// </summary>
    public void RouteStandardGoogle()
    {
        Google.On(HttpMethod.Post, TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        Google.On(HttpMethod.Get, ListUrl, HttpStatusCode.OK, Fixture.Read("calendar-list.json"));
        RouteEvents(PrimaryEventsUrl, syncToken: null, pageToken: null, "events-page1.json");
        RouteEvents(PrimaryEventsUrl, syncToken: null, pageToken: "page-2", "events-page2.json");
        RouteEvents(PrimaryEventsUrl, syncToken: "sync-token-1", pageToken: null, "events-incremental.json");
        Google.On(HttpMethod.Get, FamilyEventsUrl, HttpStatusCode.OK, Fixture.Read("events-empty.json"));
    }

    /// <summary>Routes an events URL for an exact sync/page token pair.</summary>
    public void RouteEvents(string url, string? syncToken, string? pageToken, string fixture, HttpStatusCode status = HttpStatusCode.OK, bool once = false) =>
        Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(url, StringComparison.Ordinal) && r.Query("syncToken") == syncToken && r.Query("pageToken") == pageToken,
            _ => FakeHttpHandler.Json(status, Fixture.Read(fixture)),
            once);

    /// <inheritdoc />
    public void Dispose()
    {
        Db.Dispose();
        _logs.Dispose();
    }
}
```

`tests/LeafCalendar.Tests/SyncEngineTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class SyncEngineTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Family  = "family123@group.calendar.google.com";
    const string Account = SyncHarness.AccountId;

    readonly SyncHarness _h = new();

    public void Dispose() => _h.Dispose();

    CalendarInfo Calendar(string id)
    {
        using var conn = _h.Db.Database.Open();
        return CalendarStore.GetForAccount(conn, Account).Single(c => c.Id == id);
    }

    int CountEvents(string calendarId)
    {
        using var conn = _h.Db.Database.Open();
        return EventStore.Count(conn, Account, calendarId);
    }

    StoredEvent? Get(string id)
    {
        using var conn = _h.Db.Database.Open();
        return EventStore.Get(conn, Account, Primary, id);
    }

    [Fact]
    public async Task SyncAccountAsync_FirstRun_StoresCalendarsEventsAndToken()
    {
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAccountAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Equal("sync-token-empty", Calendar(Family).SyncToken);
        Assert.Equal(4, CountEvents(Primary));
        Assert.Null(Get("evt-deleted"));
        Assert.Contains(_h.Google.Requests, r => r.Query("pageToken") == "page-2");
    }

    [Fact]
    public async Task SyncAccountAsync_SecondRun_AppliesIncrementalChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.Equal("sync-token-2", Calendar(Primary).SyncToken);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), Get("evt-single")!.Start);
        Assert.Null(Get("evt-allday"));
        Assert.NotNull(Get("evt-new"));
        Assert.Equal(4, CountEvents(Primary));
    }

    [Fact]
    public async Task SyncAccountAsync_SyncTokenExpired_DoesFullResyncReplacingStaleRows()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.RouteEvents(SyncHarness.PrimaryEventsUrl, "sync-token-1", null, "error-410.json", HttpStatusCode.Gone);
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        using (var conn = _h.Db.Database.Open())
        {
            using var doc = System.Text.Json.JsonDocument.Parse("""{"id":"evt-stale","status":"confirmed","start":{"date":"2026-01-01"},"end":{"date":"2026-01-02"}}""");
            EventStore.Apply(conn, null, Account, Primary, doc.RootElement);
        }

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.Null(Get("evt-stale"));
        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Equal(4, CountEvents(Primary));
    }

    [Fact]
    public async Task SyncAccountAsync_FailureMidPagination_KeepsPreviousDataAndToken()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(
            r => r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl, StringComparison.Ordinal) && r.Query("syncToken") == "sync-token-1" && r.Query("pageToken") is null,
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"evt-single","status":"cancelled"}],"nextPageToken":"inc-2"}"""));
        _h.Google.On(
            r => r.Query("pageToken") == "inc-2",
            _ => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        Assert.NotNull(Get("evt-single"));
        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Contains("sync.calendar.failed", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAccountAsync_OneCalendarForbidden_OthersStillSync()
    {
        _h.Google.On(HttpMethod.Get, SyncHarness.FamilyEventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.RouteStandardGoogle();

        await _h.Engine.SyncAccountAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal("sync-token-1", Calendar(Primary).SyncToken);
        Assert.Null(Calendar(Family).SyncToken);
    }

    [Fact]
    public async Task SyncAccountAsync_RefreshTokenRevoked_MarksNeedsSignInAndKeepsEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        // Revoked token is refused; routes are first-match, so this goes in before the standard routes
        _h.Google.On(r => r.Form("refresh_token") == "1//revoked", _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, Fixture.Read("error-invalid-grant.json")));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);
        var eventsBefore = CountEvents(Primary);

        _h.Tokens.SetRefreshToken(Account, "1//revoked");
        await _h.NewEngine().SyncAccountAsync(Account, ct);

        using var conn = _h.Db.Database.Open();
        Assert.Equal(AccountStatus.NeedsSignIn, AccountStore.GetAll(conn).Single().Status);
        Assert.Equal(eventsBefore, CountEvents(Primary));
    }

    [Fact]
    public async Task SyncAllAsync_AccountNeedsSignIn_IsSkipped()
    {
        _h.RouteStandardGoogle();
        using (var conn = _h.Db.Database.Open())
        {
            AccountStore.SetStatus(conn, Account, AccountStatus.NeedsSignIn);
        }

        await _h.Engine.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_h.Google.Requests);
    }

    [Fact]
    public async Task SyncAccountAsync_CalendarRemovedFromList_DeletesItsEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(HttpMethod.Get, SyncHarness.ListUrl, HttpStatusCode.OK, Fixture.Read("calendar-list.json"), once: true);
        _h.Google.On(HttpMethod.Get, SyncHarness.ListUrl, HttpStatusCode.OK, Fixture.Read("calendar-list-primary-only.json"));
        _h.RouteStandardGoogle();
        await _h.Engine.SyncAccountAsync(Account, ct);

        await _h.Engine.SyncAccountAsync(Account, ct);

        using var conn = _h.Db.Database.Open();
        Assert.Single(CalendarStore.GetForAccount(conn, Account));
        Assert.Equal(0, EventStore.Count(conn, Account, Family));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~SyncEngineTests"`
Expected: build FAILS, `SyncEngine` not found.

- [ ] **Step 3: Implement SyncEngine**

`src/LeafCalendar.Core/Sync/SyncEngine.cs`:
```csharp
using System.Text.Json;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Sync;

/// <summary>
/// Pulls Google calendars and events into the local database.
/// </summary>
/// <remarks>
/// For each account, the engine makes the calendar list match Google first. Then each calendar is
/// pulled: incrementally from its sync token, or in full when there is no token or Google answers 410.
/// Every page of a pull is fetched before anything is written. The writes and the new sync token are
/// then applied in one transaction, so a failure part-way leaves the old data and token untouched.
/// A failing calendar is logged and skipped. A rejected refresh token marks the account
/// <see cref="AccountStatus.NeedsSignIn"/> and keeps its data.
/// </remarks>
public sealed class SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log)
{
    /// <summary>Syncs every account that can sync.</summary>
    public async Task SyncAllAsync(CancellationToken ct)
    {
        IReadOnlyList<Account> accounts;
        using (var conn = database.Open())
        {
            accounts = AccountStore.GetAll(conn);
        }

        foreach (var account in accounts.Where(a => a.Status == AccountStatus.Ok))
        {
            await SyncAccountAsync(account.Id, ct);
        }
    }

    /// <summary>Syncs one account's calendar list and every calendar's events.</summary>
    public async Task SyncAccountAsync(string accountId, CancellationToken ct)
    {
        try
        {
            // Calendar List
            var entries = await google.ListCalendarsAsync(accountId, ct);

            IReadOnlyList<CalendarInfo> calendars;
            using (var conn = database.Open())
            {
                CalendarStore.ReplaceForAccount(conn, accountId, entries);
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

    async Task SyncCalendarAsync(CalendarInfo calendar, CancellationToken ct)
    {
        try
        {
            try
            {
                await PullAsync(calendar, calendar.SyncToken, ct);
            }
            catch (SyncTokenExpiredException)
            {
                log.Info("sync.calendar.full-resync", $"account={calendar.AccountId}");
                await PullAsync(calendar, null, ct);
            }
        }
        catch (Exception ex) when (IsSyncFailure(ex, ct))
        {
            log.Error("sync.calendar.failed", ex);
        }
    }

    async Task PullAsync(CalendarInfo calendar, string? syncToken, CancellationToken ct)
    {
        // Fetch Every Page First
        var items = new List<JsonElement>();
        string? pageToken = null;
        EventsPage page;

        do
        {
            page = await google.ListEventsAsync(calendar.AccountId, calendar.Id, syncToken, pageToken, ct);
            items.AddRange(page.Items);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        // Apply Atomically
        using var conn = database.Open();
        using var tx   = conn.BeginTransaction();

        if (syncToken is null)
        {
            EventStore.DeleteAllForCalendar(conn, tx, calendar.AccountId, calendar.Id);
        }

        foreach (var item in items)
        {
            EventStore.Apply(conn, tx, calendar.AccountId, calendar.Id, item);
        }

        CalendarStore.SetSyncToken(conn, tx, calendar.AccountId, calendar.Id, page.NextSyncToken);
        tx.Commit();

        log.Info("sync.calendar.done", $"account={calendar.AccountId} changes={items.Count} full={syncToken is null}");
    }

    static bool IsSyncFailure(Exception ex, CancellationToken ct) =>
        ex is GoogleApiException or HttpRequestException or JsonException or InvalidDataException ||
        (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~SyncEngineTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Sync tests/LeafCalendar.Tests
git commit -m "feat(core): add full and incremental sync engine"
```

---

### Task 14: Sync Loop

**Files:**
- Create: `src/LeafCalendar.Core/Sync/SyncLoop.cs`
- Test: `tests/LeafCalendar.Tests/SyncLoopTests.cs`

**Interfaces:**
- Consumes: `AppLog` (Task 7), `TempFolder` (Task 7).
- Produces:
  - `enum SyncMode { Visible, Tray }`
  - `sealed class SyncLoop(Func<CancellationToken, Task> syncAll, TimeProvider time, AppLog log) : IAsyncDisposable`, with these members:
    - `static TimeSpan IntervalFor(SyncMode mode)`: 15 s when visible, 60 s in the tray
    - `SyncMode Mode { get; set; }` (defaults to `Tray`)
    - `void Start()`
    - `void TriggerNow()`
    - `ValueTask DisposeAsync()`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/SyncLoopTests.cs`:
```csharp
using System.Threading.Channels;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SyncLoopTests : IDisposable
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly Channel<int> _runs = Channel.CreateUnbounded<int>();
    readonly TempFolder _logs = new();
    int _count;

    public void Dispose() => _logs.Dispose();

    SyncLoop CreateLoop(Func<Task>? body = null) => new(
        async _ =>
        {
            if (body is not null)
            {
                await body();
            }

            _runs.Writer.TryWrite(Interlocked.Increment(ref _count));
        },
        _time,
        new AppLog(_logs.Path, _time));

    async Task<int> NextRunAsync() => await _runs.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

    // Negative check: gives the loop a moment to (wrongly) run. ponytail: timing-based; raise the delay if CI is slow.
    async Task AssertNoRunAsync()
    {
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(_runs.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(SyncMode.Visible, 15)]
    [InlineData(SyncMode.Tray, 60)]
    public void IntervalFor_Mode_ReturnsSpecCadence(SyncMode mode, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncLoop.IntervalFor(mode));
    }

    [Fact]
    public async Task Start_VisibleMode_SyncsNowThenEvery15Seconds()
    {
        await using var loop = CreateLoop();
        loop.Mode = SyncMode.Visible;

        loop.Start();
        Assert.Equal(1, await NextRunAsync());

        _time.Advance(TimeSpan.FromSeconds(14));
        await AssertNoRunAsync();

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await NextRunAsync());
    }

    [Fact]
    public async Task TriggerNow_WhileWaiting_SyncsImmediately()
    {
        await using var loop = CreateLoop();
        loop.Start();
        await NextRunAsync();

        loop.TriggerNow();

        Assert.Equal(2, await NextRunAsync());
    }

    [Fact]
    public async Task Start_SyncThrows_KeepsLooping()
    {
        var first = true;
        await using var loop = CreateLoop(() =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        });

        loop.Start();
        _time.Advance(SyncLoop.IntervalFor(SyncMode.Tray));

        Assert.Equal(1, await NextRunAsync());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~SyncLoopTests"`
Expected: build FAILS, `SyncLoop` not found.

- [ ] **Step 3: Implement SyncLoop**

`src/LeafCalendar.Core/Sync/SyncLoop.cs`:
```csharp
using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Sync;

/// <summary>How often Leaf polls Google.</summary>
public enum SyncMode
{
    /// <summary>A Leaf window or the flyout is on screen.</summary>
    Visible,

    /// <summary>Leaf is only in the tray.</summary>
    Tray,
}

/// <summary>
/// Smart polling. Runs one sync right away, then again after each interval (15 s visible, 60 s tray).
/// </summary>
/// <remarks>
/// <see cref="TriggerNow"/> cuts the wait short. The app calls it on window or flyout open, resume
/// from sleep, and network reconnect. A failed sync is logged and the loop keeps going. Google push
/// needs a public HTTPS server, which Leaf doesn't have; a future push relay would only call
/// <see cref="TriggerNow"/>.
/// </remarks>
public sealed class SyncLoop(Func<CancellationToken, Task> syncAll, TimeProvider time, AppLog log) : IAsyncDisposable
{
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    /// <summary>Current cadence. Defaults to <see cref="SyncMode.Tray"/>.</summary>
    public SyncMode Mode { get; set; } = SyncMode.Tray;

    /// <summary>Polling interval for a mode.</summary>
    public static TimeSpan IntervalFor(SyncMode mode) =>
        mode == SyncMode.Visible ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60);

    /// <summary>Starts the loop (no-op if already started).</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Wakes the loop to sync now.</summary>
    public void TriggerNow()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already signaled.
        }
    }

    /// <summary>Stops the loop and waits for the current sync to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Sync
            try
            {
                await syncAll(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error("sync.loop.failed", ex);
            }

            // Wait For Interval Or Trigger
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(IntervalFor(Mode), time, waitCts.Token);
            var wake  = _wake.WaitAsync(waitCts.Token);

            await Task.WhenAny(delay, wake);
            await waitCts.CancelAsync();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~SyncLoopTests"`
Expected: PASS, 5 tests. Run it 3 times in a row. If `AssertNoRunAsync` ever fails, report it as flaky rather than raising the delay silently.

- [ ] **Step 5: Commit**

```bash
git add src/LeafCalendar.Core/Sync/SyncLoop.cs tests/LeafCalendar.Tests/SyncLoopTests.cs
git commit -m "feat(core): add smart polling sync loop"
```

---

### Task 15: Hosting (Launch Options, Paths, Google Services)

**Files:**
- Create: `src/LeafCalendar.Core/Hosting/LaunchOptions.cs`, `LeafPaths.cs`, `GoogleServices.cs`
- Test: `tests/LeafCalendar.Tests/LaunchOptionsTests.cs`, `tests/LeafCalendar.Tests/GoogleServicesTests.cs`

**Interfaces:**
- Consumes: everything in Tasks 4 to 14.
- Produces:
  - `sealed record LaunchOptions(string Profile, bool TrayProbe)` with `static LaunchOptions Parse(IReadOnlyList<string> args)`. `--profile <name>` must match `[A-Za-z0-9_-]{1,64}`; anything else falls back to `default`. `--tray-probe` sets `TrayProbe`.
  - `sealed record LeafPaths(string Root, string Profile)` with `ProfileDirectory`, `DatabasePath`, and `LogDirectory`.
  - `sealed class GoogleServices : IAsyncDisposable`, constructed with `(HttpClient http, OAuthClientCredentials credentials, ITokenStore tokenStore, LeafDatabase database, AppLog log, TimeProvider time)`:
    - Properties: `OAuth`, `AccessTokens`, `Calendar`, `Sync`, and `SyncLoop Loop`
    - `SignInFlow CreateSignIn(Func<Uri, Task> openBrowser)`
    - `Task DisconnectAsync(string accountId, CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/LaunchOptionsTests.cs`:
```csharp
using LeafCalendar.Core.Hosting;

namespace LeafCalendar.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void Parse_NoArgs_UsesDefaults()
    {
        Assert.Equal(new LaunchOptions("default", false), LaunchOptions.Parse([]));
    }

    [Fact]
    public void Parse_ProfileAndProbe_ReadsBoth()
    {
        Assert.Equal(new LaunchOptions("uitest-abc_1", true), LaunchOptions.Parse(["--profile", "uitest-abc_1", "--tray-probe"]));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("C:")]
    public void Parse_UnsafeProfile_FallsBackToDefault(string profile)
    {
        Assert.Equal("default", LaunchOptions.Parse(["--profile", profile]).Profile);
    }

    [Fact]
    public void LeafPaths_Profile_BuildsFoldersUnderRoot()
    {
        var paths = new LeafPaths(@"C:\Local", "work");

        Assert.Equal(@"C:\Local\profiles\work\leaf.db", paths.DatabasePath);
        Assert.Equal(@"C:\Local\profiles\work\Logs", paths.LogDirectory);
    }
}
```

`tests/LeafCalendar.Tests/GoogleServicesTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class GoogleServicesTests : IDisposable
{
    const string RevokeUrl = "https://oauth2.googleapis.com/revoke";

    readonly SyncHarness _h = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _h.Dispose();

    GoogleServices CreateServices() =>
        new(new HttpClient(_h.Google), new("id.apps.googleusercontent.com", "GOCSPX-test"), _h.Tokens, _h.Db.Database, _h.Log, _time);

    [Fact]
    public async Task DisconnectAsync_SignedInAccount_RevokesAndDeletesEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        _h.Google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.OK, "{}");
        _h.RouteStandardGoogle();
        await using var services = CreateServices();
        await services.Sync.SyncAccountAsync(SyncHarness.AccountId, ct);

        await services.DisconnectAsync(SyncHarness.AccountId, ct);

        Assert.Equal("1//test-refresh-token", _h.Google.Requests.Single(r => r.Uri.AbsoluteUri == RevokeUrl).Form("token"));
        Assert.Null(_h.Tokens.GetRefreshToken(SyncHarness.AccountId));
        using var conn = _h.Db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
        Assert.Empty(CalendarStore.GetForAccount(conn, SyncHarness.AccountId));
    }

    [Fact]
    public async Task DisconnectAsync_RevokeFails_StillRemovesLocally()
    {
        _h.Google.On(HttpMethod.Post, RevokeUrl, HttpStatusCode.ServiceUnavailable, "{}");
        await using var services = CreateServices();

        await services.DisconnectAsync(SyncHarness.AccountId, TestContext.Current.CancellationToken);

        Assert.Null(_h.Tokens.GetRefreshToken(SyncHarness.AccountId));
        using var conn = _h.Db.Database.Open();
        Assert.Empty(AccountStore.GetAll(conn));
        Assert.Contains("account.revoke.failed", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~LaunchOptionsTests|FullyQualifiedName~GoogleServicesTests"`
Expected: build FAILS, `LaunchOptions` not found.

- [ ] **Step 3: Implement the hosting types**

`src/LeafCalendar.Core/Hosting/LaunchOptions.cs`:
```csharp
namespace LeafCalendar.Core.Hosting;

/// <summary>
/// Command-line options.
/// </summary>
/// <remarks>
/// Recognized options:
/// <list type="bullet">
/// <item><c>--profile &lt;name&gt;</c> isolates data (UI tests use throwaway profiles). The name becomes a folder
/// and a Credential Locker prefix, so only <c>[A-Za-z0-9_-]{1,64}</c> is accepted; anything else falls back to
/// <c>default</c>.</item>
/// <item><c>--tray-probe</c> closes the window after it renders and trims memory, for the memory budget test.</item>
/// </list>
/// </remarks>
public sealed record LaunchOptions(string Profile, bool TrayProbe)
{
    /// <summary>Parses arguments (without the executable path).</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var profile   = "default";
        var trayProbe = false;

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
            }
        }

        return new LaunchOptions(IsSafeProfile(profile) ? profile : "default", trayProbe);
    }

    static bool IsSafeProfile(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
```

`src/LeafCalendar.Core/Hosting/LeafPaths.cs`:
```csharp
namespace LeafCalendar.Core.Hosting;

/// <summary>Per-profile folders under the app's local data root.</summary>
public sealed record LeafPaths(string Root, string Profile)
{
    /// <summary><c>{Root}\profiles\{Profile}</c>.</summary>
    public string ProfileDirectory => Path.Combine(Root, "profiles", Profile);

    /// <summary>SQLite database file.</summary>
    public string DatabasePath => Path.Combine(ProfileDirectory, "leaf.db");

    /// <summary>Log folder.</summary>
    public string LogDirectory => Path.Combine(ProfileDirectory, "Logs");
}
```

`src/LeafCalendar.Core/Hosting/GoogleServices.cs`:
```csharp
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Sync;

namespace LeafCalendar.Core.Hosting;

/// <summary>
/// Everything that talks to Google, wired for one OAuth client.
/// </summary>
/// <remarks>
/// It lives in Core, with no UI, so a future background helper can reuse it unchanged.
/// When the user changes the OAuth client, the app disposes this object and builds a new one.
/// </remarks>
public sealed class GoogleServices : IAsyncDisposable
{
    readonly ITokenStore _tokenStore;
    readonly LeafDatabase _database;
    readonly AppLog _log;
    readonly TimeProvider _time;

    /// <summary>Wires the Google services.</summary>
    public GoogleServices(HttpClient http, OAuthClientCredentials credentials, ITokenStore tokenStore, LeafDatabase database, AppLog log, TimeProvider time)
    {
        _tokenStore = tokenStore;
        _database   = database;
        _log        = log;
        _time       = time;

        OAuth        = new GoogleOAuthClient(http, credentials, time);
        AccessTokens = new AccessTokenProvider(OAuth, tokenStore, time);
        Calendar     = new GoogleCalendarClient(http, AccessTokens);
        Sync         = new SyncEngine(Calendar, database, log);
        Loop         = new SyncLoop(Sync.SyncAllAsync, time, log);
    }

    /// <summary>OAuth calls.</summary>
    public GoogleOAuthClient OAuth { get; }

    /// <summary>Access tokens per account.</summary>
    public AccessTokenProvider AccessTokens { get; }

    /// <summary>Calendar REST client.</summary>
    public GoogleCalendarClient Calendar { get; }

    /// <summary>Sync engine.</summary>
    public SyncEngine Sync { get; }

    /// <summary>Polling loop (not started until <see cref="SyncLoop.Start"/>).</summary>
    public SyncLoop Loop { get; }

    /// <summary>Creates a sign-in flow that opens pages with <paramref name="openBrowser"/>.</summary>
    public SignInFlow CreateSignIn(Func<Uri, Task> openBrowser) =>
        new(OAuth, _tokenStore, AccessTokens, _database, openBrowser, _time, _log);

    /// <summary>
    /// Disconnects an account. Leaf asks Google to revoke the token (best effort), then deletes the
    /// token and the account's local data. Google Calendar itself is not changed.
    /// </summary>
    public async Task DisconnectAsync(string accountId, CancellationToken ct)
    {
        // Revoke (Best Effort)
        if (_tokenStore.GetRefreshToken(accountId) is { } refreshToken)
        {
            try
            {
                await OAuth.RevokeAsync(refreshToken, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or GoogleApiException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _log.Error("account.revoke.failed", ex);
            }
        }

        // Remove Locally
        _tokenStore.RemoveRefreshToken(accountId);
        AccessTokens.Forget(accountId);

        using var conn = _database.Open();
        AccountStore.Delete(conn, accountId);
        _log.Info("account.disconnected", $"account={accountId}");
    }

    /// <summary>Stops the polling loop.</summary>
    public ValueTask DisposeAsync() => Loop.DisposeAsync();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~LaunchOptionsTests|FullyQualifiedName~GoogleServicesTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Run the whole logic suite**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: PASS, all tests (about 107).

- [ ] **Step 6: Commit**

```bash
git add src/LeafCalendar.Core/Hosting tests/LeafCalendar.Tests
git commit -m "feat(core): add launch options, profile paths, and Google service wiring"
```

---

### Task 16: App Shell (Packaged WinUI 3, Setup and Accounts Pages) With UI Tests

**Files:**
- Create: `src/LeafCalendar.App/LeafCalendar.App.csproj`, `Package.appxmanifest`, `NativeMethods.json`, `NativeMethods.txt`, `Assets/*.png`
- Create: `src/LeafCalendar.App/App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `LeafServices.cs`, `Interop/MemoryTrimmer.cs`
- Create: `src/LeafCalendar.App/ViewModels/SetupViewModel.cs`, `ViewModels/AccountsViewModel.cs`, `Views/SetupPage.xaml(.cs)`, `Views/AccountsPage.xaml(.cs)`
- Create: `tools/dev-register.ps1`, `tools/publish-aot.ps1`
- Create: `tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`, `.editorconfig`, `Support/LeafApp.cs`, `SetupTests.cs`

**Interfaces:**
- Consumes:
  - `LaunchOptions`, `LeafPaths`, `GoogleServices` (Task 15)
  - `CredentialLockerTokenStore`, `ITokenStore` (Task 5), `OAuthClientCredentials` (Task 4), `SignInException` (Task 4)
  - `LeafDatabase`, `AccountStore`, `CalendarStore`, `EventStore`, `AccountStatus` (Tasks 8 and 9)
  - `AppLog` (Task 7), `GoogleRetryHandler` (Task 11), `SyncMode` (Task 14)
- Produces:
  - The registered package `LeafCalendar` with AUMID `<PackageFamilyName>!App`.
  - Automation IDs:
    - Setup page: `AppTitleBar`, `SetupGuideText`, `ClientIdBox`, `ClientSecretBox`, `SetupError`, `SaveCredentialsButton`
    - Accounts page: `AddAccountButton`, `SyncNowButton`, `ChangeClientButton`, `AccountsList`, `AccountsMessage`
  - `LeafCalendar.App.Interop.MemoryTrimmer.Trim()`, used by Task 17.
  - UI test helper `LeafApp`, with these members:
    - `Launch(string? profile = null, string extraArguments = "")`
    - `NewProfile()`, `DeleteProfile(string)`
    - `WaitFor(string automationId)`
    - `App`, `MainWindow`, `IsNativeAot`

- [ ] **Step 1: Create the App project, manifest, and placeholder assets**

`src/LeafCalendar.App/LeafCalendar.App.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>WinExe</OutputType>
        <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
        <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
        <RootNamespace>LeafCalendar.App</RootNamespace>
        <AssemblyName>LeafCalendar</AssemblyName>
        <Platforms>x64</Platforms>
        <RuntimeIdentifier>win-x64</RuntimeIdentifier>
        <UseWinUI>true</UseWinUI>
        <EnableMsixTooling>true</EnableMsixTooling>
        <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
        <SelfContained>true</SelfContained>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
        <IsAotCompatible>true</IsAotCompatible>
        <TrimmerSingleWarn>false</TrimmerSingleWarn>
    </PropertyGroup>

    <!-- Native AOT On Release Only (AOT Debug deadlocks on .NET 10) -->
    <PropertyGroup Condition="'$(Configuration)' == 'Release'">
        <PublishAot>true</PublishAot>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.WindowsAppSDK" />
        <PackageReference Include="CommunityToolkit.Mvvm" />
        <PackageReference Include="Microsoft.Windows.CsWin32" PrivateAssets="all" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\LeafCalendar.Core\LeafCalendar.Core.csproj" />
    </ItemGroup>

    <ItemGroup>
        <Content Include="Assets\*.png" />
    </ItemGroup>
</Project>
```

`src/LeafCalendar.App/Package.appxmanifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

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
    </Application>
  </Applications>

  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

`src/LeafCalendar.App/NativeMethods.json`:
```json
{
  "$schema": "https://aka.ms/CsWin32.schema.json",
  "allowMarshaling": false
}
```

`src/LeafCalendar.App/NativeMethods.txt`:
```
GetCurrentProcess
SetProcessWorkingSetSize
```

Generate placeholder logos (solid leaf green; replaced by real art in Milestone 6). Run in PowerShell from the repo root:
```powershell
Add-Type -AssemblyName System.Drawing
$dir = 'src/LeafCalendar.App/Assets'
New-Item -ItemType Directory -Force $dir | Out-Null
$sizes = @{ Square44x44Logo = 44, 44; Square150x150Logo = 150, 150; Wide310x150Logo = 310, 150; StoreLogo = 50, 50; SplashScreen = 620, 300 }
foreach ($name in $sizes.Keys) {
    $w, $h = $sizes[$name]
    $bmp = [System.Drawing.Bitmap]::new($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 46, 125, 50))
    $g.Dispose()
    $bmp.Save("$dir/$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
```
Expected: 5 PNG files in `src/LeafCalendar.App/Assets`.

Add to the solution:
```bash
dotnet sln LeafCalendar.slnx add src/LeafCalendar.App/LeafCalendar.App.csproj --solution-folder src
```

- [ ] **Step 2: Write the App code**

`src/LeafCalendar.App/Interop/MemoryTrimmer.cs`:
```csharp
using Windows.Win32;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Releases idle memory when Leaf goes to the tray.
/// </summary>
/// <remarks>
/// A full GC runs first, then Windows is asked to page out the working set. Task Manager's number
/// drops sharply (measured ~55 MB to ~8 MB), but committed memory stays the same; pages come back
/// from the page file when used again, so the first window open after a long idle can lag slightly.
/// </remarks>
internal static class MemoryTrimmer
{
    /// <summary>Collects garbage and trims the working set.</summary>
    public static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
    }
}
```

`src/LeafCalendar.App/LeafServices.cs`:
```csharp
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Hosting;
using Microsoft.Windows.System.Power;
using Windows.Networking.Connectivity;

namespace LeafCalendar.App;

/// <summary>
/// App composition root: paths, log, database, secrets, HTTP, and (once an OAuth client is saved)
/// the Google services and sync loop.
/// </summary>
public sealed class LeafServices : IAsyncDisposable
{
    readonly HttpClient _http;

    /// <summary>Creates services for a profile under the package's local folder.</summary>
    public LeafServices(LaunchOptions options, string localFolder)
    {
        Options  = options;
        Paths    = new LeafPaths(localFolder, options.Profile);
        Log      = new AppLog(Paths.LogDirectory, Time);
        Database = new LeafDatabase(Paths.DatabasePath);
        Database.Migrate();
        Tokens = new CredentialLockerTokenStore(options.Profile);
        _http  = new HttpClient(new GoogleRetryHandler(Time) { InnerHandler = new SocketsHttpHandler() });

        // Sync Triggers
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;

        Google = CreateGoogle();
    }

    /// <summary>Launch options.</summary>
    public LaunchOptions Options { get; }

    /// <summary>Profile folders.</summary>
    public LeafPaths Paths { get; }

    /// <summary>Clock.</summary>
    public TimeProvider Time { get; } = TimeProvider.System;

    /// <summary>Log.</summary>
    public AppLog Log { get; }

    /// <summary>Database.</summary>
    public LeafDatabase Database { get; }

    /// <summary>Secrets.</summary>
    public ITokenStore Tokens { get; }

    /// <summary>Google services, or null before the OAuth client is set up.</summary>
    public GoogleServices? Google { get; private set; }

    /// <summary>Rebuilds Google services after the OAuth client changes.</summary>
    public async Task ReloadGoogleAsync()
    {
        if (Google is { } old)
        {
            await old.DisposeAsync();
        }

        Google = CreateGoogle();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;

        if (Google is { } google)
        {
            await google.DisposeAsync();
        }

        _http.Dispose();
    }

    GoogleServices? CreateGoogle()
    {
        if (Tokens.GetClientCredentials() is not { } credentials)
        {
            return null;
        }

        var google = new GoogleServices(_http, credentials, Tokens, Database, Log, Time);
        google.Loop.Start();
        return google;
    }

    void OnNetworkStatusChanged(object sender) => Google?.Loop.TriggerNow();

    void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
        {
            Google?.Loop.TriggerNow();
        }
    }
}
```

`src/LeafCalendar.App/App.xaml`:
```xml
<Application
    x:Class="LeafCalendar.App.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`src/LeafCalendar.App/App.xaml.cs`:
```csharp
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace LeafCalendar.App;

/// <summary>Leaf Calendar application entry.</summary>
public partial class App : Application
{
    LeafServices? _services;
    MainWindow? _window;
    DispatcherQueueTimer? _probeTimer;

    /// <summary>Loads XAML resources.</summary>
    public App() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = LaunchOptions.Parse([.. Environment.GetCommandLineArgs().Skip(1)]);
        _services = new LeafServices(options, ApplicationData.Current.LocalFolder.Path);

        _window = new MainWindow(_services);
        _window.Activate();

        if (options.TrayProbe)
        {
            StartTrayProbe();
        }
    }

    // Tray Probe: closes the window once it has rendered and trims memory, so the memory budget
    // test can measure tray-only mode before the real tray arrives in Milestone 4.
    void StartTrayProbe()
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _probeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _probeTimer.Interval    = TimeSpan.FromSeconds(3);
        _probeTimer.IsRepeating = false;
        _probeTimer.Tick += (_, _) =>
        {
            _window?.Close();
            _window = null;
            MemoryTrimmer.Trim();
        };
        _probeTimer.Start();
    }
}
```

`src/LeafCalendar.App/MainWindow.xaml`:
```xml
<Window
    x:Class="LeafCalendar.App.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Leaf Calendar">

    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>

    <Grid>
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
            AutomationProperties.AutomationId="AppTitleBar">
            <TitleBar.IconSource>
                <ImageIconSource ImageSource="ms-appx:///Assets/Square44x44Logo.png" />
            </TitleBar.IconSource>
        </TitleBar>
        <!-- /Title Bar -->

        <!-- Content -->
        <Frame x:Name="ContentFrame" Grid.Row="1" />
    </Grid>
</Window>
```

`src/LeafCalendar.App/MainWindow.xaml.cs`:
```csharp
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views;
using LeafCalendar.Core.Sync;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App;

/// <summary>
/// Main window. It has a tall XAML title bar (the caption buttons match its 48 px height) and a
/// Mica backdrop, and it hosts a page frame. The back button shows only when the frame can go back.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly LeafServices _services;

    /// <summary>Creates the window and shows setup or accounts.</summary>
    public MainWindow(LeafServices services)
    {
        _services = services;
        InitializeComponent();

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        Activated += OnActivated;
        Closed    += async (_, _) => await _services.DisposeAsync();

        if (_services.Google is null)
        {
            ShowSetup();
        }
        else
        {
            ShowAccounts();
        }
    }

    void ShowSetup() =>
        ContentFrame.Navigate(typeof(SetupPage), new SetupViewModel(_services.Tokens, OnCredentialsSavedAsync));

    void ShowAccounts()
    {
        ContentFrame.Navigate(typeof(AccountsPage), new AccountsViewModel(_services, ShowSetup));
        ContentFrame.BackStack.Clear();
    }

    async Task OnCredentialsSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        ShowAccounts();
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

    void OnBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }
}
```

`src/LeafCalendar.App/ViewModels/SetupViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.App.ViewModels;

/// <summary>Setup page: collects and saves the user's Google OAuth client.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    readonly ITokenStore _tokens;
    readonly Func<Task> _onSaved;

    /// <summary>Prefills the client ID when one is already saved.</summary>
    public SetupViewModel(ITokenStore tokens, Func<Task> onSaved)
    {
        _tokens   = tokens;
        _onSaved  = onSaved;
        ClientId  = tokens.GetClientCredentials()?.ClientId ?? "";
    }

    /// <summary>Client ID text.</summary>
    [ObservableProperty]
    public partial string ClientId { get; set; }

    /// <summary>Client secret text.</summary>
    [ObservableProperty]
    public partial string ClientSecret { get; set; } = "";

    /// <summary>Validation message, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => Error is not null;

    [RelayCommand]
    async Task SaveAsync()
    {
        Error = OAuthClientCredentials.Validate(ClientId, ClientSecret);
        if (Error is not null)
        {
            return;
        }

        _tokens.SetClientCredentials(new OAuthClientCredentials(ClientId.Trim(), ClientSecret.Trim()));
        ClientSecret = "";
        await _onSaved();
    }
}
```

`src/LeafCalendar.App/ViewModels/AccountsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using Windows.System;

namespace LeafCalendar.App.ViewModels;

/// <summary>One row in the accounts list.</summary>
public sealed record AccountRow(string Id, string Email, string Summary);

/// <summary>Accounts page: add, sync, and disconnect Google accounts.</summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    readonly LeafServices _services;

    /// <summary>Loads the account list.</summary>
    public AccountsViewModel(LeafServices services, Action openSetup)
    {
        _services = services;
        OpenSetup = openSetup;
        Refresh();
    }

    /// <summary>Navigates to the OAuth client setup page.</summary>
    public Action OpenSetup { get; }

    /// <summary>Accounts shown in the list.</summary>
    public ObservableCollection<AccountRow> Accounts { get; } = [];

    /// <summary>True while signing in or syncing.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Status or error text, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    /// <summary>True when <see cref="Message"/> is set.</summary>
    public bool HasMessage => Message is not null;

    /// <summary>Reloads rows from the database.</summary>
    public void Refresh()
    {
        Accounts.Clear();
        using var conn = _services.Database.Open();

        foreach (var account in AccountStore.GetAll(conn))
        {
            var calendars = CalendarStore.GetForAccount(conn, account.Id);
            var events    = calendars.Sum(c => EventStore.Count(conn, account.Id, c.Id));
            var summary   = account.Status == AccountStatus.NeedsSignIn
                ? "Needs sign-in"
                : $"{calendars.Count} calendars · {events} events";

            Accounts.Add(new AccountRow(account.Id, account.Email, summary));
        }
    }

    /// <summary>Disconnects an account (after the page confirms).</summary>
    public async Task DisconnectAsync(string accountId)
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await google.DisconnectAsync(accountId, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    async Task AddAccountAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy  = true;
        Message = "Finish signing in with Google in your browser.";
        try
        {
            var account = await google.CreateSignIn(uri => Launcher.LaunchUriAsync(uri).AsTask()).RunAsync(null, CancellationToken.None);
            Message = $"Signed in as {account.Email}. Syncing…";
            await google.Sync.SyncAccountAsync(account.Id, CancellationToken.None);
            Message = null;
        }
        catch (SignInException ex)
        {
            Message = ex.Message;
        }
        catch (HttpRequestException)
        {
            Message = "Couldn't reach Google. Check your connection and try again.";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    async Task SyncNowAsync()
    {
        if (_services.Google is not { } google)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await google.Sync.SyncAllAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}
```

`src/LeafCalendar.App/Views/SetupPage.xaml`:
```xml
<Page
    x:Class="LeafCalendar.App.Views.SetupPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <ScrollViewer>
        <StackPanel MaxWidth="560" Padding="32" Spacing="16">

            <!-- Heading -->
            <TextBlock Style="{StaticResource TitleTextBlockStyle}" Text="Connect Leaf to Google" />

            <!-- Setup Guide -->
            <RichTextBlock AutomationProperties.AutomationId="SetupGuideText">
                <Paragraph>
                    Leaf signs in with your own Google OAuth client. You only need to do this once.
                </Paragraph>
                <Paragraph>
                    1. Open
                    <Hyperlink NavigateUri="https://console.cloud.google.com/">
                        Google Cloud Console
                    </Hyperlink>
                    and create a project.
                </Paragraph>
                <Paragraph>
                    2. In APIs &amp; Services, open Library and enable the Google Calendar API and the People API.
                </Paragraph>
                <Paragraph>
                    3. In OAuth consent screen, choose External and add your Google accounts as test users. Then set the publishing status to In production. While it's in Testing, Google signs you out every 7 days.
                </Paragraph>
                <Paragraph>
                    4. In Credentials, create an OAuth client ID of type Desktop app. Paste its Client ID and Client secret below.
                </Paragraph>
            </RichTextBlock>
            <!-- /Setup Guide -->

            <!-- Credentials Form -->
            <TextBox
                Header="Client ID"
                Text="{x:Bind ViewModel.ClientId, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                AutomationProperties.AutomationId="ClientIdBox" />
            <PasswordBox
                Header="Client secret"
                Password="{x:Bind ViewModel.ClientSecret, Mode=TwoWay}"
                AutomationProperties.AutomationId="ClientSecretBox" />
            <InfoBar
                Severity="Error"
                IsClosable="False"
                IsOpen="{x:Bind ViewModel.HasError, Mode=OneWay}"
                Message="{x:Bind ViewModel.Error, Mode=OneWay}"
                AutomationProperties.AutomationId="SetupError" />
            <Button
                Style="{StaticResource AccentButtonStyle}"
                Content="Save"
                Command="{x:Bind ViewModel.SaveCommand}"
                AutomationProperties.AutomationId="SaveCredentialsButton" />
            <!-- /Credentials Form -->

        </StackPanel>
    </ScrollViewer>
</Page>
```

`src/LeafCalendar.App/Views/SetupPage.xaml.cs`:
```csharp
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views;

/// <summary>OAuth client setup page.</summary>
public sealed partial class SetupPage : Page
{
    /// <summary>Creates the page.</summary>
    public SetupPage() => InitializeComponent();

    /// <summary>Page view model (passed as the navigation parameter).</summary>
    public SetupViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (SetupViewModel)e.Parameter;
        Bindings.Update();
    }
}
```

`src/LeafCalendar.App/Views/AccountsPage.xaml`:
```xml
<Page
    x:Class="LeafCalendar.App.Views.AccountsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:LeafCalendar.App.ViewModels">

    <Grid Padding="32" RowSpacing="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- Heading -->
        <TextBlock Style="{StaticResource TitleTextBlockStyle}" Text="Accounts" />

        <!-- Actions -->
        <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="8">
            <Button
                Style="{StaticResource AccentButtonStyle}"
                Content="Add Google account"
                Command="{x:Bind ViewModel.AddAccountCommand}"
                AutomationProperties.AutomationId="AddAccountButton" />
            <Button
                Content="Sync now"
                Command="{x:Bind ViewModel.SyncNowCommand}"
                AutomationProperties.AutomationId="SyncNowButton" />
            <Button
                Content="Change OAuth client"
                Click="OnChangeClientClick"
                AutomationProperties.AutomationId="ChangeClientButton" />
            <ProgressRing IsActive="{x:Bind ViewModel.IsBusy, Mode=OneWay}" Width="20" Height="20" />
        </StackPanel>

        <!-- Status -->
        <InfoBar
            Grid.Row="2"
            Severity="Informational"
            IsOpen="{x:Bind ViewModel.HasMessage, Mode=OneWay}"
            Message="{x:Bind ViewModel.Message, Mode=OneWay}"
            AutomationProperties.AutomationId="AccountsMessage" />

        <!-- Account List -->
        <ListView
            Grid.Row="3"
            SelectionMode="None"
            ItemsSource="{x:Bind ViewModel.Accounts}"
            AutomationProperties.AutomationId="AccountsList">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="vm:AccountRow">
                    <Grid Padding="0,8" ColumnSpacing="12">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Style="{StaticResource BodyStrongTextBlockStyle}" Text="{x:Bind Email}" />
                            <TextBlock Style="{StaticResource CaptionTextBlockStyle}" Text="{x:Bind Summary}" />
                        </StackPanel>
                        <Button Grid.Column="1" Content="Disconnect" Tag="{x:Bind Id}" Click="OnDisconnectClick" />
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
        <!-- /Account List -->
    </Grid>
</Page>
```

`src/LeafCalendar.App/Views/AccountsPage.xaml.cs`:
```csharp
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views;

/// <summary>Accounts page.</summary>
public sealed partial class AccountsPage : Page
{
    /// <summary>Creates the page.</summary>
    public AccountsPage() => InitializeComponent();

    /// <summary>Page view model (passed as the navigation parameter).</summary>
    public AccountsViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (AccountsViewModel)e.Parameter;
        Bindings.Update();
    }

    void OnChangeClientClick(object sender, RoutedEventArgs e) => ViewModel.OpenSetup();

    // Disconnect deletes local data, so confirm first
    async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var accountId = (string)((Button)sender).Tag;
        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = "Disconnect this account?",
            Content           = "Leaf will sign out of this Google account and remove its calendars from this PC. Your Google Calendar isn't changed.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DisconnectAsync(accountId);
        }
    }
}
```

- [ ] **Step 3: Write the dev scripts**

`tools/dev-register.ps1`:
```powershell
# Builds Leaf Calendar (Debug, JIT, x64) and registers its loose package layout for this user.
# Used for local runs and UI tests. Requires Windows Developer Mode.
# Warning: replacing a registration from a different folder clears Leaf's LocalState (the cache
# re-syncs from Google; secrets in Credential Locker are kept).
$ErrorActionPreference = 'Stop'
$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/LeafCalendar.App/LeafCalendar.App.csproj'

# Build
dotnet build $project -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Find Layout
$manifest = Get-ChildItem (Join-Path $root 'src/LeafCalendar.App/bin/x64/Debug') -Recurse -Filter 'AppxManifest.xml' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $manifest) { throw 'AppxManifest.xml not found under bin/x64/Debug. Check the build output.' }

# Register
$existing = Get-AppxPackage LeafCalendar
if ($existing -and $existing.InstallLocation -ne $manifest.DirectoryName) {
    Write-Warning "Replacing Leaf Calendar registered from $($existing.InstallLocation). Local cache will be cleared."
    Remove-AppxPackage $existing.PackageFullName
}
Add-AppxPackage -Register $manifest.FullName -ForceApplicationShutdown
Write-Host "Registered $((Get-AppxPackage LeafCalendar).PackageFamilyName) from $($manifest.DirectoryName)"
```

`tools/publish-aot.ps1`:
```powershell
# Publishes Leaf Calendar as a Native AOT MSIX (Release, x64, unsigned). With -Register, it also
# unpacks the MSIX and registers it for this user (for memory budget tests and release checks).
# Requires .NET SDK 10 (VS C++ build tools for the AOT linker) and Windows Developer Mode for -Register.
param([switch]$Register)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app  = Join-Path $root 'src/LeafCalendar.App'

# The AOT linker setup calls vswhere.exe by bare name
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

# Publish
Remove-Item (Join-Path $app 'AppPackages') -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $app 'LeafCalendar.App.csproj') -c Release -r win-x64 -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$msix = Get-ChildItem (Join-Path $app 'AppPackages') -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $msix) { throw 'No .msix produced under AppPackages.' }
Write-Host "MSIX: $($msix.FullName)"
if (-not $Register) { return }

# Unpack And Register
$layout = Join-Path $app 'AppPackages/aot-layout'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($msix.FullName, $layout)

$existing = Get-AppxPackage LeafCalendar
if ($existing -and $existing.InstallLocation -ne $layout) {
    Write-Warning "Replacing Leaf Calendar registered from $($existing.InstallLocation). Local cache will be cleared."
    Remove-AppxPackage $existing.PackageFullName
}
Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceApplicationShutdown
Write-Host "Registered AOT build from $layout"
```

- [ ] **Step 4: Build, register, and look at it**

Run: `pwsh tools/dev-register.ps1`
Expected: `Build succeeded` with 0 warnings, then `Registered LeafCalendar_<hash> from ...`.

Run: `Start-Process "shell:AppsFolder\$((Get-AppxPackage LeafCalendar).PackageFamilyName)!App"`
Expected, checked by eye:
- A Mica window with a 48 px title bar showing the placeholder icon and "Leaf Calendar".
- Minimize, maximize, and close buttons at the same 48 px height.
- No back button.
- The "Connect Leaf to Google" setup guide.

Close it.

- [ ] **Step 5: Create the UI test project**

`tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
        <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
        <OutputType>Exe</OutputType>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <Using Include="Xunit" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\LeafCalendar.Core\LeafCalendar.Core.csproj" />
    </ItemGroup>
</Project>
```

Run:
```bash
dotnet add tests/LeafCalendar.UITests package Microsoft.NET.Test.Sdk
dotnet add tests/LeafCalendar.UITests package xunit.v3
dotnet add tests/LeafCalendar.UITests package xunit.runner.visualstudio
dotnet add tests/LeafCalendar.UITests package FlaUI.UIA3 --version 5.0.0
dotnet sln LeafCalendar.slnx add tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --solution-folder tests
```

`tests/LeafCalendar.UITests/.editorconfig`:
```ini
[*.cs]
# Test names use Method_Condition_Result underscores by convention.
dotnet_diagnostic.CA1707.severity = none
```

`tests/LeafCalendar.UITests/Support/LeafApp.cs`:
```csharp
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using LeafCalendar.Core.Auth;
using Windows.Management.Deployment;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Launches the registered Leaf package with a throwaway profile and finds elements by automation ID.
/// </summary>
public sealed class LeafApp : IDisposable
{
    const string PackageName = "LeafCalendar";

    readonly UIA3Automation _automation = new();

    LeafApp(Application app) => App = app;

    /// <summary>The running app.</summary>
    public Application App { get; }

    /// <summary>The main window (waits up to 20 s).</summary>
    public Window MainWindow => App.GetMainWindow(_automation, TimeSpan.FromSeconds(20))
        ?? throw new InvalidOperationException("Leaf's main window didn't appear.");

    /// <summary>The registered package.</summary>
    public static Windows.ApplicationModel.Package Package =>
        new PackageManager().FindPackagesForUser(string.Empty).FirstOrDefault(p => p.Id.Name == PackageName)
        ?? throw new InvalidOperationException("Leaf Calendar isn't registered. Run tools/dev-register.ps1 first.");

    /// <summary>True when the registered build is Native AOT (no managed LeafCalendar.dll).</summary>
    public static bool IsNativeAot => !File.Exists(Path.Combine(Package.InstalledLocation.Path, "LeafCalendar.dll"));

    /// <summary>Creates a unique throwaway profile name.</summary>
    public static string NewProfile() => "uitest-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Launches Leaf with <c>--profile</c> plus extra arguments.</summary>
    public static LeafApp Launch(string? profile = null, string extraArguments = "") =>
        new(Application.LaunchStoreApp($"{Package.Id.FamilyName}!App", $"--profile {profile ?? NewProfile()} {extraArguments}".Trim()));

    /// <summary>Deletes a profile's secrets and local files.</summary>
    public static void DeleteProfile(string profile)
    {
        new CredentialLockerTokenStore(profile).DeleteAll();

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            Package.Id.FamilyName,
            "LocalState",
            "profiles",
            profile);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Waits up to 15 s for an element by automation ID.</summary>
    public AutomationElement WaitFor(string automationId) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear.");

    /// <inheritdoc />
    public void Dispose()
    {
        if (!App.HasExited)
        {
            App.Kill();
        }

        App.Dispose();
        _automation.Dispose();
    }
}
```

- [ ] **Step 6: Write the UI tests**

`tests/LeafCalendar.UITests/SetupTests.cs`:
```csharp
using FlaUI.Core.Input;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public class SetupTests
{
    static void EnterCredentials(LeafApp leaf, string clientId, string secret)
    {
        leaf.WaitFor("ClientIdBox").AsTextBox().Enter(clientId);
        leaf.WaitFor("ClientSecretBox").Focus();
        Keyboard.Type(secret);
        leaf.WaitFor("SaveCredentialsButton").AsButton().Invoke();
    }

    [Fact]
    public void Launch_FreshProfile_ShowsTitleBarAndSetupGuide()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);

            Assert.NotNull(leaf.WaitFor("AppTitleBar"));
            Assert.NotNull(leaf.WaitFor("SetupGuideText"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Save_InvalidClientId_ShowsError()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile);

            EnterCredentials(leaf, "not-a-client-id", "GOCSPX-uitest");

            Assert.NotNull(leaf.WaitFor("SetupError"));
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    [Fact]
    public void Save_ValidCredentials_OpensAccountsAndPersistsAcrossLaunches()
    {
        var profile = LeafApp.NewProfile();
        try
        {
            using (var leaf = LeafApp.Launch(profile))
            {
                EnterCredentials(leaf, "123-uitest.apps.googleusercontent.com", "GOCSPX-uitest");

                Assert.NotNull(leaf.WaitFor("AddAccountButton"));
            }

            using (var relaunched = LeafApp.Launch(profile))
            {
                Assert.NotNull(relaunched.WaitFor("AddAccountButton"));
            }
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
```

- [ ] **Step 7: Run the UI tests**

Run: `dotnet test tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
Expected: PASS, 3 tests. The Leaf window flashes open and closes for each test.

If the UIA tree comes back empty (a known quirk with some packaged WinUI apps), stop and report it. Don't add sleeps. The documented fix is running the tests on an STA thread, and that needs a decision.

- [ ] **Step 8: Verify the Release AOT publish is clean**

Run: `pwsh tools/publish-aot.ps1`
Expected:
- The publish succeeds with **0 IL warnings**. `TreatWarningsAsErrors` turns any IL2xxx or IL3xxx warning into a build failure.
- There's one allowed packaging warning about `mspdbcmf.exe` (no symbols package).
- `MSIX: ...\AppPackages\...\LeafCalendar_0.1.0.0_x64.msix`.

- [ ] **Step 9: Commit**

```bash
git add src/LeafCalendar.App tools tests/LeafCalendar.UITests LeafCalendar.slnx Directory.Packages.props
git commit -m "feat(app): add packaged WinUI shell with setup and accounts pages"
```

---

### Task 17: Memory Budget

**Files:**
- Create: `tests/LeafCalendar.UITests/memory-budget.json`, `tests/LeafCalendar.UITests/MemoryTests.cs`
- Modify: `tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj` (copy `memory-budget.json` to output)

**Interfaces:**
- Consumes: `LeafApp` (Task 16) and `--tray-probe` in `App.xaml.cs` (Task 16).
- Produces: a memory budget test, `MemoryTests.TrayProbe_Measured_StaysWithinBudget`, that fails when the tray-only AOT build grows past budget.

- [ ] **Step 1: Add the budget file with measuring values**

`tests/LeafCalendar.UITests/memory-budget.json` (the 1000 values are for the measuring run only; Step 4 replaces them):
```json
{
  "trayPrivateBytesMb": 1000,
  "trayWorkingSetMb": 1000
}
```

Add to `tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`:
```xml
    <ItemGroup>
        <None Include="memory-budget.json" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>
```

- [ ] **Step 2: Write the test**

`tests/LeafCalendar.UITests/MemoryTests.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public class MemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TrayProbe_Measured_StaysWithinBudget()
    {
        if (!LeafApp.IsNativeAot)
        {
            Assert.Skip("Memory budget applies to the Release AOT package. Run tools/publish-aot.ps1 -Register first.");
        }

        var profile = LeafApp.NewProfile();
        try
        {
            using var leaf = LeafApp.Launch(profile, "--tray-probe");

            // Probe closes the window at ~3 s and trims; give it time to settle
            await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            using var process = Process.GetProcessById(leaf.App.ProcessId);
            process.Refresh();
            var privateMb    = process.PrivateMemorySize64 / (1024 * 1024);
            var workingSetMb = process.WorkingSet64 / (1024 * 1024);
            output.WriteLine($"Tray-only: private bytes {privateMb} MB, working set {workingSetMb} MB");

            using var budget = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "memory-budget.json"), TestContext.Current.CancellationToken));
            var maxPrivate    = budget.RootElement.GetProperty("trayPrivateBytesMb").GetInt64();
            var maxWorkingSet = budget.RootElement.GetProperty("trayWorkingSetMb").GetInt64();

            Assert.True(privateMb <= maxPrivate, $"Private bytes {privateMb} MB exceed budget {maxPrivate} MB.");
            Assert.True(workingSetMb <= maxWorkingSet, $"Working set {workingSetMb} MB exceeds budget {maxWorkingSet} MB.");
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
```

- [ ] **Step 3: Measure three times**

Run: `pwsh tools/publish-aot.ps1 -Register`
Expected: `Registered AOT build from ...\aot-layout`.

Run the test 3 times, with output shown:
```bash
dotnet test tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter "FullyQualifiedName~MemoryTests" --logger "console;verbosity=detailed"
```
Expected: PASS each time, with a line like `Tray-only: private bytes NN MB, working set NN MB`. Record the six numbers. The earlier spike suggests about 72 to 82 MB private and 20 to 25 MB working set.

- [ ] **Step 4: Set the budget**

Take the highest private-bytes and highest working-set values from Step 3. Multiply each by 1.2 and round up to a whole MB. Write those two numbers into `memory-budget.json`. Example: if the highest private bytes is 78 and the highest working set is 24, write `94` and `29`.

Run: `dotnet test tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj --filter "FullyQualifiedName~MemoryTests"`
Expected: PASS.

Update the spec's Section 3.4, item 7, to say: "Budget set 2026-MM-DD: private bytes ≤ X MB, working set ≤ Y MB (tray-only, AOT, x64)". Use the real date and numbers.

- [ ] **Step 5: Re-register the Debug build for everyday UI tests**

Run: `pwsh tools/dev-register.ps1`
Expected: registered from `bin\x64\Debug\...`. The memory test now skips, and the setup tests still pass.

- [ ] **Step 6: Commit**

```bash
git add tests/LeafCalendar.UITests docs/superpowers/specs/2026-09-29-leaf-calendar-design.md
git commit -m "test(ui): add tray-only memory budget test"
```

---

### Task 18: Live Google Tests

**Files:**
- Create: `tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj`, `.editorconfig`, `Support/LiveAccount.cs`, `Support/LiveGoogle.cs`, `LiveSignInTests.cs`, `LiveSyncTests.cs`

**Interfaces:**
- Consumes: `CredentialLockerTokenStore`, `GoogleOAuthClient`, `AccessTokenProvider`, `SignInFlow`, `GoogleServices`, `LeafDatabase`, `AccountStore`, `EventStore`, `CalendarStore`, and `AppLog` (Tasks 4 to 15).
- Produces:
  - An interactive one-time sign-in test that stores the throwaway account in Credential Locker under profile `live-tests`.
  - A live create/update/delete sync test. Both skip when the account isn't set up.

- [ ] **Step 1: Create the project**

`tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
        <TargetPlatformMinVersion>10.0.22000.0</TargetPlatformMinVersion>
        <OutputType>Exe</OutputType>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <Using Include="Xunit" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\LeafCalendar.Core\LeafCalendar.Core.csproj" />
    </ItemGroup>
</Project>
```

Run:
```bash
dotnet add tests/LeafCalendar.LiveTests package Microsoft.NET.Test.Sdk
dotnet add tests/LeafCalendar.LiveTests package xunit.v3
dotnet add tests/LeafCalendar.LiveTests package xunit.runner.visualstudio
dotnet sln LeafCalendar.slnx add tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --solution-folder tests
```

`tests/LeafCalendar.LiveTests/.editorconfig`:
```ini
[*.cs]
# Test names use Method_Condition_Result underscores by convention.
dotnet_diagnostic.CA1707.severity = none
```

- [ ] **Step 2: Write the support classes**

`tests/LeafCalendar.LiveTests/Support/LiveAccount.cs`:
```csharp
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using Microsoft.Data.Sqlite;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LeafCalendar.LiveTests.Support;

/// <summary>
/// The throwaway Google account used by live tests.
/// </summary>
/// <remarks>
/// Secrets live in Credential Locker under profile <c>live-tests</c>, and are set once by
/// <c>LiveSignInTests</c>. Each instance has its own temp database and log.
/// </remarks>
public sealed class LiveAccount : IAsyncDisposable
{
    /// <summary>Credential Locker profile for live tests.</summary>
    public const string Profile = "live-tests";

    readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "leaf-live", Guid.NewGuid().ToString("N"))).FullName;

    LiveAccount(OAuthClientCredentials credentials, string accountId)
    {
        AccountId = accountId;
        Database  = new LeafDatabase(Path.Combine(_folder, "leaf.db"));
        Database.Migrate();
        Log      = new AppLog(Path.Combine(_folder, "Logs"), TimeProvider.System);
        Services = new GoogleServices(Http, credentials, Tokens, Database, Log, TimeProvider.System);

        using var conn = Database.Open();
        AccountStore.Upsert(conn, new Account(accountId, "live-test", null, null, AccountStatus.Ok));
    }

    /// <summary>Shared HTTP client.</summary>
    public static HttpClient Http { get; } = new();

    /// <summary>Live secrets.</summary>
    public static CredentialLockerTokenStore Tokens { get; } = new(Profile);

    /// <summary>Signed-in account ID.</summary>
    public string AccountId { get; }

    /// <summary>Temp database.</summary>
    public LeafDatabase Database { get; }

    /// <summary>Temp log.</summary>
    public AppLog Log { get; }

    /// <summary>Wired Google services.</summary>
    public GoogleServices Services { get; }

    /// <summary>Loads the account, or returns null when live tests aren't set up.</summary>
    public static LiveAccount? TryLoad() =>
        Tokens.GetClientCredentials() is { } credentials && Tokens.GetAccountIds().FirstOrDefault() is { } accountId
            ? new LiveAccount(credentials, accountId)
            : null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleaner.
        }
    }
}
```

`tests/LeafCalendar.LiveTests/Support/LiveGoogle.cs`:
```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LeafCalendar.LiveTests.Support;

/// <summary>
/// Raw Google calls that live tests use to arrange data. The app doesn't need these yet.
/// </summary>
public sealed class LiveGoogle(LiveAccount live)
{
    static readonly Uri BaseUri = new("https://www.googleapis.com/calendar/v3/");

    /// <summary>Creates a "Leaf Test &lt;timestamp&gt;" calendar and returns its ID.</summary>
    public async Task<string> CreateTestCalendarAsync(CancellationToken ct)
    {
        var body = new JsonObject { ["summary"] = $"Leaf Test {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}" };
        var created = await SendAsync(HttpMethod.Post, "calendars", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Deletes a calendar.</summary>
    public Task DeleteCalendarAsync(string calendarId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"calendars/{Uri.EscapeDataString(calendarId)}", null, ct);

    /// <summary>Creates a one-hour event tomorrow and returns its ID.</summary>
    public async Task<string> InsertEventAsync(string calendarId, string summary, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(15);
        var body = new JsonObject
        {
            ["summary"] = summary,
            ["start"]   = new JsonObject { ["dateTime"] = start.ToString("O") },
            ["end"]     = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("O") },
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Changes an event's title.</summary>
    public Task PatchSummaryAsync(string calendarId, string eventId, string summary, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", new JsonObject { ["summary"] = summary }, ct);

    /// <summary>Deletes an event.</summary>
    public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", null, ct);

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await live.Services.AccessTokens.GetAccessTokenAsync(live.AccountId, ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await LiveAccount.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }
}
```

- [ ] **Step 3: Write the live tests**

`tests/LeafCalendar.LiveTests/LiveSignInTests.cs`:
```csharp
using System.Diagnostics;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveSignInTests
{
    // One-time interactive setup. Run with:
    //   $env:LEAF_LIVE_SIGNIN = '1'; $env:LEAF_LIVE_CLIENT_ID = '...'; $env:LEAF_LIVE_CLIENT_SECRET = '...'
    //   dotnet test tests/LeafCalendar.LiveTests --filter "FullyQualifiedName~LiveSignInTests"
    // then sign in with the THROWAWAY Google account in the browser that opens.
    [Fact]
    public async Task SignIn_Interactive_StoresThrowawayAccount()
    {
        if (Environment.GetEnvironmentVariable("LEAF_LIVE_SIGNIN") != "1")
        {
            Assert.Skip("Set LEAF_LIVE_SIGNIN=1, LEAF_LIVE_CLIENT_ID, and LEAF_LIVE_CLIENT_SECRET to sign in the throwaway account once.");
        }

        var credentials = new OAuthClientCredentials(
            Environment.GetEnvironmentVariable("LEAF_LIVE_CLIENT_ID") ?? "",
            Environment.GetEnvironmentVariable("LEAF_LIVE_CLIENT_SECRET") ?? "");
        Assert.Null(OAuthClientCredentials.Validate(credentials.ClientId, credentials.ClientSecret));

        LiveAccount.Tokens.DeleteAll();
        LiveAccount.Tokens.SetClientCredentials(credentials);

        var folder   = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "leaf-live", Guid.NewGuid().ToString("N"))).FullName;
        var database = new LeafDatabase(Path.Combine(folder, "leaf.db"));
        database.Migrate();
        await using var services = new GoogleServices(LiveAccount.Http, credentials, LiveAccount.Tokens, database, new AppLog(folder, TimeProvider.System), TimeProvider.System);

        var account = await services.CreateSignIn(uri =>
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal([account.Id], LiveAccount.Tokens.GetAccountIds());
    }
}
```

`tests/LeafCalendar.LiveTests/LiveSyncTests.cs`:
```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveSyncTests
{
    [Fact]
    public async Task Sync_CreateUpdateDelete_MirrorsGoogle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip("Live account not set up. Run LiveSignInTests once (see its comment).");
        }

        var google     = new LiveGoogle(live);
        var calendarId = await google.CreateTestCalendarAsync(ct);
        try
        {
            // Create
            var eventId = await google.InsertEventAsync(calendarId, "Leaf live create", ct);
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            Assert.Contains("Leaf live create", Get(live, calendarId, eventId)!.RawJson, StringComparison.Ordinal);

            // Update (incremental)
            var token = SyncToken(live, calendarId);
            await google.PatchSummaryAsync(calendarId, eventId, "Leaf live update", ct);
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            Assert.Contains("Leaf live update", Get(live, calendarId, eventId)!.RawJson, StringComparison.Ordinal);
            Assert.NotEqual(token, SyncToken(live, calendarId));

            // Delete
            await google.DeleteEventAsync(calendarId, eventId, ct);
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            Assert.Null(Get(live, calendarId, eventId));
        }
        finally
        {
            await google.DeleteCalendarAsync(calendarId, CancellationToken.None);
        }
    }

    static StoredEvent? Get(LiveAccount live, string calendarId, string eventId)
    {
        using var conn = live.Database.Open();
        return EventStore.Get(conn, live.AccountId, calendarId, eventId);
    }

    static string? SyncToken(LiveAccount live, string calendarId)
    {
        using var conn = live.Database.Open();
        return CalendarStore.GetForAccount(conn, live.AccountId).Single(c => c.Id == calendarId).SyncToken;
    }
}
```

- [ ] **Step 4: Verify the tests skip cleanly without setup**

Run: `dotnet test tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj`
Expected: 2 skipped, 0 failed.

- [ ] **Step 5: Hand off to the owner for the one-time live setup**

Stop and ask the owner to:
1. Create a throwaway Google account.
2. Set these environment variables for their own shell: `LEAF_LIVE_SIGNIN=1`, plus `LEAF_LIVE_CLIENT_ID` and `LEAF_LIVE_CLIENT_SECRET` from their OAuth client.
3. Run `dotnet test tests/LeafCalendar.LiveTests --filter "FullyQualifiedName~LiveSignInTests"` and sign in with the throwaway account in the browser.

Never type the owner's credentials yourself. Once they confirm, run:

Run: `dotnet test tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --filter "FullyQualifiedName~LiveSyncTests"`
Expected: PASS, and the "Leaf Test …" calendar is gone from the throwaway account afterwards.

- [ ] **Step 6: Commit**

```bash
git add tests/LeafCalendar.LiveTests LeafCalendar.slnx Directory.Packages.props
git commit -m "test(live): add live Google sign-in and sync tests"
```

---

### Task 19: Recurrence Expander

**Files:**
- Modify: `Directory.Packages.props` (add the pinned Meziantou version), `src/LeafCalendar.Core/LeafCalendar.Core.csproj` (reference it)
- Create: `src/LeafCalendar.Core/Recurrence/RecurrenceExpander.cs`
- Test: `tests/LeafCalendar.Tests/RecurrenceExpanderTests.cs`
- Modify: `tests/LeafCalendar.LiveTests/Support/LiveGoogle.cs`, `tests/LeafCalendar.LiveTests/LiveSyncTests.cs` (compare against Google's own expansion)

**Interfaces:**
- Consumes: nothing from earlier tasks in Core. The live test consumes `LiveAccount`, `LiveGoogle` (Task 18), and `EventStore` (Task 9).
- Produces: `static class LeafCalendar.Core.Recurrence.RecurrenceExpander` with:
  - `const int MaxOccurrences` (2000): the most occurrences returned for one window.
  - `const int MaxScanned` (100000): the most occurrences walked before giving up.
  - `IReadOnlyList<DateTimeOffset> ExpandTimed(IReadOnlyList<string> recurrence, DateTimeOffset start, string? timeZoneId, DateTimeOffset windowStart, DateTimeOffset windowEnd)`: occurrence starts with `windowStart <= start < windowEnd`, sorted and de-duplicated. `timeZoneId` is Google's `start.timeZone`; when it's null or unknown, the start's fixed offset is used.
  - `IReadOnlyList<DateOnly> ExpandAllDay(IReadOnlyList<string> recurrence, DateOnly start, DateOnly windowStart, DateOnly windowEnd)`: the same for all-day series.
  - `recurrence` is Google's raw `recurrence` array (`RRULE:...`, `EXDATE...`, `RDATE...`). Unreadable lines are skipped, never thrown on, since anyone can send an invite. An unreadable or missing `RRULE` means a single occurrence at `start`.
  - Callers that need events overlapping the window (not just starting in it) pass `windowStart - duration`.

- [ ] **Step 1: Add the package**

Add to the `<ItemGroup>` in `Directory.Packages.props`:
```xml
        <PackageVersion Include="Meziantou.Framework.Scheduling" Version="4.1.3" />
```

Add to `src/LeafCalendar.Core/LeafCalendar.Core.csproj`, inside the `<ItemGroup>` that holds `Microsoft.Data.Sqlite`:
```xml
        <PackageReference Include="Meziantou.Framework.Scheduling" />
```

Run: `dotnet build src/LeafCalendar.Core/LeafCalendar.Core.csproj`
Expected: `Build succeeded. 0 Warning(s)`.

- [ ] **Step 2: Write the failing tests**

`tests/LeafCalendar.Tests/RecurrenceExpanderTests.cs`:
```csharp
using System.Globalization;
using LeafCalendar.Core.Recurrence;

namespace LeafCalendar.Tests;

public class RecurrenceExpanderTests
{
    const string NewYork = "America/New_York";

    static readonly DateTimeOffset Always = DateTimeOffset.MinValue;
    static readonly DateTimeOffset Never  = DateTimeOffset.MaxValue;

    static DateTimeOffset Ny(int year, int month, int day, int hour, int minute, int offsetHours) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(offsetHours));

    static string[] Utc(IEnumerable<DateTimeOffset> values) =>
        [.. values.Select(v => v.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))];

    [Fact]
    public void ExpandTimed_WeeklyAcrossDst_KeepsWallClock()
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6"], Ny(2026, 10, 26, 9, 0, -4), NewYork, Always, Never);

        Assert.Equal(
            ["2026-10-26 13:00", "2026-10-28 13:00", "2026-11-02 14:00", "2026-11-04 14:00", "2026-11-09 14:00", "2026-11-11 14:00"],
            Utc(result));
    }

    [Theory]
    [InlineData("EXDATE;TZID=America/New_York:20261104T090000")]
    [InlineData("EXDATE:20261104T140000Z")]
    public void ExpandTimed_Exdate_RemovesOccurrence(string exdate)
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6", exdate], Ny(2026, 10, 26, 9, 0, -4), NewYork, Always, Never);

        Assert.Equal(5, result.Count);
        Assert.DoesNotContain("2026-11-04 14:00", Utc(result));
    }

    [Fact]
    public void ExpandTimed_Rdate_AddsOccurrencesInOrder()
    {
        var result = RecurrenceExpander.ExpandTimed(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=2", "RDATE;TZID=America/New_York:20261029T090000,20261030T090000"],
            Ny(2026, 10, 26, 9, 0, -4),
            NewYork,
            Always,
            Never);

        Assert.Equal(["2026-10-26 13:00", "2026-10-29 13:00", "2026-10-30 13:00", "2026-11-02 14:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_LastFridayBySetPos_ReturnsLastFridays()
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=MONTHLY;BYDAY=FR;BYSETPOS=-1;COUNT=4"], Ny(2026, 10, 30, 17, 0, -4), NewYork, Always, Never);

        Assert.Equal(["2026-10-30 21:00", "2026-11-27 22:00", "2026-12-25 22:00", "2027-01-29 22:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_SecondTuesdayUntilUtc_StopsAtUntil()
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=MONTHLY;BYDAY=2TU;UNTIL=20270101T000000Z"], Ny(2026, 10, 13, 10, 0, -4), NewYork, Always, Never);

        Assert.Equal(["2026-10-13 14:00", "2026-11-10 15:00", "2026-12-08 15:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_Window_ReturnsOnlyStartsInside()
    {
        var result = RecurrenceExpander.ExpandTimed(
            ["RRULE:FREQ=DAILY"],
            Ny(2026, 1, 1, 9, 0, -5),
            NewYork,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(["2026-10-01 13:00", "2026-10-02 13:00", "2026-10-03 13:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_NoRule_ReturnsStartOnly()
    {
        var start = Ny(2026, 10, 1, 9, 0, -4);

        Assert.Equal([start], RecurrenceExpander.ExpandTimed([], start, NewYork, Always, Never));
    }

    [Theory]
    [InlineData("RRULE:FREQ=NONSENSE")]
    [InlineData("RRULE:")]
    public void ExpandTimed_UnreadableRule_ReturnsStartOnly(string rule)
    {
        var start = Ny(2026, 10, 1, 9, 0, -4);

        Assert.Equal([start], RecurrenceExpander.ExpandTimed([rule, "EXDATE;TZID=Nowhere/Zone:garbage"], start, NewYork, Always, Never));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    public void ExpandTimed_MissingOrUnknownZone_UsesStartOffset(string? zone)
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=DAILY;COUNT=2"], Ny(2026, 10, 31, 9, 0, -4), zone, Always, Never);

        Assert.Equal(["2026-10-31 13:00", "2026-11-01 13:00"], Utc(result));
    }

    [Fact]
    public async Task ExpandTimed_HostileSecondlyRule_IsCapped()
    {
        var run = Task.Run(() => RecurrenceExpander.ExpandTimed(["RRULE:FREQ=SECONDLY"], Ny(2026, 10, 1, 9, 0, -4), NewYork, Always, Never));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(RecurrenceExpander.MaxOccurrences, result.Count);
    }

    [Fact]
    public async Task ExpandTimed_RuleThatNeverMatches_ReturnsQuickly()
    {
        var run = Task.Run(() => RecurrenceExpander.ExpandTimed(["RRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"], Ny(2026, 1, 1, 9, 0, -5), NewYork, Always, Never));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public void ExpandAllDay_MonthDay31_SkipsShortMonths()
    {
        var result = RecurrenceExpander.ExpandAllDay(["RRULE:FREQ=MONTHLY;BYMONTHDAY=31;COUNT=4"], new DateOnly(2026, 1, 31), DateOnly.MinValue, DateOnly.MaxValue);

        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 3, 31), new DateOnly(2026, 5, 31), new DateOnly(2026, 7, 31)], result);
    }

    [Fact]
    public void ExpandAllDay_YearlyWithExdate_SkipsExcludedYear()
    {
        var result = RecurrenceExpander.ExpandAllDay(
            ["RRULE:FREQ=YEARLY;COUNT=3", "EXDATE;VALUE=DATE:20270228"],
            new DateOnly(2026, 2, 28),
            DateOnly.MinValue,
            DateOnly.MaxValue);

        Assert.Equal([new DateOnly(2026, 2, 28), new DateOnly(2028, 2, 28)], result);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~RecurrenceExpanderTests"`
Expected: build FAILS, `RecurrenceExpander` not found.

- [ ] **Step 4: Implement RecurrenceExpander**

`src/LeafCalendar.Core/Recurrence/RecurrenceExpander.cs`:
```csharp
using System.Globalization;
using Meziantou.Framework.Scheduling;

namespace LeafCalendar.Core.Recurrence;

/// <summary>
/// Expands Google repeating events into occurrence start times.
/// </summary>
/// <remarks>
/// The RRULE math (DST, BYSETPOS, ordinal BYDAY, UNTIL, WKST) comes from Meziantou.Framework.Scheduling.
/// This class adds what Google's <c>recurrence</c> array needs on top: EXDATE and RDATE lines, a date
/// window, and safety limits. Invites come from anyone, so unreadable lines are skipped, and hostile
/// rules (for example <c>FREQ=SECONDLY</c>) stop at <see cref="MaxOccurrences"/> results or
/// <see cref="MaxScanned"/> steps.
/// </remarks>
/// <seealso href="https://www.meziantou.net/evaluating-cron-and-rrule-expressions-in-dotnet.htm"/>
/// <seealso href="https://developers.google.com/workspace/calendar/api/concepts/events-calendars#recurring_events"/>
public static class RecurrenceExpander
{
    /// <summary>Most occurrences returned for one window.</summary>
    public const int MaxOccurrences = 2000;

    /// <summary>Most occurrences walked (including ones before the window) before giving up.</summary>
    public const int MaxScanned = 100_000;

    /// <summary>
    /// Returns occurrence starts of a timed series with <c>windowStart &lt;= start &lt; windowEnd</c>, sorted and de-duplicated.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> ExpandTimed(
        IReadOnlyList<string> recurrence,
        DateTimeOffset start,
        string? timeZoneId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var zone      = FindZone(timeZoneId) ?? FixedZone(start.Offset);
        var wallClock = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var excluded  = ReadDates(recurrence, "EXDATE", zone).Select(v => v.Utc).OfType<DateTime>().ToHashSet();

        // Rule Occurrences
        var rule   = ReadRule(recurrence);
        IEnumerable<DateTimeOffset> series = rule is null ? [start] : rule.GetNextOccurrences(wallClock, zone);
        var result = new SortedSet<DateTimeOffset>();
        var scanned = 0;

        foreach (var occurrence in series)
        {
            if (occurrence >= windowEnd || ++scanned > MaxScanned || result.Count >= MaxOccurrences)
            {
                break;
            }

            if (occurrence >= windowStart && !excluded.Contains(occurrence.UtcDateTime))
            {
                result.Add(occurrence);
            }
        }

        // Extra Dates
        foreach (var utc in ReadDates(recurrence, "RDATE", zone).Select(v => v.Utc).OfType<DateTime>())
        {
            var extra = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), zone);
            if (extra >= windowStart && extra < windowEnd && !excluded.Contains(utc) && result.Count < MaxOccurrences)
            {
                result.Add(extra);
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Returns occurrence dates of an all-day series with <c>windowStart &lt;= date &lt; windowEnd</c>, sorted and de-duplicated.
    /// </summary>
    public static IReadOnlyList<DateOnly> ExpandAllDay(IReadOnlyList<string> recurrence, DateOnly start, DateOnly windowStart, DateOnly windowEnd)
    {
        var dates    = ReadDates(recurrence, "EXDATE", TimeZoneInfo.Utc).ToList();
        var excluded = dates.Select(v => v.Date ?? DateOnly.FromDateTime(v.Utc!.Value)).ToHashSet();

        // Rule Occurrences
        var rule   = ReadRule(recurrence);
        IEnumerable<DateOnly> series = rule is null ? [start] : rule.GetNextOccurrences(start.ToDateTime(TimeOnly.MinValue)).Select(DateOnly.FromDateTime);
        var result = new SortedSet<DateOnly>();
        var scanned = 0;

        foreach (var date in series)
        {
            if (date >= windowEnd || ++scanned > MaxScanned || result.Count >= MaxOccurrences)
            {
                break;
            }

            if (date >= windowStart && !excluded.Contains(date))
            {
                result.Add(date);
            }
        }

        // Extra Dates
        foreach (var value in ReadDates(recurrence, "RDATE", TimeZoneInfo.Utc))
        {
            var date = value.Date ?? DateOnly.FromDateTime(value.Utc!.Value);
            if (date >= windowStart && date < windowEnd && !excluded.Contains(date) && result.Count < MaxOccurrences)
            {
                result.Add(date);
            }
        }

        return [.. result];
    }

    static RecurrenceRule? ReadRule(IReadOnlyList<string> recurrence)
    {
        var line = recurrence.FirstOrDefault(l => l.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
        if (line is null || line.Length <= 6)
        {
            return null;
        }

        try
        {
            return RecurrenceRule.Parse(line[6..]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    // Reads "EXDATE;TZID=America/New_York:20261104T090000,20261105T090000", "EXDATE;VALUE=DATE:20261104",
    // or "RDATE:20261104T140000Z". Each value is either a date (all-day) or a UTC instant.
    static IEnumerable<(DateOnly? Date, DateTime? Utc)> ReadDates(IReadOnlyList<string> recurrence, string name, TimeZoneInfo defaultZone)
    {
        foreach (var line in recurrence)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var parameters = line[..colon].Split(';');
            if (!parameters[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDate = parameters.Contains("VALUE=DATE", StringComparer.OrdinalIgnoreCase);
            var tzid   = parameters.FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..];
            var zone   = FindZone(tzid) ?? defaultZone;

            foreach (var value in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (isDate)
                {
                    if (DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        yield return (date, null);
                    }

                    continue;
                }

                if (value.EndsWith('Z') &&
                    DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                {
                    yield return (null, utc);
                }
                else if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    // A wall-clock time inside a DST gap doesn't exist; shift it forward like the RFC does
                    var valid = zone.IsInvalidTime(local) ? local.AddHours(1) : local;
                    yield return (null, TimeZoneInfo.ConvertTimeToUtc(valid, zone));
                }
            }
        }
    }

    static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    static TimeZoneInfo FixedZone(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var id   = $"UTC{sign}{offset.Duration():hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~RecurrenceExpanderTests"`
Expected: PASS, 16 tests.

If `ExpandTimed_RuleThatNeverMatches_ReturnsQuickly` times out, the library searches forever inside one step, and the scan cap can't help. That's a denial-of-service risk from hostile invites. Stop and report it, don't work around it.

If `ExpandTimed_UnreadableRule_ReturnsStartOnly` fails because the library throws a different exception type, add that exact type to the `ReadRule` filter. Never use a bare `catch`.

- [ ] **Step 6: Confirm AOT stays clean**

Run: `pwsh tools/publish-aot.ps1`
Expected: 0 IL warnings. The expander isn't called from the app yet, but the package is in the AOT graph through Core.

- [ ] **Step 7: Add the live comparison against Google's own expansion**

Add these methods to `tests/LeafCalendar.LiveTests/Support/LiveGoogle.cs`, inside the class, above `SendAsync`:
```csharp
    /// <summary>Creates a repeating event with a start in <paramref name="timeZone"/> and returns its ID.</summary>
    public async Task<string> InsertRecurringEventAsync(string calendarId, string startLocal, string endLocal, string timeZone, string[] recurrence, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["summary"]    = "Leaf live recurrence",
            ["start"]      = new JsonObject { ["dateTime"] = startLocal, ["timeZone"] = timeZone },
            ["end"]        = new JsonObject { ["dateTime"] = endLocal, ["timeZone"] = timeZone },
            ["recurrence"] = new JsonArray([.. recurrence.Select(r => (JsonNode)r)]),
        };

        var created = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct);
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>Returns Google's own occurrence starts (UTC) for a repeating event.</summary>
    public async Task<List<DateTimeOffset>> ListInstanceStartsAsync(string calendarId, string eventId, CancellationToken ct)
    {
        var page = await SendAsync(HttpMethod.Get, $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}/instances?maxResults=250", null, ct);

        return [.. page!["items"]!.AsArray().Select(i => DateTimeOffset.Parse(i!["start"]!["dateTime"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime())];
    }
```

Add this test to `tests/LeafCalendar.LiveTests/LiveSyncTests.cs`, inside the class. Also add `using System.Text.Json;` and `using LeafCalendar.Core.Recurrence;` at the top of the file:
```csharp
    [Fact]
    public async Task Expand_WeeklyAcrossDstWithExdate_MatchesGoogleInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip("Live account not set up. Run LiveSignInTests once (see its comment).");
        }

        var google     = new LiveGoogle(live);
        var calendarId = await google.CreateTestCalendarAsync(ct);
        try
        {
            var eventId = await google.InsertRecurringEventAsync(
                calendarId,
                "2026-10-26T09:00:00",
                "2026-10-26T09:30:00",
                "America/New_York",
                ["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6", "EXDATE;TZID=America/New_York:20261104T090000"],
                ct);
            var expected = await google.ListInstanceStartsAsync(calendarId, eventId, ct);

            // Expand from what Leaf synced, exactly as the views will
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            using var raw = JsonDocument.Parse(Get(live, calendarId, eventId)!.RawJson);
            var start      = raw.RootElement.GetProperty("start");
            var recurrence = raw.RootElement.GetProperty("recurrence").EnumerateArray().Select(r => r.GetString()!).ToList();

            var actual = RecurrenceExpander.ExpandTimed(
                recurrence,
                start.GetProperty("dateTime").GetDateTimeOffset(),
                start.GetProperty("timeZone").GetString(),
                DateTimeOffset.MinValue,
                DateTimeOffset.MaxValue);

            Assert.Equal(expected, actual.Select(a => a.ToUniversalTime()));
        }
        finally
        {
            await google.DeleteCalendarAsync(calendarId, CancellationToken.None);
        }
    }
```

Run: `dotnet test tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --filter "FullyQualifiedName~Expand_"`
Expected: PASS when the live account is set up (5 occurrences, and the Nov 4 one is missing), or SKIPPED otherwise.

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props src/LeafCalendar.Core tests/LeafCalendar.Tests/RecurrenceExpanderTests.cs tests/LeafCalendar.LiveTests
git commit -m "feat(core): expand repeating events with Meziantou plus EXDATE/RDATE handling"
```

---

### Task 20: Log Redaction Check, Security Review, and Milestone Close

**Files:**
- Create: `tests/LeafCalendar.Tests/LogRedactionTests.cs`
- Modify: whatever the security review finds (each fix gets its own test).

**Interfaces:**
- Consumes: `SyncHarness` (Task 13), `SignInFlow` (Task 10), and `GoogleServices` (Task 15).
- Produces: a whole-flow redaction test and a security review record.

- [ ] **Step 1: Write the end-to-end log redaction test**

`tests/LeafCalendar.Tests/LogRedactionTests.cs`:
```csharp
using System.Net;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Http;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class LogRedactionTests : IDisposable
{
    static readonly HttpClient Browser = new();

    readonly SyncHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task FullFlow_SignInSyncFailDisconnect_LogHasNoSecretsOrEventContent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Sign-in, sync with one failing calendar, and disconnect, all logged to one file
        _h.Google.On(HttpMethod.Get, SyncHarness.FamilyEventsUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.Google.On(HttpMethod.Post, "https://oauth2.googleapis.com/token", HttpStatusCode.OK, Fixture.Read("token-response.json"), once: true);
        _h.Google.On(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo", HttpStatusCode.OK, Fixture.Read("userinfo.json"));
        _h.Google.On(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", HttpStatusCode.ServiceUnavailable, "{}");
        _h.RouteStandardGoogle();

        await using var services = new GoogleServices(new HttpClient(_h.Google), new("id.apps.googleusercontent.com", "GOCSPX-test-secret"), _h.Tokens, _h.Db.Database, _h.Log, _h.Time);
        var account = await services.CreateSignIn(url =>
        {
            var q = QueryString.Parse(url.Query);
            _ = Task.Run(() => Browser.GetAsync(new Uri(new Uri(q["redirect_uri"]), $"?code=4%2Fauth-code&state={Uri.EscapeDataString(q["state"])}")));
            return Task.CompletedTask;
        }).RunAsync(null, ct);
        await services.Sync.SyncAccountAsync(account.Id, ct);
        await services.Sync.SyncAccountAsync(account.Id, ct);
        await services.DisconnectAsync(account.Id, ct);

        var log = await File.ReadAllTextAsync(_h.LogPath, ct);

        // Every secret and every piece of event content from the fixtures
        string[] forbidden =
        [
            "ya29.", "1//", "4/auth-code", "eyJ", "GOCSPX-",
            "leaf.tester@gmail.com", "family123@group.calendar.google.com",
            "Dentist", "Company holiday", "Team standup", "Lunch with Sam", "Leaf Tester",
        ];
        Assert.All(forbidden, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
        Assert.Contains("signin.completed", log, StringComparison.Ordinal);
        Assert.Contains("sync.calendar.failed", log, StringComparison.Ordinal);
        Assert.Contains("account.revoke.failed", log, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj --filter "FullyQualifiedName~LogRedactionTests"`
Expected: PASS. If it fails, a log call leaks data. Fix the log call, not the test.

- [ ] **Step 3: Security review**

Invoke the `security-review` skill on the full Milestone 1 diff. Then check each item below and write the result (pass, or the fix made) into the task's report:

1. `git grep -n "GOCSPX-\|ya29\.\|1//" -- src` returns nothing. Secrets appear only in test fixtures, and those are fake.
2. Secrets never reach SQLite. Sign in with a real account (Task 16 app), then check the database file:
   `Select-String -Path "$env:LOCALAPPDATA\Packages\$((Get-AppxPackage LeafCalendar).PackageFamilyName)\LocalState\profiles\default\leaf.db" -Pattern "1//","ya29.","GOCSPX-" -SimpleMatch`
   Expected: no output.
3. Secrets never reach the real log. Run the same `Select-String` on `...\profiles\default\Logs\leaf.log`, adding the account email. Expected: no output.
4. Leaf listens on loopback only. While sign-in waits, `Get-NetTCPConnection -State Listen -OwningProcess (Get-Process LeafCalendar).Id` shows only `127.0.0.1`.
5. No vulnerable packages. `dotnet list LeafCalendar.slnx package --vulnerable --include-transitive` reports none.
6. No records leak secrets through `ToString`. Only `OAuthClientCredentials` and `TokenSet` hold secrets, and both override `ToString()` (tests exist).
7. `--profile` input is validated (tests exist in `LaunchOptionsTests`).

- [ ] **Step 4: Full verification**

Run: `dotnet build LeafCalendar.slnx -c Debug`
Expected: 0 warnings, 0 errors.

Run: `dotnet test tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj`
Expected: all PASS.

Run: `pwsh tools/dev-register.ps1; dotnet test tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj`
Expected: 3 PASS, 1 skipped (memory test on a Debug build).

Run: `pwsh tools/publish-aot.ps1`
Expected: 0 IL warnings.

Manual end-to-end check with the owner's real OAuth client and account:
1. Save the client.
2. Add an account.
3. Confirm the accounts list shows calendar and event counts.
4. Edit an event on calendar.google.com, wait 15 s with the window focused, and click Sync now. Leaf's counts or data update, which you can check with the `EventStore` via the app's summary line.
5. Disconnect the account and confirm the dialog. The account disappears.

- [ ] **Step 5: Commit**

```bash
git add tests/LeafCalendar.Tests/LogRedactionTests.cs
git commit -m "test: verify logs stay free of secrets and event content"
```

---

## Self-Review Notes

- **Spec coverage:** here's where each Milestone 1 item from spec Section 12 is covered.
  - Solution and packaging: Tasks 1 and 16
  - Release-only AOT: Task 16, Step 8
  - Memory budget: Task 17
  - Recurrence expansion: Task 19 (Meziantou plus the EXDATE/RDATE helper, with a live comparison against Google's instances)
  - OAuth: Tasks 1, 2, 4, 5, 6, and 10
  - REST client: Tasks 11 and 12
  - SQLite: Tasks 8 and 9
  - Initial and incremental sync: Task 13
  - Polling cadence and triggers: Tasks 14 and 16
  - Logging and redaction: Tasks 7 and 20
  - Security: Section 4 items are covered across the tasks, plus Task 20
  - The three test layers: logic (every task), UI (Tasks 16 and 17), live (Task 18)
- **Deferred on purpose (later milestones):**
  - Outbox and conflicts tables (Milestone 3)
  - The "sync 1 minute before reminders" trigger (Milestone 4)
  - The tray icon and EcoQoS (Milestone 4)
  - The settings table (Milestone 2)
  - Link and scheme allowlist (Milestone 3, where links become clickable)
