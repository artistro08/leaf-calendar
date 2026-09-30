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
    const string PackageName = "LeafCalendar";

    readonly UIA3Automation _automation = new();

    LeafApp(Application app) => App = app;

    /// <summary>The running app.</summary>
    public Application App { get; }

    /// <summary>The main window (waits up to 20 s). Found by title: with Settings open, it may not be the process's "main" window.</summary>
    public Window MainWindow => TopLevelWindow("Leaf Calendar", TimeSpan.FromSeconds(20))
        ?? throw new InvalidOperationException("Leaf's main window didn't appear.");

    /// <summary>The Settings window (waits up to 15 s).</summary>
    public Window SettingsWindow => TopLevelWindow("Settings", TimeSpan.FromSeconds(15))
        ?? throw new InvalidOperationException("Leaf's Settings window didn't appear.");

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

    /// <summary>Opens Settings from the sidebar's settings button and shows a page (<c>General</c>, <c>Calendars</c>, <c>TimeZones</c>, <c>Accounts</c>, <c>About</c>).</summary>
    public Window OpenSettings(string page = "General")
    {
        WaitFor("SettingsButton").AsButton().Invoke();
        var settings = SettingsWindow;
        var item     = Retry.WhileNull(() => settings.FindFirstDescendant(cf => cf.ByAutomationId($"SettingsNav_{page}")), TimeSpan.FromSeconds(15)).Result
            ?? throw new InvalidOperationException($"Settings page '{page}' isn't in the navigation.");
        item.Patterns.SelectionItem.Pattern.Select();
        return settings;
    }

    /// <summary>Waits up to 15 s for an element in the Settings window by automation ID.</summary>
    public AutomationElement WaitInSettings(string automationId) =>
        Retry.WhileNull(() => SettingsWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in Settings.");

    Window? TopLevelWindow(string title, TimeSpan timeout) =>
        Retry.WhileNull(() => App.GetAllTopLevelWindows(_automation).FirstOrDefault(w => NameOf(w) == title), timeout).Result;

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

        new CredentialLockerTokenStore(profile).DeleteAll();

        var folder = ProfileFolder(profile);

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

    /// <summary>True when an element with this ID is currently in any of the app's windows (dialogs can be separate).</summary>
    public bool ExistsAnywhere(string automationId) =>
        App.GetAllTopLevelWindows(_automation).Any(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null);

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
    /// rows holding a pixel whose lightness differs from the region's top-left pixel by more than 0.2 (NaN when there's none).
    /// </summary>
    public static double InkCenterY(Rectangle region)
    {
        using var shot       = FlaUI.Core.Capturing.Capture.Rectangle(region);
        var background       = shot.Bitmap.GetPixel(0, 0).GetBrightness();
        var rows             = Enumerable.Range(0, shot.Bitmap.Height)
            .Where(y => Enumerable.Range(0, shot.Bitmap.Width).Any(x => Math.Abs(shot.Bitmap.GetPixel(x, y).GetBrightness() - background) > 0.2f))
            .ToList();

        return rows.Count == 0 ? double.NaN : region.Y + (rows[0] + rows[^1] + 1) / 2.0;
    }

    /// <summary>Links Leaf opened, oldest first (fake-Google mode records them instead of opening a browser).</summary>
    public static IReadOnlyList<string> LaunchedLinks(string profile)
    {
        var file = Path.Combine(ProfileFolder(profile), "launched.txt");
        return File.Exists(file) ? File.ReadAllLines(file) : [];
    }

    /// <summary>Waits (up to 5 s) until <paramref name="element"/> stops moving on screen, so a navigation's scroll has landed.</summary>
    public static void WaitUntilStill(AutomationElement element)
    {
        var last = element.BoundingRectangle;
        Retry.WhileFalse(
            () =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
                var now     = element.BoundingRectangle;
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
        var left   = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenLeft);
        var top    = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenTop);
        var width  = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenWidth);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.VirtualScreenHeight);
        var x      = (int)Math.Round((to.X - left) * 65535.0 / (width - 1));
        var y      = (int)Math.Round((to.Y - top) * 65535.0 / (height - 1));
        NativeMethods.mouse_event(NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk, x, y, 0, 0);
    }

    /// <summary>Drags an element by (dx, dy) screen pixels, grabbing it <paramref name="fromTop"/> of the way down.</summary>
    public static void DragBy(AutomationElement element, int dx, int dy, double fromTop = 0.3, bool alt = false)
    {
        var box   = element.BoundingRectangle;
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
    static string NameOf(AutomationElement element)
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
    static string CommandLine(Process process)
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

    static class NativeMethods
    {
        internal const uint MouseEventMove        = 0x0001;
        internal const uint MouseEventVirtualDesk = 0x4000;
        internal const uint MouseEventAbsolute    = 0x8000;
        internal const int VirtualScreenLeft      = 76;
        internal const int VirtualScreenTop       = 77;
        internal const int VirtualScreenWidth     = 78;
        internal const int VirtualScreenHeight    = 79;

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extraInfo);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetForegroundWindow();

        [DllImport("ntdll.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int NtQueryInformationProcess(nint process, int informationClass, nint information, int length, out int returnLength);
    }
}
