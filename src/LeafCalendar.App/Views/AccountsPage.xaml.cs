using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views;

/// <summary>Accounts page.</summary>
public sealed partial class AccountsPage : Page
{
    /// <summary>Creates the page.</summary>
    public AccountsPage() => InitializeComponent();

    /// <summary>Page view model (passed as the navigation parameter).</summary>
    public AccountsViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (AccountsViewModel)e.Parameter;
        Bindings.Update();
    }

    void OnChangeClientClick(object sender, RoutedEventArgs e) => ViewModel.OpenSetup();

    // Disconnect deletes local data, so confirm first
    async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var accountId = (string)((Button)sender).Tag;
        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = "Disconnect this account?",
            Content           = "Leaf will sign out of this Google account and remove its calendars from this PC. Your Google Calendar isn't changed.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DisconnectAsync(accountId);
        }
    }
}
