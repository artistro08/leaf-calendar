using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using System.Text.Json;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Signs a Google account in through the system browser.
/// </summary>
/// <remarks>
/// The steps are:
/// <list type="number">
/// <item>Start a loopback listener, create a PKCE verifier and state, and open Google's consent page.</item>
/// <item>Wait up to <see cref="Timeout"/> for the redirect carrying this state (others get a 404), then check it again.</item>
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
    /// <exception cref="SignInException">Sign-in failed, was canceled, or timed out.</exception>
    public Task<Account> RunAsync(string? loginHint, CancellationToken ct) => RunAsync(loginHint, null, ct);

    /// <summary>
    /// Runs sign-in and returns the saved account. With <paramref name="expectedAccountId"/>, only that account may come
    /// back (signing in again for more permissions): another Google user saves nothing.
    /// </summary>
    /// <exception cref="WrongAccountException">Google signed in a different user than <paramref name="expectedAccountId"/>.</exception>
    /// <exception cref="SignInException">Sign-in failed, was canceled, or timed out.</exception>
    public async Task<Account> RunAsync(string? loginHint, string? expectedAccountId, CancellationToken ct)
    {
        using var listener = new LoopbackListener();
        var verifier = Pkce.CreateVerifier();
        var state    = Pkce.CreateState();

        // Start Timeout Before Opening The Browser
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        IReadOnlyDictionary<string, string> reply;
        try
        {
            // Open Consent Page
            log.Info("signin.started");
            await openBrowser(oauth.BuildAuthorizationUrl(listener.RedirectUri, state, Pkce.CreateChallenge(verifier), loginHint)).WaitAsync(linked.Token);

            // Wait For Redirect
            reply = await listener.WaitForCallbackAsync(state, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.Info("signin.failed", "reason=timeout");
            throw new SignInTimeoutException("Sign-in timed out. Try again.");
        }

        // Check Reply
        if (!Pkce.StateMatches(state, reply.GetValueOrDefault("state")))
        {
            throw Fail("state-mismatch", "The sign-in reply didn't match this request. Try again.");
        }

        if (reply.TryGetValue("error", out var error))
        {
            throw Fail("google-error", error == "access_denied" ? "Sign-in was canceled." : "Google couldn't complete sign-in. Try again.");
        }

        if (!reply.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw Fail("missing-code", "Google didn't return a sign-in code. Try again.");
        }

        // Exchange Code
        TokenSet tokens;
        GoogleUserInfo user;
        try
        {
            tokens = await oauth.ExchangeCodeAsync(code, verifier, listener.RedirectUri, ct);

            if (!tokens.HasScope(GoogleOAuthClient.CalendarScope))
            {
                await TryRevokeAsync(tokens, ct);
                throw Fail("calendar-scope-missing", "Leaf needs calendar access. Sign in again and allow calendar access.");
            }

            if (tokens.RefreshToken is null)
            {
                throw Fail("refresh-token-missing", "Google didn't allow offline access. Try again.");
            }

            user = await oauth.GetUserInfoAsync(tokens.AccessToken, ct);
        }

        // The OAuth Client Was Rejected (wrong ID or secret, deleted, or not allowed): retrying can't help, fixing it can
        catch (GoogleApiException ex) when (ex.Reason is "invalid_client" or "unauthorized_client" or "deleted_client")
        {
            log.Error("signin.exchange-failed", ex);
            throw Fail("client-rejected", "Google didn't accept the OAuth client's ID or secret. Check them in setup's Connect your Google Cloud client step, or in Settings › Accounts › Change OAuth client.");
        }
        catch (Exception ex) when (ex is HttpRequestException or GoogleApiException or InvalidGrantException or InvalidDataException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            log.Error("signin.exchange-failed", ex);
            throw Fail("exchange-failed", "Google couldn't complete sign-in. Try again.");
        }

        // Only The Expected Account (nothing of the other user is kept; its grant isn't revoked, Leaf may hold it)
        if (expectedAccountId is not null && user.Sub != expectedAccountId)
        {
            log.Info("signin.failed", "reason=wrong-account");
            throw new WrongAccountException(user.Email, loginHint);
        }

        // Save Account
        tokenStore.SetRefreshToken(user.Sub, tokens.RefreshToken!);
        accessTokens.Seed(user.Sub, tokens);

        var account = new Account(user.Sub, user.Email, user.Name, user.Picture, AccountStatus.Ok, user.Hd ?? "");
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
        catch (Exception ex) when (ex is HttpRequestException or GoogleApiException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
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
