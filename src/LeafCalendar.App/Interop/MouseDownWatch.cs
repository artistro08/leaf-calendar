using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Reports every mouse button press anywhere on screen (a low-level <c>WH_MOUSE_LL</c> hook), in physical pixels, while
/// it's started: the tray menu and flyout close on a click outside them even when Windows never told their host it
/// lost the foreground. Presses are only watched, never swallowed. One watch at a time; the hook procedure is a static
/// <c>[UnmanagedCallersOnly]</c> function (no delegate for the GC to collect).
/// </summary>
/// <remarks>
/// Windows passes every mouse move through a low-level hook and waits for it, so the hook runs on a thread of its own:
/// on the UI thread, anything that kept it busy (building the main window, layout, a collection) made the whole mouse
/// stutter. Presses are reported on that thread; the caller marshals to the UI.
/// </remarks>
internal static unsafe class MouseDownWatch
{
    private const uint WmLeftDown = 0x0201;
    private const uint WmRightDown = 0x0204;
    private const uint WmMiddleDown = 0x0207;
    private const uint WmXDown = 0x020B;

    // The watch thread's ID (0 when none runs), and where its presses go
    private static uint s_threadId;
    private static volatile Action<int, int>? s_pressed;

    /// <summary>True while a watch is installed.</summary>
    public static bool IsWatching => s_threadId != 0;

    /// <summary>
    /// Starts reporting presses to <paramref name="pressed"/>, on the watch's own thread (replacing a watch already
    /// running). False when Windows refused the hook.
    /// </summary>
    public static bool Start(Action<int, int> pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);

        Stop();
        s_pressed = pressed;

        // The Thread Reports Its ID Once The Hook Is In (0 when refused); the wait is only for SetWindowsHookEx
        uint threadId = 0;
        using var started = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var module = (HINSTANCE)(nint)PInvoke.GetModuleHandle(default(PCWSTR)).Value;
            var hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &HookProc, module, 0);
            if (hook.IsNull)
            {
                started.Set();
                return;
            }

            // A message queue first, so Stop's quit message can't arrive before there's a queue to take it
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
            threadId = PInvoke.GetCurrentThreadId();
            started.Set();

            // The hook runs while this loop waits for messages; quit ends it
            while (PInvoke.GetMessage(out _, HWND.Null, 0, 0) > 0)
            {
            }

            PInvoke.UnhookWindowsHookEx(hook);
        })
        {
            IsBackground = true,
            Name = "Leaf Calendar mouse watch",
        };
        thread.Start();
        started.Wait();

        s_threadId = threadId;
        return threadId != 0;
    }

    /// <summary>Stops the watch (safe to call when none is running). It ends on its own thread a moment later.</summary>
    public static void Stop()
    {
        if (s_threadId != 0)
        {
            PInvoke.PostThreadMessage(s_threadId, PInvoke.WM_QUIT, 0, 0);
            s_threadId = 0;
        }

        s_pressed = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
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
