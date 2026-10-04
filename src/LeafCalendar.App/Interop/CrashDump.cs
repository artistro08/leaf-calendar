using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LeafCalendar.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Crash dumps in the log folder while Detailed logging is on, so a crash can be traced to the line that caused it.
/// </summary>
/// <remarks>
/// Three routes, one dump per process at most (<see cref="AppLog.MaxDumps"/> kept in the folder):
/// <list type="bullet">
/// <item>Managed crashes: the App's unhandled-exception handlers call <see cref="Write(AppLog)"/>.</item>
/// <item>Native crashes outside .NET (an access violation): the process's unhandled-exception filter.</item>
/// <item>Fail-fast crashes (a XAML stowed exception, 0xC000027B, or a fail-fast from .NET) skip both, so Windows Error
/// Reporting writes those, into the same folder, through <c>WerRegisterAppLocalDump</c>.</item>
/// </list>
/// Each dump holds the threads, their stacks, and the loaded modules, not the heap. A stack can still hold bits of what
/// was on screen, which is why dumps exist only while Detailed logging is on, and turning it off deletes them.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/minidumpapiset/nf-minidumpapiset-minidumpwritedump"/>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/werapi/nf-werapi-werregisterapplocaldump"/>
internal static unsafe class CrashDump
{
    // How long a crashing thread waits for the dump writer
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);

    private static AppLog? s_log;
    private static string? s_relativeLogFolder;
    private static int s_written;

    /// <summary>
    /// Hooks the native unhandled-exception filter for <paramref name="log"/> (once) and applies Detailed logging.
    /// <paramref name="relativeLogFolder"/> is the log folder relative to the package's local folder (for Windows Error Reporting).
    /// </summary>
    public static void Install(AppLog log, string relativeLogFolder)
    {
        if (s_log is not null)
        {
            return;
        }

        s_log = log;
        s_relativeLogFolder = relativeLogFolder;
        PInvoke.SetUnhandledExceptionFilter(&OnNativeCrash);
        Apply(log.Detailed);
    }

    /// <summary>
    /// Follows the Detailed logging switch: on, Windows Error Reporting keeps fail-fast crashes in the log folder; off, it
    /// stops, and the dumps already there are deleted.
    /// </summary>
    public static void Apply(bool detailed)
    {
        if (s_log is not { } log || s_relativeLogFolder is not { } folder)
        {
            return;
        }

        var result = detailed ? PInvoke.WerRegisterAppLocalDump(folder) : PInvoke.WerUnregisterAppLocalDump();
        if (result.Failed)
        {
            log.Info("app.crash.wer", $"detailed={detailed} hresult=0x{(uint)result.Value:X8}");
        }

        if (!detailed)
        {
            log.DeleteDumps();
        }
    }

    /// <summary>Writes a dump of this process now (from a managed crash handler) when Detailed logging is on.</summary>
    public static void Write(AppLog log) => Write(log, null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnNativeCrash(EXCEPTION_POINTERS* exception)
    {
        // Nothing may escape a native crash filter; Windows goes on to end the process either way
        try
        {
            if (s_log is { Detailed: true } log)
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

    // Once per process (the XAML and AppDomain handlers can both report one crash), from a fresh thread, since a
    // process shouldn't dump the thread that's writing; the crashing thread waits a bounded time
    private static void Write(AppLog log, EXCEPTION_POINTERS* exception)
    {
        if (!log.Detailed || Interlocked.Exchange(ref s_written, 1) == 1)
        {
            return;
        }

        var crashingThread = PInvoke.GetCurrentThreadId();
        var pointers = (nint)exception;
        var writer = new Thread(() => WriteFrom(log, crashingThread, pointers)) { IsBackground = true };
        writer.Start();
        if (!writer.Join(WriteTimeout))
        {
            log.Info("app.crash.dump", "failed error=Timeout");
        }
    }

    private static void WriteFrom(AppLog log, uint crashingThread, nint pointers)
    {
        try
        {
            var path = log.NextDumpPath();
            using var file = File.Create(path);
            var info = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = crashingThread,
                ExceptionPointers = (EXCEPTION_POINTERS*)pointers,
                ClientPointers = false,
            };

            var type = MINIDUMP_TYPE.MiniDumpNormal | MINIDUMP_TYPE.MiniDumpWithThreadInfo | MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
            var ok = PInvoke.MiniDumpWriteDump(
                PInvoke.GetCurrentProcess(),
                PInvoke.GetCurrentProcessId(),
                (HANDLE)file.SafeFileHandle.DangerousGetHandle(),
                type,
                pointers == 0 ? null : &info,
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
