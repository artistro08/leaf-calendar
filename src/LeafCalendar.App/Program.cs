using System.Runtime.InteropServices;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using WinRT;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace LeafCalendar.App;

/// <summary>A later launch handed to the running Leaf: how it was started, and its arguments when it has any.</summary>
internal sealed record Activation(ExtendedActivationKind Kind, string? Arguments);

/// <summary>
/// Entry point. Leaf runs once per profile: a second launch with the same profile hands its activation to the running
/// Leaf and exits. How this process was started (a plain launch, Windows' sign-in startup task, or a notification) is
/// kept for the App.
/// </summary>
public static class Program
{
    // How long a second launch waits for the running Leaf to take its activation before giving up and exiting
    const uint RedirectTimeoutMs = 10_000;

    // Redirected Activations: one can arrive before the app is ready for it, so it waits here until the app is
    static readonly Lock ActivationGate = new();
    static readonly List<Activation> Pending = [];
    static Action<Activation>? _onActivated;

    /// <summary>This launch's options.</summary>
    internal static LaunchOptions Options { get; private set; } = LaunchOptions.Parse([]);

    /// <summary>How Windows started this process.</summary>
    internal static ExtendedActivationKind StartKind { get; private set; } = ExtendedActivationKind.Launch;

    /// <summary>The notification's argument when a notification click started this process, else null.</summary>
    internal static string? StartArgument { get; private set; }

    /// <summary>Redirects to the running Leaf for this profile, or starts the app.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // How This Launch Started
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var started    = Read(activation);
        StartKind      = started.Kind;
        Options        = LaunchOptions.Parse(args);

        // A Notification Click That Started Leaf Belongs To The Profile It Names (Windows starts it without --profile)
        if (started.Kind == ExtendedActivationKind.AppNotification)
        {
            StartArgument = started.Arguments;
            if (ToastArgs.Parse(started.Arguments)?.Profile is { } profile && LaunchOptions.Parse(["--profile", profile]).Profile == profile)
            {
                Options = Options with { Profile = profile };
            }
        }

        // Single Instance Per Profile (the key ignores case, like Windows' profile folders; UI tests' parallel uitest-* profiles stay independent)
        var main = AppInstance.FindOrRegisterForKey("LeafCalendar-" + Options.Profile.ToLowerInvariant());
        if (!main.IsCurrent)
        {
            RedirectTo(main, activation);
            return;
        }

        // Listen Right Away, So A Redirect That Arrives While The App Starts Isn't Lost
        main.Activated += (_, e) => OnRedirected(Read(e));

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    /// <summary>
    /// Runs <paramref name="onActivated"/> for every later launch of this profile (on the launch's thread; marshal to
    /// the UI yourself), and now for any that arrived while the app was starting.
    /// </summary>
    internal static void HandleActivations(Action<Activation> onActivated)
    {
        Activation[] pending;
        lock (ActivationGate)
        {
            _onActivated = onActivated;
            pending      = [.. Pending];
            Pending.Clear();
        }

        foreach (var activation in pending)
        {
            onActivated(activation);
        }
    }

    static void OnRedirected(Activation activation)
    {
        Action<Activation>? handler;
        lock (ActivationGate)
        {
            handler = _onActivated;
            if (handler is null)
            {
                Pending.Add(activation);
            }
        }

        handler?.Invoke(activation);
    }

    // What an activation carries: a notification's argument, or a plain launch's command line. The WinRT payload is read
    // through As<T>(), which Native AOT supports (a C# cast of a WinRT object read back isn't safe there).
    static Activation Read(AppActivationArguments args)
    {
        if (args.Data is null)
        {
            return new Activation(args.Kind, null);
        }

        try
        {
            return args.Kind switch
            {
                ExtendedActivationKind.AppNotification => new Activation(args.Kind, args.Data.As<AppNotificationActivatedEventArgs>().Argument),
                ExtendedActivationKind.Launch          => new Activation(args.Kind, args.Data.As<Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs>().Arguments),
                _                                      => new Activation(args.Kind, null),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return new Activation(args.Kind, null);
        }
    }

    // Microsoft's documented pattern: redirect on a background thread while this STA thread waits with COM pumping,
    // so the redirect can't deadlock it. The wait is bounded: a running Leaf that never answers doesn't keep this
    // process around; it just exits.
    static unsafe void RedirectTo(AppInstance main, AppActivationArguments args)
    {
        // This process was just launched by the user, so it may hand the foreground to the running Leaf
        PInvoke.AllowSetForegroundWindow(main.ProcessId);

        var redirect = Task.Run(() => main.RedirectActivationToAsync(args).AsTask().Wait());
        var handle   = (HANDLE)((IAsyncResult)redirect).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle();

        uint index;
        _ = PInvoke.CoWaitForMultipleObjects((uint)CWMO_FLAGS.CWMO_DEFAULT, RedirectTimeoutMs, 1, &handle, &index);

        // The wait handle belongs to the task, so it must outlive the wait
        GC.KeepAlive(redirect);
    }
}
