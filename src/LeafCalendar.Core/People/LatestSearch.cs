namespace LeafCalendar.Core.People;

/// <summary>
/// Runs one search at a time for a box the user types in: starting a search cancels the one before it, and a result
/// that arrives after a newer search started is dropped.
/// </summary>
/// <remarks>Call it from one thread (the UI thread).</remarks>
public sealed class LatestSearch<T> : IDisposable
    where T : class
{
    CancellationTokenSource? _current;

    /// <summary>Runs <paramref name="search"/>; null when a newer search (or <see cref="Cancel"/>) replaced it.</summary>
    public async Task<T?> RunAsync(Func<CancellationToken, Task<T>> search)
    {
        Cancel();
        var mine = _current = new CancellationTokenSource();

        try
        {
            var result = await search(mine.Token);
            return ReferenceEquals(mine, _current) ? result : null;
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Cancels the running search, so its result is dropped.</summary>
    public void Cancel()
    {
        // Cancel, Then Dispose (the documented order; the canceled token stays readable)
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }

    /// <summary>Cancels the running search.</summary>
    public void Dispose() => Cancel();
}
