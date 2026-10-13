using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Api;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed partial class LocalApiTests : IDisposable
{
    private const string MeetLink = "https://meet.google.com/abc-defg-hij?authuser=leaf.tester%40gmail.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    [GeneratedRegex("request=([0-9a-f]{32})")]
    private static partial Regex RequestToken();

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
        var reply = await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Leaf closed the connection.");
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.Clone();
    }

    // The approval notification's Allow, as UI tests click notifications (a second launch with --toast-action)
    private void Allow()
    {
        var line = LeafApp.WaitForNotification(_profile, l => l.StartsWith("show\tapi\t", StringComparison.Ordinal));
        var token = RequestToken().Match(line).Groups[1].Value;
        using var second = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --toast-action {new ToastArgs(ToastAction.AllowApp, _profile, Request: token).Encode()}");
        Assert.True(Retry.WhileFalse(() => second.App.HasExited, TimeSpan.FromSeconds(15)).Success);
    }

    [Fact]
    public async Task ApprovedApp_ListsEventsAndJoins()
    {
        using var leaf = Launch();
        TurnOn(leaf);

        await using var pipe = await ConnectAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var hello = SendAsync(pipe, reader, 1, "leaf.hello");
        Allow();

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
        Allow();
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
}
