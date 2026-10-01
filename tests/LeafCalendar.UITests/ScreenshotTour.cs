using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ScreenshotTour : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly List<string> _profiles = [];

    public void Dispose()
    {
        foreach (var profile in _profiles)
        {
            LeafApp.DeleteProfile(profile);
        }

        _google.Dispose();
    }

    /// <summary>
    /// Every screen in light and dark, at 1366 × 768 (Store minimum) and at the 1086 × 540 minimum window. Set LEAF_SCREENSHOTS
    /// to a folder to run it (LEAF_SCREENS narrows it to a comma-separated list of screens). Fake data only, so screenshots
    /// never show anyone's real calendar. Each shot starts from a fresh profile seeded with the theme, so one screen's
    /// changes never leak into the next.
    /// </summary>
    [Fact]
    public void Capture_AllScreens()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the tour.");
        }

        var only    = Environment.GetEnvironmentVariable("LEAF_SCREENS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var screens = AccessibilityTests.ScreenNames.Where(s => only is null || only.Contains(s, StringComparer.OrdinalIgnoreCase));
        var failed  = new List<string>();

        Directory.CreateDirectory(folder);
        foreach (var screen in screens)
        {
            foreach (var (width, height) in new[] { (1366, 768), (1086, 540) })
            {
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    var profile = SeededProfile.Create(new LeafSettings { Theme = theme });
                    _profiles.Add(profile);

                    // One Screen That Won't Open Doesn't Stop The Tour (it's listed at the end)
                    try
                    {
                        using var leaf = AccessibilityTests.Open(screen, _google, profile, l => Place(l, width, height));
                        Thread.Sleep(600);
                        foreach (var window in leaf.AllWindows())
                        {
                            var title = string.Concat((window.Properties.Name.ValueOrDefault ?? "popup").Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
                            window.CaptureToFile(Path.Combine(folder, $"{screen}-{theme.ToString().ToLowerInvariant()}-{width}-{title}.png"));
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
                    {
                        failed.Add($"{screen} {theme} {width}: {ex.Message}");
                    }
                    finally
                    {
                        LeafApp.DeleteProfile(profile);
                    }
                }
            }
        }

        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    // Sizes The Window (screen pixels) At LEAF_SCREENS_X From The Left (default 40), So A Window Pinned On Top Elsewhere Stays Out Of The Shots
    static void Place(LeafApp leaf, int width, int height)
    {
        var x = int.TryParse(Environment.GetEnvironmentVariable("LEAF_SCREENS_X"), out var left) ? left : 40;
        leaf.MainWindow.Patterns.Transform.Pattern.Move(x, 40);
        leaf.MainWindow.Patterns.Transform.Pattern.Resize(width, height);
        Thread.Sleep(500);
    }
}
