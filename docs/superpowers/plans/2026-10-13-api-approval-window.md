# Local API Approval Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the local API's approval notification with a standalone, always-on-top "Allow {app} to connect?" window that shows the program's icon and the app's own, and sends you back to the app after Allow.

**Architecture:** Core reads what an app sends with its first `leaf.hello` (`ApiHello`: a checked PNG icon and a checked return link) and carries it with the question (`ApiCaller`, `PendingApproval`); the window's words come from Core (`ApiApprovalText`) so they're tested. The App's pipe server builds the `ApiCaller` from the connection (process ID, path, file name) and the first line; `App.Api.cs` shows one `ApprovalWindow` at a time, closes it when the question ends, and after Allow opens the return link or brings the app's window forward. The notification path is removed.

**Tech Stack:** C# 14, .NET 10, WinUI 3 (Windows App SDK), Native AOT, xunit v3, FlaUI.

**Spec:** `docs/superpowers/specs/2026-10-13-api-approval-window-design.md` (changes section 3.5 of `docs/superpowers/specs/2026-10-13-local-api-design.md`).

## Global Constraints

- Run everything from the repo root. Build: `dotnet build LeafCalendar.slnx -c Debug`. Logic tests: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug` (one class: `--filter-class "*Name"`).
- The build treats every warning as an error; a task isn't done until the build has 0 warnings.
- Files are UTF-8 with CRLF line endings; edit in place (the Edit tool), never rewrite existing files.
- Namespaces match folders (`NamespaceLayoutTests`). File-scoped namespaces. Doc comments on every public member.
- Native AOT: `x:Bind` only, no reflection, `System.Text.Json` only through source-generated contexts.
- Visible wording is exactly the spec's section 7 table; nothing else changes wording. Untrusted names go through `DisplayText.Clean`.
- Icon rules: base64 PNG, at most 64 KB (65,536 bytes) decoded, PNG signature then `IHDR`, width and height each 1 to 256.
- Return link rules: absolute, at most 2,048 characters, no user info, scheme not `http`, `https`, `file`, `ftp`, `javascript`, `data`, `shell`, `search-ms`, `search`, nor any `ms-` scheme.
- Never log icons, return links, paths with the user name, or params; log event names, file names and reason words only.
- Do not commit; the owner commits. Never run two UI test processes at once. If the UI run pauses (`Paused:`), stop and tell the owner; never delete the pause file.

## Review Focus

1. A keystroke while the window pops up (Enter or Space) must not approve: the read-only path box holds focus. Pinned by the Enter/Esc UI test in Task 6.
2. Turning the switch off while the window is open closes it and the app is refused. Pinned by the switch-off UI test in Task 6.
3. An icon that passes Core's header check but that Windows can't decode still shows the window, with the program's icon alone. Pinned by the broken-icon UI test in Task 6.
4. A refused return link (web, file, `ms-…`, user info) is never opened, and Allow falls back to refocusing. Pinned by the `ReadReturnUrl` logic tests in Task 1.
5. A question that ends while it waits behind another window never shows. Pinned by the cancel and timeout logic tests in Task 2 (the queue skips any token `ApiApprovals.Find` no longer knows).

---

### Task 1: `ApiHello`: read the icon and return link from the first line

**Files:**
- Create: `src/LeafCalendar.Core/Api/ApiHello.cs`
- Modify: `src/LeafCalendar.Core/Api/ApiJsonContext.cs` (`HelloParams`)
- Test: `tests/LeafCalendar.Tests/ApiHelloTests.cs` (new), `tests/LeafCalendar.Tests/ApiDispatcherTests.cs`

**Interfaces:**
- Produces: `public sealed record ApiHello(ReadOnlyMemory<byte> Icon, Uri? ReturnUrl)` with `static readonly ApiHello None`, `static ApiHello Read(string line)`, `static ReadOnlyMemory<byte> ReadIcon(string? base64)` (empty when refused), `static Uri? ReadReturnUrl(string? text)`, consts `MaxIconBytes = 65_536`, `MaxIconSize = 256`, `MaxReturnUrlLength = 2048`.
- Produces: `HelloParams(string? Client = null, string? Icon = null, string? ReturnUrl = null)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LeafCalendar.Tests/ApiHelloTests.cs`:

```csharp
using System.Buffers.Binary;
using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiHelloTests
{
    // A real 1 × 1 PNG
    internal const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    public static TheoryData<string> BadIcons => new()
    {
        "not base64!",
        PngHeader(257, 16),
        PngHeader(16, 257),
        PngHeader(0, 16),
        PngHeader(16, 0),
        Convert.ToBase64String("GIF89a, not a PNG at all...."u8.ToArray()),
        Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
        PngHeader(16, 16, extra: ApiHello.MaxIconBytes),
    };

    private static string Hello(string paramsJson) => $$"""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{{paramsJson}}}""";

    // A PNG's first 24 bytes (signature, IHDR length and type, width, height), which is all Leaf checks, plus padding
    internal static string PngHeader(uint width, uint height, int extra = 0)
    {
        var bytes = new byte[24 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void Read_IconAndLink_KeepsBoth()
    {
        var hello = ApiHello.Read(Hello($$"""{"client":"Raycast","icon":"{{OnePixelPng}}","returnUrl":"raycast://extensions/leaf"}"""));

        Assert.Equal(Convert.FromBase64String(OnePixelPng), hello.Icon.ToArray());
        Assert.Equal("raycast://extensions/leaf", hello.ReturnUrl!.OriginalString);
    }

    [Fact]
    public void Read_HelloWithoutExtras_HasNeither()
    {
        var hello = ApiHello.Read(Hello("""{"client":"Raycast"}"""));

        Assert.True(hello.Icon.IsEmpty);
        Assert.Null(hello.ReturnUrl);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"events.list","params":{"icon":"x"}}""")]
    [InlineData("not json")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"surprise":1}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"icon":5}}""")]
    public void Read_NotAUsableHello_IsNone(string line)
    {
        Assert.Same(ApiHello.None, ApiHello.Read(line));
    }

    [Theory]
    [MemberData(nameof(BadIcons))]
    public void ReadIcon_BreaksARule_IsEmpty(string base64)
    {
        Assert.True(ApiHello.ReadIcon(base64).IsEmpty);
    }

    [Fact]
    public void ReadIcon_LargestAllowed_Passes()
    {
        Assert.False(ApiHello.ReadIcon(PngHeader(256, 256, extra: ApiHello.MaxIconBytes - 24)).IsEmpty);
    }

    [Theory]
    [InlineData("https://example.com/back")]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ms-settings:privacy")]
    [InlineData("MS-SETTINGS:privacy")]
    [InlineData("ms-word:ofe|u|https://example.com/a.docx")]
    [InlineData("shell:startup")]
    [InlineData("search-ms:query=x")]
    [InlineData("search:query=x")]
    [InlineData("raycast://user:secret@extensions/leaf")]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void ReadReturnUrl_Refused_IsNull(string? text)
    {
        Assert.Null(ApiHello.ReadReturnUrl(text));
    }

    [Fact]
    public void ReadReturnUrl_TooLong_IsNull()
    {
        Assert.Null(ApiHello.ReadReturnUrl("raycast://" + new string('a', ApiHello.MaxReturnUrlLength)));
    }

    [Theory]
    [InlineData("raycast://extensions/leaf")]
    [InlineData("obsidian://open?vault=notes")]
    [InlineData("slack:open")]
    public void ReadReturnUrl_AppScheme_Passes(string text)
    {
        Assert.Equal(text, ApiHello.ReadReturnUrl(text)!.OriginalString);
    }
}
```

In `tests/LeafCalendar.Tests/ApiDispatcherTests.cs`, after `Hello_ReturnsVersionsAndProfile`, add:

```csharp
    [Fact]
    public async Task Hello_WithIconAndReturnUrl_IsAccepted()
    {
        var result = await _api.CallAsync("leaf.hello", $$"""{"client":"Raycast","icon":"{{ApiHelloTests.OnePixelPng}}","returnUrl":"raycast://extensions/leaf"}""");

        Assert.Equal(1, result.GetProperty("apiVersion").GetInt32());
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`
Expected: FAIL to compile with "The name 'ApiHello' does not exist in the current context".

- [ ] **Step 3: Add the params and write `ApiHello`**

In `src/LeafCalendar.Core/Api/ApiJsonContext.cs`, replace

```csharp
/// <summary><c>leaf.hello</c>'s params.</summary>
public sealed record HelloParams(string? Client = null);
```

with

```csharp
/// <summary><c>leaf.hello</c>'s params: the app's name for the log, and (first line only) its icon and return link (<see cref="ApiHello"/>).</summary>
public sealed record HelloParams(string? Client = null, string? Icon = null, string? ReturnUrl = null);
```

Create `src/LeafCalendar.Core/Api/ApiHello.cs`:

```csharp
using System.Buffers.Binary;
using System.Collections.Frozen;

namespace LeafCalendar.Core.Api;

/// <summary>
/// What an app sends with its first <c>leaf.hello</c> for the approval window (approval window spec 3): its icon, and
/// the link that takes you back to it after Allow.
/// </summary>
/// <remarks>
/// Both are read only from a connection's first line, since Leaf asks before it runs any request, and both are untrusted:
/// the icon must be a small PNG (checked by its signature and header here; Windows decodes it in the App) and the link
/// an app's own scheme, never the web, files, scripts, data or Windows' own. Anything that breaks a rule reads as
/// nothing, so the window falls back to the program's own icon and to bringing the app's window forward. Never throws.
/// </remarks>
public sealed record ApiHello(ReadOnlyMemory<byte> Icon, Uri? ReturnUrl)
{
    /// <summary>The most bytes an icon may have, decoded.</summary>
    public const int MaxIconBytes = 65_536;

    /// <summary>The widest and tallest an icon may be, in pixels.</summary>
    public const int MaxIconSize = 256;

    /// <summary>The longest return link.</summary>
    public const int MaxReturnUrlLength = 2048;

    /// <summary>Nothing sent (or nothing usable).</summary>
    public static readonly ApiHello None = new(ReadOnlyMemory<byte>.Empty, null);

    // Schemes a return link may not use (and every ms- scheme): the web, files, scripts, data, and Windows' own
    private static readonly FrozenSet<string> RefusedSchemes = new[]
    {
        "http", "https", "file", "ftp", "javascript", "data", "shell", "search-ms", "search",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The icon and return link a connection's first line carries; <see cref="None"/> when it isn't a usable <c>leaf.hello</c> request.</summary>
    public static ApiHello Read(string line)
    {
        HelloParams p;
        try
        {
            var request = ApiMessage.Parse(line);
            if (request.Method != "leaf.hello")
            {
                return None;
            }

            p = ApiParams.Read(request.Params, ApiJsonContext.Default.HelloParams);
        }
        catch (ApiProtocolException)
        {
            return None;
        }

        var icon = ReadIcon(p.Icon);
        var link = ReadReturnUrl(p.ReturnUrl);
        return icon.IsEmpty && link is null ? None : new ApiHello(icon, link);
    }

    /// <summary>The PNG a base64 icon decodes to, or empty when it breaks a rule (too big, not a PNG, wider or taller than <see cref="MaxIconSize"/>).</summary>
    public static ReadOnlyMemory<byte> ReadIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > (MaxIconBytes + 2) / 3 * 4)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte[] png;
        try
        {
            png = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // Signature, Then The IHDR Chunk's Width And Height (big-endian, bytes 16 to 23)
        if (png.Length < 24 || png.Length > MaxIconBytes || !png.AsSpan(0, 8).SequenceEqual(PngSignature) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        return width is >= 1 and <= MaxIconSize && height is >= 1 and <= MaxIconSize ? png : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>The return link, or null when it isn't an absolute link to an app's own scheme (at most <see cref="MaxReturnUrlLength"/> characters, no user info).</summary>
    public static Uri? ReadReturnUrl(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxReturnUrlLength || !Uri.TryCreate(text, UriKind.Absolute, out var link))
        {
            return null;
        }

        var refused = RefusedSchemes.Contains(link.Scheme) || link.Scheme.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || link.UserInfo.Length > 0;
        return refused ? null : link;
    }
}
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*ApiHelloTests"` then `--filter-class "*ApiDispatcherTests"`
Expected: PASS. If `Read_IconAndLink_KeepsBoth` fails on `OriginalString`, check the link wasn't rebuilt (`ReadReturnUrl` must return the parsed `Uri` itself).

---

### Task 2: The question carries the caller; the window's words live in Core

**Files:**
- Modify: `src/LeafCalendar.Core/Api/ApiApprovals.cs`
- Create: `src/LeafCalendar.Core/Api/ApiApprovalText.cs`
- Test: `tests/LeafCalendar.Tests/ApiApprovalsTests.cs`, `tests/LeafCalendar.Tests/ApiApprovalTextTests.cs` (new)

**Interfaces:**
- Consumes: `ApiHello` (Task 1).
- Produces: `public sealed record ApiCaller(string Path, string Name, int ProcessId, ApiHello Hello)`; `public sealed record PendingApproval(string Token, ApiCaller Caller)`; `ApiApprovals.AskAsync(ApiCaller caller, Action<PendingApproval> prompt, CancellationToken ct)`; `ApiApprovalText.Headline(ApiCaller)`, `ApiApprovalText.Body` (const), `ApiApprovalText.RuntimeWarning(ApiCaller)` (null when not a runtime).

- [ ] **Step 1: Write the failing tests**

In `tests/LeafCalendar.Tests/ApiApprovalsTests.cs`, replace the `Ask` helper with:

```csharp
    private static ApiCaller Caller(string path = Path) => new(path, "Raycast", 1234, ApiHello.None);

    private Task<bool> Ask(ApiApprovals approvals, string path = Path) =>
        approvals.AskAsync(Caller(path), _prompts.Add, TestContext.Current.CancellationToken);
```

In `AskAsync_Canceled_AsksAgainNextTime`, replace `approvals.AskAsync(Path, "Raycast", _prompts.Add, stop.Token)` with `approvals.AskAsync(Caller(), _prompts.Add, stop.Token)`. In `Find_PendingToken_NamesTheApp`, replace `approvals.Find(_prompts[0].Token)!.Path` with `approvals.Find(_prompts[0].Token)!.Caller.Path`. Add:

```csharp
    [Fact]
    public void AskAsync_KeepsWhatTheAppSent()
    {
        var approvals = new ApiApprovals(_time);
        var caller = new ApiCaller(Path, "Raycast", 4321, new ApiHello(new byte[] { 1, 2, 3 }, new Uri("raycast://extensions/leaf")));

        _ = approvals.AskAsync(caller, _prompts.Add, TestContext.Current.CancellationToken);

        Assert.Same(caller, Assert.Single(_prompts).Caller);
    }
```

Create `tests/LeafCalendar.Tests/ApiApprovalTextTests.cs` (these replace the notification's tests, which Task 4 deletes):

```csharp
using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiApprovalTextTests
{
    private static ApiCaller Caller(string path, string name) => new(path, name, 1, ApiHello.None);

    [Fact]
    public void Headline_NamesTheApp()
    {
        Assert.Equal("Raycast wants to connect to Leaf", ApiApprovalText.Headline(Caller(@"C:\Apps\Raycast.exe", "Raycast")));
    }

    [Fact]
    public void Headline_DropsCharactersThatReorderText()
    {
        Assert.Equal("Raycast wants to connect to Leaf", ApiApprovalText.Headline(Caller(@"C:\Apps\Raycast.exe", "Ray\u202Ecast")));
    }

    [Fact]
    public void Body_SaysWhatTheAppCanDo()
    {
        Assert.Equal("It will be able to see your events, create and move them, and join meetings.", ApiApprovalText.Body);
    }

    [Fact]
    public void RuntimeWarning_GeneralRuntime_NamesTheFile()
    {
        Assert.Equal("Allowing node.exe lets every script that uses it connect.", ApiApprovalText.RuntimeWarning(Caller(@"C:\nodejs\node.exe", "Node.js")));
    }

    [Fact]
    public void RuntimeWarning_OrdinaryApp_IsNull()
    {
        Assert.Null(ApiApprovalText.RuntimeWarning(Caller(@"C:\Apps\Raycast.exe", "Raycast")));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet build LeafCalendar.slnx -c Debug`
Expected: FAIL to compile with "The type or namespace name 'ApiCaller' could not be found".

- [ ] **Step 3: Implement**

In `src/LeafCalendar.Core/Api/ApiApprovals.cs`, replace

```csharp
/// <summary>An approval waiting on your answer: the notification's token, and the program asking.</summary>
public sealed record PendingApproval(string Token, string Path, string Name);
```

with

```csharp
/// <summary>
/// A program asking to connect: its full path and name (from its file), its process (to bring its window back after
/// Allow), and what it sent with its first <c>leaf.hello</c>.
/// </summary>
public sealed record ApiCaller(string Path, string Name, int ProcessId, ApiHello Hello);

/// <summary>An approval waiting on your answer: its random token, and the program asking.</summary>
public sealed record PendingApproval(string Token, ApiCaller Caller);
```

In the class remarks, replace `Tokens are 128 random bits, so a notification's arguments can't be guessed.` with `Tokens are 128 random bits.` Replace the `AskAsync` summary and signature, and the body's uses of `path` and `name`:

```csharp
    /// <summary>
    /// Asks once for the caller's path (calling <paramref name="prompt"/> to show the question) and waits for the
    /// answer; false when refused before, refused now, or unanswered within <see cref="Wait"/>.
    /// </summary>
    public async Task<bool> AskAsync(ApiCaller caller, Action<PendingApproval> prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);

        var path = caller.Path;
        TaskCompletionSource<bool> answer;
```

and inside it replace `asked = new PendingApproval(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), path, name);` with `asked = new PendingApproval(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), caller);`. The rest of the method (lock, `_refused`, `_pending`, `finally`) is unchanged.

Create `src/LeafCalendar.Core/Api/ApiApprovalText.cs`:

```csharp
using LeafCalendar.Core.Tray;

namespace LeafCalendar.Core.Api;

/// <summary>
/// The approval window's words (approval window spec 7). The app's name comes from its program file, so it's cleaned
/// like event text before it's shown.
/// </summary>
public static class ApiApprovalText
{
    /// <summary>What an approved app can do.</summary>
    public const string Body = "It will be able to see your events, create and move them, and join meetings.";

    private const int MaxNameLength = 100;

    /// <summary>"{name} wants to connect to Leaf".</summary>
    public static string Headline(ApiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return $"{DisplayText.Clean(caller.Name, MaxNameLength)} wants to connect to Leaf";
    }

    /// <summary>The warning for a program that runs other scripts (Node, Python, PowerShell and the like), or null.</summary>
    public static string? RuntimeWarning(ApiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return ApiClients.IsGeneralRuntime(caller.Path) ? $"Allowing {Path.GetFileName(caller.Path)} lets every script that uses it connect." : null;
    }
}
```

`App.Api.cs` and `ToastContent.ApproveApp` still use the old record and stop compiling; Task 4 replaces both. To keep this task building, change them now:
- `src/LeafCalendar.Core/Alerts/ToastContent.cs` `ApproveApp`: use `request.Caller.Name` and `request.Caller.Path` in place of `request.Name` and `request.Path`.
- `src/LeafCalendar.App/App.Api.cs`: in `ApproveAsync`, call `_approvals!.AskAsync(new ApiCaller(path, ClientName(path), 0, ApiHello.None), request => …, ct)`; in `ShowApproval` use `request.Caller.Path`; in `AnswerApproval` use `request.Caller.Path` and `request.Caller.Name`.
- `tests/LeafCalendar.Tests/ToastContentTests.cs`: in both `ApproveApp_…` tests, build `new PendingApproval("0123456789abcdef0123456789abcdef", new ApiCaller(@"C:\Apps\Raycast.exe", "Raycast <script>", 1, ApiHello.None))` (and the `node.exe`/`Node.js` one the same way).

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), then `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug`
Expected: all pass.

---

### Task 3: The approval window, the icons, and icons on Settings rows

**Files:**
- Create: `src/LeafCalendar.App/Api/ApiIcons.cs`
- Create: `src/LeafCalendar.App/Views/Api/ApprovalWindow.xaml`, `src/LeafCalendar.App/Views/Api/ApprovalWindow.xaml.cs`
- Modify: `src/LeafCalendar.App/Controls/SettingRow.cs`, `src/LeafCalendar.App/Styles/LeafTheme.xaml` (the `LeafSettingRowStyle` template), `src/LeafCalendar.App/Views/Settings/ConnectedAppsPage.xaml.cs`
- Test: `XamlLintTests` and `NamespaceLayoutTests` run over the new files (no new test code; App code has no unit harness, UI tests come in Task 6).

**Interfaces:**
- Consumes: `PendingApproval`, `ApiCaller`, `ApiApprovalText` (Task 2).
- Produces: `internal static class ApiIcons { Task<ImageSource?> ProgramAsync(string path, uint size); Task<ImageSource?> PngAsync(ReadOnlyMemory<byte> png); }` (never throw); `public sealed partial class ApprovalWindow : Window` with `ApprovalWindow(PendingApproval request, AppTheme theme, AppLog log)`, `PendingApproval Request { get; }`, `event EventHandler<bool>? Answered` (raised at most once; closing from code raises nothing); `SettingRow.Image` (`ImageSource?`).

- [ ] **Step 1: `ApiIcons`**

Create `src/LeafCalendar.App/Api/ApiIcons.cs`:

```csharp
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace LeafCalendar.App.Api;

/// <summary>
/// Images for the local API's approval window and Settings › Connected apps: a program file's own icon (the shell's
/// thumbnail of the file, as File Explorer shows it) and an app's PNG. Both are made on the UI thread and never throw:
/// a file that's gone, or bytes Windows can't decode, give null and the caller shows the generic app icon instead.
/// </summary>
internal static class ApiIcons
{
    /// <summary>The program's icon at about <paramref name="size"/> pixels, or null.</summary>
    public static async Task<ImageSource?> ProgramAsync(string path, uint size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null)
            {
                return null;
            }

            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception)
        {
            // A missing file, no access, or no thumbnail: Windows reports each as a different exception or HRESULT
            return null;
        }
    }

    /// <summary>An app's PNG (already checked by <see cref="Core.Api.ApiHello.ReadIcon"/>) as an image, or null when Windows can't decode it.</summary>
    public static async Task<ImageSource?> PngAsync(ReadOnlyMemory<byte> png)
    {
        if (png.IsEmpty)
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.ToArray().AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            // Bytes that pass the header check but aren't a whole PNG: the decoder's HRESULT varies
            return null;
        }
    }
}
```

- [ ] **Step 2: The window's XAML**

Create `src/LeafCalendar.App/Views/Api/ApprovalWindow.xaml`:

```xml
<Window
    x:Class="LeafCalendar.App.Views.Api.ApprovalWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Leaf Calendar">

    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>

    <Grid x:Name="RootGrid" AutomationProperties.AutomationId="ApiApprovalRoot">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>
        <Grid.KeyboardAccelerators>
            <KeyboardAccelerator Key="Escape" Invoked="OnEscapeInvoked" />
        </Grid.KeyboardAccelerators>

        <!-- Title Bar (app icon and name; the window draws only Close) -->
        <TitleBar
            x:Name="AppTitleBar"
            Title="Leaf Calendar"
            IsBackButtonVisible="False"
            IsPaneToggleButtonVisible="False"
            AutomationProperties.AutomationId="ApiApprovalTitleBar">
            <TitleBar.Resources>
                <x:Double x:Key="TitleBarCompactHeight">48</x:Double>
                <x:Double x:Key="TitleBarMinDragRegionWidth">0</x:Double>
            </TitleBar.Resources>
            <TitleBar.IconSource>
                <ImageIconSource>
                    <ImageIconSource.ImageSource>
                        <!-- Decoded at its size on screen (16 DIP at this scale), so it isn't shrunk with jagged edges -->
                        <BitmapImage UriSource="ms-appx:///Assets/AppLogo.png" DecodePixelWidth="16" DecodePixelHeight="16" DecodePixelType="Logical" />
                    </ImageIconSource.ImageSource>
                </ImageIconSource>
            </TitleBar.IconSource>
        </TitleBar>

        <!-- Question (the program's icon with the app's own beside it, who asks, what it can do, and its full path) -->
        <StackPanel Grid.Row="1" Padding="24,12,24,24" Spacing="12">
            <StackPanel Orientation="Horizontal" Spacing="8">
                <Grid Width="48" Height="48">
                    <FontIcon
                        x:Name="ProgramGlyph"
                        FontSize="48"
                        Glyph="&#xECAA;"
                        AutomationProperties.AccessibilityView="Raw" />
                    <Image
                        x:Name="ProgramIcon"
                        Width="48"
                        Height="48"
                        Visibility="Collapsed"
                        AutomationProperties.AccessibilityView="Raw"
                        AutomationProperties.AutomationId="ApiApprovalProgramIcon" />
                </Grid>
                <Image
                    x:Name="AppIcon"
                    Width="24"
                    Height="24"
                    VerticalAlignment="Bottom"
                    Visibility="Collapsed"
                    AutomationProperties.AccessibilityView="Raw"
                    AutomationProperties.AutomationId="ApiApprovalAppIcon" />
            </StackPanel>
            <TextBlock
                x:Name="HeadlineText"
                Style="{StaticResource SubtitleTextBlockStyle}"
                TextWrapping="Wrap"
                AutomationProperties.HeadingLevel="Level1"
                AutomationProperties.LiveSetting="Assertive"
                AutomationProperties.AutomationId="ApiApprovalHeadline" />
            <TextBlock x:Name="BodyText" TextWrapping="Wrap" AutomationProperties.AutomationId="ApiApprovalBody" />
            <TextBlock
                x:Name="WarningText"
                Foreground="{ThemeResource SystemFillColorCautionBrush}"
                TextWrapping="Wrap"
                Visibility="Collapsed"
                AutomationProperties.AutomationId="ApiApprovalWarning" />

            <!-- Path (read-only; it holds focus when the window opens, so Enter and Space never press a button) -->
            <TextBox
                x:Name="PathBox"
                IsReadOnly="True"
                TextWrapping="Wrap"
                AutomationProperties.Name="Program path"
                AutomationProperties.AutomationId="ApiApprovalPath" />
        </StackPanel>

        <!-- Answer (side by side; neither is the default: Esc and Close refuse) -->
        <Grid
            Grid.Row="2"
            Padding="24"
            ColumnSpacing="8"
            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
            BorderThickness="0,1,0,0"
            ColumnDefinitions="*,*">
            <Button
                HorizontalAlignment="Stretch"
                Content="Allow"
                Click="OnAllowClick"
                AutomationProperties.AutomationId="ApiApprovalAllowButton" />
            <Button
                Grid.Column="1"
                HorizontalAlignment="Stretch"
                Content="Don't allow"
                Click="OnDenyClick"
                AutomationProperties.AutomationId="ApiApprovalDenyButton" />
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 3: The window's code**

Create `src/LeafCalendar.App/Views/Api/ApprovalWindow.xaml.cs`:

```csharp
using LeafCalendar.App.Api;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace LeafCalendar.App.Views.Api;

/// <summary>
/// The local API's "{app} wants to connect to Leaf" window (approval window spec 2): small, always on top, Close only,
/// centered on the monitor under the cursor and as tall as its content. It shows the program's icon (the generic app
/// icon until it loads, or when there is none) with the app's own beside it, who asks, what it can do, the runtime
/// warning when it applies, and the full path, then Allow and Don't allow. Neither button is the default: the read-only
/// path box takes focus, so Enter and Space do nothing. Esc and Close refuse.
/// </summary>
/// <remarks>
/// <see cref="Answered"/> is raised at most once, for a button, Esc, or the user closing the window. The app closes the
/// window from code when the question ends some other way (it timed out, or the API stopped); that raises nothing.
/// </remarks>
public sealed partial class ApprovalWindow : Window
{
    // Client Width In DIPs (the spec's 420); The Height Follows The Content
    private const double ClientWidth = 420;

    private readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    private readonly AppLog _log;
    private bool _answered;

    /// <summary>Creates the window for one question.</summary>
    public ApprovalWindow(PendingApproval request, AppTheme theme, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        _log = log;
        InitializeComponent();

        // Window Presenter: Fixed Size, Close Only, Always On Top
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(_presenter);
        AppWindow.SetIcon(App.IconPath);

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        MainWindow.ApplyTheme(AppWindow, RootGrid, theme);

        // Text
        var caller = request.Caller;
        HeadlineText.Text = ApiApprovalText.Headline(caller);
        BodyText.Text = ApiApprovalText.Body;
        if (ApiApprovalText.RuntimeWarning(caller) is { } warning)
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }

        PathBox.Text = caller.Path;

        // Closed By The User Refuses (Close from code raises no Closing)
        AppWindow.Closing += (_, _) => Answer(false);
        RootGrid.Loaded += OnLoaded;
        WindowPlacement.CenterOnCursorMonitor(AppWindow, ClientWidth, 320);
    }

    /// <summary>The question this window asks.</summary>
    public PendingApproval Request { get; }

    /// <summary>Raised once with true for Allow, false for Don't allow, Esc, or Close.</summary>
    public event EventHandler<bool>? Answered;

    // Fits The Height To The Content, Puts Focus On The Path, Then Loads The Icons; nothing may escape
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RootGrid.Measure(new Size(ClientWidth, double.PositiveInfinity));
            WindowPlacement.CenterOnCursorMonitor(AppWindow, ClientWidth, RootGrid.DesiredSize.Height);
            PathBox.Focus(FocusState.Programmatic);

            if (await ApiIcons.ProgramAsync(Request.Caller.Path, 96) is { } program)
            {
                ProgramIcon.Source = program;
                ProgramIcon.Visibility = Visibility.Visible;
                ProgramGlyph.Visibility = Visibility.Collapsed;
            }

            if (await ApiIcons.PngAsync(Request.Caller.Hello.Icon) is { } app)
            {
                AppIcon.Source = app;
                AppIcon.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _log.Info("api.window.failed", $"error={ex.GetType().Name}");
        }
    }

    private void OnAllowClick(object sender, RoutedEventArgs e) => Finish(true);

    private void OnDenyClick(object sender, RoutedEventArgs e) => Finish(false);

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(false);
    }

    private void Finish(bool allow)
    {
        Answer(allow);
        Close();
    }

    private void Answer(bool allow)
    {
        if (_answered)
        {
            return;
        }

        _answered = true;
        Answered?.Invoke(this, allow);
    }
}
```

- [ ] **Step 4: `SettingRow.Image`**

In `src/LeafCalendar.App/Controls/SettingRow.cs`, add `using Microsoft.UI.Xaml.Media;` and, after `IconVisibilityProperty`:

```csharp
    /// <summary>Identifies <see cref="Image"/>.</summary>
    public static readonly DependencyProperty ImageProperty =
        DependencyProperty.Register(nameof(Image), typeof(ImageSource), typeof(SettingRow), new PropertyMetadata(null, OnImageChanged));

    /// <summary>Identifies <see cref="ImageVisibility"/>.</summary>
    public static readonly DependencyProperty ImageVisibilityProperty =
        DependencyProperty.Register(nameof(ImageVisibility), typeof(Visibility), typeof(SettingRow), new PropertyMetadata(Visibility.Collapsed));
