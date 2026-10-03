using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace LeafCalendar.App.Tray;

/// <summary>
/// The tray's invisible host window (Layers' pattern): the right-click menu (spec 8.3) and the left-click flyout
/// (spec 8.2) open from it as stock controls in popups of their own, placed by the taskbar edge.
/// </summary>
/// <remarks>
/// <para>
/// The flyout is a 360 × 560 DIP panel of always-active desktop acrylic (design standard 2) next to the tray icon, 12
/// DIPs from the taskbar and the screen edges. It slides in from the taskbar edge with a fade (250 ms decelerate) and
/// back out (167 ms accelerate) when it closes by Esc, focus leaving, or a second click; the popup's edge on the
/// taskbar side clips the slide. Stock light dismiss, Esc, focus, and screen reader support come from the
/// <c>Flyout</c>. The host is created once and kept hidden, so the flyout opens at once; only Quit closes it.
/// </para>
/// <para>
/// The flyout shows the next event with a large Join button, the agenda by day with a Join button per meeting, and "New
/// event". Its rows are App records (AOT list rule) whose clicks are closures over their event (AOT read-back rule).
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable.")]
public sealed partial class TrayHost : Window
{
    // Fluent Motion (design standard 11)
    static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan ExitDuration  = TimeSpan.FromMilliseconds(167);

    // The icon click that closed the flyout or menu (by taking focus, or a press outside) arrives just after the close,
    // so it mustn't open either again
    const long ReopenGuardMs = 300;

    readonly AppLog _log;
    Action? _pendingOpen;
    TaskbarEdge _edge;
    AgendaModel? _model;
    Storyboard? _motion;
    long _agendaClosedAt;
    long _menuDismissedAt;
    long _agendaDismissedAt;
    bool _exitFinished;
    bool _shuttingDown;

