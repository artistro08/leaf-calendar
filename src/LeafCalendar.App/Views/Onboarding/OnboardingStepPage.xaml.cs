using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace LeafCalendar.App.Views.Onboarding;

/// <summary>What the onboarding frame passes each step page: the window's view model and the step to show.</summary>
public sealed record OnboardingStepArgs(OnboardingViewModel ViewModel, OnboardingStep Step);

/// <summary>
/// One onboarding step. Every navigation makes a new page showing one step's panel (so the frame's slide moves the
/// old step out and the new one in); the panels bind to the window's shared view model.
/// </summary>
public sealed partial class OnboardingStepPage : Page
{
    /// <summary>Creates the page.</summary>
    public OnboardingStepPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);

        // Errors Scroll Into View (each sits under the step's controls, below the fold of the fixed-size window; its
        // size changes as it opens and its message wraps, so it's brought into view at its final size)
        foreach (var error in new[] { ClientError, SignInError, SyncError })
        {
            error.SizeChanged += (_, _) => BringIntoViewWhenOpen(error);
        }
    }

    /// <summary>The onboarding window's view model.</summary>
    public OnboardingViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = (OnboardingStepArgs)e.Parameter;
        ViewModel = args.ViewModel;

        // Show This Step's Panel
        WelcomeStep.Visibility = Shown(args.Step == OnboardingStep.Welcome);
        ClientStep.Visibility = Shown(args.Step == OnboardingStep.Client);
        SignInStep.Visibility = Shown(args.Step == OnboardingStep.SignIn);
        SyncingStep.Visibility = Shown(args.Step == OnboardingStep.Syncing);
        DoneStep.Visibility = Shown(args.Step == OnboardingStep.Done);

        Bindings.Update();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // The page is leaving: stop listening to the shared view model, so it can go away and its hidden boxes don't
        // keep writing back
        Bindings.StopTracking();
    }

    // Scrolls an open error into view (its own reference, never read back from the tree)
    private static void BringIntoViewWhenOpen(InfoBar error)
    {
        if (!error.IsOpen)
        {
            return;
        }

        error.StartBringIntoView();
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    // Safe as async void: LaunchAsync logs a failed launch and returns false, it never throws
    private async void OnConsoleLinkClick(Hyperlink sender, HyperlinkClickEventArgs args) => await ViewModel.OpenConsoleAsync();

    // Enter in the client ID moves on to the secret
    private void OnClientIdKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ClientSecretBox.Focus(FocusState.Keyboard);
        }
    }

    // Enter in the secret runs the step's primary action (Next)
    private void OnClientSecretKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ViewModel.RunPrimaryAsync();
        }
    }
}