```

after the `Glyph` property:

```csharp
    /// <summary>A 24 px picture for the left edge, such as an app's icon; set it with an empty <see cref="Glyph"/>. None when null.</summary>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>Shown when <see cref="Image"/> is set (for the template).</summary>
    public Visibility ImageVisibility
    {
        get => (Visibility)GetValue(ImageVisibilityProperty);
        private set => SetValue(ImageVisibilityProperty, value);
    }
```

and at the end:

```csharp
    private static void OnImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingRow)d).ImageVisibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
```

Update the class summary's first sentence to "a card with an optional 16 px icon (or a 24 px picture), a header …". In `src/LeafCalendar.App/Styles/LeafTheme.xaml`, in `LeafSettingRowStyle`'s template, directly after the `FontIcon` add:

```xml
                            <Image
                                Width="24"
                                Height="24"
                                Margin="0,0,16,0"
                                VerticalAlignment="Center"
                                Source="{TemplateBinding Image}"
                                Visibility="{TemplateBinding ImageVisibility}"
                                AutomationProperties.AccessibilityView="Raw" />
```

- [ ] **Step 5: Icons in Settings › Connected apps**

In `src/LeafCalendar.App/Views/Settings/ConnectedAppsPage.xaml.cs`, add `using LeafCalendar.App.Api;`. In `Load()`, replace

```csharp
            AppsList.Children.Add(new SettingRow { Glyph = "", Header = app.Name, Description = app.Path, Content = remove });
