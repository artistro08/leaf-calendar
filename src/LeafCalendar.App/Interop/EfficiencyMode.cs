using Windows.Win32;
using Windows.Win32.System.Threading;

namespace LeafCalendar.App.Interop;

/// <summary>
/// Windows efficiency mode (EcoQoS) while Leaf is only in the tray (spec 3.4 item 3): the CPU runs Leaf's work at its
/// most efficient speed. Timers, sync, and notifications keep working.
/// </summary>
internal static unsafe class EfficiencyMode
{
    // PROCESS_POWER_THROTTLING_CURRENT_VERSION and PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    const uint CurrentVersion = 1;
    const uint ExecutionSpeed = 0x1;

    /// <summary>Turns efficiency mode on (tray only) or off (a window is open), where off hands throttling back to Windows.</summary>
    public static void Set(bool on)
    {
        // Both masks zero is the reset: an execution-speed control with a zero state would opt out of throttling instead
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version     = CurrentVersion,
            ControlMask = on ? ExecutionSpeed : 0,
            StateMask   = on ? ExecutionSpeed : 0,
        };

        _ = PInvoke.SetProcessInformation(PInvoke.GetCurrentProcess(), PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }
}
