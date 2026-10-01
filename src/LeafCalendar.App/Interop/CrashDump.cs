using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LeafCalendar.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Writes a small crash dump (threads, stacks, the memory they point to, and the loaded modules; not the whole heap) to the log folder when Detailed logging
/// is on, so a crash can be traced to the line that caused it. The log keeps at most <see cref="AppLog.MaxDumps"/>.
/// Managed crashes write one from the App's unhandled-exception handlers; a native crash (an access violation outside
/// .NET) writes one from the process's unhandled-exception filter. Crashes Windows ends on the spot (fail-fast) can't be
/// caught here; Windows Error Reporting keeps those.
/// </summary>
/// <remarks>
/// A dump holds the stack memory of each thread, which can include bits of what was on screen. It's written only while
/// Detailed logging is on, and Settings says so.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/minidumpapiset/nf-minidumpapiset-minidumpwritedump"/>
internal static unsafe class CrashDump
{
    static AppLog? _log;

    /// <summary>Hooks the native unhandled-exception filter for <paramref name="log"/> (once; it acts only while Detailed logging is on).</summary>
    public static void Install(AppLog log)
    {
        if (_log is not null)
        {
            return;
        }

        _log = log;
        PInvoke.SetUnhandledExceptionFilter(&OnNativeCrash);
    }

    /// <summary>Writes a dump of this process now (from a managed crash handler) when Detailed logging is on.</summary>
    public static void Write(AppLog log) => Write(log, null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int OnNativeCrash(EXCEPTION_POINTERS* exception)
    {
        // Nothing may escape a native crash filter; Windows goes on to end the process either way
        try
        {
            if (_log is { Detailed: true } log)
            {
                log.Info("app.crash.native", $"code=0x{(uint)exception->ExceptionRecord->ExceptionCode.Value:X8}");
                Write(log, exception);
            }
        }
#pragma warning disable CA1031 // A crash filter: whatever fails, the process is ending anyway
        catch (Exception)
#pragma warning restore CA1031
        {
            // The process is ending
        }

        // EXCEPTION_CONTINUE_SEARCH: Windows Error Reporting still sees the crash
        return 0;
    }

    static void Write(AppLog log, EXCEPTION_POINTERS* exception)
    {
        if (!log.Detailed)
        {
            return;
        }

        try
        {
            var path = log.NextDumpPath();
            using var file = File.Create(path);
            var info = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId          = PInvoke.GetCurrentThreadId(),
                ExceptionPointers = exception,
                ClientPointers    = false,
            };

            var type = MINIDUMP_TYPE.MiniDumpWithIndirectlyReferencedMemory | MINIDUMP_TYPE.MiniDumpWithThreadInfo | MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
            var ok   = PInvoke.MiniDumpWriteDump(
                PInvoke.GetCurrentProcess(),
                PInvoke.GetCurrentProcessId(),
                (HANDLE)file.SafeFileHandle.DangerousGetHandle(),
                type,
                exception is null ? null : &info,
                null,
                null);
            log.Info("app.crash.dump", ok ? $"file={Path.GetFileName(path)}" : $"failed error={Marshal.GetLastPInvokeError()}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Info("app.crash.dump", $"failed error={ex.GetType().Name}");
        }
    }
}
