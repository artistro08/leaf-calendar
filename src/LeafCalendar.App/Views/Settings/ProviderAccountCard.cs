using LeafCalendar.App.Controls;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Scheduling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// One scheduling provider's card in Settings › Accounts (scheduling providers spec 9.3), built from its
/// <see cref="SchedulingProviderInfo"/> so a new OAuth provider needs no new page code: add your own OAuth client, sign in
/// through the browser (with Cancel), sign out, sign in again, or remove the client.
/// </summary>
/// <remarks>
/// The client secret only passes through the form's <see cref="PasswordBox"/>, which is cleared after Save, on Cancel and
/// when the page lets go (<see cref="Release"/>); it's never logged. The OAuth client can't be changed while signed in:
/// Sign out first.
/// </remarks>
internal sealed class ProviderAccountCard
{
    // Segoe Fluent "DateTime", the Scheduling nav item's icon
    private const string Glyph = "";

    // Shown when saving, signing out or removing fails for a reason the user can't act on (the Accounts page's own text)
    private const string GenericFailure = "Something went wrong. Try again.";

    private readonly SettingsContext _context;
    private readonly SchedulingProviderInfo _info;
    private readonly SettingRow _row;
    private readonly ProgressRing _ring;
    private readonly Button _cancelSignIn;
    private readonly Button _add;
    private readonly Button _remove;
    private readonly Button _signOut;
    private readonly Button _signIn;
    private readonly InfoBar _error;
    private readonly InfoBar _success;
    private readonly StackPanel _form;
    private readonly TextBox _clientId;
    private readonly PasswordBox _clientSecret;
    private readonly Button _save;

    // Cancels the sign-in waiting on the browser; null when none is
    private CancellationTokenSource? _signInCts;

    // True while a save, sign-in, sign-out or removal runs (the buttons are off)
    private bool _busy;

    /// <summary>Builds the card for one provider.</summary>
    public ProviderAccountCard(SettingsContext context, SchedulingProviderInfo info)
    {
        _context = context;
        _info = info;
        var id = info.Id;

        // State Buttons (only the current state's show, see Refresh)
        _ring = new ProgressRing { Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 0) };
        AutomationProperties.SetName(_ring, "Finish signing in in your browser.");
        _cancelSignIn = NewButton("Cancel", $"ProviderCancelSignIn_{id}", (_, _) => _signInCts?.Cancel());
        _add = NewButton("Add OAuth client…", $"ProviderAddClient_{id}", (_, _) => ShowForm(true));
        _remove = NewButton("Remove OAuth client", $"ProviderRemoveClient_{id}", OnRemoveClick);
        _signOut = NewButton("Sign out", $"ProviderSignOut_{id}", OnSignOutClick);
        _signIn = NewButton($"Sign in with {info.Name}", $"ProviderSignIn_{id}", OnSignInClick);
        _signIn.Style = StyleOf("AccentButtonStyle");

