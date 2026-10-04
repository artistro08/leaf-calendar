using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Makes the test process per-monitor DPI aware before any test runs. UI Automation reports element
/// bounds in screen pixels, and Leaf is per-monitor aware, so the mouse must move in screen pixels too;
/// a DPI-unaware test process has its cursor positions scaled, and on a 125% display the pointer (and
/// the mouse wheel) lands 1.25 times too far from the screen's corner.
/// </summary>
internal static class DpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = -4;

    [ModuleInitializer]
    internal static void Initialize() => NativeMethods.SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(nint value);
    }
}
