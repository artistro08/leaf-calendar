using LeafCalendar.Core.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace LeafCalendar.App;

/// <summary>
/// Entry point. Leaf runs once per profile: a second launch with the same profile hands its activation to the running
/// Leaf (which comes to the front) and exits. Its other arguments are ignored.
/// </summary>
public static class Program
{
    /// <summary>Redirects to the running Leaf for this profile, or starts the app.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single Instance Per Profile (UI tests' parallel uitest-* profiles stay independent)
        var options = LaunchOptions.Parse(args);
        var main    = AppInstance.FindOrRegisterForKey("LeafCalendar-" + options.Profile);
        if (!main.IsCurrent)
        {
            RedirectTo(main);
            return;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    // Microsoft's documented pattern: redirect on a background thread while this STA thread waits with COM pumping,
    // so the redirect can't deadlock it
    static unsafe void RedirectTo(AppInstance main)
    {
        // This process was just launched by the user, so it may hand the foreground to the running Leaf
        PInvoke.AllowSetForegroundWindow(main.ProcessId);

        var args     = AppInstance.GetCurrent().GetActivatedEventArgs();
        var redirect = Task.Run(() => main.RedirectActivationToAsync(args).AsTask().Wait());
        var handle   = (HANDLE)((IAsyncResult)redirect).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle();

        uint index;
        _ = PInvoke.CoWaitForMultipleObjects((uint)CWMO_FLAGS.CWMO_DEFAULT, PInvoke.INFINITE, 1, &handle, &index);
    }
}
