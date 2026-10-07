using FlaUI.Core.AutomationElements;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class ScreenshotTour : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly List<string> _profiles = [];

    // Settings Pages And Window Widths For Capture_SettingsPages (0 is the minimum)
    private static readonly string[] SettingsPages = ["General", "Calendars", "TimeZones", "Notifications", "Tray", "Shortcuts", "Accounts", "About"];
    private static readonly int[] SettingsWidths = [0, 1500];

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

        var only = Environment.GetEnvironmentVariable("LEAF_SCREENS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var screens = AccessibilityTests.ScreenNames.Where(s => only is null || only.Contains(s, StringComparer.OrdinalIgnoreCase));
        var failed = new List<string>();

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

                        // The Tray Flyout And Menu Open In Popups Of Their Own (no window to capture), so the screen around them
                        if (screen.StartsWith("Tray", StringComparison.Ordinal))
                        {
                            FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(folder, $"{screen}-{theme.ToString().ToLowerInvariant()}-{width}-screen.png"));
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
    /// Every Settings page in light and dark, with the main window at its minimum and wide (1500 px), so the capped,
    /// centered column and the pane's collapse both show. Set LEAF_SCREENSHOTS to run it (LEAF_SCREENS narrows
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

        var only = Environment.GetEnvironmentVariable("LEAF_SCREENS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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
                var settings = leaf.OpenSettings(page);
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
                    else if (page == "Notifications")
                    {
                        leaf.ExpandInSettings("JoinNowExpander");
                    }

                    settings.SetForeground();
                    Thread.Sleep(400);
                    settings.CaptureToFile(Path.Combine(folder, $"settings-{page.ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}-{(width == 0 ? "narrow" : "wide")}.png"));

                    // General's Working Hours, Further Down (an expander: its rows show once it's open)
                    if (page == "General")
                    {
                        leaf.ExpandInSettings("WorkingHoursExpander");
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
            var name = theme.ToString().ToLowerInvariant();
            var profile = SeededProfile.Create(new LeafSettings { Theme = theme, JoinShortcut = "Ctrl+Alt+Shift+F9", FlyoutShortcut = "Ctrl+Alt+Shift+F10" });
            _profiles.Add(profile);
            using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
            leaf.WaitFor("SettingsButton");
            var settings = leaf.OpenSettings("Shortcuts");
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

    /// <summary>
    /// The screens the main tour doesn't reach, in light and dark at 1366 × 768: the day, month and 4-day views, the view
    /// menu, an event's right-click menu, a new event, the undo notice after a delete, the offline icons after 3 failed
    /// syncs, search results in the command menu, and onboarding's first two steps. Set LEAF_SCREENSHOTS to run it.
    /// </summary>
    [Fact]
    public void Capture_MoreScreens()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the extra screens.");
        }

        const string dentist = "Event_evt-single_202610011300";
        var screens = new (string Name, Func<AppTheme, LeafSettings> Settings, Action<LeafApp> Open)[]
        {
            ("DayView", t => new LeafSettings { Theme = t, ViewMode = CalendarViewMode.Day }, l => l.WaitFor(dentist)),
            ("MonthView", t => new LeafSettings { Theme = t, ViewMode = CalendarViewMode.Month }, l => l.WaitFor("PeriodTitle")),
            ("FourDays", t => new LeafSettings { Theme = t, ViewMode = CalendarViewMode.Days, CustomDayCount = 4 }, l => l.WaitFor(dentist)),
            ("ViewMenu", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist); l.WaitFor("ViewModeButton").Click(); l.WaitForAnywhere("ViewDay"); }),
            ("EventMenu", t => new LeafSettings { Theme = t }, l => l.WaitFor(dentist).RightClick()),
            ("NewEvent", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist); l.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_C); l.WaitFor("EditorTitle"); }),
            ("DeleteUndo", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist).Click(); l.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DELETE); }),
            ("Offline", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist); _google.Offline = true; l.SyncUntilOffline(); }),
            ("Search", t => new LeafSettings { Theme = t }, l =>
            {
                l.WaitFor(dentist);
                l.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_K);
                l.WaitForAnywhere("CommandSearchBox").Focus();
                FlaUI.Core.Input.Keyboard.Type("dent");
            }),
            ("MeetWith", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist); l.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_F); l.WaitForAnywhere("PeoplePickerBox"); }),

            // Saved Share Groups (Eastern time, which the share helpers' drags measure by): new picks, a saved group on
            // the grid, the group open in the panel, and the approve editor
            ("SharePicks", t => new LeafSettings { Theme = t, PrimaryTimeZone = "America/New_York" }, l => { ShareAvailabilityTests.StartSharing(l); ShareAvailabilityTests.DragHours(l, 10, 12); l.WaitFor("ShareSlot_0"); }),
            ("SavedTimes", t => new LeafSettings { Theme = t, PrimaryTimeZone = "America/New_York" }, SavedShareGroupTests.Save),
            ("SavedGroup", t => new LeafSettings { Theme = t, PrimaryTimeZone = "America/New_York" }, l => { SavedShareGroupTests.Save(l); SavedShareGroupTests.OpenFirst(l); }),
            ("SavedApprove", t => new LeafSettings { Theme = t, PrimaryTimeZone = "America/New_York" }, SavedShareGroupTests.SaveAndApprove),

            // Wide Windows (2200 x 1100 screen pixels): at 125% scale 1366 x 768 is barely past the minimum
            ("WideWeek", t => new LeafSettings { Theme = t }, l => l.WaitFor(dentist)),
            ("WideMonth", t => new LeafSettings { Theme = t, ViewMode = CalendarViewMode.Month }, l => l.WaitFor("PeriodTitle")),
            ("WideDetails", t => new LeafSettings { Theme = t }, l => l.WaitFor(dentist).Click()),
            ("WideEditor", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist).Click(); l.WaitFor("DetailsEditButton").AsButton().Invoke(); l.WaitFor("EditorTitle"); }),
            ("WideSettings", t => new LeafSettings { Theme = t }, l => { l.WaitFor(dentist); l.OpenSettings("General"); }),
            ("WideSavedGroup", t => new LeafSettings { Theme = t, PrimaryTimeZone = "America/New_York" }, l => { SavedShareGroupTests.Save(l); SavedShareGroupTests.OpenFirst(l); }),
        };

        Directory.CreateDirectory(folder);
        var failed = new List<string>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            var name = theme.ToString().ToLowerInvariant();
            foreach (var (screen, settings, open) in screens)
            {
                _google.Offline = false;
                var profile = SeededProfile.Create(settings(theme));
                _profiles.Add(profile);
                try
                {
                    using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
                    var wide = screen.StartsWith("Wide", StringComparison.Ordinal);
                    Place(leaf, wide ? 2200 : 1366, wide ? 1100 : 768);
                    open(leaf);
                    Thread.Sleep(800);
                    CaptureAll(leaf, folder, $"more-{screen}-{name}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
                {
                    failed.Add($"{screen} {theme}: {ex.Message}");
                }
                finally
                {
                    LeafApp.DeleteProfile(profile);
                }
            }

            // Onboarding (a profile with no account, only the theme saved), the welcome step and the OAuth client step
            var fresh = LeafApp.NewProfile();
            _profiles.Add(fresh);
            var database = new Core.Data.LeafDatabase(Path.Combine(LeafApp.ProfileFolder(fresh), "leaf.db"));
            database.Migrate();
            using (var conn = database.Open())
            {
                Core.Settings.SettingsStore.Save(conn, new LeafSettings { Theme = theme });
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                using var leaf = LeafApp.Launch(fresh, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
                leaf.WaitInOnboarding("OnboardingPrimaryButton");
                Thread.Sleep(800);
                CaptureAll(leaf, folder, $"more-OnboardingWelcome-{name}");
                leaf.OnboardingPrimary();
                Thread.Sleep(1200);
                CaptureAll(leaf, folder, $"more-OnboardingClient-{name}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
            {
                failed.Add($"Onboarding {theme}: {ex.Message}");
            }
        }

        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    /// <summary>
    /// The command menu with what people type, in light and dark: nothing, an event, actions by name, a date in words,
    /// a view, a setting, nothing found, and the Jump to date mode. Each query is captured as typed, then run with Enter
    /// and captured again. Every query starts from a fresh launch, so what one command did never shows in the next.
    /// Set LEAF_SCREENSHOTS to run it.
    /// </summary>
    [Fact]
    public void Capture_CommandMenu()
    {
        var folder = Environment.GetEnvironmentVariable("LEAF_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set LEAF_SCREENSHOTS to a folder to capture the command menu.");
        }

        // The Query, And Whether The Menu Opens In Jump To Date Mode ("." on the calendar)
        (string Query, bool JumpToDate)[] queries =
        [
            ("", false), ("dent", false), ("design", false), ("new", false), ("settings", false), ("today", false),
            ("month", false), ("next friday", false), ("oct 15", false), ("sync", false), ("dark", false), ("zzzzqq", false),
            ("", true), ("two weeks from now", true),
        ];
        Directory.CreateDirectory(folder);
        var failed = new List<string>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            var name = theme.ToString().ToLowerInvariant();
            for (var i = 0; i < queries.Length; i++)
            {
                var (query, jump) = queries[i];
                var label = $"palette-{i:00}-{(jump ? "jump-" : "")}{(query.Length == 0 ? "empty" : string.Concat(query.Split(' ')))}-{name}";
                var profile = SeededProfile.Create(new LeafSettings { Theme = theme });
                _profiles.Add(profile);
                try
                {
                    using var leaf = LeafApp.Launch(profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
                    Place(leaf, 1366, 768);
                    leaf.WaitFor("Event_evt-single_202610011300");
                    if (jump)
                    {
                        leaf.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.OEM_PERIOD);
                    }
                    else
                    {
                        leaf.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_K);
                    }

                    leaf.WaitForAnywhere("CommandSearchBox").Focus();
                    FlaUI.Core.Input.Keyboard.Type(query);
                    Thread.Sleep(700);
                    CaptureAll(leaf, folder, label + "-typed");

                    // Run It
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                    Thread.Sleep(1500);
                    CaptureAll(leaf, folder, label + "-ran");
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
                {
                    failed.Add($"{label}: {ex.Message}");
                }
                finally
                {
                    LeafApp.DeleteProfile(profile);
                }
            }
        }

        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    // Every window the app has open (the main window, popups, onboarding), one file each
    private static void CaptureAll(LeafApp leaf, string folder, string prefix)
    {
        foreach (var window in leaf.AllWindows())
        {
            var title = string.Concat((window.Properties.Name.ValueOrDefault ?? "popup").Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
            window.CaptureToFile(Path.Combine(folder, $"{prefix}-{title}.png"));
        }
    }

    // Sizes The Window (screen pixels) At LEAF_SCREENS_X From The Left (default 40), So A Window Pinned On Top Elsewhere Stays Out Of The Shots
    private static void Place(LeafApp leaf, int width, int height)
    {
        var x = int.TryParse(Environment.GetEnvironmentVariable("LEAF_SCREENS_X"), out var left) ? left : 40;
        leaf.MainWindow.Patterns.Transform.Pattern.Move(x, 40);
        leaf.MainWindow.Patterns.Transform.Pattern.Resize(width, height);
        Thread.Sleep(500);
    }
}
