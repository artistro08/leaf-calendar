using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace LeafCalendar.App.Interop;

/// <summary>
/// The invisible, topmost anchor window a stock flyout or menu opens from (Layers' <c>WindowStyles.MakeInvisibleHost</c>).
/// </summary>
/// <remarks>
/// A stock <c>MenuFlyout</c> or <c>Flyout</c> needs a XAML root. The host is borderless, topmost, hidden from the
/// taskbar and Alt+Tab, and fully transparent (layered alpha 0), since Windows clamps a 1 × 1 size up to its minimum.
/// The flyouts draw in popups of their own (<c>ShouldConstrainToRootBounds="False"</c>), so they show normally.
/// </remarks>
internal static class InvisibleHost
{
    /// <summary>Makes <paramref name="window"/> an invisible host.</summary>
    public static void Apply(Window window)
    {
        // Tiny, Borderless, Topmost, Hidden From Switchers
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Resize(new SizeInt32(1, 1));

        // Fully Transparent
        var hwnd  = Handle(window);
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) | WINDOW_EX_STYLE.WS_EX_LAYERED;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)style);
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 0, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    /// <summary>Where the window's client area (its XAML root) starts on screen, in physical pixels.</summary>
    public static PointInt32 ClientOrigin(Window window)
    {
        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(Handle(window), ref origin);
        return new PointInt32(origin.X, origin.Y);
    }

    /// <summary>
    /// Takes the foreground (light dismiss needs it: without it a popup closes as soon as it opens). A plain
    /// SetForegroundWindow was refused once Leaf wasn't the last app used, so the tray flyout and menu stopped opening
    /// after a click or two; this joins the front window's input thread to ask (<see cref="Foreground.Take"/>), and only
    /// runs for a tray click or a hotkey the user just pressed. False when Windows still refused.
    /// </summary>
    public static bool TakeForeground(Window window) => Foreground.Take(Handle(window));

    static HWND Handle(Window window) => new(WindowNative.GetWindowHandle(window));
}
