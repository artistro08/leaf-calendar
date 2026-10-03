using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Accounts › Change OAuth client: the client ID and secret as setting cards, with Save and Cancel. Uses the
/// setup form's view model, so validation and Credential Locker storage are the same. Saving reloads Google and goes
/// back to Accounts; Cancel just goes back.
/// </summary>
public sealed partial class ClientPage : Page
{
    SettingsContext _context = null!;

    /// <summary>Creates the page.</summary>
    public ClientPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <summary>Page view model.</summary>
    public SetupViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context  = (SettingsContext)e.Parameter;
        ViewModel = new SetupViewModel(_context.Services.Tokens, OnSavedAsync, _context.Services.Log);
        Bindings.Update();
    }

    async Task OnSavedAsync()
    {
        await _context.Services.ReloadGoogleAsync();
        _context.Host.Show(SettingsSection.Accounts);
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => _context.Host.Show(SettingsSection.Accounts);

    // Enter in the client ID moves on to the secret
    void OnClientIdKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            SecretBox.Focus(FocusState.Keyboard);
        }
    }

    // Enter saves (the form's primary action)
    void OnSecretKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.SaveCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.SaveCommand.Execute(null);
        }
    }
}
