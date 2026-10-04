using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LeafCalendar.Core.Auth;
using Windows.Management.Deployment;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Launches the registered Leaf package with a throwaway profile and finds elements by automation ID.
/// </summary>
public sealed class LeafApp : IDisposable
{
    private const string PackageName = "LeafCalendar";

    private readonly UIA3Automation _automation = new();

    private LeafApp(Application app) => App = app;

    /// <summary>The running app.</summary>
    public Application App { get; }

    /// <summary>
    /// The main window (waits up to 20 s). Found by its title bar (its title is the dates on screen, which change), so
    /// onboarding, whose title bar has another ID, is never mistaken for it.
    /// </summary>
    public Window MainWindow => Retry.WhileNull(FindMainWindow, TimeSpan.FromSeconds(20)).Result
        ?? throw new InvalidOperationException("Leaf's main window didn't appear.");

    /// <summary>How many main windows are open right now (0 or 1).</summary>
    public int MainWindowCount() => App.GetAllTopLevelWindows(_automation).Count(IsMainWindow);

    /// <summary>The Settings view's navigation, in the main window in place of the calendar (waits up to 15 s).</summary>
    public AutomationElement SettingsView =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsNavigation")), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException("Leaf's Settings view didn't appear.");

    /// <summary>True while Settings shows in the main window.</summary>
    public bool IsSettingsOpen => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsNavigation")) is not null;