```

with

```csharp
            var row = new SettingRow { Glyph = "\uECAA", Header = app.Name, Description = app.Path, Content = remove };
            AppsList.Children.Add(row);
            _ = ShowIconAsync(row, app.Path);
```

and add the method after `Load()`:

```csharp
    // The program's own icon in place of the generic one once it's read (a program that's gone keeps the generic one); never throws
    private static async Task ShowIconAsync(SettingRow row, string path)
    {
        if (await ApiIcons.ProgramAsync(path, 48) is { } icon)
        {
            row.Glyph = "";
            row.Image = icon;
        }
    }
```

Update the class summary to mention "each with its program's icon".

- [ ] **Step 6: Build and lint**

Run: `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), then `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*XamlLintTests"` and `--filter-class "*NamespaceLayoutTests"`
Expected: PASS. If `System.Runtime.InteropServices.WindowsRuntime` isn't found for `AsBuffer`, replace the write with `using var writer = new DataWriter(stream); writer.WriteBytes(png.ToArray()); await writer.StoreAsync(); writer.DetachStream();` (`Windows.Storage.Streams.DataWriter`).

---

### Task 4: Wire it in: the pipe sends the caller, the window replaces the notification, Allow goes back to the app

**Files:**
- Modify: `src/LeafCalendar.App/Api/ApiPipeServer.cs`, `src/LeafCalendar.App/App.Api.cs`, `src/LeafCalendar.App/App.xaml.cs`, `src/LeafCalendar.App/LeafServices.cs`
- Modify (remove the notification): `src/LeafCalendar.Core/Alerts/ToastArgs.cs`, `src/LeafCalendar.Core/Alerts/ToastContent.cs`, `tests/LeafCalendar.Tests/ToastContentTests.cs`

