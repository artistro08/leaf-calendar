using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Keeps a new window off the screen until XAML has drawn it. <c>Activate</c> shows a window at once, and until XAML's
/// first frame it is solid black (measured at about 200 ms opening the main window from the tray). A cloaked window is
/// still shown, active and drawn by Windows; only the screen doesn't show it (DWM cloaking, as Windows' own apps use it).
/// </summary>
internal static unsafe class WindowCloak
{
    /// <summary>Hides (<paramref name="cloaked"/> true) or shows <paramref name="window"/> on screen without hiding the window itself.</summary>
    public static void Set(Window window, bool cloaked)
    {
        var hwnd = new HWND(Win32Interop.GetWindowFromWindowId(window.AppWindow.Id));
        BOOL value = cloaked;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
    }
}
