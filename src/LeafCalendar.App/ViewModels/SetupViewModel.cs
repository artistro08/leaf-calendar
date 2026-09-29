using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.App.ViewModels;

/// <summary>Setup page: collects and saves the user's Google OAuth client.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    readonly ITokenStore _tokens;
    readonly Func<Task> _onSaved;

    /// <summary>Prefills the client ID when one is already saved.</summary>
    public SetupViewModel(ITokenStore tokens, Func<Task> onSaved)
    {
        _tokens   = tokens;
        _onSaved  = onSaved;
        ClientId  = tokens.GetClientCredentials()?.ClientId ?? "";
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
    async Task SaveAsync()
    {
        Error = OAuthClientCredentials.Validate(ClientId, ClientSecret);
        if (Error is not null)
        {
            return;
        }

        _tokens.SetClientCredentials(new OAuthClientCredentials(ClientId.Trim(), ClientSecret.Trim()));
        ClientSecret = "";
        await _onSaved();
    }
}
