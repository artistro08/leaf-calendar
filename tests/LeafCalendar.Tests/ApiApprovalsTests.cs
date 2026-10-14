using LeafCalendar.Core.Api;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class ApiApprovalsTests
{
    private const string Path = @"C:\Apps\Raycast\node.exe";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly List<PendingApproval> _prompts = [];

    private static ApiCaller Caller(string path = Path) => new(path, "Raycast", 1234, ApiHello.None);

    private Task<bool> Ask(ApiApprovals approvals, string path = Path) =>
        approvals.AskAsync(Caller(path), _prompts.Add, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AskAsync_Allow_IsTrue()
    {
        var approvals = new ApiApprovals(_time);
        var asking = Ask(approvals);

        approvals.Answer(Assert.Single(_prompts).Token, allow: true);

        Assert.True(await asking);
    }

    [Fact]
    public async Task AskAsync_SamePathTwice_OnePrompt()
    {
        var approvals = new ApiApprovals(_time);
        var first = Ask(approvals);
        var second = Ask(approvals, Path.ToUpperInvariant());

        approvals.Answer(Assert.Single(_prompts).Token, allow: true);

        Assert.True(await first);
        Assert.True(await second);
    }

    [Fact]
    public async Task Answer_Deny_IsRemembered()
    {
        var approvals = new ApiApprovals(_time);
        var asking = Ask(approvals);

        approvals.Answer(_prompts[0].Token, allow: false);

        Assert.False(await asking);
        Assert.True(approvals.IsRefused(Path));
        Assert.False(await Ask(approvals));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task AskAsync_NoAnswer_TimesOutWithoutRemembering()
    {
        var approvals = new ApiApprovals(_time);
        var asking = Ask(approvals);

        _time.Advance(ApiApprovals.Wait + TimeSpan.FromSeconds(1));

        Assert.False(await asking);
        Assert.False(approvals.IsRefused(Path));
        Assert.Null(approvals.Find(_prompts[0].Token));
    }

    [Fact]
    public async Task AskAsync_Canceled_AsksAgainNextTime()
    {
        var approvals = new ApiApprovals(_time);
        using var stop = new CancellationTokenSource();
        var asking = approvals.AskAsync(Caller(), _prompts.Add, stop.Token);

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Null(approvals.Find(_prompts[0].Token));
        _ = Ask(approvals);
        Assert.Equal(2, _prompts.Count);
    }

    [Fact]
    public void Find_UnknownToken_IsNull()
    {
        Assert.Null(new ApiApprovals(_time).Find("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void Find_PendingToken_NamesTheApp()
    {
        var approvals = new ApiApprovals(_time);
        _ = Ask(approvals);

        Assert.Equal(Path, approvals.Find(_prompts[0].Token)!.Caller.Path);
    }

    [Fact]
    public void AskAsync_KeepsWhatTheAppSent()
    {
        var approvals = new ApiApprovals(_time);
        var caller = new ApiCaller(Path, "Raycast", 4321, new ApiHello(new byte[] { 1, 2, 3 }, new Uri("raycast://extensions/leaf")));

        _ = approvals.AskAsync(caller, _prompts.Add, TestContext.Current.CancellationToken);

        Assert.Same(caller, Assert.Single(_prompts).Caller);
    }

    [Fact]
    public void Tokens_AreRandomHex()
    {
        var approvals = new ApiApprovals(_time);
        _ = Ask(approvals);
        _ = Ask(approvals, @"C:\other.exe");

        Assert.All(_prompts, p => Assert.Matches("^[0-9a-f]{32}$", p.Token));
        Assert.NotEqual(_prompts[0].Token, _prompts[1].Token);
    }
}