**Interfaces:**
- Consumes: `ApiHello.Read`, `ApiCaller`, `PendingApproval`, `ApiApprovals.AskAsync(ApiCaller, …)` (Tasks 1, 2); `ApprovalWindow` (Task 3).
- Produces: `ApiPipeServer(string name, ApiDispatcher dispatcher, Func<string, bool> isApproved, Func<ApiCaller, CancellationToken, Task<bool>> approve, AppLog log)`; `LeafServices.LaunchReturnLinkAsync(Uri link)`.

- [ ] **Step 1: Remove the notification from Core (and its tests)**

In `src/LeafCalendar.Core/Alerts/ToastArgs.cs`:
- delete the `AllowApp` and `DenyApp` enum members and their doc comments;
- restore the record summary to `… or account it's about.` (drop the `AllowApp`/`DenyApp` sentence);
- remove the `string? Request = null` parameter, the `Add(parts, "request", Request);` line, the `var request = …` line, `|| request is ""` from the empty check, the `|| (action is ToastAction.AllowApp or ToastAction.DenyApp && …)` condition (rejoining the `if` on one line as it was), and `, request` from the final `new ToastArgs(…)`.

In `src/LeafCalendar.Core/Alerts/ToastContent.cs`: delete `ApiGroup`, `ApprovalTag`, `ApproveApp` (with their doc comments) and `using LeafCalendar.Core.Api;`.