    /// <summary>Creates the hidden host; a failed open is logged to <paramref name="log"/> by event name and error type.</summary>
    public TrayHost(AppLog log)
    {
        _log = log;
        InitializeComponent();
        InvisibleHost.Apply(this);
        ScrollIndicator.ShowOnHover(AgendaScroll);

        // A host shown for the first time loads its content a moment later, so the open waits for it
        Root.Loaded += (_, _) => RunPendingOpen();

        // A Press Outside Closes The Menu Or Flyout (watched only while one is open)
        Menu.Opened += (_, _) => WatchPresses();

        // Only Quit Really Closes It
        AppWindow.Closing += (_, e) =>
        {
            if (!_shuttingDown)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    /// <summary>Open Leaf Calendar.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>New event (the menu or the flyout's footer).</summary>
    public event EventHandler? NewEventRequested;

    /// <summary>Join next meeting.</summary>
    public event EventHandler? JoinNextRequested;

    /// <summary>Sync now.</summary>
    public event EventHandler? SyncRequested;

    /// <summary>Settings….</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The flyout opened.</summary>
    public event EventHandler? AgendaOpened;

    /// <summary>The flyout closed.</summary>
    public event EventHandler? AgendaClosed;

    /// <summary>A flyout row was clicked: show that event in the main window.</summary>
    public event EventHandler<CalendarOccurrence>? OpenEventRequested;

    /// <summary>A flyout Join button was clicked.</summary>
    public event EventHandler<CalendarOccurrence>? JoinRequested;

    /// <summary>True while the flyout is open.</summary>
    public bool IsAgendaOpen => Agenda.IsOpen;

    /// <summary>Opens the menu for a right-click at a screen point (physical pixels), growing away from the taskbar.</summary>
    public void ShowMenu(int x, int y, AppTheme theme)
    {
        // The Icon Click That Just Closed The Menu (the press watch) Mustn't Open It Again On Its Release; one that closed
        // the flyout opens the menu as usual
        if (Environment.TickCount64 - _menuDismissedAt < ReopenGuardMs)
        {
            return;
        }

        try
        {
            // One Popup At A Time (a second right-click moves the menu). The flyout goes at once, not by its slide: closing
            // as the menu opened, it took the menu's focus with it and the menu light-dismissed too
            CloseAgendaAtOnce();
            if (Menu.IsOpen)
            {
                Menu.Hide();
            }

            var screen   = TrayScreen.At(x, y);
            var (ax, ay) = TrayPlacement.MenuAnchor(x, y, screen.Area, screen.Edge, screen.Scale);
            Root.RequestedTheme = MainWindow.ElementThemeOf(theme);
            Open(ax, ay, screen.Scale, position => Menu.ShowAt(Root, new FlyoutShowOptions { Position = position, Placement = MenuPlacement(screen.Edge) }));
        }
        catch (Exception ex)
        {
            Fail("tray.menu.open.failed", ex);
        }
    }

    /// <summary>Opens the flyout next to the tray icon (or at the primary taskbar's far end when its place is unknown).</summary>
    public void ShowAgenda(AgendaModel model, PixelRect? icon, AppTheme theme)
    {
        if (Agenda.IsOpen || Environment.TickCount64 - _agendaClosedAt < ReopenGuardMs || Environment.TickCount64 - _agendaDismissedAt < ReopenGuardMs)
        {
            return;
        }

        try
        {
            if (Menu.IsOpen)
            {
                Menu.Hide();
            }

            // Placement: the panel next to the icon, opened from the frame's corner on the taskbar side
            var screen   = icon is { } r ? TrayScreen.At((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2) : TrayScreen.Primary();
            var panel    = TrayPlacement.Flyout(screen.Area, screen.Edge, icon, screen.Scale);
            var (ax, ay) = TrayPlacement.FlyoutAnchor(TrayPlacement.Frame(panel, screen.Scale), screen.Edge);
            _edge                      = screen.Edge;
            AgendaPanel.Width          = panel.Width / screen.Scale;
            AgendaPanel.Height         = panel.Height / screen.Scale;
            Root.RequestedTheme        = MainWindow.ElementThemeOf(theme);
            AgendaFrame.RequestedTheme = MainWindow.ElementThemeOf(theme);
            UpdateAgenda(model);

            Open(ax, ay, screen.Scale, position => Agenda.ShowAt(Root, new FlyoutShowOptions
            {
                Position  = position,
                Placement = AgendaPlacement(screen.Edge),
                ShowMode  = FlyoutShowMode.Standard,
            }));
        }
        catch (Exception ex)
        {
            Fail("tray.flyout.open.failed", ex);
        }
    }

    /// <summary>Shows new content in the flyout (a sync or the minute clock while it's open).</summary>
    public void UpdateAgenda(AgendaModel model)
    {
        _model = model;

        // Next Up
        var next = model.Next;
        NextPanel.Visibility       = next is null ? Visibility.Collapsed : Visibility.Visible;
        NothingNextText.Visibility = next is null ? Visibility.Visible : Visibility.Collapsed;
        NothingNextText.Text       = model.NothingNext;
        NextTitle.Text             = next?.Item.Title ?? "";
        NextWhen.Text              = next is null ? "" : $"{next.Item.When} · {next.Countdown}";
        NextJoinButton.Visibility  = next?.Item.Link is null ? Visibility.Collapsed : Visibility.Visible;
        NextJoinLogo.Provider      = next?.Item.Link is { } link ? LinkSafety.ProviderOf(link) : null;

        // Agenda
        List<AgendaDayRow> days = [.. model.Days.Select(d => new AgendaDayRow(d.Header, [.. d.Items.Select(Row)]))];
        AgendaDays.ItemsSource = days;
        AgendaEmpty.Visibility = days.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes the flyout (it slides out first).</summary>
    public void HideAgenda()
    {
        if (Agenda.IsOpen)
        {
            Agenda.Hide();
        }
    }

    // Closes the flyout without its slide (also ending a slide-out a click on the taskbar already started)
    void CloseAgendaAtOnce()
    {
        if (!Agenda.IsOpen)
        {
            return;
        }

        _motion?.Stop();
        _motion       = null;
        _exitFinished = true;
        Agenda.Hide();
    }

    /// <summary>Lets the window really close (Quit).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        MouseDownWatch.Stop();
        Close();
    }

    // =========================================================================
    // OPENING
    // =========================================================================

    // Moves the host to the anchor (twice: crossing into a monitor with another scale resizes it), shows it, takes the
    // foreground (light dismiss needs it), then opens at the anchor in DIPs from the host's client origin
    void Open(int x, int y, double scale, Action<Point> open)
    {
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(true);
        Activate();
        if (!InvisibleHost.TakeForeground(this))
        {
            // Without the foreground, light dismiss may not close the popup; Esc and its own buttons still do
            _log.Info("tray.host.foreground.refused");
        }

        var origin   = InvisibleHost.ClientOrigin(this);
        var position = new Point((x - origin.X) / scale, (y - origin.Y) / scale);
        _pendingOpen = () => open(position);
        if (Root.IsLoaded)
        {
            RunPendingOpen();
        }
    }

    // Also runs from Root.Loaded, where an escaping exception would end the process
    void RunPendingOpen()
    {
        var open = _pendingOpen;
        _pendingOpen = null;
        try
        {
            open?.Invoke();
        }
        catch (Exception ex)
        {
            Fail("tray.host.open.failed", ex);
        }
    }

    // A failed open must never strand the invisible topmost host on screen; the type only, never content
    void Fail(string eventName, Exception ex)
    {
        _log.Info(eventName, $"error={ex.GetType().Name}");
        try
        {
            _pendingOpen = null;
            HideHostIfIdle();
        }
        catch (Exception hideEx)
        {
            _log.Info("tray.host.hide.failed", $"error={hideEx.GetType().Name}");
        }
    }

    // Grows away from the taskbar (Sony Control's tray menu)
    static FlyoutPlacementMode MenuPlacement(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedRight,
        TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
        TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        _                 => FlyoutPlacementMode.TopEdgeAlignedRight,
    };

    // The frame's corner on the taskbar side sits on the anchor (TrayPlacement.FlyoutAnchor)
    static FlyoutPlacementMode AgendaPlacement(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedLeft,
        TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
        TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        _                 => FlyoutPlacementMode.TopEdgeAlignedLeft,
    };

    // =========================================================================
    // FLYOUT
    // =========================================================================

    // Row IDs carry the instance's start like the main view's (FlyoutEvent_{id}_{UTC yyyyMMddHHmm}), so a series' rows differ
    AgendaRow Row(AgendaItem item)
    {
        var o     = item.Occurrence;
        var start = o.Start.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        return new AgendaRow(
            item.Title,
            item.When,
            LeafBrushes.FromHex(EventColors.ResolveAccent(o.ColorId, o.CalendarColor)),
            item.Link is null ? Visibility.Collapsed : Visibility.Visible,
            item.Link is { } link ? LinkSafety.ProviderOf(link) : null,
            $"FlyoutEvent_{o.EventId}_{start}",
            $"FlyoutJoin_{o.EventId}_{start}",
            () => Request(OpenEventRequested, o),
            () => Request(JoinRequested, o));
    }

    // Closes the flyout, then hands the event to the App (a click handler, so nothing may escape)
    void Request(EventHandler<CalendarOccurrence>? handler, CalendarOccurrence occurrence)
    {
        try
        {
            HideAgenda();
            handler?.Invoke(this, occurrence);
        }
        catch (Exception ex)
        {
            _log.Info("tray.flyout.click.failed", $"error={ex.GetType().Name}");
        }
    }

    void OnNextJoinClick(object sender, RoutedEventArgs e)
    {
        if (_model?.Next is { } next)
        {
            Request(JoinRequested, next.Item.Occurrence);
        }
    }

    void OnAgendaOpened(object sender, object e)
    {
        WatchPresses();
        _exitFinished = false;
        Slide(HiddenOffset(), new Point(0, 0), 0, 1, EnterDuration, enter: true, onDone: null);
        AgendaOpened?.Invoke(this, EventArgs.Empty);
    }

    // Esc, focus leaving, or HideAgenda: slide back behind the taskbar first, then close for real
    void OnAgendaClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_exitFinished || _shuttingDown)
        {
            return;
        }

        args.Cancel = true;
        Slide(new Point(PanelShift.X, PanelShift.Y), HiddenOffset(), AgendaPanel.Opacity, 0, ExitDuration, enter: false, onDone: () =>
        {
            _exitFinished = true;
            Agenda.Hide();
        });
    }

    void OnAgendaClosed(object sender, object e)
    {
        _agendaClosedAt = Environment.TickCount64;
        _exitFinished   = false;

        // The rows stay for the next open (UpdateAgenda refills them); only the model goes
        _model = null;
        HideHostIfIdle();
        StopWatchingIfIdle();
        AgendaClosed?.Invoke(this, EventArgs.Empty);
    }

    // Far enough to put the whole panel past the frame's taskbar-side edge
    Point HiddenOffset()
    {
        var width  = AgendaPanel.Width + TrayPlacement.MarginDip;
        var height = AgendaPanel.Height + TrayPlacement.MarginDip;
        return _edge switch
        {
            TaskbarEdge.Top   => new Point(0, -height),
            TaskbarEdge.Left  => new Point(-width, 0),
            TaskbarEdge.Right => new Point(width, 0),
            _                 => new Point(0, height),
        };
    }

    // Slide and fade together; the storyboard is this method's own (never read back from the panel)
    void Slide(Point from, Point to, double fromOpacity, double toOpacity, TimeSpan duration, bool enter, Action? onDone)
    {
        _motion?.Stop();
        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(PanelShift, "X", from.X, to.X, duration, enter));
        storyboard.Children.Add(Animate(PanelShift, "Y", from.Y, to.Y, duration, enter));
        storyboard.Children.Add(Animate(AgendaPanel, "Opacity", fromOpacity, toOpacity, duration, enter));
        storyboard.Completed += (_, _) =>
        {
            if (_motion == storyboard)
            {
                _motion = null;
                onDone?.Invoke();
            }
        };

        _motion = storyboard;
        storyboard.Begin();
    }

