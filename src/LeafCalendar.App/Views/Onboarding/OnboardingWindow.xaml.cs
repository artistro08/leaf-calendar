using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.Interop;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LeafCalendar.App.Views.Onboarding;

/// <summary>
/// The first-run window: a small fixed window (520 × 640 DIPs, Close only, centered on the monitor under the cursor)
/// with Mica and a stock title bar, shown instead of the main window until Leaf has an OAuth client and an account.
/// Steps (Welcome, Google Cloud OAuth client, Sign in, Syncing, Done) slide through a frame; a pinned footer holds
/// Back, a line-style <see cref="PipsPager"/> step indicator, and the step's primary action. Closing before the first
/// sync finishes asks "Leave setup?"; leaving exits the app unless an account already exists, in which case the main
/// window opens. "Open Leaf Calendar" opens the main window and closes this one.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable; the view model is disposed when the window closes.")]
public sealed partial class OnboardingWindow : Window
{
    // Client Size In DIPs (the design standard's onboarding window; the steps fit without scrolling at 100%)
    const double ClientWidth  = 520;
    const double ClientHeight = 640;

    readonly LeafServices _services;
    readonly Action _openMain;
    readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    bool _asking;
    bool _closing;

    /// <summary>Creates the window. <paramref name="openMain"/> opens the main window (when setup finishes, or is left with an account).</summary>
    public OnboardingWindow(LeafServices services, Action openMain)
    {
        _services = services;
        _openMain = openMain;
        ViewModel = new OnboardingViewModel(services, DispatcherQueue);
        InitializeComponent();
        StepPips.NumberOfPages = OnboardingFlow.StepCount;

        // Window Presenter: Fixed Size, Close Only (so double-clicking the title bar doesn't maximize)
        _presenter.IsResizable   = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        AppWindow.SetPresenter(_presenter);
        AppWindow.SetIcon(App.IconPath);

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // Window Title (Alt+Tab, taskbar, UI tests): the stock TitleBar copies its own "Leaf Calendar" onto the
        // window when it loads, so put the window's title back once it has
        var title = Title;
        AppTitleBar.Loaded += (_, _) => AppWindow.Title = title;

        // Theme (the saved setting, when there is one)
        using (var conn = services.Database.Open())
        {
            MainWindow.ApplyTheme(AppWindow, RootGrid, SettingsStore.Load(conn).Theme);
        }

        // Steps
        ViewModel.StepChanged += (_, forward) => ShowStep(forward ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft);
        ViewModel.Finished    += (_, _) => Finish();
        StepFrame.Navigate(typeof(OnboardingStepPage), new OnboardingStepArgs(ViewModel, ViewModel.Step), new SuppressNavigationTransitionInfo());

        // Closing Asks First
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => ViewModel.Dispose();

        WindowPlacement.CenterOnCursorMonitor(AppWindow, ClientWidth, ClientHeight);
    }

    /// <summary>The steps' state and work.</summary>
    public OnboardingViewModel ViewModel { get; }

    /// <summary>Restores the window if needed and brings it to the front (another launch was redirected here).</summary>
    public void BringToFront()
    {
        if (_presenter.State == OverlappedPresenterState.Minimized)
        {
            _presenter.Restore();
        }

        Activate();
        PInvoke.SetForegroundWindow(new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id)));
    }

    void ShowStep(SlideNavigationTransitionEffect effect)
    {
        StepFrame.Navigate(typeof(OnboardingStepPage), new OnboardingStepArgs(ViewModel, ViewModel.Step), new SlideNavigationTransitionInfo { Effect = effect });
        StepFrame.BackStack.Clear();
    }

    void OnBackClick(object sender, RoutedEventArgs e) => ViewModel.GoBack();

    void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.CancelSignIn();

    // RunPrimaryAsync never throws (failures show on the step)
    void OnPrimaryClick(object sender, RoutedEventArgs e) => _ = ViewModel.RunPrimaryAsync();

    // "Open Leaf Calendar": the main window opens first, so the app doesn't exit when this one closes
    void Finish()
    {
        _closing = true;
        _openMain();
        Close();
    }

    // Leave Setup: exits (this is the only window) unless an account already exists, then the main window opens
    void Leave()
    {
        _closing = true;
        if (!OnboardingFlow.ExitsOnLeave(_services.HasAccount()))
        {
            _openMain();
        }

        Close();
    }

    // The X: after the first sync it finishes; before, it asks (Keep setting up is the default)
    async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing)
        {
            return;
        }

        if (!ViewModel.AsksBeforeClosing)
        {
            _closing = true;
            _openMain();
            return;
        }

        args.Cancel = true;
        if (_asking)
        {
            return;
        }

        _asking = true;
        var dialog = new ContentDialog
        {
            XamlRoot          = RootGrid.XamlRoot,
            RequestedTheme    = RootGrid.ActualTheme,
            Title             = "Leave setup?",
            Content           = "Leaf needs a Google account to show your calendar. You can finish setup later.",
            PrimaryButtonText = "Leave",
            CloseButtonText   = "Keep setting up",
            DefaultButton     = ContentDialogButton.Close,
        };

        // async void: anything that escapes here would end the process
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Leave();
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("onboarding.leave.failed", ex);
        }
        finally
        {
            _asking = false;
        }
    }
}