In `tests/LeafCalendar.Tests/ToastContentTests.cs`: delete `Parse_AllowApp_NeedsAHexRequest`, `ApproveApp_NamesTheAppAndPath_WithAllowAndDontAllow`, `ApproveApp_GeneralRuntime_Warns`, and `using LeafCalendar.Core.Api;` if nothing else uses it. (`ApiApprovalTextTests` from Task 2 now covers the words.)

- [ ] **Step 2: `LeafServices.LaunchReturnLinkAsync`**

In `src/LeafCalendar.App/LeafServices.cs`, replace the body of `LaunchAsync` after its `LinkSafety` check with `return await OpenAsync(uri);`, and add below it:

```csharp
    /// <summary>
    /// Opens the link an app sent to bring you back to it after you allowed it (approval window spec 4). The link was
    /// checked by <see cref="Core.Api.ApiHello.ReadReturnUrl"/> (an app's own scheme only), so <see cref="LinkSafety"/>,
    /// which allows only web and meeting links, doesn't apply. Recorded, logged and reported like <see cref="LaunchAsync"/>.
    /// </summary>
    public Task<bool> LaunchReturnLinkAsync(Uri link) => OpenAsync(link);

    // Opens a checked link (its canonical address); in fake-Google mode it's appended to launched.txt instead. Never throws.
    private async Task<bool> OpenAsync(Uri uri)
    {
        try
        {
            if (Options.FakeGoogle is not null)
            {
                await File.AppendAllTextAsync(Path.Combine(Paths.ProfileDirectory, "launched.txt"), uri.OriginalString + Environment.NewLine);
                return true;
            }

            // Open the canonical address (what the details panel shows), not the event's own spelling of it
            return await Launcher.LaunchUriAsync(new Uri(uri.AbsoluteUri));
        }
        catch (Exception ex)
        {
            // The address and message may hold event content, so only the scheme and the error type are logged
            Log.Info("link.launch.failed", string.Create(CultureInfo.InvariantCulture, $"scheme={uri.Scheme} error={ex.GetType().Name} hresult=0x{ex.HResult:X8}"));
            return false;
        }
    }
```

