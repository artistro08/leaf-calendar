using FlaUI.Core.AutomationElements;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ScreenshotTour : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly List<string> _profiles = [];

    // Settings Pages And Window Widths For Capture_SettingsPages (0 is the minimum)
    static readonly string[] SettingsPages  = ["General", "Calendars", "TimeZones", "Notifications", "Tray", "Shortcuts", "Accounts", "About"];
    static readonly int[]    SettingsWidths = [0, 1500];

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

    /// <summary>
    /// Every Settings page in light and dark, with the Settings window at its 640 DIP minimum and wide (1500 px), so the
    /// capped, centered column and the pane's collapse both show. Set LEAF_SCREENSHOTS to run it (LEAF_SCREENS narrows
    /// the pages, such as "General,TimeZones"). Expanders on the page are opened first, so their rows show.
    /// </summary>
    [Fact]
    public void Capture_SettingsPages()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the Settings pages.");
        }

        var only  = Environment.GetEnvironmentVariable("LEAF_SCREENS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var pages = SettingsPages.Where(p => only is null || only.Contains(p, StringComparer.OrdinalIgnoreCase));

        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            var profile = SeededProfile.Create(new LeafSettings { Theme = theme });
            _profiles.Add(profile);
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("SettingsButton");
            foreach (var page in pages)
            {
                // The Main Window Minimized, So Only Settings Shows In The Shot
                var settings = leaf.OpenSettings(page);
                leaf.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
                foreach (var width in SettingsWidths)
                {
                    // 0 Asks For The Minimum (the window clamps it)
                    settings.Patterns.Transform.Pattern.Move(40, 40);
                    settings.Patterns.Transform.Pattern.Resize(width, 900);
                    Thread.Sleep(600);
                    // The Expanders Open, So Their Rows Show
                    if (page == "TimeZones")
                    {
                        leaf.ExpandInSettings("PrimaryZoneExpander");
                    }
                    else if (page == "Accounts")
                    {
                        leaf.ExpandInSettings($"AccountExpander_{SeededProfile.AccountId}");
                    }

                    // Only Settings In The Shot (the main window minimized again: opening Settings can bring it back)
                    leaf.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
                    settings.SetForeground();
                    Thread.Sleep(400);
                    settings.CaptureToFile(Path.Combine(folder, $"settings-{page.ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}-{(width == 0 ? "narrow" : "wide")}.png"));

                    // General's Working Hours, Further Down
                    if (page == "General")
                    {
                        leaf.WaitInSettings("WorkDaysButton").Patterns.ScrollItem.Pattern.ScrollIntoView();
                        Thread.Sleep(400);
                        settings.CaptureToFile(Path.Combine(folder, $"settings-general-workinghours-{theme.ToString().ToLowerInvariant()}-{(width == 0 ? "narrow" : "wide")}.png"));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The shortcut picker in light and dark: the Shortcuts page's buttons, then its dialog with the current shortcut
    /// (valid), Shift+J (invalid), nothing (Esc), and the other shortcut's keys (taken). Set LEAF_SCREENSHOTS to run it.
    /// </summary>
    [Fact]
    public void Capture_ShortcutPicker()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the shortcut picker.");
        }

        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            var name    = theme.ToString().ToLowerInvariant();
            var profile = SeededProfile.Create(new LeafSettings { Theme = theme, JoinShortcut = "Ctrl+Alt+Shift+F9", FlyoutShortcut = "Ctrl+Alt+Shift+F10" });
            _profiles.Add(profile);
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("SettingsButton");
            var settings = leaf.OpenSettings("Shortcuts");
            leaf.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
            settings.Patterns.Transform.Pattern.Move(40, 40);
            settings.Patterns.Transform.Pattern.Resize(1100, 800);
            settings.SetForeground();
            leaf.WaitInSettings("JoinShortcutButton");
            Thread.Sleep(600);
            settings.CaptureToFile(Path.Combine(folder, $"shortcut-button-{name}.png"));

            // The Dialog's States
            leaf.WaitInSettings("JoinShortcutButton").AsButton().Invoke();
            leaf.WaitForAnywhere("ShortcutPreview");
            Thread.Sleep(600);
            settings.CaptureToFile(Path.Combine(folder, $"shortcut-dialog-valid-{name}.png"));
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_J);
            Thread.Sleep(600);
            settings.CaptureToFile(Path.Combine(folder, $"shortcut-dialog-invalid-{name}.png"));
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(600);
            settings.CaptureToFile(Path.Combine(folder, $"shortcut-dialog-empty-{name}.png"));
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT, FlaUI.Core.WindowsAPI.VirtualKeyShort.F10);
            Thread.Sleep(600);
            settings.CaptureToFile(Path.Combine(folder, $"shortcut-dialog-taken-{name}.png"));
            leaf.WaitForAnywhere("CloseButton").AsButton().Invoke();
        }
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
