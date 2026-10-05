using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Sync;

/// <summary>How often Leaf polls Google.</summary>
public enum SyncMode
{
    /// <summary>A Leaf window or the flyout is on screen.</summary>
    Visible,

    /// <summary>Leaf is only in the tray.</summary>
    Tray,

    /// <summary>Leaf is only in the tray, and Windows is on Energy Saver or the connection is metered.</summary>
    Saver,
}

/// <summary>
/// Smart polling. Runs one sync right away, then again after each interval (15 s visible, 60 s tray, 5 minutes on
/// Energy Saver or a metered connection).
/// </summary>
/// <remarks>
/// <see cref="TriggerNow"/> cuts the wait short. The app calls it on window or flyout open, resume
/// from sleep, and network reconnect. A failed sync is logged and the loop keeps going. Google push
/// needs a public HTTPS server, which Leaf doesn't have; a future push relay would only call
/// <see cref="TriggerNow"/>.
/// </remarks>
public sealed class SyncLoop(Func<CancellationToken, Task> syncAll, TimeProvider time, AppLog log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;
    private SyncMode _mode = SyncMode.Tray;

    /// <summary>
    /// Current cadence. Defaults to <see cref="SyncMode.Tray"/>. A faster cadence on a running loop starts at once (a
    /// loop not yet started syncs at once anyway).
    /// </summary>
    public SyncMode Mode
    {
        get => _mode;
        set
        {
            var faster = IntervalFor(value) < IntervalFor(_mode);
            _mode = value;
            if (faster && _loop is not null)
            {
                TriggerNow();
            }
        }
    }

    /// <summary>The wait between syncs: 15 s on screen, 60 s in the tray, 5 minutes on Energy Saver or a metered connection.</summary>
    public static TimeSpan IntervalFor(SyncMode mode) => mode switch
    {
        SyncMode.Visible => TimeSpan.FromSeconds(15),
        SyncMode.Saver => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// The cadence for the current state: on screen always syncs at the visible pace; otherwise Energy Saver or a
    /// metered connection slows the tray pace.
    /// </summary>
    /// <param name="visible">A Leaf window or the flyout is on screen.</param>
    /// <param name="energySaver">Windows' Energy Saver is on.</param>
    /// <param name="metered">The internet connection is metered (fixed or variable cost).</param>
    /// <returns>The mode to run the loop in.</returns>
    public static SyncMode ModeFor(bool visible, bool energySaver, bool metered) =>
        visible ? SyncMode.Visible : energySaver || metered ? SyncMode.Saver : SyncMode.Tray;

    /// <summary>Starts the loop (no-op if already started).</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Wakes the loop to sync now.</summary>
    public void TriggerNow()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already signaled.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while triggering.
        }
    }

    /// <summary>Stops the loop and waits for the current sync to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _stop.CancelAsync();
            if (_loop is not null)
            {
                await _loop;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            // Shutdown must go on, so log and still release the handles below
            log.Error("sync.loop.dispose-failed", ex);
        }
        finally
        {
            _stop.Dispose();
            _wake.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Sync
            try
            {
                await syncAll(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.Error("sync.loop.failed", ex);
            }

            // Wait For Interval Or Trigger
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(IntervalFor(Mode), time, waitCts.Token);
            var wake = _wake.WaitAsync(waitCts.Token);

            await Task.WhenAny(delay, wake);
            await waitCts.CancelAsync();
        }
    }
}
