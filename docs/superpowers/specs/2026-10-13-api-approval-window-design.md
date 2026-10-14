# Local API Approval Window: Design

Owner request, 2026-10-13, on branch `feat/local-api` (PR #31). The local API asks "Allow {app} to connect?" in a standalone pop-up window, like 1Password's, instead of a Windows notification. The window shows one icon: the app's own when it sends one, else the connecting program's. After Allow, Leaf sends you back to the app. This changes section 3.5 of `2026-10-13-local-api-design.md`; when built, that section, `docs/api.md` and `PRIVACY.md` are updated to match.

## 1. Decisions (owner, 2026-10-13)

| Topic | Decision |
| --- | --- |
| Where the question shows | A standalone pop-up window, every time, even when Leaf is only in the tray. The calendar window isn't opened or changed. |
| Notification | Removed. The window is the only way to answer. |
| Icons | One icon (owner, revised 2026-10-13): the app's own when it sent one Windows can decode, else the program file's, else the generic app glyph. |
| Icons in Settings | The program file's icon on each approved app's card. The app-sent icon is never saved. |
| Back to the app | After Allow: the app's `returnUrl` when it sent a valid one, else Leaf hands focus back to the app's own window. |

Considered and turned down: a dialog inside the calendar window (it needs the window open, and WinUI shows one `ContentDialog` at a time, so it would collide with the editor's and conflict dialogs); the app-sent icon alone (any program can send any logo, and the program path is the only fact Leaf can check).

## 2. The Approval Window

A small top-level window of its own (`Views/Api/ApprovalWindow`), separate from the main window, the tray flyout and onboarding.

- **Size and place:** about 420 wide, as tall as its content (never a fixed height, so larger text sizes fit). Centered on the monitor the mouse is on. Always on top. Not resizable, no minimize or maximize. Mica backdrop and the stock title bar, like the onboarding window.
- **Icon:** one, at 48 × 48: the app's own when it sent one Windows can decode (even when the decode fails late, on corrupt pixels), else the program's, else the generic app glyph.
- **Text** (pending owner approval, quoted in section 7): the headline with the app's name, what the app can do, the general-runtime warning when it applies, and the full program path in small secondary text that can be selected and copied.
- **Buttons:** Allow (the accent button) and Don't allow, side by side at the bottom like a Windows dialog. Allow is off for the first 3 seconds after the window opens (owner, 2026-10-13), so a click meant for something else can't approve; Don't allow works at once.
- **No accidental approval:** no default button, and the question area (not a button) has focus when the window opens, so Enter and Space do nothing; Allow takes a click, or Tab then Enter. The question area's accessible name is the headline, so screen readers say who's asking. Esc and the title bar's close button answer Don't allow. The window can pop up while you're typing in the app that asked; a keystroke must never approve it.
- **Focus:** Windows doesn't let a background program take focus from the one you're using. The window shows on top, but may need a click before it has focus. Leaf doesn't try to get around this.
- **No answer in 60 seconds:** the window closes. The app is refused, and the refusal isn't remembered (unchanged).
- **One at a time:** when a second program asks while a window is open, its question waits and its window opens after the first is answered or times out. Its own 60 seconds still count from when it asked; a question that timed out while waiting never shows.
- **Ends with the API:** turning the switch off, or quitting Leaf, closes an open window and drops waiting questions.
- **Sound:** none. The window is on screen; the notification sound setting doesn't apply.
- **Theme and access:** follows the theme setting while open; stock theme brushes with HighContrast entries, `AutomationId`s on the window, both buttons and the icons, and the headline read out when the window opens.

## 3. What Apps Send: `leaf.hello`

`leaf.hello` takes two new optional params. Both are read only from the connection's **first** line, and only when that line is a `leaf.hello` request, because Leaf asks for approval before it runs the first request. On a later `leaf.hello`, or from an app already approved, they're accepted and ignored. Neither is ever saved or logged: the log says only whether each was `ok`, `none` (not sent) or `refused` (broke a rule), and when Windows couldn't decode an icon.

| Param | Rules | When it breaks a rule |
| --- | --- | --- |
| `icon` | Base64 PNG. At most 64 KB decoded. Starts with the PNG signature; width and height (from the PNG header) each 1 to 256. | Ignored: the window shows the program's icon. One that passes but Windows can't decode does the same. |
| `returnUrl` | An absolute URI, at most 2,048 characters, whose scheme is an app's own: not `http`, `https`, `microsoft-edge`, `file`, `ftp`, `javascript`, `data`, `shell`, `search-ms`, `search`, or any scheme starting with `ms-`. No user info. | Ignored: Leaf refocuses the app's window instead (section 4). |

`HelloParams` refuses unknown members, so both are added to it; a hello with only the old `client` keeps working.

## 4. Back to the App

Runs only after **Allow**, never after Don't allow, Esc, closing, or a timeout. It runs after the approval is saved and the waiting request is released, and nothing in it can fail the request.

1. **A valid `returnUrl`:** Leaf opens it with the Windows launcher. Not through `LinkSafety`, which only allows web links; section 3's rules are the check.
2. **Otherwise:** Leaf finds the connecting program's visible top-level window (by the client's process ID, unowned, not a tool window) and brings it to the front, restoring it first when it's minimized. Leaf may hand focus over at that moment because you just clicked in its window. No such window (a script in a terminal, or a program that already hid its window): nothing happens.

## 5. Settings › Connected apps

Each approved app's card shows its program file's icon in place of the glyph, read when the page opens; a program that's gone shows the generic app glyph. Nothing new is saved.

## 6. Code Changes

| Where | Change |
| --- | --- |
| `Core/Api/ApiHello.cs` (new) | Reads `icon` and `returnUrl` from a first line by the section 3 rules: `ApiHello.Read(string line)` gives the PNG bytes (empty when refused), the return link (or null), and a log line saying `ok`, `none` or `refused` for each. Never throws. |
| `Core/Api/ApiJsonContext.cs` | `HelloParams` gains `Icon` and `ReturnUrl`. |
| `Core/Api/ApiApprovals.cs` | `PendingApproval` carries an `ApiCaller` (path, name, process ID, and the first line's `ApiHello`). The one-question-per-path rule is unchanged. |
| `App/Api/ApiPipeServer.cs` | Passes the first line's `ApiHello` to the approval. |
| `App/Views/Api/ApprovalWindow.xaml` (new) | The window (section 2). |
| `App/App.Api.cs` | Shows windows one at a time and closes them on answer, timeout or stop; after Allow, opens the return link or refocuses the app. |
| `App/Api/ApiIcons.cs` (new) | A program file's icon (through the file's thumbnail) and an app's PNG as images, for the window and Settings. |
| `App/Controls/SettingRow.cs` and its template | An `Image` in place of the glyph, for the app icons in Settings. |
| `App/LeafServices.cs` | `LaunchReturnLinkAsync`: opens a checked return link (recorded in `launched.txt` in fake-Google mode, like other links). |
| `App/Views/Settings/ConnectedAppsPage.xaml.cs` | Program icons on the cards. |
| Refocus | `Process.MainWindowHandle` (the process's visible, unowned main window), `IsIconic` and `ShowWindow` (restore a minimized window, added to `NativeMethods.txt`), and `SetForegroundWindow`. |
| Removed | `ToastContent.ApproveApp`, `ApprovalTag`, `ApiGroup`; `ToastAction.AllowApp` and `DenyApp`; the `request` toast argument; their tests and the `HandleToast` branch. |

## 7. Wording (pending owner approval)

| Where | Text |
| --- | --- |
| Window title bar | "Leaf Calendar" |
| Headline | "{name} wants to connect to Leaf" |
| Body | "It will be able to see your events, create and move them, and join meetings." |
| General runtime | "Allowing {file} to connect will give access to all of your calendars." (owner, 2026-10-13) |
| Path | the full program path, as is |
| Buttons | "Allow", "Don't allow" |

The headline and body are the notification's already-shown strings, moved to the window; the runtime line is the owner's new wording. Names and paths shown are cleaned of characters that reorder or hide text (`DisplayText.Clean`); a name that cleans to nothing becomes the file name.

## 8. Security

| Threat | Defense |
| --- | --- |
| An app shows another app's logo | Possible: the owner chose one icon, the app's when it sends one. The full program path (from the file Windows says connected) and the name from that file stay on screen as the check. |
| A keystroke approves by accident | No default button; Enter does nothing; Esc and close refuse. |
| A hostile icon | 64 KB cap, PNG signature and header checked in Core, decoded by Windows' image decoder in the App; a failure shows the program's icon alone. |
| A hostile return link | Opened only after you chose Allow for that program. App-only schemes; web, file, script, data and Windows-internal schemes refused; length capped; no user info. An approved program can already open any link itself, so this adds no new reach. |
| A flood of questions | One window at a time per program path (unchanged), one on screen at a time, 60-second limit, Don't allow remembered until Leaf restarts (unchanged). |

## 9. Testing

- **Logic tests (Core):** `ApiHello`: a valid icon and link; each `icon` rule (bad base64, too big, not PNG, too wide, zero size); each refused scheme, a relative link, user info, too long; a first line that isn't `leaf.hello`; `leaf.hello` with `icon` and `returnUrl` passes params checks.
- **UI tests:** `LocalApiTests` approves through the window's Allow button instead of `--toast-action`; new cases: Enter does nothing and Esc refuses (`not_approved`); turning the switch off closes the window; an app icon shows in place of the program's, no icon or one Windows can't decode shows the program's, and Allow is off for its first seconds; Allow opens a `returnUrl` (read from `launched.txt` in fake-Google mode). The 60-second close is covered by the logic tests (a UI test would wait a real minute); refocusing another app is checked by hand (a test can't safely watch another app take focus).
- **Screenshots:** `ScreenshotTour` captures the window in light and dark, with and without an app icon, and with the runtime warning (asked from Windows PowerShell, a general runtime).
- **Lint:** the new XAML passes `XamlLintTests`.

## 10. Out of Scope

- Saving the app's icon, or showing it in Settings.
- Changing the approval rules themselves (what's approved, the 60 seconds, what's remembered).
- A return link for apps already approved (they never see the window).
