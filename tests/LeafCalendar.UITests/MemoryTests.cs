using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public class MemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TrayProbe_Measured_StaysWithinBudget()
    {
        if (!LeafApp.IsNativeAot)
        {
            Assert.Skip("Memory budget applies to the Release AOT package. Run tools/publish-aot.ps1 -Register first.");
        }

        using var google = new FakeGoogleServer();
        var profile = SeededProfile.Create();
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {google.BaseUri} --tray-probe");

            // Probe closes the window at ~3 s, keeps sync running in tray mode, and trims; let it settle
            await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Contains(google.Requests, r => r.Contains("/events", StringComparison.Ordinal));

            await AssertWithinBudget(leaf, "Tray-only", "trayWorkingSetMb");
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    // The owner's report: tray-only Leaf Calendar held about 146 MB after the flyout opened and closed. The flyout's host
    // window stays alive so it opens at once, so its working set budget sits above plain tray-only's
    [Fact]
    public async Task TrayFlyout_OpenedAndClosed_StaysWithinBudget()
    {
        if (!LeafApp.IsNativeAot)
        {
            Assert.Skip("Memory budget applies to the Release AOT package. Run tools/publish-aot.ps1 -Register first.");
        }

        using var google = new FakeGoogleServer();
        var profile = SeededProfile.Create();
        try
        {
            using var leaf = LeafApp.Launch(profile, $"--fake-google {google.BaseUri} --tray-probe");
            await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Open The Flyout, Then Close It
            leaf.PostTrayMessage(LeafApp.TraySelect);
            leaf.WaitForPopup("FlyoutNewEvent");
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            leaf.PostTrayMessage(LeafApp.TraySelect);
            Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutNewEvent"), TimeSpan.FromSeconds(5)).Success, "The flyout didn't close.");
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await AssertWithinBudget(leaf, "After the flyout", "trayAfterFlyoutWorkingSetMb");
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }

    // Private bytes against the tray budget, and the working set against the budget named by workingSetKey
    private async Task AssertWithinBudget(LeafApp leaf, string label, string workingSetKey)
    {
        using var process = Process.GetProcessById(leaf.App.ProcessId);
        process.Refresh();
        var privateMb = process.PrivateMemorySize64 / (1024 * 1024);
        var workingSetMb = process.WorkingSet64 / (1024 * 1024);
        output.WriteLine($"{label}: private bytes {privateMb} MB, working set {workingSetMb} MB");

        using var budget = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "memory-budget.json"), TestContext.Current.CancellationToken));
        var maxPrivate = budget.RootElement.GetProperty("trayPrivateBytesMb").GetInt64();
        var maxWorkingSet = budget.RootElement.GetProperty(workingSetKey).GetInt64();

        Assert.True(privateMb <= maxPrivate, $"Private bytes {privateMb} MB exceed budget {maxPrivate} MB.");
        Assert.True(workingSetMb <= maxWorkingSet, $"Working set {workingSetMb} MB exceeds budget {maxWorkingSet} MB.");
    }
}