    // Fluent curves: decelerate (0,0)-(0,1) in, accelerate (1,0)-(1,1) out
    static DoubleAnimationUsingKeyFrames Animate(DependencyObject target, string property, double from, double to, TimeSpan duration, bool enter)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime   = duration,
            Value     = to,
            KeySpline = enter
                ? new KeySpline { ControlPoint1 = new Point(0, 0), ControlPoint2 = new Point(0, 1) }
                : new KeySpline { ControlPoint1 = new Point(1, 0), ControlPoint2 = new Point(1, 1) },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    // =========================================================================
    // MENU
    // =========================================================================

    // A closed window has no AppWindow to hide
    void HideHostIfIdle()
    {
        if (!_shuttingDown && !Menu.IsOpen && !Agenda.IsOpen)
        {
            AppWindow.Hide();
        }
    }

    void OnMenuClosed(object sender, object e)
    {
        HideHostIfIdle();
        StopWatchingIfIdle();
    }

    // =========================================================================
    // PRESSES OUTSIDE
    // =========================================================================

    // While the menu or flyout is open, any mouse press off them closes it. Light dismiss alone missed some: it needs the
    // host to lose the foreground, which Windows doesn't always report. A press on the tray icon closes it too, and the
    // icon's own click that follows doesn't open the flyout again
    void WatchPresses()
    {
        if (!MouseDownWatch.IsWatching && !MouseDownWatch.Start(OnPressed))
        {
            _log.Info("tray.watch.refused");
        }
    }