(The `try`/`catch` moves out of `LaunchAsync` into `OpenAsync` unchanged.)

- [ ] **Step 3: The pipe server builds the caller**

In `src/LeafCalendar.App/Api/ApiPipeServer.cs`:
- add `using System.Diagnostics;`;
- change the constructor's `Func<string, CancellationToken, Task<bool>> approve` to `Func<ApiCaller, CancellationToken, Task<bool>> approve`;
- in the class remarks, replace "a new one is asked once per connection" with "a new one is asked once per connection, with its icon and return link from the first line (<see cref="ApiHello"/>)";
- in `ServeAsync`, replace

```csharp
                if (ClientPath(pipe) is not { } path)
                {
```

with

```csharp
                if (Client(pipe) is not { } client)
                {
```

and add right after that block `var (processId, path) = client;`. Replace `var asked = false;` with

```csharp
                var asked = false;
                var first = true;
```

and replace the approval block

```csharp
                    // Approved Now, Or Asked Once On This Connection (an app removed in Settings is refused here)
                    if (!isApproved(path))
                    {
                        if (asked || !await approve(path, ct))
```

with

```csharp
                    // What The App Sent With Its First Line (its icon and return link), For The Question
                    var hello = first ? ApiHello.Read(line) : ApiHello.None;
                    first = false;

                    // Approved Now, Or Asked Once On This Connection (an app removed in Settings is refused here)
                    if (!isApproved(path))
                    {
                        if (asked || !await approve(new ApiCaller(path, ClientName(path), processId, hello), ct))
```

- replace `ClientPath` with:

```csharp
    // The connecting program's process ID and full path, from Windows (null when they can't be read, such as a process that already ended)
    private static unsafe (int ProcessId, string Path)? Client(NamedPipeServerStream pipe)
    {
        uint pid;
        if (!PInvoke.GetNamedPipeClientProcessId((HANDLE)pipe.SafePipeHandle.DangerousGetHandle(), &pid))
        {
            return null;
        }

        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size)
                ? ((int)pid, new string(buffer, 0, (int)size))
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
```

- move `ClientName` from `App.Api.cs` into `ApiPipeServer` unchanged (same comment, `private static string ClientName(string path)`; it needs `using System.Diagnostics;`).

- [ ] **Step 4: `App.Api.cs`: one window at a time, and back to the app**

