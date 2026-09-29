using System.Threading.Channels;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class SyncLoopTests : IDisposable
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly Channel<int> _runs = Channel.CreateUnbounded<int>();
    readonly Channel<int> _invoked = Channel.CreateUnbounded<int>();
    readonly TempFolder _logs = new();
    int _count;

    public void Dispose() => _logs.Dispose();

    SyncLoop CreateLoop(Func<Task>? body = null) => new(
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

    async Task<int> NextRunAsync() => await _runs.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

    static async Task SettleAsync() => await Task.Delay(100, TestContext.Current.CancellationToken);

    async Task AwaitInvokedAsync() => await _invoked.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

    // Negative check: gives the loop a moment to (wrongly) run. ponytail: timing-based; raise the delay if CI is slow.
    async Task AssertNoRunAsync()
    {
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(_runs.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(SyncMode.Visible, 15)]
    [InlineData(SyncMode.Tray, 60)]
    public void IntervalFor_Mode_ReturnsSpecCadence(SyncMode mode, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncLoop.IntervalFor(mode));
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
}
