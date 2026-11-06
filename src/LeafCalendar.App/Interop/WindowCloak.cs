using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Keeps a window being shown off the screen until XAML has drawn it. Shown at once, a window is blank until XAML's
/// first frame (black on its first show, the theme's gray after a hide). A cloaked window is still shown, active and
/// drawn by Windows; only the screen doesn't show it (DWM cloaking, as Windows' own apps use it).
/// </summary>
internal static unsafe class WindowCloak
{
    /// <summary>Hides (<paramref name="cloaked"/> true) or shows <paramref name="window"/> on screen without hiding the window itself.</summary>
    public static void Set(Window window, bool cloaked)
    {
        var hwnd = InvisibleHost.Handle(window);
        BOOL value = cloaked;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
    }
}
