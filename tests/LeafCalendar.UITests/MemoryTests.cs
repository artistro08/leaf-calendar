using System.Diagnostics;
using System.Text.Json;
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

            using var process = Process.GetProcessById(leaf.App.ProcessId);
            process.Refresh();
            var privateMb = process.PrivateMemorySize64 / (1024 * 1024);
            var workingSetMb = process.WorkingSet64 / (1024 * 1024);
            output.WriteLine($"Tray-only: private bytes {privateMb} MB, working set {workingSetMb} MB");

            using var budget = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "memory-budget.json"), TestContext.Current.CancellationToken));
            var maxPrivate = budget.RootElement.GetProperty("trayPrivateBytesMb").GetInt64();
            var maxWorkingSet = budget.RootElement.GetProperty("trayWorkingSetMb").GetInt64();

            Assert.True(privateMb <= maxPrivate, $"Private bytes {privateMb} MB exceed budget {maxPrivate} MB.");
            Assert.True(workingSetMb <= maxWorkingSet, $"Working set {workingSetMb} MB exceeds budget {maxWorkingSet} MB.");
        }
        finally
        {
            LeafApp.DeleteProfile(profile);
        }
    }
}
