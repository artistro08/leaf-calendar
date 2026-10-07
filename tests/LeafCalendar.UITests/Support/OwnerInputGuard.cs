using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using LeafCalendar.UITests.Support;
using Xunit.v3;

[assembly: PauseWhenOwnerMovesMouse]

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Watches for the owner's own hand on the mouse while UI tests run. FlaUI sends its input with SendInput, which Windows
/// marks as injected; a real mouse is not, so a low-level mouse hook can tell them apart. Real input pauses the run by
/// writing a pause file, and the run stays paused until the owner deletes it.
/// </summary>
internal static class OwnerInputGuard
{
    private const int MouseLowLevelHook = 14;
    private const int MouseMove = 0x0200;
    private const uint InjectedFlag = 0x01;

    // A real move shorter than this from where the cursor last settled is jitter, not the owner moving the mouse
    private const int JitterPixels = 3;

    private static readonly Lock StartLock = new();

    // Kept in a field so the garbage collector never frees the delegate Windows calls back into
    private static HookProc? s_hookProc;
    private static bool s_started;
    private static int s_interrupted;
    private static int s_anchorX;
    private static int s_anchorY;
    private static bool s_hasAnchor;
    private static int s_seenX;
    private static int s_seenY;

    /// <summary>The file whose existence means the run is paused. Only the owner resumes, by deleting it.</summary>
    public static string PauseFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafCalendar.UITests", "paused");

    /// <summary>Starts the hook thread once per test process and waits until the hook is in place.</summary>
    public static void Start()
    {
        lock (StartLock)
        {
            if (s_started)
            {
                return;
            }

            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Owner input guard" };
            thread.Start();
            ready.Wait();
            s_started = true;
        }
    }

    /// <summary>Clears the interrupted flag and says whether real mouse input happened since the last call.</summary>
    public static bool TakeInterrupted() => Interlocked.Exchange(ref s_interrupted, 0) == 1;

    /// <summary>Writes the pause file unless it is already there.</summary>
    public static void WritePauseFile()
    {
        if (File.Exists(PauseFile))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PauseFile)!);
        File.WriteAllText(PauseFile, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " The owner moved the mouse.");
    }

    // Installs the hook and pumps messages on this thread, which a low-level hook needs to be called
    private static void Run(ManualResetEventSlim ready)
    {
        s_hookProc = OnMouse;
        var hook = NativeMethods.SetWindowsHookExW(MouseLowLevelHook, s_hookProc, NativeMethods.GetModuleHandleW(null), 0);
        ready.Set();
        if (hook == 0)
        {
            Console.Error.WriteLine("The pause guard is off: the mouse hook couldn't be installed, so moving the mouse won't pause UI tests.");
            return;
        }

        while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(in message);
            NativeMethods.DispatchMessageW(in message);
        }
    }

    // Called by Windows for every mouse event; it must return quickly, so the file is written on the thread pool.
    // A real move is measured from an anchor: where the cursor settled after FlaUI last moved it. FlaUI moves the cursor
    // with SetCursorPos too, which this hook never sees, so before judging an event the cursor is read with
    // GetCursorPos (in the hook it still shows where the cursor was before this event). When that differs from the
    // last point this hook saw, something unseen moved the cursor, so the anchor moves there; a 1 px twitch right after
    // a FlaUI move is then measured from where FlaUI left the cursor, not from where it was before
    private static nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MouseHookInfo>(data);
            var injected = (info.Flags & InjectedFlag) != 0;
            var isMove = message == MouseMove;
            if (s_hasAnchor && NativeMethods.GetCursorPos(out var cursor) && (cursor.X != s_seenX || cursor.Y != s_seenY))
            {
                (s_anchorX, s_anchorY) = (cursor.X, cursor.Y);
            }

            (s_seenX, s_seenY) = (info.X, info.Y);
            if (injected || !s_hasAnchor)
            {
                // Injected moves (and the first event seen) set where the cursor settled, so a real move is measured from there
                if (isMove || !s_hasAnchor)
                {
                    (s_anchorX, s_anchorY, s_hasAnchor) = (info.X, info.Y, true);
                }

                if (!injected && !isMove)
                {
                    Trip();
                }
            }
            else if (!isMove || Math.Abs(info.X - s_anchorX) >= JitterPixels || Math.Abs(info.Y - s_anchorY) >= JitterPixels)
            {
                Trip();
            }
        }

        return NativeMethods.CallNextHookEx(0, code, message, data);
    }

    // Marks the run interrupted and writes the pause file off the hook thread
    private static void Trip()
    {
        if (Interlocked.Exchange(ref s_interrupted, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    WritePauseFile();
                }
                catch (IOException)
                {
                    // The after-test check writes it again
                }
            });
        }
    }

    private delegate nint HookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint SetWindowsHookExW(int hookId, HookProc proc, nint module, uint threadId);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetMessageW(out Message message, nint window, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(in Message message);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint DispatchMessageW(in Message message);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetModuleHandleW(string? moduleName);
    }
}

/// <summary>
/// Pauses the UI test run when the owner moves the mouse. Before each test it fails at once while the pause file exists,
/// so a paused run stops touching the screen; after each test it fails the test if real mouse input happened during it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PauseWhenOwnerMovesMouseAttribute : BeforeAfterTestAttribute
{
    /// <inheritdoc/>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        OwnerInputGuard.Start();
        // Real input between tests pauses the run too
        if (OwnerInputGuard.TakeInterrupted())
        {
            OwnerInputGuard.WritePauseFile();
        }

        if (File.Exists(OwnerInputGuard.PauseFile))
        {
            Assert.Fail($"Paused: the owner moved the mouse. Waiting for the owner to resume (delete {OwnerInputGuard.PauseFile}).");
        }
    }

    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (OwnerInputGuard.TakeInterrupted())
        {
            OwnerInputGuard.WritePauseFile();
            Assert.Fail("Interrupted: the owner moved the mouse during this test. Re-run it after resuming.");
        }
    }
}
