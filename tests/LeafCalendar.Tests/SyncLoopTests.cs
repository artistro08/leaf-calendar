using System.Threading.Channels;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SyncLoopTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly Channel<int> _runs = Channel.CreateUnbounded<int>();
    private readonly Channel<int> _invoked = Channel.CreateUnbounded<int>();
    private readonly TempFolder _logs = new();
    private int _count;

    public void Dispose() => _logs.Dispose();

    private SyncLoop CreateLoop(Func<Task>? body = null) => new(
        async _ =>
        {
            _invoked.Writer.TryWrite(1);

            if (body is not null)
            {
                await body();
            }

            _runs.Writer.TryWrite(Interlocked.Increment(ref _count));
        },
        _time,
        new AppLog(_logs.Path, _time));

    private async Task<int> NextRunAsync() => await _runs.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

    private static async Task SettleAsync() => await Task.Delay(100, TestContext.Current.CancellationToken);

    private async Task AwaitInvokedAsync() => await _invoked.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

    // Negative check: gives the loop a moment to (wrongly) run. ponytail: timing-based; raise the delay if CI is slow.
    private async Task AssertNoRunAsync()
    {
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(_runs.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(SyncMode.Visible, 15)]
    [InlineData(SyncMode.Tray, 60)]
    [InlineData(SyncMode.Saver, 300)]
    public void IntervalFor_Mode_ReturnsSpecCadence(SyncMode mode, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncLoop.IntervalFor(mode));
    }

    [Theory]
    [InlineData(true, false, false, SyncMode.Visible)]
    [InlineData(true, true, true, SyncMode.Visible)]
    [InlineData(false, false, false, SyncMode.Tray)]
    [InlineData(false, true, false, SyncMode.Saver)]
    [InlineData(false, false, true, SyncMode.Saver)]
    public void ModeFor_OnScreenWinsThenSaverThenTray(bool visible, bool energySaver, bool metered, SyncMode expected) =>
        Assert.Equal(expected, SyncLoop.ModeFor(visible, energySaver, metered));

    [Fact]
    public async Task Mode_SaverToTray_DoesNotWaitOutTheLongInterval()
    {
        await using var loop = CreateLoop();
        loop.Mode = SyncMode.Saver;
        loop.Start();
        Assert.Equal(1, await NextRunAsync());

        // A minute on Saver is too soon for the next sync
        await SettleAsync();
        _time.Advance(TimeSpan.FromSeconds(61));
        await AssertNoRunAsync();

        // Back to the tray pace: the wait ends now, not 4 minutes later
        loop.Mode = SyncMode.Tray;
        Assert.Equal(2, await NextRunAsync());
    }

    [Fact]
    public async Task Start_VisibleMode_SyncsNowThenEvery15Seconds()
    {
        await using var loop = CreateLoop();
        loop.Mode = SyncMode.Visible;

        loop.Start();
        Assert.Equal(1, await NextRunAsync());
        await SettleAsync();

        _time.Advance(TimeSpan.FromSeconds(14));
        await AssertNoRunAsync();

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await NextRunAsync());
    }

    [Fact]
    public async Task TriggerNow_WhileWaiting_SyncsImmediately()
    {
        await using var loop = CreateLoop();
        loop.Start();
        await NextRunAsync();
        await SettleAsync();

        loop.TriggerNow();

        Assert.Equal(2, await NextRunAsync());
    }

    [Fact]
    public async Task Start_SyncThrows_KeepsLooping()
    {
        var first = true;
        await using var loop = CreateLoop(() =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        });

        loop.Start();
        await AwaitInvokedAsync();
        await SettleAsync();
        _time.Advance(SyncLoop.IntervalFor(SyncMode.Tray));

        Assert.Equal(1, await NextRunAsync());
    }

    [Fact]
    public async Task Start_TaskCanceledExceptionThrows_KeepsLooping()
    {
        var first = true;
        await using var loop = CreateLoop(() =>
        {
            if (first)
            {
                first = false;
                throw new TaskCanceledException("timeout");
            }

            return Task.CompletedTask;
        });

        loop.Start();
        await AwaitInvokedAsync();
        await SettleAsync();
        _time.Advance(SyncLoop.IntervalFor(SyncMode.Tray));

        Assert.Equal(1, await NextRunAsync());
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_TriggerNowIsSafe()
    {
        await using var loop = CreateLoop();
        loop.Start();
        await NextRunAsync();

        await loop.DisposeAsync();
        loop.TriggerNow();
        await loop.DisposeAsync();
    }
}
