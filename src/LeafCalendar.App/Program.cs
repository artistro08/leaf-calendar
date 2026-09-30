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
    // How long a second launch waits for the running Leaf to take its activation before giving up and exiting
    const uint RedirectTimeoutMs = 10_000;

    // Redirected Activations: one can arrive before the app is ready for it, so it waits here until the app is
    static readonly Lock ActivationGate = new();
    static Action? _onActivated;
    static bool _activationPending;

    /// <summary>Redirects to the running Leaf for this profile, or starts the app.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single Instance Per Profile (the key ignores case, like Windows' profile folders; UI tests' parallel uitest-* profiles stay independent)
        var options = LaunchOptions.Parse(args);
        var main    = AppInstance.FindOrRegisterForKey("LeafCalendar-" + options.Profile.ToLowerInvariant());
        if (!main.IsCurrent)
        {
            RedirectTo(main);
            return;
        }

        // Listen Right Away, So A Redirect That Arrives While The App Starts Isn't Lost
        main.Activated += (_, _) => OnRedirected();

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    /// <summary>
    /// Runs <paramref name="onActivated"/> for every later launch of this profile (on the launch's thread; marshal to
    /// the UI yourself), and once now if one already arrived while the app was starting.
    /// </summary>
    internal static void HandleActivations(Action onActivated)
    {
        bool pending;
        lock (ActivationGate)
        {
            _onActivated       = onActivated;
            pending            = _activationPending;
            _activationPending = false;
        }

        if (pending)
        {
            onActivated();
        }
    }

    static void OnRedirected()
    {
        Action? handler;
        lock (ActivationGate)
        {
            handler = _onActivated;
            if (handler is null)
            {
                _activationPending = true;
            }
        }

        handler?.Invoke();
    }

    // Microsoft's documented pattern: redirect on a background thread while this STA thread waits with COM pumping,
    // so the redirect can't deadlock it. The wait is bounded: a running Leaf that never answers doesn't keep this
    // process around; it just exits.
    static unsafe void RedirectTo(AppInstance main)
    {
        // This process was just launched by the user, so it may hand the foreground to the running Leaf
        PInvoke.AllowSetForegroundWindow(main.ProcessId);

        var args     = AppInstance.GetCurrent().GetActivatedEventArgs();
        var redirect = Task.Run(() => main.RedirectActivationToAsync(args).AsTask().Wait());
        var handle   = (HANDLE)((IAsyncResult)redirect).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle();

        uint index;
        _ = PInvoke.CoWaitForMultipleObjects((uint)CWMO_FLAGS.CWMO_DEFAULT, RedirectTimeoutMs, 1, &handle, &index);

        // The wait handle belongs to the task, so it must outlive the wait
        GC.KeepAlive(redirect);
    }
}
