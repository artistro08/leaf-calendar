using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.App.ViewModels;

/// <summary>The OAuth client form (onboarding's client step and Settings › Accounts › Change OAuth client): collects and saves the user's Google OAuth client.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ITokenStore _tokens;
    private readonly Func<Task> _onSaved;
    private readonly AppLog _log;

    /// <summary>Prefills the client ID when one is already saved.</summary>
    public SetupViewModel(ITokenStore tokens, Func<Task> onSaved, AppLog log)
    {
        _tokens = tokens;
        _onSaved = onSaved;
        _log = log;
        ClientId = tokens.GetClientCredentials()?.ClientId ?? "";
    }

    /// <summary>Client ID text.</summary>
    [ObservableProperty]
    public partial string ClientId { get; set; }

    /// <summary>Client secret text.</summary>
    [ObservableProperty]
    public partial string ClientSecret { get; set; } = "";

    /// <summary>Validation message, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => Error is not null;

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = OAuthClientCredentials.Validate(ClientId, ClientSecret);
        if (Error is not null)
        {
            return;
        }

        // Save And Reload: a failure here must not terminate the process (the log gets the
        // exception type and redacted message only, never the secret). The secret box clears only once the reload
        // worked, so Try again still has it.
        try
        {
            _tokens.SetClientCredentials(new OAuthClientCredentials(ClientId.Trim(), ClientSecret.Trim()));
            await _onSaved();
            ClientSecret = "";
        }
        catch (Exception ex)
        {
            _log.Error("setup.save.failed", ex);
            Error = "Couldn't save the client. Try again.";
        }
    }
}
