using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>Settings › Accounts: the Google accounts (add, disconnect), Sync now, and the OAuth client.</summary>
public sealed partial class AccountsPage : Page
{
    SettingsContext _context = null!;

    /// <summary>Creates the page.</summary>
    public AccountsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <summary>x:Bind helper: failures show as errors, progress as information.</summary>
    public static InfoBarSeverity SeverityFor(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    /// <summary>Page view model (the window's, see <see cref="SettingsWindow.Accounts"/>).</summary>
    public AccountsViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context  = (SettingsContext)e.Parameter;
        ViewModel = _context.Window.Accounts;
        if (!ViewModel.IsBusy)
        {
            ViewModel.Refresh();
        }

        Bindings.Update();
    }

    void OnChangeClientClick(object sender, RoutedEventArgs e) => _context.Window.ShowClientSetup();

    // Disconnect deletes local data (and any edits Google doesn't have yet), so confirm first
    async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string accountId })
        {
            return;
        }

        var unsent  = ViewModel.UnsentFor(accountId);
        var content = "Leaf will sign out of this Google account and remove its calendars from this PC. Your Google Calendar isn't changed.";
        if (unsent > 0)
        {
            content += string.Create(CultureInfo.GetCultureInfo("en-US"), $"\n\n{unsent} {(unsent == 1 ? "change you made hasn't" : "changes you made haven't")} reached Google yet and will be lost.");
        }

        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            RequestedTheme    = ActualTheme,
            Title             = "Disconnect this account?",
            Content           = content,
            PrimaryButtonText = "Disconnect",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };

        // async void: anything that escapes here would terminate the process
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DisconnectAsync(accountId);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("account.disconnect.failed", ex);
        }
    }
}
