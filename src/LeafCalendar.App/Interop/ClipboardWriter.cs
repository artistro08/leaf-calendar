using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Puts a package on the clipboard, trying again for a moment while another app has it open. Clipboard watchers (a
/// browser, a clipboard manager, Windows clipboard history) open the clipboard right after every change to read it, so
/// a copy made just then fails with CLIPBRD_E_CANT_OPEN even though the clipboard is free a few milliseconds later.
/// </summary>
internal static class ClipboardWriter
{
    // CLIPBRD_E_CANT_OPEN: another window has the clipboard open
    private const int CantOpen = unchecked((int)0x800401D0);

    // Up to about 200 ms in all, short enough to wait on the UI thread for a click
    private const int Tries = 10;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Sets <paramref name="package"/> as the clipboard's content, and keeps it there after Leaf Calendar closes when
    /// <paramref name="flush"/> is true.
    /// </summary>
    /// <exception cref="COMException">The clipboard stayed busy, or Windows refused it for another reason.</exception>
    public static void Set(DataPackage package, bool flush = false)
    {
        WhileBusy(() => Clipboard.SetContent(package));
        if (flush)
        {
            WhileBusy(Clipboard.Flush);
        }
    }

    // ponytail: waits on the calling (UI) thread, at most about 200 ms; make it async if a copy ever feels slow
    private static void WhileBusy(Action write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (COMException ex) when (ex.HResult == CantOpen && attempt < Tries)
            {
                Thread.Sleep(Pause);
            }
        }
    }
}