    /// <summary>The main window title bar's back button, while it shows (in Settings, and after a command-menu jump).</summary>
    public AutomationElement? BackButton() =>
        MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("AppTitleBar"))?.FindFirstDescendant(cf => cf.ByAutomationId("PART_BackButton"));

    /// <summary>Leaves Settings with the title bar's back button and waits (up to 10 s) for the calendar to be back.</summary>
    public void CloseSettings()
    {
        var back = Retry.WhileNull(BackButton, TimeSpan.FromSeconds(5)).Result
            ?? throw new InvalidOperationException("The title bar has no back button.");
        back.AsButton().Invoke();
        if (!Retry.WhileTrue(() => IsSettingsOpen, TimeSpan.FromSeconds(10)).Success)
        {
            throw new InvalidOperationException("Settings didn't close.");
        }
    }

    /// <summary>The first-run onboarding window (waits up to 20 s). Its window title differs from the main window's, so neither is mistaken for the other.</summary>
    public Window OnboardingWindow => TopLevelWindow("Set up Leaf Calendar", TimeSpan.FromSeconds(20))
        ?? throw new InvalidOperationException("Leaf's onboarding window didn't appear.");

    /// <summary>Waits up to 15 s for an element in the onboarding window by automation ID.</summary>
    public AutomationElement WaitInOnboarding(string automationId) =>
        Retry.WhileNull(() => OnboardingWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in onboarding.");

    /// <summary>True when an element with this ID is in the onboarding window right now (false once the window is gone).</summary>
    public bool InOnboarding(string automationId) =>
        App.GetAllTopLevelWindows(_automation).FirstOrDefault(w => NameOf(w) == "Set up Leaf Calendar")?.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null;

    /// <summary>Waits (up to 15 s) for onboarding's primary button to be enabled, then presses it.</summary>
    public void OnboardingPrimary()
    {
        var button = WaitInOnboarding("OnboardingPrimaryButton").AsButton();
        if (!Retry.WhileFalse(() => button.IsEnabled, TimeSpan.FromSeconds(15)).Success)
        {
            throw new InvalidOperationException("Onboarding's primary button stayed disabled.");
        }

        button.Invoke();
    }

    /// <summary>The step indicator's accessible name ("Step 2 of 5").</summary>
    public string OnboardingStepName => WaitInOnboarding("StepIndicator").Name;

    /// <summary>True when <paramref name="window"/> is the foreground window.</summary>
    public static bool IsForeground(Window window) => NativeMethods.GetForegroundWindow() == window.Properties.NativeWindowHandle.Value;

    /// <summary>How many of the app's windows have this title.</summary>
    public int WindowCount(string title) => App.GetAllTopLevelWindows(_automation).Count(w => NameOf(w) == title);

    /// <summary>
    /// Opens Settings from the sidebar's settings button and shows a page (<c>General</c>, <c>Calendars</c>, <c>TimeZones</c>,
    /// <c>Accounts</c>, <c>About</c>), or, while Settings already shows (the sidebar isn't on screen then), picks that page in
    /// its navigation. Settings shows in the main window, which is returned.
    /// </summary>
    public Window OpenSettings(string page = "General")
    {
        if (!IsSettingsOpen)
        {
            WaitFor("SettingsButton").AsButton().Invoke();
        }

        var settings = SettingsView;
        var item = Retry.WhileNull(() => settings.FindFirstDescendant(cf => cf.ByAutomationId($"SettingsNav_{page}")), TimeSpan.FromSeconds(15)).Result
            ?? throw new InvalidOperationException($"Settings page '{page}' isn't in the navigation.");
        item.Patterns.SelectionItem.Pattern.Select();
        return MainWindow;
    }

    /// <summary>Waits up to 15 s for an element in the Settings view by automation ID.</summary>
    public AutomationElement WaitInSettings(string automationId) =>
        Retry.WhileNull(() => SettingsView.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in Settings.");

    /// <summary>Opens a Settings expander (an account, the primary time zone) by automation ID, so its rows show.</summary>
    public void ExpandInSettings(string automationId)
    {
        var pattern = WaitInSettings(automationId).Patterns.ExpandCollapse.Pattern;
        if (pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
        {
            pattern.Expand();
        }
    }

    /// <summary>Runs "Sync now" from the command menu (Ctrl+K), on the calendar (the menu's only match is picked as it opens).</summary>
    public void SyncNow()
    {
        Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        WaitForAnywhere("CommandSearchBox").AsTextBox().Text = "sync now";
        WaitForAnywhere("CommandResult_sync");
        Keyboard.Type(VirtualKeyShort.RETURN);
        if (!Retry.WhileTrue(() => ExistsAnywhere("CommandSearchBox"), TimeSpan.FromSeconds(5)).Success)
        {
            throw new InvalidOperationException("The command menu stayed open after Sync now.");
        }
    }

    /// <summary>
    /// With the fake Google refusing connections, runs Sync now until the title bar shows Leaf is offline: syncing stays in
    /// the background, so the offline icon shows only once 3 syncs in a row couldn't reach Google, and the window's own
    /// loop only syncs every 15 s. A sync asked for while one runs is the same sync, so it keeps asking (up to 60 s).
    /// </summary>
    public void SyncUntilOffline()
    {
        var watch = Stopwatch.StartNew();
        while (!Exists("OfflineIndicator"))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new InvalidOperationException("Leaf never showed it was offline.");
            }

            SyncNow();
            Retry.WhileFalse(() => Exists("OfflineIndicator"), TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>In Settings › Accounts, opens the first account's expander and presses its Disconnect button.</summary>
    public void PressDisconnectInSettings()
    {
        var expander = Retry.WhileNull(
                () => SettingsView.FindAllDescendants().FirstOrDefault(e => e.Properties.AutomationId.ValueOrDefault?.StartsWith("AccountExpander_", StringComparison.Ordinal) == true),
                TimeSpan.FromSeconds(15)).Result
            ?? throw new InvalidOperationException("No account expander appeared in Settings.");
        expander.Patterns.ExpandCollapse.Pattern.Expand();

        var button = Retry.WhileNull(() => expander.FindFirstDescendant(cf => cf.ByName("Disconnect").And(cf.ByControlType(ControlType.Button))), TimeSpan.FromSeconds(5)).Result
            ?? throw new InvalidOperationException("The account expander has no Disconnect button.");
        button.AsButton().Invoke();
    }

    private Window? FindMainWindow() => App.GetAllTopLevelWindows(_automation).FirstOrDefault(IsMainWindow);

    private static bool IsMainWindow(Window window) => window.FindFirstDescendant(cf => cf.ByAutomationId("AppTitleBar")) is not null;

    private Window? TopLevelWindow(string title, TimeSpan timeout) =>
        Retry.WhileNull(() => App.GetAllTopLevelWindows(_automation).FirstOrDefault(w => NameOf(w) == title), timeout).Result;

    // Looked up once per run: listing every installed package is slow, and polls read the package's folder often
    private static readonly Lazy<Windows.ApplicationModel.Package> LazyPackage = new(() =>
        new PackageManager().FindPackagesForUser(string.Empty).FirstOrDefault(p => p.Id.Name == PackageName)
        ?? throw new InvalidOperationException("Leaf Calendar isn't registered. Run tools/dev-register.ps1 first."));

    /// <summary>The registered package.</summary>
    public static Windows.ApplicationModel.Package Package => LazyPackage.Value;

    /// <summary>True when the registered build is Native AOT (no managed LeafCalendar.dll).</summary>
    public static bool IsNativeAot => !File.Exists(Path.Combine(Package.InstalledLocation.Path, "LeafCalendar.dll"));

    /// <summary>Creates a unique throwaway profile name.</summary>
    public static string NewProfile() => "uitest-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Launches Leaf with <c>--profile</c> plus extra arguments.</summary>
    public static LeafApp Launch(string? profile = null, string extraArguments = "") =>
        new(Application.LaunchStoreApp($"{Package.Id.FamilyName}!App", $"--profile {profile ?? NewProfile()} {extraArguments}".Trim()));

    /// <summary>The package's local folder for <paramref name="profile"/>.</summary>
    public static string ProfileFolder(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        Package.Id.FamilyName,
        "LocalState",
        "profiles",
        profile);

    /// <summary>IDs of the Leaf processes running with <paramref name="profile"/> (found by command line).</summary>
    public static IReadOnlyList<int> ProcessIds(string profile)
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("LeafCalendar"))
        {
            using (process)
            {
                if ((CommandLine(process) + " ").Contains($"--profile {profile} ", StringComparison.Ordinal))
                {
                    ids.Add(process.Id);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Ends any Leaf still running with the profile (e.g. one a helper launched before it threw, so no one disposed it),
    /// waits for it to exit so leaf.db is closed, then deletes the profile's secrets and local files.
    /// </summary>
    public static void DeleteProfile(string profile)
    {
        foreach (var id in ProcessIds(profile))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone
            }
        }

        // Secrets (the Locker too, for profiles from before secrets moved to the profile folder)
        var folder = ProfileFolder(profile);
        new ProtectedFileTokenStore(folder).DeleteAll();
        new CredentialLockerTokenStore(profile).DeleteAll();

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Waits up to 15 s for an element by automation ID.</summary>
    public AutomationElement WaitFor(string automationId) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear.");

    /// <summary>Waits up to 15 s for an element by its accessible name.</summary>
    public AutomationElement WaitForName(string name) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element named '{name}' didn't appear.");

    /// <summary>Waits up to 15 s for an element in any of the app's windows (flyouts and dialogs can be separate).</summary>
    public AutomationElement WaitForAnywhere(string automationId) =>
        Retry.WhileNull(
            () => App.GetAllTopLevelWindows(_automation)
                .Select(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)))
                .FirstOrDefault(e => e is not null),
            TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in any window.");

    /// <summary>Every element with this ID in any of the app's windows right now (popups can be separate).</summary>
    public IReadOnlyList<AutomationElement> FindAllAnywhere(string automationId) =>
        [.. App.GetAllTopLevelWindows(_automation).SelectMany(w => w.FindAllDescendants(cf => cf.ByAutomationId(automationId)))];

    /// <summary>True when a tooltip showing <paramref name="text"/> is open in any of the app's windows.</summary>
    public bool ToolTipShows(string text) =>
        App.GetAllTopLevelWindows(_automation).Any(w => w
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ToolTip))
            .Any(t => t.Name == text || t.FindFirstDescendant(cf => cf.ByName(text)) is not null));

    /// <summary>True when an element with this ID is currently in any of the app's windows (dialogs can be separate).</summary>
    public bool ExistsAnywhere(string automationId) =>
        App.GetAllTopLevelWindows(_automation).Any(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null);

    /// <summary>The element with keyboard focus, or null.</summary>
    public AutomationElement? Focused() => _automation.FocusedElement();

    /// <summary>Every top-level window of the app (main, settings, onboarding, tray flyout, dialogs), popups included (the tray's aren't always reported as windows).</summary>
    public Window[] AllWindows() => [.. _automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(App.ProcessId)).Select(e => e.AsWindow())];

    /// <summary>True when an element with this ID is currently in the main window.</summary>
    public bool Exists(string automationId) => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null;

    /// <summary>Turns the mouse wheel over the middle of <paramref name="element"/> (positive clicks scroll up, negative down).</summary>
    public static void WheelOver(AutomationElement element, int clicks)
    {
        var box = element.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(box.X + box.Width / 2, box.Y + box.Height / 2));
        Thread.Sleep(100);
        Mouse.Scroll(clicks);
    }

    /// <summary>
    /// The screen y of the vertical center of the ink in <paramref name="region"/>: halfway between the top and bottom
    /// ink rows (see <see cref="Ink"/>), or NaN when there's none.
    /// </summary>
    public static double InkCenterY(Rectangle region)
    {
        using var ink = Ink.Capture(region);
        return ink.Measure()?.CenterY ?? double.NaN;
    }

    /// <summary>The main window's client area in screen pixels.</summary>
    public Rectangle ClientBounds
    {
        get
        {
            var hwnd = MainWindow.Properties.NativeWindowHandle.Value;
            var corner = new NativeMethods.PointStruct();
            NativeMethods.GetClientRect(hwnd, out var client);
            NativeMethods.ClientToScreen(hwnd, ref corner);
            return new Rectangle(corner.X, corner.Y, client.Right - client.Left, client.Bottom - client.Top);
        }
    }

    /// <summary>Screen pixels per DIP on the main window's monitor (1.25 at 125%).</summary>
    public double Scale => NativeMethods.GetDpiForWindow(MainWindow.Properties.NativeWindowHandle.Value) / 96.0;

    /// <summary>
    /// Links Leaf opened, oldest first (fake-Google mode records them instead of opening a browser). Read again for up
    /// to a second while Leaf still has the file open for writing.
    /// </summary>
    public static IReadOnlyList<string> LaunchedLinks(string profile)
    {
        var file = Path.Combine(ProfileFolder(profile), "launched.txt");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.Exists(file) ? File.ReadAllLines(file) : [];
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

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

    /// <summary>Waits (up to 5 s) until <paramref name="element"/> stops moving on screen, so a navigation's scroll has landed.</summary>
    public static void WaitUntilStill(AutomationElement element)
    {
        var last = element.BoundingRectangle;
        Retry.WhileFalse(
            () =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
                var now = element.BoundingRectangle;
                var settled = now == last && !element.IsOffscreen;
                last = now;
                return settled;
            },
            TimeSpan.FromSeconds(5));
    }

    /// <summary>Drags with the left button in small steps, so the app sees a real drag (holding Alt when asked).</summary>
    public static void Drag(Point start, Point end, bool alt = false)
    {
        MoveMouse(start);
        Thread.Sleep(150);
        if (alt)
        {
            Keyboard.Press(VirtualKeyShort.ALT);
        }

        Mouse.Down(MouseButton.Left);
        Thread.Sleep(150);
        for (var i = 1; i <= 12; i++)
        {
            MoveMouse(new Point(start.X + (end.X - start.X) * i / 12, start.Y + (end.Y - start.Y) * i / 12));
            Thread.Sleep(30);
        }

        Mouse.Up(MouseButton.Left);
        if (alt)
        {
            Keyboard.Release(VirtualKeyShort.ALT);
        }

        Thread.Sleep(300);
    }

    /// <summary>
    /// Moves the mouse so the app sees it (hover). FlaUI moves the cursor with SetCursorPos, which WinUI doesn't report
    /// as pointer moves, so a drag would jump from press to release; this injects a real mouse move (absolute, across
    /// all monitors).
    /// </summary>
    public static void MoveMouse(Point to)
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenLeft);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenTop);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenWidth);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenHeight);
        var x = (int)Math.Round((to.X - left) * 65535.0 / (width - 1));
        var y = (int)Math.Round((to.Y - top) * 65535.0 / (height - 1));
        NativeMethods.mouse_event(NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk, x, y, 0, 0);
    }

    /// <summary>Drags an element by (dx, dy) screen pixels, grabbing it <paramref name="fromTop"/> of the way down.</summary>
    public static void DragBy(AutomationElement element, int dx, int dy, double fromTop = 0.3, bool alt = false)
    {
        var box = element.BoundingRectangle;
        var start = new Point(box.X + box.Width / 2, box.Y + (int)(box.Height * fromTop));
        Drag(start, new Point(start.X + dx, start.Y + dy), alt);
    }

    /// <summary>True when any text in the app's windows contains <paramref name="text"/> (dialog bodies have no automation ID).</summary>
    public bool AnyTextContains(string text) =>
        App.GetAllTopLevelWindows(_automation)
            .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            .Any(e => NameOf(e).Contains(text, StringComparison.Ordinal));

    /// <summary>True when the main window is the foreground window and isn't minimized.</summary>
    public bool IsInFront =>
        NativeMethods.GetForegroundWindow() == MainWindow.Properties.NativeWindowHandle.Value
        && MainWindow.Patterns.Window.Pattern.WindowVisualState.Value != WindowVisualState.Minimized;

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

    /// <summary>True while this Leaf's invisible tray host window (the menu's and the flyout's anchor) is shown.</summary>
    public bool IsTrayHostShown()
    {
        var hwnd = nint.Zero;
        while ((hwnd = NativeMethods.FindWindowEx(nint.Zero, hwnd, null, "Leaf Calendar tray")) != nint.Zero)
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == App.ProcessId)
            {
                return NativeMethods.IsWindowVisible(hwnd);
            }
        }

        return false;
    }

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

    private AutomationElement? FindInPopups(string automationId) =>
        _automation.GetDesktop()
            .FindAllChildren(cf => cf.ByProcessId(App.ProcessId))
            .Select(w => w.Properties.AutomationId.ValueOrDefault == automationId ? w : w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)))
            .FirstOrDefault(e => e is not null);

    /// <summary>Sizes the main window (in screen pixels), so panes overflow and scroll.</summary>
    public void Resize(int width, int height)
    {
        MainWindow.Patterns.Transform.Pattern.Move(40, 40);
        MainWindow.Patterns.Transform.Pattern.Resize(width, height);
        Thread.Sleep(500);
    }

    /// <summary>Focuses the main window and presses a key chord (e.g. Control + Shift + E).</summary>
    public void Press(params VirtualKeyShort[] keys)
    {
        MainWindow.Focus();
        Keyboard.TypeSimultaneously(keys);
    }

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

    // An element's name (a window's title), or empty when it's going away while being read (a closing window or
    // dialog answers with a COM error or "not supported")
    private static string NameOf(AutomationElement element)
    {
        try
        {
            return element.Properties.Name.ValueOrDefault ?? "";
        }
        catch (Exception ex) when (ex is COMException or FlaUI.Core.Exceptions.PropertyNotSupportedException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return "";
        }
    }

    // The process's command line (ProcessCommandLineInformation), or empty when it can't be read
    private static string CommandLine(Process process)
    {
        const int ProcessCommandLineInformation = 60;
        try
        {
            _ = NativeMethods.NtQueryInformationProcess(process.Handle, ProcessCommandLineInformation, 0, 0, out var size);
            if (size <= 0)
            {
                return "";
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.NtQueryInformationProcess(process.Handle, ProcessCommandLineInformation, buffer, size, out _) != 0)
                {
                    return "";
                }

                // UNICODE_STRING: Length (bytes), MaximumLength, then the Buffer pointer
                var length = (ushort)Marshal.ReadInt16(buffer);
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return "";
        }
    }

    private static class NativeMethods
    {
        internal const uint MouseEventMove = 0x0001;
        internal const uint MouseEventVirtualDesk = 0x4000;
        internal const uint MouseEventAbsolute = 0x8000;
        internal const int VirtualScreenLeft = 76;
        internal const int VirtualScreenTop = 77;
        internal const int VirtualScreenWidth = 78;
        internal const int VirtualScreenHeight = 79;
        internal const int PrimaryScreenWidth = 0;
        internal const int PrimaryScreenHeight = 1;

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extraInfo);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint hwnd, out RectStruct rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint hwnd);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(nint hwnd, ref PointStruct point);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetDpiForWindow(nint hwnd);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RectStruct
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PointStruct
        {
            public int X;
            public int Y;
        }

        [DllImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int NtQueryInformationProcess(nint process, int informationClass, nint information, int length, out int returnLength);
    }
}