In `src/LeafCalendar.App/App.Api.cs`:
- remove `using System.Diagnostics;` only if nothing else needs it (Step 4's `Process` does: keep it), remove `using LeafCalendar.Core.Alerts;`, add `using LeafCalendar.App.Views.Api;`, `using Windows.Win32;`, `using Windows.Win32.Foundation;`;
- add fields after `_apiStopped`:

```csharp
    private readonly Queue<PendingApproval> _waitingApprovals = new();
    private ApprovalWindow? _approvalWindow;
```

- in `StartApi`, the server is built with `ApproveAsync` unchanged (its signature changes below);
- replace `ApproveAsync`, `ShowApproval` and `AnswerApproval` with:

```csharp
    // A new program asks: its window (one on screen at a time), a 60-second wait, and the window closes once the
    // question is answered, timed out, or the API stopped
    private async Task<bool> ApproveAsync(ApiCaller caller, CancellationToken ct)
    {
        PendingApproval? shown = null;
        try
        {
            return await _approvals!.AskAsync(caller, request =>
            {
                shown = request;
                _dispatcher.TryEnqueue(() => ShowApproval(request));
            }, ct);
        }
        finally
        {
            if (shown is { } asked)
            {
                _dispatcher.TryEnqueue(() => EndApproval(asked));
            }
        }
    }

    // Queues the question; it shows once no other is on screen
    private void ShowApproval(PendingApproval request)
    {
        _waitingApprovals.Enqueue(request);
        ShowNextApproval();
    }

    // The next waiting question that's still open (one that ended while it waited never shows); a dispatcher callback, so nothing may escape
    private void ShowNextApproval()
    {
        try
        {
            while (_approvalWindow is null && _waitingApprovals.TryDequeue(out var next))
            {
                if (_approvals?.Find(next.Token) is null)
                {
                    continue;
                }

                var window = new ApprovalWindow(next, CurrentSettings().Theme, _services!.Log);
                window.Answered += (_, allow) => AnswerApproval(next, allow);
                window.Closed += (_, _) =>
                {
                    _approvalWindow = null;
                    ShowNextApproval();
                };
                _approvalWindow = window;
                window.Activate();
            }
        }
        catch (Exception ex)
        {
            _services?.Log.Info("api.prompt.failed", $"error={ex.GetType().Name}");
        }
    }

    // The question ended (answered, timed out, or the API stopped): its window closes; a queued one is skipped when its turn comes
    private void EndApproval(PendingApproval request)
    {
        if (_approvalWindow is { } window && window.Request == request)
        {
            window.Close();
        }
    }

    // Allow or Don't allow (on the UI thread): an approval is saved before the waiting connection hears it, then Allow sends you back to the app
    private void AnswerApproval(PendingApproval request, bool allow)
    {
        if (_approvals?.Find(request.Token) is null)
        {
            return;
        }

        var caller = request.Caller;
        if (allow)
        {
            UpdateSettings(s => s.WithApprovedApp(new ApprovedApp(caller.Path, caller.Name, _services!.Time.GetUtcNow())));
        }

        _approvals.Answer(request.Token, allow);
        _services!.Log.Info(allow ? "api.allowed" : "api.denied", $"client={Path.GetFileName(caller.Path)}");
        if (allow)
        {
            _ = ReturnToAppAsync(caller);
        }
    }

    // After Allow: the app's return link when it sent a valid one, else its own window comes forward (Leaf may hand focus
    // over, since you just clicked in its window; no visible window, nothing happens); nothing may escape
    private async Task ReturnToAppAsync(ApiCaller caller)
    {
        try
        {
            if (caller.Hello.ReturnUrl is { } link)
            {
                await _services!.LaunchReturnLinkAsync(link);
                _services.Log.Info("api.returned", "by=link");
                return;
            }

            using var process = Process.GetProcessById(caller.ProcessId);
            if (process.MainWindowHandle != 0)
            {
                PInvoke.SetForegroundWindow(new HWND(process.MainWindowHandle));
                _services!.Log.Info("api.returned", "by=window");
            }
        }
        catch (Exception ex)
        {
            // The app already quit (ArgumentException) or can't be read
            _services?.Log.Info("api.return.failed", $"error={ex.GetType().Name}");
        }
    }
```

- delete `ClientName` (moved in Step 3).

In `src/LeafCalendar.App/App.xaml.cs`, delete the `case ToastAction.AllowApp or ToastAction.DenyApp:` block (its comment, the two lines, and `return;`).

- [ ] **Step 5: Build and run the logic suite**

Run: `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), then `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug`
Expected: all pass. If `new HWND(process.MainWindowHandle)` doesn't compile, use `(HWND)process.MainWindowHandle`.

---

### Task 5: Documentation

**Files:**
- Modify: `docs/api.md`, `docs/superpowers/specs/2026-10-13-local-api-design.md`, `PRIVACY.md`

- [ ] **Step 1: `docs/api.md`**

Find the approval section (search for "notification") and replace its description of the Windows notification with: Leaf opens a small "{app} wants to connect to Leaf" window on top of your screen with **Allow** and **Don't allow**; your request waits up to 60 seconds for the answer. In the `leaf.hello` section, document the two new optional params with the rules from the spec's section 3 table, say both count only when `leaf.hello` is the **first** message on the connection, and add this example request:

```json
{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"client":"My extension","icon":"iVBORw0KGgo…","returnUrl":"raycast://extensions/me/leaf"}}
```

Add: "After **Allow**, Leaf opens `returnUrl`; without one it brings your program's main window to the front."

- [ ] **Step 2: Local API spec**

In `docs/superpowers/specs/2026-10-13-local-api-design.md`: in 3.2's table, change the **Approval** row to "A standalone approval window (`2026-10-13-api-approval-window-design.md`). The connection waits for the answer."; in 3.5 step 4, replace "Leaf shows a notification naming the app and its full path, with **Allow** and **Don't allow**" with "Leaf shows the approval window naming the app with its icons and full path, with **Allow** and **Don't allow** (approval window spec)"; replace "Don't allow, dismissing, or no answer in time" with "Don't allow, Esc, closing the window, or no answer in time"; replace "Only one approval notification per path at a time" with "Only one question per path at a time"; replace the paragraph starting "The prompt runs under the existing toast escaping" with "The window's name comes from the program file and is cleaned like event text; the app's icon and return link are checked as the approval window spec says."; in section 4's `leaf.hello` row, change params to `{ client?: string, icon?: string, returnUrl?: string }` and add "`icon` and `returnUrl` count only on the first line (approval window spec 3)."; in section 6, add "Each approved app's card shows its program's icon."

- [ ] **Step 3: `PRIVACY.md`**

In the connected apps section, add one sentence: "An app may send its icon and a link back to itself when it connects; Leaf shows the icon in the approval window and opens the link after you allow it, and keeps neither."

---

### Task 6: UI tests, screenshots, install

**Files:**
- Modify: `tests/LeafCalendar.UITests/Support/LeafApp.cs`, `tests/LeafCalendar.UITests/LocalApiTests.cs`, `tests/LeafCalendar.UITests/ScreenshotTour.cs`

**Interfaces:**
- Consumes: AutomationIds from Task 3: `ApiApprovalRoot`, `ApiApprovalAllowButton`, `ApiApprovalDenyButton`, `ApiApprovalPath`, `ApiApprovalAppIcon`, `ApiApprovalWarning`.
- Produces: `LeafApp.ApprovalWindow` (`Window`), `LeafApp.WaitInApproval(string automationId)`, `LeafApp.IsApprovalOpen` (`bool`).

- [ ] **Step 1: `LeafApp` helpers**

In `tests/LeafCalendar.UITests/Support/LeafApp.cs`, after `InOnboarding`, add:

```csharp
    /// <summary>The local API's approval window, waiting up to 20 s for it.</summary>
    public Window ApprovalWindow => Retry.WhileNull(FindApprovalWindow, TimeSpan.FromSeconds(20)).Result
        ?? throw new InvalidOperationException("The approval window didn't appear.");

    /// <summary>True while the approval window is open.</summary>
    public bool IsApprovalOpen => FindApprovalWindow() is not null;

    /// <summary>Waits up to 15 s for an element in the approval window by automation ID.</summary>
    public AutomationElement WaitInApproval(string automationId) =>
        Retry.WhileNull(() => ApprovalWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
            ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in the approval window.");

    // The top-level window holding the approval root (a window that closes mid-search is skipped)
    private Window? FindApprovalWindow() =>
        App.GetAllTopLevelWindows(_automation).FirstOrDefault(w =>
        {
            try
            {
                return w.FindFirstDescendant(cf => cf.ByAutomationId("ApiApprovalRoot")) is not null;
            }
            catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
                return false;
            }
        });
