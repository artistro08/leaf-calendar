using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Reads, clears, and fills the Windows clipboard from the test process (Win32, so it works on any thread). Another
/// app can hold the clipboard for a moment, so a read that can't open it returns null and callers retry.
/// </summary>
static class Clipboard
{
    const uint UnicodeText    = 13;
    const uint GlobalMoveable = 0x0002;

    /// <summary>The clipboard's text, or null when it has none or is busy.</summary>
    public static string? Text()
    {
        if (!NativeMethods.OpenClipboard(0))
        {
            return null;
        }

        try
        {
            var handle = NativeMethods.GetClipboardData(UnicodeText);
            var data   = handle == 0 ? 0 : NativeMethods.GlobalLock(handle);
            if (data == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(data);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>Empties the clipboard, so a later read can't pass on old text.</summary>
    public static void Clear()
    {
        Assert.True(NativeMethods.OpenClipboard(0), "Couldn't open the clipboard.");
        NativeMethods.EmptyClipboard();
        NativeMethods.CloseClipboard();
    }

    /// <summary>
    /// Puts <paramref name="html"/> on the clipboard as HTML Format (CF_HTML) with <paramref name="plainText"/> beside it,
    /// the way a browser or Word copy does.
    /// </summary>
    public static void SetHtml(string html, string plainText)
    {
        // CF_HTML: A Header Of UTF-8 Byte Offsets, Then The Page Around The Fragment
        const string Header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string Before = "<html><body><!--StartFragment-->";
        const string After  = "<!--EndFragment--></body></html>";
        var start       = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, Header, 0, 0, 0, 0));
        var fragment    = start + Encoding.UTF8.GetByteCount(Before);
        var fragmentEnd = fragment + Encoding.UTF8.GetByteCount(html);
        var end         = fragmentEnd + Encoding.UTF8.GetByteCount(After);
        var page        = string.Format(CultureInfo.InvariantCulture, Header, start, end, fragment, fragmentEnd) + Before + html + After;

        Assert.True(NativeMethods.OpenClipboard(0), "Couldn't open the clipboard.");
        try
        {
            NativeMethods.EmptyClipboard();
            Put(NativeMethods.RegisterClipboardFormatW("HTML Format"), [.. Encoding.UTF8.GetBytes(page), 0]);
            Put(UnicodeText, Encoding.Unicode.GetBytes(plainText + "\0"));
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // Hands the clipboard a moveable global copy of the bytes (the clipboard owns it once set)
    static void Put(uint format, byte[] bytes)
    {
        var memory = NativeMethods.GlobalAlloc(GlobalMoveable, (nuint)bytes.Length);
        var data   = NativeMethods.GlobalLock(memory);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        NativeMethods.GlobalUnlock(memory);
        Assert.NotEqual(0, NativeMethods.SetClipboardData(format, memory));
    }

    static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(nint owner);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetClipboardData(uint format);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint SetClipboardData(uint format, nint memory);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint RegisterClipboardFormatW(string format);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GlobalLock(nint memory);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(nint memory);
    }
}