    void StopWatchingIfIdle()
    {
        if (!Menu.IsOpen && !Agenda.IsOpen)
        {
            MouseDownWatch.Stop();
        }
    }

    // Raised inside the hook (on this thread): only the cheap check here, the close after it returns
    void OnPressed(int x, int y)
    {
        if (InvisibleHost.IsOnPopup(this, x, y))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Each Popup Remembers Its Own Dismissal, so the icon click that closed one can still open the other
                var now = Environment.TickCount64;
                if (Menu.IsOpen)
                {
                    _menuDismissedAt = now;
                    Menu.Hide();
                }

                if (Agenda.IsOpen)
                {
                    _agendaDismissedAt = now;
                    HideAgenda();
                }
            }
            catch (Exception ex)
            {
                _log.Info("tray.dismiss.failed", $"error={ex.GetType().Name}");
            }
        });
    }

    // The flyout's Open calendar: the flyout closes, then the main window opens (a click handler, so nothing may escape)
    void OnFlyoutOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            HideAgenda();
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Info("tray.flyout.click.failed", $"error={ex.GetType().Name}");
        }
    }

    // A click handler, so nothing may escape
    void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Info("tray.flyout.click.failed", $"error={ex.GetType().Name}");
        }
    }

    // A click handler, so nothing may escape
    void OnNewEventClick(object sender, RoutedEventArgs e)
    {
        try
        {
            HideAgenda();
            NewEventRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Info("tray.flyout.click.failed", $"error={ex.GetType().Name}");
        }
    }

    void OnJoinNextClick(object sender, RoutedEventArgs e) => JoinNextRequested?.Invoke(this, EventArgs.Empty);

    void OnSyncClick(object sender, RoutedEventArgs e) => SyncRequested?.Invoke(this, EventArgs.Empty);

    // A click handler, so nothing may escape
    void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Info("tray.flyout.click.failed", $"error={ex.GetType().Name}");
        }
    }

    void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