```

- [ ] **Step 2: `LocalApiTests`**

In `tests/LeafCalendar.UITests/LocalApiTests.cs`:
- remove `using System.Text.RegularExpressions;`, `using LeafCalendar.Core.Alerts;`, the `RequestToken` regex, and `partial` from the class;
- add `using FlaUI.Core.Input;` and `using FlaUI.Core.WindowsAPI;`;
- replace `Allow()` with:

```csharp
    // The approval window's Allow
    private static void Allow(LeafApp leaf) => leaf.WaitInApproval("ApiApprovalAllowButton").AsButton().Invoke();

    // A line sent without waiting for its reply (the reply waits on the approval)
    private static async Task SendLineAsync(Stream pipe, int id, string method, string paramsJson)
    {
        var line = $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""" + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), TestContext.Current.CancellationToken);
        await pipe.FlushAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadReplyAsync(StreamReader reader)
    {
        var reply = await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Leaf closed the connection.");
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.Clone();
    }
```

- in `ApprovedApp_ListsEventsAndJoins` and `RemovedApp_IsRefused`, replace `Allow();` with `Allow(leaf);`;
- add the new tests:

```csharp
    [Fact]
    public async Task EnterDoesNothing_EscRefuses()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var hello = SendAsync(pipe, reader, 1, "leaf.hello");
        leaf.WaitInApproval("ApiApprovalPath");
        leaf.ApprovalWindow.SetForeground();

        Keyboard.Type(VirtualKeyShort.ENTER);
        Keyboard.Type(VirtualKeyShort.SPACE);
        Thread.Sleep(500);
        Assert.True(leaf.IsApprovalOpen);

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.Equal("not_approved", (await hello).GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.True(Retry.WhileTrue(() => leaf.IsApprovalOpen, TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public async Task SwitchOff_ClosesTheWindow()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", "{}");
        leaf.WaitInApproval("ApiApprovalAllowButton");

        leaf.WaitInSettings("ApiAllowSwitch").Patterns.Toggle.Pattern.Toggle();

        Assert.True(Retry.WhileTrue(() => leaf.IsApprovalOpen, TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public async Task AppIcon_ShowsBesideTheProgramIcon()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", $$"""{"icon":"{{OnePixelPng}}"}""");

        leaf.WaitInApproval("ApiApprovalAppIcon");
        Allow(leaf);
        Assert.Equal(1, (await ReadReplyAsync(reader)).GetProperty("result").GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task BrokenIcon_ShowsTheWindowWithoutIt()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        // A PNG header Leaf's check passes, with nothing after it for Windows to decode
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 16, 0, 0, 0, 16 };
        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", $$"""{"icon":"{{Convert.ToBase64String(header)}}"}""");

        leaf.WaitInApproval("ApiApprovalAllowButton");
        Thread.Sleep(1000);
        Assert.Null(leaf.ApprovalWindow.FindFirstDescendant(cf => cf.ByAutomationId("ApiApprovalAppIcon")));
        Allow(leaf);
        await ReadReplyAsync(reader);
    }

    [Fact]
    public async Task Allow_OpensTheReturnLink()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", """{"returnUrl":"leaf-uitest://back"}""");

        Allow(leaf);
        await ReadReplyAsync(reader);
        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains("leaf-uitest://back"), TimeSpan.FromSeconds(10)).Success);
    }
```

- add the constant at the top of the class: `internal const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";`

- [ ] **Step 3: Screenshot tour**

In `tests/LeafCalendar.UITests/ScreenshotTour.cs`, add `"ConnectedApps"` to `SettingsPages` after `"Notifications"`, and add this test after `Capture_MoreScreens`:

```csharp
    /// <summary>
    /// The local API's approval window in light and dark: with the app's own icon, without one, and asked from Windows
    /// PowerShell (a general runtime, so the warning shows). Set LEAF_SCREENSHOTS to run it.
    /// </summary>
    [Fact]
    public async Task Capture_ApprovalWindow()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the approval window.");
        }

        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            var name = theme.ToString().ToLowerInvariant();
            foreach (var (shot, hello, fromPowerShell) in new[] { ("ApprovalIcon", $$"""{"icon":"{{LocalApiTests.OnePixelPng}}"}""", false), ("ApprovalPlain", "{}", false), ("ApprovalRuntime", "{}", true) })
            {
                var profile = SeededProfile.Create(new LeafSettings { Theme = theme, AllowApiConnections = true });
                _profiles.Add(profile);
                try
                {
                    using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
                    leaf.WaitFor("PeriodTitle");
                    var line = $$"""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{{hello}}}""";
                    var pipeName = Core.Api.ApiClients.PipeName(Environment.UserName, profile);

                    // Asked From Windows PowerShell (a general runtime), Or From This Test
                    using var powershell = fromPowerShell
                        ? System.Diagnostics.Process.Start("powershell.exe", $"-NoProfile -Command \"$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{pipeName}', 'InOut'); $p.Connect(10000); $w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true; $w.WriteLine('{line.Replace("\"", "\\\"", StringComparison.Ordinal)}'); Start-Sleep 60\"")
                        : null;
                    await using var pipe = fromPowerShell ? null : new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                    if (pipe is not null)
                    {
                        await pipe.ConnectAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                        await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), TestContext.Current.CancellationToken);
                    }

                    leaf.WaitInApproval("ApiApprovalAllowButton");
                    Thread.Sleep(1200);
                    CaptureAll(leaf, folder, $"more-{shot}-{name}");
                    powershell?.Kill();
                }
                finally
                {
                    LeafApp.DeleteProfile(profile);
                }
            }
        }
    }
```

- [ ] **Step 4: Install, then run the affected UI classes (never two at once)**

Run: `pwsh tools/sign-install.ps1`
Then: `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*LocalApiTests"`
Expected: 8 pass. If tests fail with `Paused:`, stop and tell the owner.
Then: `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*CommandMenuTests"` (Settings pages are listed there; it must still pass).
Then, with `LEAF_SCREENSHOTS` set to a scratch folder: `--filter-method "*ScreenshotTour.Capture_ApprovalWindow"`, and review the six shots (theme, icons side by side, warning in caution color, path box, buttons, height fits text).

- [ ] **Step 5: Manual checks for the owner (not automatable)**

1. With a real app in front (Raycast or a terminal), the window shows on top without taking focus; clicking it works.
2. Allow with no `returnUrl` brings the asking program's window back.
3. Two programs asking at once: the second window opens after the first is answered.
4. Settings › Connected apps shows each approved program's icon.

---

## Self-Review Notes

- Spec 2 (window): Task 3. Spec 3 (`leaf.hello`): Task 1. Spec 4 (back to the app): Task 4 Step 4, return link tested in Task 6. Spec 5 (Settings): Task 3 Step 5. Spec 6 (code changes, removal): Tasks 1 to 4. Spec 7 (wording): Task 2 (`ApiApprovalText`, tested) and Task 3 XAML ("Leaf Calendar", "Allow", "Don't allow", "Program path"). Spec 8 (security): Tasks 1 and 3. Spec 9 (testing): Tasks 1, 2, 6.
- Names used across tasks: `ApiHello.Read/ReadIcon/ReadReturnUrl/None`, `ApiCaller(Path, Name, ProcessId, Hello)`, `PendingApproval(Token, Caller)`, `ApiApprovalText.Headline/Body/RuntimeWarning`, `ApiIcons.ProgramAsync/PngAsync`, `ApprovalWindow(request, theme, log)`, `.Request`, `.Answered`, `LeafServices.LaunchReturnLinkAsync`, `LeafApp.ApprovalWindow/WaitInApproval/IsApprovalOpen`.
