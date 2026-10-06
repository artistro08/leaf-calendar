// Track A (Milestone 5 Tasks 3-5) owns this file: the command menu, the cheat sheet, and time travel.
using System.ComponentModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // The command menu, built on first use and kept for the page's life
    private Flyout? _commandFlyout;
    private CommandMenu? _commandMenu;
    private Border? _commandAnchor;
    private bool _dialogOpen;

    // The time travel and zone switch bars above the calendar
    private TimeTravelBar? _travelBar;

    /// <summary>The command menu opened (true) or closed (false); the window dims behind it while it's open.</summary>
    public event EventHandler<bool>? CommandMenuShown;

    /// <summary>
    /// The Next month button's center, in window DIPs, with the sidebar open; null before the sidebar was first laid out.
    /// </summary>
    /// <remarks>
    /// Measured against the sidebar itself (which settles at the window's left edge), so a pane still sliding in doesn't
    /// move it. The last measure is kept while the sidebar is collapsed, so the search icon knows where it's headed
    /// the moment the sidebar starts to open.
    /// </remarks>
    public double? MiniMonthNextCenterX =>
        Sidebar.MiniMonthNextButton is { ActualWidth: > 0 } next
            ? _miniMonthNextCenterX = next.TransformToVisual(Sidebar).TransformPoint(new Windows.Foundation.Point(next.ActualWidth / 2, 0)).X
            : _miniMonthNextCenterX;

    private double? _miniMonthNextCenterX;

    /// <summary>
    /// With the sidebar closed, starts the period title after <paramref name="right"/> (the right edge the title bar search
    /// icon has with the sidebar closed, in window DIPs), so the icon never covers it. With the sidebar open the title
    /// keeps its usual inset.
    /// </summary>
    public void KeepTitleClearOf(double right)
    {
        // The search glyph's ink ends 8 in from its button's edge; the title starts the usual inset after it
        var left = Math.Max(PaneToggleClearance + TitleInset, right - 8 + TitleInset);
        if (left == _titleClosedLeft)
        {
            return;
        }

        _titleClosedLeft = left;
        PlaceTitle();
    }

    // The period title's inset with the sidebar closed (clear of the title bar's pane toggle and search icon)
    private double _titleClosedLeft = PaneToggleClearance + TitleInset;

    // The period title's inset for the sidebar as it is now, eased with the sidebar's edge while it slides
    private void PlaceTitle()
    {
        var left = IsSidebarOpen ? TitleInset : _titleClosedLeft;
        PeriodTitle.Margin = new Thickness(left, 9, 0, 8);
        RideSidebarEdge(PeriodTitle, _titleClosedLeft, TitleInset, left);
    }

    // Called once when the page opens: the zone bars
    private void AttachNavigate()
    {
        // At The Bottom, With The Other Toasts (first, above the notice)
        _travelBar = new TimeTravelBar(ViewModel);
        Toasts.Children.Insert(0, _travelBar);
        FloatWhileOpen(_travelBar.TravelBar);
        FloatWhileOpen(_travelBar.ZoneSwitchBar);
        ViewModel.PropertyChanged += OnNavigatePropertyChanged;
        ViewModel.LayoutChanged += OnNavigateLayoutChanged;
    }

    // Called from Detach: undo everything AttachNavigate wired to the long-lived view model (the menu holds it too)
    private void DetachNavigate()
    {
        ViewModel.PropertyChanged -= OnNavigatePropertyChanged;
        ViewModel.LayoutChanged -= OnNavigateLayoutChanged;
        if (_travelBar is not null)
        {
            Toasts.Children.Remove(_travelBar);
            _travelBar = null;
        }

        CloseShortcutSheet();
        _commandFlyout?.Hide();
        _commandFlyout = null;
        _commandMenu = null;
        if (_commandAnchor is not null)
        {
            Root.SizeChanged -= OnCommandRootSizeChanged;
            Root.Children.Remove(_commandAnchor);
            _commandAnchor = null;
        }
    }

    private void OnNavigatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.TravelZoneId) or nameof(CalendarViewModel.ZoneSwitchOffer))
        {
            _travelBar?.Update(ViewModel);
        }
    }

    // Settings changes raise LayoutChanged: the zone on screen may have moved (SyncZone is a no-op when the zone
    // stayed, so its own LayoutChanged can't loop)
    private void OnNavigateLayoutChanged(object? sender, EventArgs e)
    {
        ViewModel.SyncZone();
        _travelBar?.Update(ViewModel);
    }

    // =========================================================================
    // COMMAND MENU
    // =========================================================================

    // Ctrl+K, Ctrl+F, /, and the title bar's search icon: the menu opens empty, under the title bar, with focus in its box
    private void OpenCommandMenu()
    {
        if (_commandFlyout is null || _commandMenu is null)
        {
            var menu = new CommandMenu(ViewModel, RunCommandRow);
            var style = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            // The presenter fits the 640 wide menu and its border exactly, and never scrolls sideways (a 640 cap, border
            // included, left the menu 2 DIPs too wide: a horizontal scrollbar, and the menu off center)
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
            style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
            style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));

            // The window dims behind the menu while it's open (closed by Esc, a pick, or a click outside it). No open or
            // close animation: the menu is a keyboard flow, so it's simply there
            var flyout = new Flyout { Content = menu, FlyoutPresenterStyle = style, AreOpenCloseAnimationsEnabled = false };
            flyout.Opened += (_, _) =>
            {
                menu.FocusBox();
                CommandMenuShown?.Invoke(this, true);
            };
            flyout.Closed += (_, _) =>
            {
                CommandMenuShown?.Invoke(this, false);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ReturnFocusAfterMenu);
            };

            (_commandFlyout, _commandMenu) = (flyout, menu);
        }

        // The flyout hangs off an invisible anchor that sits at the window's horizontal center (Root spans the whole
        // window; the old fixed point 56 DIPs down read as "under the title bar", and was set once at open). The anchor
        // re-places itself on every resize, so the menu stays centered while it's open.
        if (_commandAnchor is null)
        {
            _commandAnchor = new Border { Width = 640, Height = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            Root.Children.Add(_commandAnchor);
            Root.SizeChanged += OnCommandRootSizeChanged;
        }

        // The Events Are Read For The Search Before The First Keystroke
        ViewModel.WarmSearch();
        _beforeMenu = FocusManager.GetFocusedElement(XamlRoot);
        PlaceCommandAnchor();
        _commandMenu.Reset();
        _commandFlyout.ShowAt(_commandAnchor, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private void OnCommandRootSizeChanged(object sender, SizeChangedEventArgs e) => PlaceCommandAnchor();

    // What had focus when the command menu opened
    private object? _beforeMenu;

    // After the menu closes (and the picked row ran): focus the row put in a box, menu, or dialog stays, and focus the menu
    // gave back to where it was stays; anything else (nothing, or Windows' fallback, the mini month's first chevron, whose
    // ring then showed and which Space paged) rests on the calendar instead
    private void ReturnFocusAfterMenu()
    {
        var before = _beforeMenu;
        _beforeMenu = null;
        if (ShortcutsBlocked() || (before is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), before)))
        {
            return;
        }

        FocusCalendar();
    }

    // The menu's top is where a full size menu (search row, the results at their tallest, footer) would be centered
    // vertically, so it doesn't jump as the results grow and shrink; a short window clamps it and shortens the list
    private void PlaceCommandAnchor()
    {
        if (_commandAnchor is null || _commandMenu is null)
        {
            return;
        }

        const double FullHeight = 56 + 1 + CommandMenu.ResultsMaxHeight + 40;
        var height = Root.ActualHeight;
        var top = Math.Max(8, (height - FullHeight) / 2);
        _commandAnchor.Margin = new Thickness(0, top, 0, 0);
        _commandMenu.LimitResultsHeight(Math.Max(96, height - top - 8 - 96));
    }

    // Runs the picked row (the menu closes first, so focus is back on the calendar for what the row opens)
    private void RunCommandRow(CommandRow row, bool jump)
    {
        // Jump To Date Stays In The Menu: it asks for the date there
        if (row.Item is { Command: Core.Views.CalendarCommand.GoToDate })
        {
            _commandMenu?.AskForDate();
            return;
        }

        _commandFlyout?.Hide();

        // A Row Hides A Showing Editor, Like Closing The Panel (the edit is kept; C or E brings it back)
        if (EditorShowing)
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
                var editorBefore = ViewModel.Editing;
                if (item.Command != Core.Views.CalendarCommand.None)
                {
                    RunCommand(item.Command, item.Days);
                }
                else
                {
                    RunAction(item.Id);
                }

                // "Create Event “…”" Titles The New Event (a hidden editor it brought back keeps its own title)
                if (row.EventTitle.Length > 0 && ViewModel.Editing is { } editor && editor != editorBefore)
                {
                    editor.Title = row.EventTitle;
                }

                break;
        }
    }

    // Actions with no shortcut, by ID; an unknown ID does nothing
    private void RunAction(string id)
    {
        var vm = ViewModel;
        switch (id)
        {
            case "quit":
                vm.QuitApp?.Invoke();
                return;

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
                vm.Fire(vm.SyncNowAsync, "sync.now.failed");
                return;
        }

        // Settings Pages
        SettingsSection? section = id switch
        {
            "settings-general" => SettingsSection.General,
            "settings-calendars" => SettingsSection.Calendars,
            "settings-time-zones" => SettingsSection.TimeZones,
            "settings-notifications" => SettingsSection.Notifications,
            "settings-tray" => SettingsSection.Tray,
            "settings-shortcuts" => SettingsSection.Shortcuts,
            "settings-accounts" => SettingsSection.Accounts,
            "settings-about" => SettingsSection.About,
            _ => null,
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
    private Border? _sheet;
    private DependencyObject? _beforeSheet;

    // ?: the cheat sheet as a panel at the left of the calendar view, over it (? again, Esc, or its close button closes it)
    private void ShowShortcutSheet()
    {
        if (_sheet is not null)
        {
            CloseShortcutSheet();
            return;
        }

        var (panel, filter) = ShortcutSheet.Panel(this, ViewModel.Settings, CloseShortcutSheet);
        panel.HorizontalAlignment = HorizontalAlignment.Left;

        // 32 from the top clears the corner's time zones button (4 down, about 22 tall), which the card cut through at 16
        panel.Margin = new Thickness(16, 32, 16, 16);
        Grid.SetRow(panel, 2);
        Float(panel);
        Island.Children.Add(panel);
        _sheet = panel;
        SlideSheet(panel, show: true);
        _beforeSheet = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        filter.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => filter.Focus(FocusState.Programmatic));
    }

    private void CloseShortcutSheet()
    {
        if (_sheet is null)
        {
            return;
        }

        // It slides back out to the left, then goes
        var sheet = _sheet;
        _sheet = null;
        SlideSheet(sheet, show: false);
        if (_beforeSheet is { } before)
        {
            _ = FocusManager.TryFocusAsync(before, FocusState.Programmatic);
        }

        _beforeSheet = null;
    }

    // Lifts a floating card (the cheat sheet) over the calendar view: raised 32 like a flyout, its
    // shadow falling on the view
    // The sheet flies in from the left, like the keyboard buttons it opens from: it starts just past the island's left
    // edge, which the island clips at, so it comes out from behind the sidebar when that's open (or from the window's
    // edge), and goes back the same way
    private static readonly TimeSpan SheetSlide = TimeSpan.FromMilliseconds(250);

    private void SlideSheet(Border sheet, bool show)
    {
        var lift = sheet.Translation.Z;
        var away = new System.Numerics.Vector3(-(float)(ShortcutSheet.PanelWidth + sheet.Margin.Left), 0, lift);
        var home = new System.Numerics.Vector3(0, 0, lift);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            if (!show)
            {
                Island.Children.Remove(sheet);
            }

            return;
        }

        if (show)
        {
            // Placed off to the side first, then the transition carries it in once it's on screen
            sheet.Translation = away;
            sheet.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                // Closed Before It Came On Screen: it stays out (its own close slid it away)
                if (_sheet != sheet)
                {
                    return;
                }

                sheet.TranslationTransition = new Vector3Transition { Duration = SheetSlide };
                sheet.Translation = home;
            });
            return;
        }

        sheet.IsHitTestVisible = false;
        sheet.TranslationTransition = new Vector3Transition { Duration = SheetSlide };
        sheet.Translation = away;

        // One Sheet Leaving At A Time: one still sliding out from an earlier close goes now, so none is left behind
        _sheetGone?.Stop();
        RemoveLeavingSheet();
        _sheetLeaving = sheet;
        _sheetGone = DispatcherQueue.CreateTimer();
        _sheetGone.Interval = SheetSlide;
        _sheetGone.IsRepeating = false;
        _sheetGone.Tick += (_, _) => RemoveLeavingSheet();
        _sheetGone.Start();
    }

    // Removes a closed sheet once it has slid out (held so it lives until it fires), and the sheet it removes
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sheetGone;
    private Border? _sheetLeaving;

    private void RemoveLeavingSheet()
    {
        if (_sheetLeaving is { } leaving)
        {
            Island.Children.Remove(leaving);
            _sheetLeaving = null;
        }
    }

    // Raises a card (the cheat sheet, a toast at the bottom) 32 over the calendar view, which takes its shadow
    private void Float(UIElement card) => Raise(card, raised: true);

    // A bar at the bottom is raised only while it's open: a closed InfoBar keeps its place in the toasts, and raised it
    // left a ghost shadow on the calendar
    private void FloatWhileOpen(InfoBar bar)
    {
        bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) => Raise(bar, bar.IsOpen));
        Raise(bar, bar.IsOpen);
    }

    private void Raise(UIElement card, bool raised)
    {
        if (!raised)
        {
            card.Translation = System.Numerics.Vector3.Zero;
            card.Shadow = null;
            return;
        }

        card.Translation = new System.Numerics.Vector3(0, 0, 32);
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(ViewHost);
        card.Shadow = shadow;
    }

    // One dialog at a time: our own flag covers Leaf's sheet and time travel; any other dialog already open (a scope
    // question, the conflict dialog) makes WinUI refuse a second one, which is noted by type and otherwise ignored
    private void ShowDialog(Func<Task> show, string eventName)
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
    private void StartTimeTravel() => ShowDialog(async () =>
    {
        if (await AskTravelZoneAsync() is { } zoneId)
        {
            ViewModel.TravelTo(zoneId);
        }
    }, "calendar.timetravel.failed");

    // The zone picker ("Go" waits for a picked suggestion). Suggestions go to the box as rows of plain strings
    // (ZoneSuggestions; a list of Core records can't be marshaled to WinRT under Native AOT), the zone you're in disabled,
    // and come back by matching our own rows. The box's text is written here, from the pick, not from the row
    private async Task<string?> AskTravelZoneAsync()
    {
        IReadOnlyList<TimeZoneChoice> suggestions = [];
        List<ListViewItem> rows = [];
        TimeZoneChoice? picked = null;

        var box = new AutoSuggestBox { PlaceholderText = "Search a city or zone (Tokyo, NYC, UTC)", Width = 360, UpdateTextOnSelect = false };
        AutomationProperties.SetName(box, "Time zone");
        AutomationProperties.SetAutomationId(box, "TimeTravelBox");
        Controls.FirstSuggestion.Highlight(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Time travel",
            Content = box,
            PrimaryButtonText = "Go",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        // Typing Again Drops The Pick
        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            picked = null;
            dialog.IsPrimaryButtonEnabled = false;
            suggestions = TimeZoneCatalog.Search(sender.Text, ViewModel.Now);
            rows = Controls.ZoneSuggestions.Rows(suggestions, ViewModel.Zone);
            sender.ItemsSource = rows;
        };
        // The highlighted suggestion (the first one as they list, or where Up/Down moved to) is the pick, so Go takes it;
        // the typed text stays until Enter
        box.SuggestionChosen += (_, args) =>
        {
            picked = Controls.ZoneSuggestions.Chosen(rows, suggestions, args.SelectedItem);
            dialog.IsPrimaryButtonEnabled = picked is not null;
        };
        // Enter: the highlighted suggestion (else the first one) fills the box; Enter on a filled-in pick goes. Esc: a
        // typed search is cleared; Esc on an empty box closes (the dialog's own Esc)
        var went = false;
        box.QuerySubmitted += (sender, args) =>
        {
            if (picked is not null && sender.Text == picked.ToString())
            {
                went = true;
                dialog.Hide();
            }
            else if (args.ChosenSuggestion is not null)
            {
                if (picked is not null)
                {
                    sender.Text = picked.ToString();
                }
            }
            else if (rows.Count > 0 && Controls.ZoneSuggestions.Chosen(rows, suggestions, rows[0]) is { } first)
            {
                picked = first;
                dialog.IsPrimaryButtonEnabled = true;
                sender.Text = first.ToString();
            }
        };
        box.PreviewKeyDown += (sender, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && box.Text.Length > 0)
            {
                picked = null;
                dialog.IsPrimaryButtonEnabled = false;
                box.Text = "";
                box.ItemsSource = null;
                e.Handled = true;
            }
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        Controls.CtrlEnter.Submits(dialog);

        var result = await dialog.ShowAsync();
        return went || result == ContentDialogResult.Primary ? picked?.Id : null;
    }
}
