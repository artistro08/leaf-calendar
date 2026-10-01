using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Tray;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Leaf's notification-area icon and the hidden window that receives its messages (spec 8.1).
/// </summary>
/// <remarks>
/// <para>
/// WinUI has no tray icon, so this calls <c>Shell_NotifyIconW</c> through CsWin32 without marshaling, the way Layers'
/// tray icon does. The hidden window (class <c>LeafCalendarTray</c>, a tool window that's never shown) is an ordinary
/// top-level window rather than <c>HWND_MESSAGE</c>, because message-only windows miss broadcasts like
/// <c>TaskbarCreated</c>. It's created on the UI thread, whose message loop dispatches its messages, so every event here
/// is raised on the UI thread.
/// </para>
/// <para>
/// The icon uses <c>NOTIFYICON_VERSION_4</c>: a click or Enter arrives as <c>NIN_SELECT</c> / <c>NIN_KEYSELECT</c>,
/// and a right-click or the menu key as <c>WM_CONTEXTMENU</c> with the anchor point in wParam. <c>TaskbarCreated</c>
/// (Explorer restarted) adds the icon again, a theme or DPI change reloads it at the taskbar's size, and
/// <c>WM_HOTKEY</c> (the global shortcuts are registered on this window) is passed on. No exception may leave the
/// window procedure: it would end the process.
/// </para>
/// <para>
/// The art is a placeholder (the app logo); Milestone 6 draws light and dark tray glyphs and gives the icon a fixed
/// <c>NIF_GUID</c> identity, which only survives updates on a signed package.
/// </para>
/// </remarks>
internal sealed unsafe class TrayIcon : IDisposable
{
    const string WindowClass = "LeafCalendarTray";
    const uint IconId        = 1;

    // Messages And Values (declared here, so a name missing from the metadata can't break the build)
    const uint CallbackMessage     = 0x8000 + 1;
    const uint WmContextMenu       = 0x007B;
    const uint WmHotkey            = 0x0312;
    const uint WmSettingChange     = 0x001A;
    const uint WmDpiChanged        = 0x02E0;
    const uint NinSelect           = 0x0400;
    const uint NinKeySelect        = 0x0401;
    const uint NotifyIconVersion4  = 4;
    const uint IconResourceVersion = 0x00030000;

    static TrayIcon? s_current;

    readonly AppLog _log;
    readonly HWND _hwnd;
    readonly uint _taskbarCreated;
    HICON _icon;
    int _iconSize;
    string _tooltip = "Leaf Calendar";
    bool _disposed;

