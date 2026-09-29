using Windows.Win32;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Releases idle memory when Leaf goes to the tray.
/// </summary>
/// <remarks>
/// A full GC runs first, then Windows is asked to page out the working set. Task Manager's number
/// drops sharply (measured ~55 MB to ~8 MB), but committed memory stays the same; pages come back
/// from the page file when used again, so the first window open after a long idle can lag slightly.
/// </remarks>
internal static class MemoryTrimmer
{
    /// <summary>Collects garbage and trims the working set.</summary>
    public static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
    }
}
