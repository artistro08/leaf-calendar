using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Api;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class LocalApiTests : IDisposable
{
    internal const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private string PipeName => ApiClients.PipeName(Environment.UserName, _profile);

    // 8 AM in New York on Oct 1, so the fixtures' Design review (2 PM) is the next meeting
    private LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");
        leaf.WaitFor("Event_evt-meeting_202610011800");
        return leaf;
    }

    // Settings › Connected apps, switch on
    private static void TurnOn(LeafApp leaf)
    {
        leaf.OpenSettings("ConnectedApps");
        var toggle = leaf.WaitInSettings("ApiAllowSwitch").Patterns.Toggle.Pattern;
        if (toggle.ToggleState.Value != ToggleState.On)
        {
            toggle.Toggle();
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        return pipe;
    }

    private static async Task<JsonElement> SendAsync(Stream pipe, StreamReader reader, int id, string method, string paramsJson = "{}")
    {
        var line = $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""" + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), TestContext.Current.CancellationToken);
        await pipe.FlushAsync(TestContext.Current.CancellationToken);
        var reply = await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Leaf Calendar closed the connection.");
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.Clone();
    }

    // The approval window's Allow
    private static void Allow(LeafApp leaf)
    {
        var allow = leaf.WaitInApproval("ApiApprovalAllowButton").AsButton();
        Assert.True(Retry.WhileFalse(() => allow.IsEnabled, TimeSpan.FromSeconds(10)).Success);
        allow.Invoke();
    }

    // A line sent without waiting for its reply (the reply waits on the approval)
    private static async Task SendLineAsync(Stream pipe, int id, string method, string paramsJson)
    {
        var line = $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""" + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), TestContext.Current.CancellationToken);
        await pipe.FlushAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadReplyAsync(StreamReader reader)
    {
        var reply = await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Leaf Calendar closed the connection.");
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ApprovedApp_ListsEventsAndJoins()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var hello = SendAsync(pipe, reader, 1, "leaf.hello");
        Allow(leaf);

        Assert.Equal(1, (await hello).GetProperty("result").GetProperty("apiVersion").GetInt32());
        leaf.WaitInSettings("ApiRemoveButton_0");

        var events = await SendAsync(pipe, reader, 2, "events.list", """{"start":"2026-10-01T00:00:00-04:00","end":"2026-10-02T00:00:00-04:00"}""");
        Assert.Contains(events.GetProperty("result").EnumerateArray(), e => e.GetProperty("title").GetString() == "Design review");

        var join = await SendAsync(pipe, reader, 3, "meetings.join");
        Assert.True(join.GetProperty("result").GetProperty("opened").GetBoolean());
        Assert.Contains(MeetLink, LeafApp.LaunchedLinks(_profile));
    }

    [Fact]
    public async Task RemovedApp_IsRefused()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var hello = SendAsync(pipe, reader, 1, "leaf.hello");
        Allow(leaf);
        await hello;

        leaf.WaitInSettings("ApiRemoveButton_0").AsButton().Invoke();
        leaf.WaitInSettings("ApiNoAppsText");

        var refused = await SendAsync(pipe, reader, 2, "leaf.hello");
        Assert.Equal("not_approved", refused.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SwitchOff_NoPipe()
    {
        using var leaf = Launch();

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAsync<TimeoutException>(() => pipe.ConnectAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnterDoesNothing_EscRefuses()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var hello = SendAsync(pipe, reader, 1, "leaf.hello");
        leaf.WaitInApproval("ApiApprovalPath");
        leaf.ApprovalWindow.SetForeground();

        Keyboard.Type(VirtualKeyShort.ENTER);
        Keyboard.Type(VirtualKeyShort.SPACE);
        Thread.Sleep(500);
        Assert.True(leaf.IsApprovalOpen);

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.Equal("not_approved", (await hello).GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.True(Retry.WhileTrue(() => leaf.IsApprovalOpen, TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public async Task AllowIsOff_ForTheFirstSeconds()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", "{}");

        Assert.False(leaf.WaitInApproval("ApiApprovalAllowButton").IsEnabled);
        Assert.True(leaf.WaitInApproval("ApiApprovalDenyButton").IsEnabled);

        // No App Icon Sent: The Program's Own
        leaf.WaitInApprovalRaw("ApiApprovalProgramIcon");
        Allow(leaf);
        await ReadReplyAsync(reader);
    }

    [Fact]
    public async Task SwitchOff_ClosesTheWindow()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", "{}");
        leaf.WaitInApproval("ApiApprovalAllowButton");

        leaf.WaitInSettings("ApiAllowSwitch").Patterns.Toggle.Pattern.Toggle();

        Assert.True(Retry.WhileTrue(() => leaf.IsApprovalOpen, TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public async Task AppIcon_ShowsInsteadOfTheProgramIcon()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", $$"""{"icon":"{{OnePixelPng}}"}""");

        leaf.WaitInApprovalRaw("ApiApprovalAppIcon");
        Allow(leaf);
        Assert.Equal(1, (await ReadReplyAsync(reader)).GetProperty("result").GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task BrokenIcon_ShowsTheProgramIcon()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        // A PNG header Leaf Calendar's check passes, with nothing after it for Windows to decode
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 16, 0, 0, 0, 16 };
        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", $$"""{"icon":"{{Convert.ToBase64String(header)}}"}""");

        leaf.WaitInApprovalRaw("ApiApprovalProgramIcon");
        Allow(leaf);
        await ReadReplyAsync(reader);
    }

    [Fact]
    public async Task Allow_OpensTheReturnLink()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await SendLineAsync(pipe, 1, "leaf.hello", """{"returnUrl":"leaf-uitest://back"}""");

        Allow(leaf);
        await ReadReplyAsync(reader);
        Assert.True(Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains("leaf-uitest://back"), TimeSpan.FromSeconds(10)).Success);
    }
}
