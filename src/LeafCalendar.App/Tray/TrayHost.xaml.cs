using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;

namespace LeafCalendar.App.Tray;

/// <summary>
/// The tray's invisible host window (Layers' <c>TrayMenuHost</c>): the stock right-click menu opens from it, placed by
/// the taskbar edge (spec 8.3). Theme, Esc, outside-click dismissal, keyboard, and screen readers come from the stock
/// control. Created once with the tray and kept hidden; only Quit closes it.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable.")]
public sealed partial class TrayHost : Window
{
    Action? _pendingOpen;
    bool _shuttingDown;

    /// <summary>Creates the hidden host.</summary>
    public TrayHost()
    {
        InitializeComponent();
        InvisibleHost.Apply(this);

        // A host shown for the first time loads its content a moment later, so the open waits for it
        Root.Loaded += (_, _) => RunPendingOpen();

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

    /// <summary>New event.</summary>
    public event EventHandler? NewEventRequested;

    /// <summary>Join next meeting.</summary>
    public event EventHandler? JoinNextRequested;

    /// <summary>Sync now.</summary>
    public event EventHandler? SyncRequested;

    /// <summary>Settings….</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Opens the menu for a right-click at a screen point (physical pixels), growing away from the taskbar.</summary>
    public void ShowMenu(int x, int y, AppTheme theme)
    {
        var screen   = TrayScreen.At(x, y);
        var (ax, ay) = TrayPlacement.MenuAnchor(x, y, screen.Area, screen.Edge, screen.Scale);
        Root.RequestedTheme = MainWindow.ElementThemeOf(theme);
        Open(ax, ay, screen.Scale, position => Menu.ShowAt(Root, new FlyoutShowOptions { Position = position, Placement = MenuPlacement(screen.Edge) }));
    }

    /// <summary>Lets the window really close (Quit).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
    }

    // Moves the host to the anchor (twice: crossing into a monitor with another scale resizes it), shows it, takes the
    // foreground (light dismiss needs it), then opens at the anchor in DIPs from the host's client origin
    void Open(int x, int y, double scale, Action<Point> open)
    {
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(true);
        Activate();
        InvisibleHost.TakeForeground(this);

        var origin   = InvisibleHost.ClientOrigin(this);
        var position = new Point((x - origin.X) / scale, (y - origin.Y) / scale);
        _pendingOpen = () => open(position);
        if (Root.IsLoaded)
        {
            RunPendingOpen();
        }
    }

    void RunPendingOpen()
    {
        var open = _pendingOpen;
        _pendingOpen = null;
        open?.Invoke();
    }

    // Grows away from the taskbar (Sony Control's tray menu)
    static FlyoutPlacementMode MenuPlacement(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top   => FlyoutPlacementMode.BottomEdgeAlignedRight,
        TaskbarEdge.Left  => FlyoutPlacementMode.RightEdgeAlignedBottom,
        TaskbarEdge.Right => FlyoutPlacementMode.LeftEdgeAlignedBottom,
        _                 => FlyoutPlacementMode.TopEdgeAlignedRight,
    };

    // A closed window has no AppWindow to hide
    void HideHostIfIdle()
    {
        if (!_shuttingDown && !Menu.IsOpen)
        {
            AppWindow.Hide();
        }
    }

    void OnMenuClosed(object sender, object e) => HideHostIfIdle();

    void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    void OnNewEventClick(object sender, RoutedEventArgs e) => NewEventRequested?.Invoke(this, EventArgs.Empty);

    void OnJoinNextClick(object sender, RoutedEventArgs e) => JoinNextRequested?.Invoke(this, EventArgs.Empty);

    void OnSyncClick(object sender, RoutedEventArgs e) => SyncRequested?.Invoke(this, EventArgs.Empty);

    void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
