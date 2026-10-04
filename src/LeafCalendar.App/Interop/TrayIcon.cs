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
/// The art is today's date (<see cref="SetDay"/>), white in dark mode and black in light mode (a theme switch reloads
/// it), drawn at the taskbar's exact small-icon size (<see cref="TrayGlyph"/>), so it's never scaled.
/// </para>
/// <para>
/// The icon has a fixed identity per profile (<c>NIF_GUID</c>), so an icon left behind by a Leaf that was killed or
/// crashed is deleted when the next one adds its own. Windows ties the GUID to the signed package; when it refuses
/// the GUID (an unsigned dev build that moved), the icon goes by window and ID as before. A crash removes the icon on
/// the way out (<see cref="Dispose"/> from the App's crash handlers).
/// </para>
/// </remarks>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const string WindowClass = "LeafCalendarTray";
    private const uint IconId = 1;

    // Messages And Values (declared here, so a name missing from the metadata can't break the build)
    private const uint CallbackMessage = 0x8000 + 1;
    private const uint WmContextMenu = 0x007B;
    private const uint WmHotkey = 0x0312;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDpiChanged = 0x02E0;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NotifyIconVersion4 = 4;
    private const uint IconResourceVersion = 0x00030000;

    private static TrayIcon? s_current;

    private readonly AppLog _log;
    private readonly HWND _hwnd;
    private readonly uint _taskbarCreated;
    private HICON _icon;
    private int _iconSize;
    private readonly Guid _identity;
    private bool _byGuid = true;
    private bool _visible;
    private long _lastKeySelect;

    // Two key selects closer than this are Enter's one press
    private const long KeySelectRepeatMs = 100;
    private int _day;
    private string _tooltip = "Leaf Calendar";
    private bool _disposed;

    /// <summary>Creates the hidden window and adds the icon showing <paramref name="day"/> (1–31). Only one may exist.</summary>
    /// <exception cref="InvalidOperationException">A tray icon already exists.</exception>
    /// <exception cref="Win32Exception">The window couldn't be created.</exception>
    public TrayIcon(AppLog log, int day, string profile, bool visible = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, 31);

        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon may exist.");
        }

        _log = log;
        _day = day;
        _identity = IdentityFor(profile);
        _visible = visible;
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
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = instance,
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
            if (_visible)
            {
                Add();
            }
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

    /// <summary>
    /// Shows or hides the icon (the hidden window stays either way: the global shortcuts are registered on it). Hidden,
    /// Leaf runs in the background with nothing in the notification area.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_disposed || visible == _visible)
        {
            return;
        }

        _visible = visible;
        if (visible)
        {
            Add();
            return;
        }

        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
    }

    /// <summary>Shows another day of the month (1–31) on the icon; the same day does nothing.</summary>
    public void SetDay(int day)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, 31);
        if (_disposed || day == _day)
        {
            return;
        }

        _day = day;
        LoadIcon();
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

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
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId, guidItem = _byGuid ? _identity : Guid.Empty };
        return PInvoke.Shell_NotifyIconGetRect(in id, out var rect).Succeeded ? new PixelRect(rect.left, rect.top, rect.right, rect.bottom) : null;
    }

    /// <summary>
    /// Removes the icon and the hidden window. Safe to call twice, and from a crash handler on another thread (the icon
    /// still goes; only the window is left for the process's end to clean up).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        PInvoke.DestroyWindow(_hwnd);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
        }

        s_current = null;
    }

    // Adds the icon (fails while Explorer isn't up yet at sign-in; TaskbarCreated adds it then). An icon a killed or
    // crashed Leaf left with this identity goes first; if Windows refuses the identity, the icon goes by window and ID
    private void Add()
    {
        LoadIcon();
        const NOTIFY_ICON_DATA_FLAGS Flags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        if (_byGuid)
        {
            var stale = Data(0);
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in stale);
        }

        var data = Data(Flags);
        var added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
        if (!added && _byGuid)
        {
            _log.Info("tray.guid.refused");
            _byGuid = false;
            data = Data(Flags);
            added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
        }

        if (!added)
        {
            // Neither way worked (Explorer isn't up yet): the identity is tried again when it is
            _byGuid = true;
            _log.Info("tray.add.failed");
            return;
        }

        data.Anonymous.uVersion = NotifyIconVersion4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = _byGuid ? flags | NOTIFY_ICON_DATA_FLAGS.NIF_GUID : flags,
            guidItem = _byGuid ? _identity : Guid.Empty,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127)).CopyTo(data.szTip.AsSpan());
        return data;
    }

    // The icon's fixed identity for a profile (profiles run side by side, so each has its own): a name-based GUID
    private static Guid IdentityFor(string profile)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("LeafCalendar.TrayIcon|" + profile), hash);
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50); // version 5-style (name-based)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash[..16]);
    }

    // Today's date for the system theme (white in dark mode, black in light mode), drawn at the taskbar's small-icon size
    // (else the next size up, else the largest), turned into an icon at that size
    private void LoadIcon()
    {
        // Remembered even when the load fails, so a failed size isn't retried on every setting change
        var size = IconSize();
        _iconSize = size;
        try
        {
            var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Tray", TrayGlyph.FileName(_day, TaskbarIsLight(), size)));
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

            _icon = icon;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            _log.Info("tray.icon.failed", $"error={ex.GetType().Name}");
        }
    }

    // True when the taskbar is light (Settings > Personalization > Colors, "Choose your default Windows mode")
    private static bool TaskbarIsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    // The taskbar's small-icon size, in pixels
    private static int IconSize() => PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, TaskbarDpi());

    // WM_SETTINGCHANGE names the changed area in lParam; "ImmersiveColorSet" is a light/dark theme switch
    private static bool IsColorSetChange(LPARAM lParam) =>
        lParam.Value != 0 && new string((char*)lParam.Value) == "ImmersiveColorSet";

    // The taskbar's own DPI, then 96
    private static uint TaskbarDpi()
    {
        var taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar.IsNull ? 0u : PInvoke.GetDpiForWindow(taskbar);
        return dpi == 0 ? 96u : dpi;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
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
            s_current?._log.Info("tray.message.failed", $"error={ex.GetType().Name}");
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // True when the message was handled here
    private bool OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        // Icon Events (version 4: the event in lParam's low word, the anchor point in wParam)
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam.Value & 0xFFFF))
            {
                case NinSelect:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;

                case NinKeySelect:
                    // Enter on the icon arrives as two selects in a row (a shell quirk); the second would close what the first opened
                    var now = Environment.TickCount64;
                    if (now - _lastKeySelect >= KeySelectRepeatMs)
                    {
                        _lastKeySelect = now;
                        Invoked?.Invoke(this, EventArgs.Empty);
                    }

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

        // Explorer Restarted (its icons are gone, so the identity is tried again)
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            _byGuid = true;
            if (_visible)
            {
                Add();
            }

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