    /// <summary>Creates the hidden window and adds the icon. Only one may exist.</summary>
    /// <exception cref="InvalidOperationException">A tray icon already exists.</exception>
    /// <exception cref="Win32Exception">The window couldn't be created.</exception>
    public TrayIcon(AppLog log)
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon may exist.");
        }

        _log      = log;
        s_current = this;

        try
        {
            // Hidden Window (the exe's module handle is borrowed, never owned, so nothing ever frees it)
            var instance = (HINSTANCE)(nint)PInvoke.GetModuleHandle(default(PCWSTR)).Value;
            fixed (char* className = WindowClass)
            fixed (char* title = "Leaf Calendar tray")
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize        = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc   = &WindowProc,
                    hInstance     = instance,
                    lpszClassName = className,
                };
                PInvoke.RegisterClassEx(in windowClass);

                _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, title, WINDOW_STYLE.WS_OVERLAPPED, 0, 0, 0, 0, HWND.Null, HMENU.Null, instance, null);
            }

            if (_hwnd.IsNull)
            {
                throw new Win32Exception();
            }

            // Explorer Restarts Are Announced With This Message
            _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
            Add();
        }
        catch
        {
            // A Half-Built Icon Leaves Nothing Behind, So A Later Attempt Can Start Clean
            if (!_hwnd.IsNull)
            {
                PInvoke.DestroyWindow(_hwnd);
            }

            if (!_icon.IsNull)
            {
                PInvoke.DestroyIcon(_icon);
            }

            s_current = null;
            throw;
        }
    }

    /// <summary>The icon was clicked, or Enter was pressed on it.</summary>
    public event EventHandler? Invoked;

    /// <summary>The icon was right-clicked, or the menu key was pressed on it; the point is in screen pixels.</summary>
    public event EventHandler<(int X, int Y)>? ContextMenuRequested;

    /// <summary>A global shortcut registered on this window was pressed; the argument is its ID.</summary>
    public event EventHandler<int>? HotkeyPressed;

    /// <summary>The hidden window (global shortcuts are registered on it).</summary>
    public nint Handle => _hwnd;

    /// <summary>Changes the tooltip (at most 127 characters are shown).</summary>
    public void SetTooltip(string text)
    {
        if (_disposed || text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>The icon's screen rectangle, or null when the shell can't say (it's in the overflow, or Explorer is restarting).</summary>
    public PixelRect? IconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId };
        return PInvoke.Shell_NotifyIconGetRect(in id, out var rect).Succeeded ? new PixelRect(rect.left, rect.top, rect.right, rect.bottom) : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data  = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        PInvoke.DestroyWindow(_hwnd);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
        }

        s_current = null;
    }

    // Adds the icon (fails while Explorer isn't up yet at sign-in; TaskbarCreated adds it then)
    void Add()
    {
        LoadIcon();
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            _log.Info("tray.add.failed");
            return;
        }

        data.Anonymous.uVersion = NotifyIconVersion4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize           = (uint)sizeof(NOTIFYICONDATAW),
            hWnd             = _hwnd,
            uID              = IconId,
            uFlags           = flags,
            uCallbackMessage = CallbackMessage,
            hIcon            = _icon,
        };
        _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127)).CopyTo(data.szTip.AsSpan());
        return data;
    }

    // Placeholder Art: the app logo PNG, turned into an icon at the taskbar's small-icon size
    void LoadIcon()
    {
        try
        {
            var png  = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"));
            var size = IconSize();
            HICON icon;
            fixed (byte* bits = png)
            {
                icon = PInvoke.CreateIconFromResourceEx(bits, (uint)png.Length, true, IconResourceVersion, size, size, IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }

            if (icon.IsNull)
            {
                _log.Info("tray.icon.failed");
                return;
            }

            if (!_icon.IsNull)
            {
                PInvoke.DestroyIcon(_icon);
            }

            _icon     = icon;
            _iconSize = size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Info("tray.icon.failed", $"error={ex.GetType().Name}");
        }
    }

    // The taskbar's small-icon size, in pixels
    static int IconSize() => PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, TaskbarDpi());

    // WM_SETTINGCHANGE names the changed area in lParam; "ImmersiveColorSet" is a light/dark theme switch
    static bool IsColorSetChange(LPARAM lParam) =>
        lParam.Value != 0 && new string((char*)lParam.Value) == "ImmersiveColorSet";

    // The taskbar's own DPI, then 96
    static uint TaskbarDpi()
    {
        var taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        var dpi     = taskbar.IsNull ? 0u : PInvoke.GetDpiForWindow(taskbar);
        return dpi == 0 ? 96u : dpi;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_current is { } self && hwnd == self._hwnd && self.OnMessage(message, wParam, lParam))
            {
                return new LRESULT(0);
            }
        }
#pragma warning disable CA1031 // An exception leaving an unmanaged callback ends the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            s_current?._log.Error("tray.message.failed", ex);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // True when the message was handled here
    bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        // Icon Events (version 4: the event in lParam's low word, the anchor point in wParam)
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam.Value & 0xFFFF))
            {
                case NinSelect or NinKeySelect:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;

                case WmContextMenu:
                    ContextMenuRequested?.Invoke(this, ((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF)));
                    break;
            }

            return true;
        }

        // Global Shortcuts
        if (message == WmHotkey)
        {
            HotkeyPressed?.Invoke(this, (int)wParam.Value);
            return true;
        }

        // Explorer Restarted
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            Add();
            return true;
        }

        // Theme Or DPI: reload the icon at the new size (most setting changes are neither, so those are skipped)
        if (message == WmDpiChanged || (message == WmSettingChange && (IsColorSetChange(lParam) || IconSize() != _iconSize)))
        {
            LoadIcon();
            var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON);
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
        }

        return false;
    }
}
