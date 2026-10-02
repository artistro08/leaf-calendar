// Track A (Milestone 5 Tasks 3-5) owns this file: the command menu, the cheat sheet, and time travel.
using System.ComponentModel;
using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // The command menu, built on first use and kept for the page's life
    Flyout? _commandFlyout;
    CommandMenu? _commandMenu;
    bool _dialogOpen;

    // The time travel and zone switch bars above the calendar
    TimeTravelBar? _travelBar;

    /// <summary>The command menu opened (true) or closed (false); the window dims behind it while it's open.</summary>
    public event EventHandler<bool>? CommandMenuShown;

    /// <summary>
    /// The Next month button's center, in window DIPs, wherever the sliding sidebar has it now; null while the sidebar
    /// takes no room or before it's laid out.
    /// </summary>
    /// <remarks>
    /// Measured against the sidebar (laid out, so no render offsets), then moved by how far its slot is from fully open:
    /// the sidebar keeps its width and rides the slot's edge.
    /// </remarks>
    public double? MiniMonthNextCenterX =>
        SidebarSlot.ActualWidth > 0 && Sidebar.MiniMonthNextButton is { ActualWidth: > 0 } next
            ? next.TransformToVisual(Sidebar).TransformPoint(new Windows.Foundation.Point(next.ActualWidth / 2, 0)).X + SidebarSlot.ActualWidth - SidebarWidth
            : null;

    /// <summary>
    /// With the sidebar closed, starts the period title after <paramref name="right"/> (the title bar search icon's right
    /// edge, in window DIPs), so the icon never covers it. With the sidebar open the title keeps its place.
    /// </summary>
    public void KeepTitleClearOf(double right)
    {
        if (IsSidebarOpen)
        {
            return;
        }

        // The search glyph's ink ends 8 in from its button's edge; the title starts the usual inset after it
        _titleClear = Math.Max(PaneToggleClearance + TitleInset, right - 8 + TitleInset);
        PlaceTitle();
    }

    // Called once when the page opens: the zone bars
    void AttachNavigate()
    {
        _travelBar = new TimeTravelBar(ViewModel);
        IslandBars.Children.Add(_travelBar);
        ViewModel.PropertyChanged += OnNavigatePropertyChanged;
        ViewModel.LayoutChanged   += OnNavigateLayoutChanged;
    }

    // Called from Detach: undo everything AttachNavigate wired to the long-lived view model (the menu holds it too)
    void DetachNavigate()
    {
        ViewModel.PropertyChanged -= OnNavigatePropertyChanged;
        ViewModel.LayoutChanged   -= OnNavigateLayoutChanged;
        if (_travelBar is not null)
        {
            IslandBars.Children.Remove(_travelBar);
            _travelBar = null;
        }

        CloseShortcutSheet();
        _commandFlyout?.Hide();
        _commandFlyout = null;
        _commandMenu   = null;
    }

    void OnNavigatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.TravelZoneId) or nameof(CalendarViewModel.ZoneSwitchOffer))
        {
            _travelBar?.Update(ViewModel);
        }
    }

    // Settings changes raise LayoutChanged: the zone on screen may have moved (SyncZone is a no-op when the zone
    // stayed, so its own LayoutChanged can't loop)
    void OnNavigateLayoutChanged(object? sender, EventArgs e)
    {
        ViewModel.SyncZone();
        _travelBar?.Update(ViewModel);
    }

    // =========================================================================
    // COMMAND MENU
    // =========================================================================

    // Ctrl+K, Ctrl+F, /, and the title bar's search icon: the menu opens empty, under the title bar, with focus in its box
    void OpenCommandMenu()
    {
        if (_commandFlyout is null || _commandMenu is null)
        {
            var menu   = new CommandMenu(ViewModel, RunCommandRow);
            var style  = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            // The presenter fits the 640 wide menu and its border exactly, and never scrolls sideways (a 640 cap, border
            // included, left the menu 2 DIPs too wide: a horizontal scrollbar, and the menu off center)
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
            style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
            style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));

            // The window dims behind the menu while it's open (closed by Esc, a pick, or a click outside it)
            var flyout = new Flyout { Content = menu, FlyoutPresenterStyle = style };
            flyout.Opened += (_, _) =>
            {
                menu.FocusBox();
                CommandMenuShown?.Invoke(this, true);
            };
            flyout.Closed += (_, _) => CommandMenuShown?.Invoke(this, false);

            (_commandFlyout, _commandMenu) = (flyout, menu);
        }

        _commandMenu.Reset();
        _commandFlyout.ShowAt(Root, new FlyoutShowOptions
        {
            Position  = new Windows.Foundation.Point(Root.ActualWidth / 2, 56),
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode  = FlyoutShowMode.Standard,
        });
    }

    // Runs the picked row (the menu closes first, so focus is back on the calendar for what the row opens)
    void RunCommandRow(CommandRow row, bool jump)
    {
        _commandFlyout?.Hide();

        // A Date Or An Event Hides A Showing Editor, Like Closing The Panel (the edit is kept; C or E brings it back)
        if (row.Kind is CommandRowKind.Event or CommandRowKind.Date && ViewModel.Editing is not null && IsDetailsOpen)
        {
            SetDetailsOpen(false, animate: true);
        }

        switch (row.Kind)
        {
            case CommandRowKind.Event when row.Hit is { } hit:
                ViewModel.OpenSearchHit(hit, jump);
                break;

            case CommandRowKind.Date when row.Date is { } date:
                ViewModel.NavigateTo(date);
                break;

            case CommandRowKind.Action when row.Item is { } item:
                if (item.Command != Core.Views.CalendarCommand.None)
                {
                    RunCommand(item.Command, item.Days);
                }
                else
                {
                    RunAction(item.Id);
                }

                break;
        }
    }

    // Actions with no shortcut, by ID; an unknown ID does nothing
    void RunAction(string id)
    {
        var vm = ViewModel;
        switch (id)
        {
            case "toggle-week-numbers":
                vm.Update(s => s with { ShowWeekNumbers = !s.ShowWeekNumbers });
                return;

            case "toggle-24-hour":
                vm.Update(s => s with { Use24HourTime = !s.Use24HourTime });
                return;

            case "toggle-working-hours":
                vm.Update(s => s with { WorkingHours = s.WorkingHours with { Enabled = !s.WorkingHours.Enabled } });
                return;

            case "sync":
                vm.Fire(vm.RefreshAsync);
                return;
        }

        // Settings Pages
        SettingsSection? section = id switch
        {
            "settings-general"       => SettingsSection.General,
            "settings-calendars"     => SettingsSection.Calendars,
            "settings-time-zones"    => SettingsSection.TimeZones,
            "settings-notifications" => SettingsSection.Notifications,
            "settings-tray"          => SettingsSection.Tray,
            "settings-shortcuts"     => SettingsSection.Shortcuts,
            "settings-accounts"      => SettingsSection.Accounts,
            "settings-about"         => SettingsSection.About,
            _                        => null,
        };
        if (section is { } page)
        {
            vm.OpenSettings?.Invoke(page);
        }
    }

    // =========================================================================
    // CHEAT SHEET
    // =========================================================================

    // The open cheat sheet panel, or null, and what had focus before it opened (focus goes back there when it closes)
    Border? _sheet;
    DependencyObject? _beforeSheet;

    // ?: the cheat sheet as a panel at the right of the calendar view, over it (? again, Esc, or its close button closes it)
    void ShowShortcutSheet()
    {
        if (_sheet is not null)
        {
            CloseShortcutSheet();
            return;
        }

        var (panel, filter) = ShortcutSheet.Panel(this, ViewModel.Settings, CloseShortcutSheet);
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Margin              = new Thickness(16);
        Grid.SetRow(panel, 2);
        Float(panel);
        Island.Children.Add(panel);
        _sheet       = panel;
        _beforeSheet = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        filter.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => filter.Focus(FocusState.Programmatic));
    }

    void CloseShortcutSheet()
    {
        if (_sheet is null)
        {
            return;
        }

        Island.Children.Remove(_sheet);
        _sheet = null;
        if (_beforeSheet is { } before)
        {
            _ = FocusManager.TryFocusAsync(before, FocusState.Programmatic);
        }

        _beforeSheet = null;
    }

    // Lifts a floating card (the cheat sheet) over the calendar view: raised 32 like a flyout, its
    // shadow falling on the view
    void Float(UIElement card)
    {
        card.Translation = new System.Numerics.Vector3(0, 0, 32);
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(ViewHost);
        card.Shadow = shadow;
    }

    // One dialog at a time: our own flag covers Leaf's sheet and time travel; any other dialog already open (a scope
    // question, the conflict dialog) makes WinUI refuse a second one, which is noted by type and otherwise ignored
    void ShowDialog(Func<Task> show, string eventName)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;
        ViewModel.Fire(async () =>
        {
            try
            {
                await show();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                ViewModel.LogInfo(eventName, $"error={ex.GetType().Name}");
            }
            finally
            {
                _dialogOpen = false;
            }
        }, eventName);
    }

    // =========================================================================
    // TIME TRAVEL
    // =========================================================================

    // Z: pick a zone to view the calendar in, for this session
    void StartTimeTravel() => ShowDialog(async () =>
    {
        if (await AskTravelZoneAsync() is { } zoneId)
        {
            ViewModel.TravelTo(zoneId);
        }
    }, "calendar.timetravel.failed");

    // The zone picker ("Go" waits for a picked suggestion). Suggestions go to the box as plain strings (a list of Core
    // records can't be marshaled to WinRT under Native AOT) and come back by matching our own list
    async Task<string?> AskTravelZoneAsync()
    {
        IReadOnlyList<TimeZoneChoice> suggestions = [];
        TimeZoneChoice? picked = null;

        var box = new AutoSuggestBox { PlaceholderText = "Search a city or zone (Tokyo, NYC, UTC)", Width = 360 };
        AutomationProperties.SetName(box, "Time zone");
        AutomationProperties.SetAutomationId(box, "TimeTravelBox");

        var dialog = new ContentDialog
        {
            XamlRoot               = XamlRoot,
            RequestedTheme         = ActualTheme,
            Title                  = "Time travel",
            Content                = box,
            PrimaryButtonText      = "Go",
            CloseButtonText        = "Cancel",
            DefaultButton          = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        // Typing Again Drops The Pick
        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            picked                        = null;
            dialog.IsPrimaryButtonEnabled = false;
            suggestions                   = TimeZoneCatalog.Search(sender.Text, ViewModel.Now);
            sender.ItemsSource            = suggestions.Select(c => c.ToString()).ToList();
        };
        box.SuggestionChosen += (_, args) =>
        {
            picked                        = args.SelectedItem is string text ? suggestions.FirstOrDefault(c => c.ToString() == text) : null;
            dialog.IsPrimaryButtonEnabled = picked is not null;
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? picked?.Id : null;
    }
}
