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

    /// <summary>The package's local folder for <paramref name="profile"/>.</summary>
    public static string ProfileFolder(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        Package.Id.FamilyName,
        "LocalState",
        "profiles",
        profile);

    /// <summary>Deletes a profile's secrets and local files.</summary>
    public static void DeleteProfile(string profile)
    {
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
            .Any(e => e.Name.Contains(text, StringComparison.Ordinal));

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
    }
}
