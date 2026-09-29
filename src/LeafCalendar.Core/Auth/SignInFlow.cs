using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Signs a Google account in through the system browser.
/// </summary>
/// <remarks>
/// The steps are:
/// <list type="number">
/// <item>Start a loopback listener, create a PKCE verifier and state, and open Google's consent page.</item>
/// <item>Wait up to <see cref="Timeout"/> for the redirect, then check the state before trusting anything in it.</item>
/// <item>Exchange the code, and require calendar access plus a refresh token.</item>
/// <item>Read the user's ID and email, save the refresh token to <see cref="ITokenStore"/>, and upsert the account.</item>
/// </list>
/// Every failure throws <see cref="SignInException"/> with a message meant for the user.
/// </remarks>
public sealed class SignInFlow(
    GoogleOAuthClient oauth,
    ITokenStore tokenStore,
    AccessTokenProvider accessTokens,
    LeafDatabase database,
    Func<Uri, Task> openBrowser,
    TimeProvider time,
    AppLog log)
{
    /// <summary>How long Leaf waits for the browser to come back.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs sign-in and returns the saved account.</summary>
    /// <exception cref="SignInException">Sign-in failed, was cancelled, or timed out.</exception>
    public async Task<Account> RunAsync(string? loginHint, CancellationToken ct)
    {
        using var listener = new LoopbackListener();
        var verifier = Pkce.CreateVerifier();
        var state    = Pkce.CreateState();

        // Open Consent Page
        log.Info("signin.started");
        await openBrowser(oauth.BuildAuthorizationUrl(listener.RedirectUri, state, Pkce.CreateChallenge(verifier), loginHint));

        // Wait For Redirect
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        IReadOnlyDictionary<string, string> reply;
        try
        {
            reply = await listener.WaitForCallbackAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Fail("timeout", "Sign-in timed out. Try again.");
        }

        // Check Reply
        if (!Pkce.StateMatches(state, reply.GetValueOrDefault("state")))
        {
            throw Fail("state-mismatch", "The sign-in reply didn't match this request. Try again.");
        }

        if (reply.TryGetValue("error", out var error))
        {
            throw Fail("google-error", error == "access_denied" ? "Sign-in was cancelled." : "Google couldn't complete sign-in. Try again.");
        }

        if (!reply.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw Fail("missing-code", "Google didn't return a sign-in code. Try again.");
        }

        // Exchange Code
        var tokens = await oauth.ExchangeCodeAsync(code, verifier, listener.RedirectUri, ct);

        if (!tokens.HasScope(GoogleOAuthClient.CalendarScope))
        {
            await TryRevokeAsync(tokens, ct);
            throw Fail("calendar-scope-missing", "Leaf needs calendar access. Sign in again and allow calendar access.");
        }

        if (tokens.RefreshToken is null)
        {
            throw Fail("refresh-token-missing", "Google didn't allow offline access. Try again.");
        }

        // Save Account
        var user = await oauth.GetUserInfoAsync(tokens.AccessToken, ct);
        tokenStore.SetRefreshToken(user.Sub, tokens.RefreshToken);
        accessTokens.Seed(user.Sub, tokens);

        var account = new Account(user.Sub, user.Email, user.Name, user.Picture, AccountStatus.Ok);
        using (var conn = database.Open())
        {
            AccountStore.Upsert(conn, account);
        }

        log.Info("signin.completed", $"account={user.Sub}");
        return account;
    }

    async Task TryRevokeAsync(TokenSet tokens, CancellationToken ct)
    {
        try
        {
            await oauth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or GoogleApiException)
        {
            log.Error("signin.revoke-failed", ex);
        }
    }

    SignInException Fail(string reason, string message)
    {
        log.Info("signin.failed", $"reason={reason}");
        return new SignInException(message);
    }
}
