using LeafCalendar.App.Api;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LeafCalendar.App.Views.Api;

/// <summary>
/// The local API's "{app} wants to connect to Leaf" window (approval window spec 2): small, always on top, Close only,
/// centered on the monitor under the cursor and as tall as its content. It shows one icon (the app's own when it sent
/// one Windows can decode, else the program's, else the generic app glyph), who asks, what it can do, the runtime
/// warning when it applies, and the full path, then Allow (the accent button, on 3 seconds after the window opens) and
/// Don't allow. Neither is the default: the question area (named by the headline, so screen readers say who's asking)
/// takes focus, so Enter and Space do nothing. Esc and Close refuse. It follows the theme setting while it's open.
/// </summary>
/// <remarks>
/// <see cref="Answered"/> is raised at most once, for a button, Esc, or the user closing the window. The app closes the
/// window from code when the question ends some other way (it timed out, or the API stopped); that raises nothing.
/// </remarks>
public sealed partial class ApprovalWindow : Window
{
    // Client Width In DIPs (the spec's 420); The Height Follows The Content
    private const double ClientWidth = 420;

    // How Long Allow Stays Off After The Window Opens (a click or keystroke meant for something else can't approve)
    private static readonly TimeSpan AllowDelay = TimeSpan.FromSeconds(3);

    private readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    private readonly AppLog _log;

    // Kept in a field so its Tick keeps firing
    private DispatcherQueueTimer? _allowTimer;
    private bool _answered;

    /// <summary>Creates the window for one question.</summary>
    public ApprovalWindow(PendingApproval request, AppTheme theme, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        _log = log;
        InitializeComponent();

        // Window Presenter: Fixed Size, Close Only, Always On Top
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(_presenter);
        AppWindow.SetIcon(App.IconPath);

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        MainWindow.ApplyTheme(AppWindow, RootGrid, theme);

        // Text
        var caller = request.Caller;
        HeadlineText.Text = ApiApprovalText.Headline(caller);
        BodyText.Text = ApiApprovalText.Body;
        if (ApiApprovalText.RuntimeWarning(caller) is { } warning)
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }

        PathText.Text = ApiApprovalText.DisplayPath(caller);
        AutomationProperties.SetName(QuestionArea, HeadlineText.Text);

        // Closed By The User Refuses (Close from code raises no Closing); Allow's timer stops either way
        AppWindow.Closing += (_, _) => Answer(false);
        Closed += (_, _) => _allowTimer?.Stop();
        RootGrid.Loaded += OnLoaded;
        WindowPlacement.CenterOnCursorMonitor(AppWindow, ClientWidth, 320);
    }

    /// <summary>The question this window asks.</summary>
    public PendingApproval Request { get; }

    /// <summary>Raised once with true for Allow, false for Don't allow, Esc, or Close.</summary>
    public event EventHandler<bool>? Answered;

    /// <summary>Applies the theme setting (it changed while the window is open).</summary>
    public void ApplyTheme(AppTheme theme) => MainWindow.ApplyTheme(AppWindow, RootGrid, theme);

    // Starts Allow's Delay, Fits The Height To The Content, Puts Focus On The Question, Then Loads The Icon; nothing may escape
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Allow Turns On After The Delay (first, so nothing below can leave it off; Don't allow works at once)
            _allowTimer = DispatcherQueue.CreateTimer();
            _allowTimer.Interval = AllowDelay;
            _allowTimer.IsRepeating = false;
            _allowTimer.Tick += OnAllowDelayEnded;
            _allowTimer.Start();

            RootGrid.Measure(new Size(ClientWidth, double.PositiveInfinity));
            WindowPlacement.CenterOnCursorMonitor(AppWindow, ClientWidth, RootGrid.DesiredSize.Height);
            QuestionArea.Focus(FocusState.Programmatic);

            // Screen Readers Say Who's Asking (the headline was set before it had an automation peer, so no change was raised)
            FrameworkElementAutomationPeer.CreatePeerForElement(HeadlineText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

            // The Icon: the app's own when it sent one Windows can decode, else the program's
            if (await ApiIcons.PngAsync(Request.Caller.Hello.Icon) is { } app)
            {
                ShowIcon(app, "ApiApprovalAppIcon");
            }
            else
            {
                await ShowProgramIconAsync(logUndecodable: !Request.Caller.Hello.Icon.IsEmpty);
            }
        }
        catch (Exception ex)
        {
            _log.Info("api.window.failed", $"error={ex.GetType().Name}");
        }
    }

    // A dispatcher callback, so nothing may escape
    private void OnAllowDelayEnded(DispatcherQueueTimer sender, object args)
    {
        try
        {
            AllowButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _log.Info("api.window.failed", $"error={ex.GetType().Name}");
        }
    }

    // An icon failed after its header was read (corrupt pixels): the app's falls back to the program's, the program's to
    // the generic glyph, so the slot is never empty; nothing may escape
    private async void OnIconFailed(object sender, ExceptionRoutedEventArgs e)
    {
        try
        {
            if (AutomationProperties.GetAutomationId(IconImage) == "ApiApprovalAppIcon")
            {
                await ShowProgramIconAsync(logUndecodable: true);
            }
            else
            {
                ShowGlyph();
            }
        }
        catch (Exception ex)
        {
            _log.Info("api.window.failed", $"error={ex.GetType().Name}");
        }
    }

    // The program's own icon, or the generic glyph when it has none (an app icon Windows couldn't decode is logged)
    private async Task ShowProgramIconAsync(bool logUndecodable)
    {
        if (logUndecodable)
        {
            _log.Info("api.icon.undecodable");
        }

        if (await ApiIcons.ProgramAsync(Request.Caller.Path, 96) is { } program)
        {
            ShowIcon(program, "ApiApprovalProgramIcon");
        }
        else
        {
            ShowGlyph();
        }
    }

    private void ShowGlyph()
    {
        IconImage.Visibility = Visibility.Collapsed;
        IconGlyph.Visibility = Visibility.Visible;
    }

    // Shows one image in the icon slot, its automation ID saying whose it is (UI tests read it)
    private void ShowIcon(ImageSource image, string automationId)
    {
        AutomationProperties.SetAutomationId(IconImage, automationId);
        IconImage.Source = image;
        IconImage.Visibility = Visibility.Visible;
        IconGlyph.Visibility = Visibility.Collapsed;
    }

    private void OnAllowClick(object sender, RoutedEventArgs e) => Finish(true);

    private void OnDenyClick(object sender, RoutedEventArgs e) => Finish(false);

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(false);
    }

    // The window closes even if answering failed, so it never sits on screen ignoring clicks
    private void Finish(bool allow)
    {
        try
        {
            Answer(allow);
        }
        finally
        {
            Close();
        }
    }

    private void Answer(bool allow)
    {
        if (_answered)
        {
            return;
        }

        _answered = true;
        Answered?.Invoke(this, allow);
    }
}
