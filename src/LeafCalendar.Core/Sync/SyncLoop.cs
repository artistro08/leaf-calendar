using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Sync;

/// <summary>How often Leaf polls Google.</summary>
public enum SyncMode
{
    /// <summary>A Leaf window or the flyout is on screen.</summary>
    Visible,

    /// <summary>Leaf is only in the tray.</summary>
    Tray,
}

/// <summary>
/// Smart polling. Runs one sync right away, then again after each interval (15 s visible, 60 s tray).
/// </summary>
/// <remarks>
/// <see cref="TriggerNow"/> cuts the wait short. The app calls it on window or flyout open, resume
/// from sleep, and network reconnect. A failed sync is logged and the loop keeps going. Google push
/// needs a public HTTPS server, which Leaf doesn't have; a future push relay would only call
/// <see cref="TriggerNow"/>.
/// </remarks>
public sealed class SyncLoop(Func<CancellationToken, Task> syncAll, TimeProvider time, AppLog log) : IAsyncDisposable
{
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly CancellationTokenSource _stop = new();
    Task? _loop;
    bool _disposed;

    /// <summary>Current cadence. Defaults to <see cref="SyncMode.Tray"/>.</summary>
    public SyncMode Mode { get; set; } = SyncMode.Tray;

    /// <summary>Polling interval for a mode.</summary>
    public static TimeSpan IntervalFor(SyncMode mode) =>
        mode == SyncMode.Visible ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60);

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

        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    async Task RunAsync(CancellationToken ct)
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
            var wake  = _wake.WaitAsync(waitCts.Token);

            await Task.WhenAny(delay, wake);
            await waitCts.CancelAsync();
        }
    }
}