        // Card
        _row = new SettingRow
        {
            Glyph = Glyph,
            Header = info.Name,
            Description = info.Description,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _ring, _cancelSignIn, _add, _remove, _signOut, _signIn },
            },
        };
        AutomationProperties.SetName(_row, info.Name);
        AutomationProperties.SetAutomationId(_row, $"ProviderCard_{id}");

        // Error (collapsed while closed: a closed InfoBar still takes its margin)
        _error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetAutomationId(_error, $"ProviderError_{id}");

        // Success (above the card, after a sign-in; closable, and cleared by the next action)
        _success = new InfoBar
        {
            Severity = InfoBarSeverity.Success,
            IsClosable = true,
            Message = $"Successfully signed in to {info.Name}",
            Visibility = Visibility.Collapsed,
        };
        _success.Closed += (_, _) => SetVisible(_success, false);
        AutomationProperties.SetLiveSetting(_success, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_success, $"ProviderSuccess_{id}");

        // Client Form: the guide with a link to the provider's client page, the redirect URI to register (selectable),
        // then the client ID and secret, and Save and Cancel right-aligned like the Google OAuth client form
        var clientPage = new Hyperlink { Inlines = { new Run { Text = info.ClientPageName } } };
        AutomationProperties.SetAutomationId(clientPage, $"ProviderClientPage_{id}");
        clientPage.Click += (_, _) => _ = context.Services.LaunchAsync(info.OAuth.ClientPage);
        var guide = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Inlines = { new Run { Text = "Create an OAuth client in " }, clientPage, new Run { Text = ", and add this redirect URI:" } },
        };
        var redirect = new TextBlock
        {
            Text = $"http://127.0.0.1:{info.OAuth.RedirectPort}{info.OAuth.RedirectPath}",
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
        };
        AutomationProperties.SetAutomationId(redirect, $"ProviderRedirectUri_{id}");

        _clientId = new TextBox { Width = 320 };
        AutomationProperties.SetName(_clientId, "Client ID");
        AutomationProperties.SetAutomationId(_clientId, $"ProviderClientId_{id}");
        _clientId.TextChanged += (_, _) => UpdateSave();
        _clientSecret = new PasswordBox { Width = 320 };
        AutomationProperties.SetName(_clientSecret, "Client secret");
        AutomationProperties.SetAutomationId(_clientSecret, $"ProviderClientSecret_{id}");
        _clientSecret.PasswordChanged += (_, _) => UpdateSave();

        _save = NewButton("Save", $"ProviderSaveClient_{id}", OnSaveClick);
        _save.Style = StyleOf("AccentButtonStyle");
        _save.IsEnabled = false;
        var cancelForm = NewButton("Cancel", $"ProviderCancelClient_{id}", (_, _) => ShowForm(false));

        _form = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Children =
            {
                guide,
                redirect,
                new SettingRow { Header = "Client ID", Content = _clientId },
                new SettingRow { Header = "Client secret", Content = _clientSecret },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { _save, cancelForm },
                },
            },
        };

        View = new StackPanel { Spacing = 4, Children = { _success, _row, _error, _form } };
        Refresh();
    }

    /// <summary>The success message, the card, its error and its client form, for the page to place.</summary>
    public StackPanel View { get; }

    private SchedulingHub Hub => _context.Services.Scheduling;

    /// <summary>Shows the provider's state: only that state's buttons, and the line under its name.</summary>
    public void Refresh()
    {
        var state = Hub.StateOf(_info.Id);
        var signingIn = _signInCts is not null;
        var idle = !signingIn && !_busy;

        _row.Description = signingIn
            ? "Finish signing in in your browser."
            : state switch
            {
                ProviderState.SignedIn => "Signed in",
                ProviderState.NeedsReconnect => $"{_info.Name} signed Leaf Calendar out. Sign in again to keep sharing booking links.",
                _ => _info.Description,
            };

        // A client saved elsewhere (or this form's Save) closes the form
        if (state != ProviderState.NoClient)
        {
            ShowForm(false);
        }

        _ring.IsActive = signingIn;
        SetVisible(_ring, signingIn);
        SetVisible(_cancelSignIn, signingIn);
        SetVisible(_add, !signingIn && state == ProviderState.NoClient && _form.Visibility == Visibility.Collapsed);
        SetVisible(_remove, !signingIn && state == ProviderState.NotSignedIn);
        SetVisible(_signOut, !signingIn && state is ProviderState.SignedIn or ProviderState.NeedsReconnect);
        SetVisible(_signIn, !signingIn && state is ProviderState.NotSignedIn or ProviderState.NeedsReconnect);
        _signIn.Content = state == ProviderState.NeedsReconnect ? "Sign in again" : $"Sign in with {_info.Name}";

        _add.IsEnabled = idle;
        _remove.IsEnabled = idle;
        _signOut.IsEnabled = idle;
        _signIn.IsEnabled = idle;
    }

    /// <summary>The page is going: a sign-in waiting on the browser is canceled, and the form and its secret are cleared.</summary>
    public void Release()
    {
        _signInCts?.Cancel();
        ShowForm(false);
        ShowError(null);
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    private static void SetVisible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button NewButton(string content, string automationId, RoutedEventHandler click)
    {
        var button = new Button { Content = content };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += click;
        return button;
    }

    private void ShowError(string? message)
    {
        ShowSuccess(false);
        _error.Message = message ?? "";
        _error.IsOpen = message is not null;
        SetVisible(_error, message is not null);
    }

    private void ShowSuccess(bool show)
    {
        _success.IsOpen = show;
        SetVisible(_success, show);
    }

    // Opens or closes the client form; closing clears both boxes, so the secret never lingers
    private void ShowForm(bool show)
    {
        if (!show)
        {
            _clientId.Text = "";
            _clientSecret.Password = "";
        }

        SetVisible(_form, show);
        SetVisible(_add, !show && Hub.StateOf(_info.Id) == ProviderState.NoClient && _signInCts is null);
        if (show)
        {
            ShowError(null);
            _clientId.Focus(FocusState.Programmatic);
        }
    }

    // Both boxes are required
    private void UpdateSave() => _save.IsEnabled = !_busy && _clientId.Text.Trim().Length > 0 && _clientSecret.Password.Trim().Length > 0;

    // Save: stores both encrypted (through the hub), then clears the boxes
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_save.IsEnabled)
        {
            return;
        }

        try
        {
            Hub.SaveClient(_info.Id, new ProviderClient(_clientId.Text, _clientSecret.Password));
        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("scheduling.accounts.client.save.failed", $"provider={_info.Id} error={ex.GetType().Name}");
            _clientSecret.Password = "";
            ShowError(GenericFailure);
            return;
        }

        ShowForm(false);
        Refresh();
    }

    // Logs why a sign-in failed: Leaf Calendar's own fixed exception text and the inner exception's type (plus a JSON path), never
    // anything Cal.com sent or any secret
    private void LogSignInFailure(Exception ex)
    {
        var reason = ex is SchedulingAuthException or SchedulingUnavailableException ? ex.Message : "";
        var inner = ex.InnerException is { } i ? i.GetType().Name : "";
        var path = ex.InnerException is System.Text.Json.JsonException { Path: { } p } ? p : "";
        _context.Services.Log.Info("scheduling.accounts.signin.failed", $"provider={_info.Id} error={ex.GetType().Name} reason=\"{reason}\" inner={inner} path={path}");
    }

    // Sign in (or sign in again) through the browser, with Cancel; a refusal or failure says why and saves nothing
    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _signInCts is not null)
        {
            return;
        }

        var services = _context.Services;
        using var cts = new CancellationTokenSource();
        _signInCts = cts;
        _busy = true;
        ShowError(null);
        Refresh();

        // async void: anything that escapes here would terminate the process
        try
        {
            await Hub.SignInAsync(_info.Id, services.OpenSignInPageAsync, cts.Token);
            ShowSuccess(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Canceled (Cancel, or the page let go): nothing was saved, so there's nothing to say
        }
        catch (SchedulingAuthException ex)
        {
            LogSignInFailure(ex);
            ShowError($"{_info.Name} didn't accept this OAuth client. Check the client ID and secret, and that the redirect URI is registered.");
        }
        catch (SchedulingPortBusyException)
        {
            ShowError($"Port {_info.OAuth.RedirectPort} is busy, so Leaf Calendar can't finish signing in. Close the app using it and try again.");
        }
        catch (Exception ex) when (ex is SchedulingUnavailableException or InvalidDataException)
        {
            LogSignInFailure(ex);
            ShowError($"Couldn't reach {_info.Name}. Check your connection and try again.");
        }
        catch (TimeoutException)
        {
            services.Log.Info("scheduling.accounts.signin.timeout", $"provider={_info.Id}");
            ShowError("Sign-in timed out. Try again.");
        }
        catch (SignInException ex)
        {
            // The browser couldn't be opened (the Google sign-in's own text)
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            LogSignInFailure(ex);
            ShowError(GenericFailure);
        }
        finally
        {
            _signInCts = null;
            _busy = false;
            Refresh();
        }
    }

    // Sign out: removes the refresh token (the OAuth client stays), then the provider's saved groups become plain groups
    private async void OnSignOutClick(object sender, RoutedEventArgs e) => await EndAsync(() => Hub.SignOut(_info.Id), "signout", forgetSettings: false);

    // Remove the client: all of the provider's secrets, its settings and its saved groups
    private async void OnRemoveClick(object sender, RoutedEventArgs e) => await EndAsync(() => Hub.RemoveClient(_info.Id), "remove", forgetSettings: true);

    // Ends the provider's sign-in (a failure there changes nothing), then clears its saved groups off the UI thread and
    // reloads the calendar's groups. The buttons are off meanwhile, so a double click can't run it twice.
    private async Task EndAsync(Action end, string step, bool forgetSettings)
    {
        if (_busy)
        {
            return;
        }

        var services = _context.Services;
        var id = _info.Id;
        _busy = true;
        ShowError(null);
        Refresh();
        try
        {
            try
            {
                end();
            }
            catch (Exception ex)
            {
                services.Log.Info($"scheduling.accounts.{step}.failed", $"provider={id} error={ex.GetType().Name}");
                ShowError(GenericFailure);
                return;
            }

            if (forgetSettings)
            {
                _context.Save(s => s.WithoutProvider(id));
            }

            try
            {
                await Task.Run(() =>
                {
                    using var conn = services.Database.Open();
                    ShareGroupStore.ClearProvider(conn, id);
                });
                _context.Calendar.ReloadGroups();
            }
            catch (Exception ex)
            {
                services.Log.Info("scheduling.accounts.groups.clear.failed", $"provider={id} error={ex.GetType().Name}");
            }
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
