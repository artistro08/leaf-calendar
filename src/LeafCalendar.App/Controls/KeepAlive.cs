using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Keeps objects Leaf just let go of alive for a few seconds, because WinUI may still reach them.
/// </summary>
/// <remarks>
/// When a view swaps an item list or an editor's view model, WinUI finishes with the old items on a later layout or input
/// pass, through references .NET doesn't count. In the Native AOT build a garbage collection that lands in between frees
/// them, and WinUI's next call into one ends the process (a <c>NullReferenceException</c> in
/// <c>ComWrappers.ManagedObjectWrapper.QueryInterface</c>, queried for <c>IFrameworkElement</c>). Holding them a little
/// longer than WinUI needs closes that window. UI thread only.
/// </remarks>
/// <seealso href="https://github.com/microsoft/microsoft-ui-reactor/pull/1302"/>
internal static class KeepAlive
{
    // Longer than any deferred XAML pass (the reactor fix uses 1 s for a 150 ms tick)
    static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(3);

    static readonly List<(object Item, long Until)> Held = [];
    static DispatcherQueueTimer? _sweep;

    /// <summary>Holds <paramref name="item"/> for a few seconds (nothing for null).</summary>
    public static void Hold(object? item)
    {
        if (item is null)
        {
            return;
        }

        Held.Add((item, Environment.TickCount64 + (long)HoldFor.TotalMilliseconds));

        // One Sweep Timer On The UI Thread, Running Only While Something Is Held
        if (_sweep is null)
        {
            _sweep          = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _sweep.Interval = HoldFor;
            _sweep.Tick    += (_, _) => Sweep();
        }

        if (!_sweep.IsRunning)
        {
            _sweep.Start();
        }
    }

    static void Sweep()
    {
        var now = Environment.TickCount64;
        Held.RemoveAll(h => h.Until <= now);
        if (Held.Count == 0)
        {
            _sweep?.Stop();
        }
    }
}
