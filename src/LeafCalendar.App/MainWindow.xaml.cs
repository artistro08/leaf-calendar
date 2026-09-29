using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views;
using LeafCalendar.Core.Sync;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App;

/// <summary>
/// Main window. It has a tall XAML title bar (the caption buttons match its 48 px height) and a
/// Mica backdrop, and it hosts a page frame. The back button shows only when the frame can go back.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly LeafServices _services;

    /// <summary>Creates the window and shows setup or accounts.</summary>
    public MainWindow(LeafServices services)
    {
        _services = services;
        InitializeComponent();

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        Activated += OnActivated;
        Closed    += async (_, _) => await _services.DisposeAsync();

        if (_services.Google is null)
        {
            ShowSetup();
        }
        else
        {
            ShowAccounts();
        }
    }

    void ShowSetup() =>
        ContentFrame.Navigate(typeof(SetupPage), new SetupViewModel(_services.Tokens, OnCredentialsSavedAsync));

    void ShowAccounts()
    {
        ContentFrame.Navigate(typeof(AccountsPage), new AccountsViewModel(_services, ShowSetup));
        ContentFrame.BackStack.Clear();
    }

    async Task OnCredentialsSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        ShowAccounts();
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _services.Google is not { } google)
        {
            return;
        }

        google.Loop.Mode = SyncMode.Visible;
        google.Loop.TriggerNow();
    }

    void OnBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }
}
