using Windows.Win32;
using Windows.Win32.Foundation;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Brings one of Leaf's windows to the front while another app has it, such as the browser that just finished
/// sign-in. Windows only lets the app the user is working in hand over the foreground, so Leaf joins the front window's
/// input thread for the moment it asks (<c>AttachThreadInput</c>), then lets go. Used only for something the user just
/// did in Leaf (signing in, opening Leaf again, clicking its tray icon), never to steal focus on its own.
/// </summary>
internal static class Foreground
{
    /// <summary>Puts <paramref name="window"/> in front and gives it the keyboard; false when Windows still refused.</summary>
    public static bool Take(HWND window)
    {
        var front    = PInvoke.GetForegroundWindow();
        var theirs   = front.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(front, out _);
        var ours     = PInvoke.GetCurrentThreadId();
        // Never joins a hung app's thread: sharing its input state would hang Leaf with it
        var attached = theirs != 0 && theirs != ours && !PInvoke.IsHungAppWindow(front) && PInvoke.AttachThreadInput(ours, theirs, true);
        try
        {
            PInvoke.BringWindowToTop(window);
            return PInvoke.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(ours, theirs, false);
            }
        }
    }
}
