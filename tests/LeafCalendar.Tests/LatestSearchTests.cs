using LeafCalendar.Core.People;

namespace LeafCalendar.Tests;

public class LatestSearchTests
{
    [Fact]
    public async Task RunAsync_OnlySearch_ReturnsItsResult()
    {
        using var latest = new LatestSearch<string>();

        var result = await latest.RunAsync(_ => Task.FromResult("alice"));

        Assert.Equal("alice", result);
    }

    [Fact]
    public async Task RunAsync_NewerSearchStarts_CancelsTheOlderAndDropsItsResult()
    {
        using var latest = new LatestSearch<string>();
        var slow = new TaskCompletionSource<string>();
        var token = CancellationToken.None;
        var older = latest.RunAsync(ct =>
        {
            token = ct;
            return slow.Task;
        });

        var newer = await latest.RunAsync(_ => Task.FromResult("alice"));
        slow.SetResult("stale");

        Assert.True(token.IsCancellationRequested);
        Assert.Null(await older);
        Assert.Equal("alice", newer);
    }

    [Fact]
    public async Task RunAsync_OlderThrowsCanceled_ReturnsNull()
    {
        using var latest = new LatestSearch<string>();
        var older = latest.RunAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        });

        await latest.RunAsync(_ => Task.FromResult("alice"));

        Assert.Null(await older);
    }

    [Fact]
    public async Task Cancel_DropsTheRunningSearch()
    {
        using var latest = new LatestSearch<string>();
        var slow = new TaskCompletionSource<string>();
        var search = latest.RunAsync(_ => slow.Task);

        latest.Cancel();
        slow.SetResult("stale");

        Assert.Null(await search);
    }
}
