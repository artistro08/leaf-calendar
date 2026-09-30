using System.Runtime.InteropServices;

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Reads and clears the Windows clipboard's text from the test process (Win32, so it works on any thread). Another
/// app can hold the clipboard for a moment, so a read that can't open it returns null and callers retry.
/// </summary>
static class Clipboard
{
    const uint UnicodeText = 13;

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

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GlobalLock(nint memory);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(nint memory);
    }
}
