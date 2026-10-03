using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Reports every mouse button press anywhere on screen (a low-level <c>WH_MOUSE_LL</c> hook), in physical pixels, while
/// it's started: the tray menu and flyout close on a click outside them even when Windows never told their host it
/// lost the foreground. Presses are only watched, never swallowed. One watch at a time; it runs on the thread that
/// started it (the UI thread), while that thread pumps messages, and the hook procedure is a static
/// <c>[UnmanagedCallersOnly]</c> function (no delegate for the GC to collect).
/// </summary>
internal static unsafe class MouseDownWatch
{
    const uint WmLeftDown   = 0x0201;
    const uint WmRightDown  = 0x0204;
    const uint WmMiddleDown = 0x0207;
    const uint WmXDown      = 0x020B;

    static HHOOK s_hook;
    static Action<int, int>? s_pressed;

    /// <summary>True while a watch is installed.</summary>
    public static bool IsWatching => !s_hook.IsNull;

    /// <summary>Starts reporting presses to <paramref name="pressed"/> (replacing a watch already running). False when Windows refused.</summary>
    public static bool Start(Action<int, int> pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);

        Stop();
        s_pressed = pressed;
        var module = (HINSTANCE)(nint)PInvoke.GetModuleHandle(default(PCWSTR)).Value;
        s_hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &HookProc, module, 0);
        return !s_hook.IsNull;
    }

    /// <summary>Stops the watch (safe to call when none is running).</summary>
    public static void Stop()
    {
        // The handle goes either way: Windows drops a low-level hook on its own when its callback is too slow, and
        // unhooking that one fails; kept, it would read as still watching and no new watch would ever start
        if (!s_hook.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(s_hook);
            s_hook = default;
        }

        s_pressed = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (nCode == (int)PInvoke.HC_ACTION && s_pressed is { } pressed && (uint)wParam.Value is WmLeftDown or WmRightDown or WmMiddleDown or WmXDown)
            {
                var info = (MSLLHOOKSTRUCT*)lParam.Value;
                pressed(info->pt.X, info->pt.Y);
            }
        }
#pragma warning disable CA1031 // An exception leaving an unmanaged callback ends the process; the press just goes on
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return PInvoke.CallNextHookEx(default, nCode, wParam, lParam);
    }
}
