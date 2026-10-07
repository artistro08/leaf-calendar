using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// About ten showcase screenshots of Leaf on the desktop: every other window minimized first (Win+M), Leaf centered
/// at 80% of the main screen up to 1366 × 768, and each shot taken of the window with the desktop showing around it. Set
/// LEAF_SHOWCASE to a folder to run it. Fake data only, so no one's real calendar shows. Each shot starts from a
/// fresh profile seeded with its theme.
/// </summary>
public sealed class ShowcaseTour : IDisposable
{
    private const string Dentist = "Event_evt-single_202610011300";

    // Desktop Shown Around The Window (screen pixels, each side)
    private const int Margin = 96;

    private readonly FakeGoogleServer _google = new();
    private readonly List<string> _profiles = [];

    public void Dispose()
    {
        foreach (var profile in _profiles)
        {
            LeafApp.DeleteProfile(profile);
        }

        _google.Dispose();
    }

    [Fact]
    public void Capture_Showcase()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SHOWCASE");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SHOWCASE to a folder to capture the showcase.");
        }

        var eastern = "America/New_York";
        var shots = new (string Name, LeafSettings Settings, Action<LeafApp> Open)[]
        {
            ("01-week-light", new LeafSettings { Theme = AppTheme.Light }, l => l.WaitFor(Dentist)),
            ("02-week-dark", new LeafSettings { Theme = AppTheme.Dark }, l => l.WaitFor(Dentist)),
            ("03-month-light", new LeafSettings { Theme = AppTheme.Light, ViewMode = CalendarViewMode.Month }, l => l.WaitFor("PeriodTitle")),
            ("04-details-light", new LeafSettings { Theme = AppTheme.Light }, l => l.WaitFor(Dentist).Click()),
            ("05-editor-light", new LeafSettings { Theme = AppTheme.Light }, l =>
            {
                l.WaitFor(Dentist).Click();
                l.WaitFor("DetailsEditButton").AsButton().Invoke();
                l.WaitFor("EditorTitle");
            }),
            ("06-command-menu-dark", new LeafSettings { Theme = AppTheme.Dark }, l =>
            {
                l.WaitFor(Dentist);
                l.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
                l.WaitForAnywhere("CommandSearchBox").Focus();
                Keyboard.Type("next friday");
            }),
            ("07-share-times-light", new LeafSettings { Theme = AppTheme.Light, PrimaryTimeZone = eastern }, l =>
            {
                ShareAvailabilityTests.StartSharing(l);
                ShareAvailabilityTests.DragHours(l, 10, 12);
                l.WaitFor("ShareSlot_0");
            }),
            ("08-saved-group-dark", new LeafSettings { Theme = AppTheme.Dark, PrimaryTimeZone = eastern }, l =>
            {
                SavedShareGroupTests.Save(l);
                SavedShareGroupTests.OpenFirst(l);
            }),
            ("09-settings-light", new LeafSettings { Theme = AppTheme.Light }, l =>
            {
                l.WaitFor(Dentist);
                l.OpenSettings("General");
            }),
        };

        Directory.CreateDirectory(folder);
        var screen = Capture.MainScreen().Bitmap.Size;
        var failed = new List<string>();
        foreach (var (name, settings, open) in shots)
        {
            var profile = SeededProfile.Create(settings);
            _profiles.Add(profile);
            try
            {
                MinimizeAll();
                using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
                var window = Center(leaf, screen);
                open(leaf);
                Thread.Sleep(1000);
                Shoot(Around(window, screen), folder, name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
            {
                failed.Add($"{name}: {ex.Message}");
            }
            finally
            {
                LeafApp.DeleteProfile(profile);
            }
        }

        // Tray Flyout: the main window parked small at the top left (the flyout needs it loaded), the shot taken around
        // the flyout's own popup by the tray
        var trayProfile = SeededProfile.Create(new LeafSettings { Theme = AppTheme.Light });
        _profiles.Add(trayProfile);
        try
        {
            MinimizeAll();
            using var leaf = AccessibilityTests.Open("TrayFlyout", _google, trayProfile, l =>
            {
                l.MainWindow.Patterns.Transform.Pattern.Move(0, 0);
                l.MainWindow.Patterns.Transform.Pattern.Resize(1086, 540);
            });
            Thread.Sleep(1000);
            // The Flyout Panel: the largest element above the Join button that isn't screen-sized (the host window
            // the flyout opens from is a small strip, so the top of the chain isn't it)
            var flyout = Rectangle.Empty;
            for (var e = leaf.WaitForPopup("FlyoutJoinButton"); e is not null; e = e.Parent)
            {
                var r = e.BoundingRectangle;
                if (r.Width < screen.Width * 9 / 10 && r.Width * r.Height > flyout.Width * flyout.Height)
                {
                    flyout = r;
                }
            }

            // Not Kept To The Main Screen: the tray can be on another monitor
            Shoot(Rectangle.Inflate(flyout, Margin, Margin), folder, "10-tray-flyout-light");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            failed.Add($"10-tray-flyout-light: {ex.Message}");
        }
        finally
        {
            LeafApp.DeleteProfile(trayProfile);
        }

        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    // Win+M: every other window goes down, so the desktop shows around Leaf
    private static void MinimizeAll()
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.LWIN, VirtualKeyShort.KEY_M);
        Thread.Sleep(1000);
    }

    // The main window at 80% of the screen, at most 1366 × 768, centered; its bounds once placed
    private static Rectangle Center(LeafApp leaf, Size screen)
    {
        var width = Math.Min(screen.Width * 4 / 5, 1366);
        var height = Math.Min(screen.Height * 4 / 5, 768);
        var transform = leaf.MainWindow.Patterns.Transform.Pattern;
        transform.Move((screen.Width - width) / 2, (screen.Height - height) / 2);
        transform.Resize(width, height);
        Thread.Sleep(500);
        return leaf.MainWindow.BoundingRectangle;
    }

    // The window grown by the margin on every side, kept on the screen
    private static Rectangle Around(Rectangle window, Size screen)
    {
        var area = Rectangle.Inflate(window, Margin, Margin);
        return Rectangle.Intersect(area, new Rectangle(Point.Empty, screen));
    }

    private static void Shoot(Rectangle area, string folder, string name) =>
        Capture.Rectangle(area).ToFile(Path.Combine(folder, $"{name}.png"));
}
