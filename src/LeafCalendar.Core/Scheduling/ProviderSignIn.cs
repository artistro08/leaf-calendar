using System.Net.Sockets;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// Signs in to a scheduling provider through the system browser with your own OAuth client, and returns the refresh token.
/// </summary>
/// <remarks>
/// Follows the Google sign-in's safety rules: PKCE S256 and a random <c>state</c>; the listener binds 127.0.0.1 only, on
/// the provider's fixed port and path, and answers only a redirect carrying this state. The client secret goes only in
/// the token request body. Saving the refresh token is the caller's job.
/// </remarks>
public sealed class ProviderSignIn(HttpClient http, SchedulingOAuth oauth, ProviderClient client, Func<Uri, Task> openBrowser, TimeProvider time)
{
    /// <summary>How long Leaf waits for the browser to come back (the same as the Google sign-in).</summary>
    public static readonly TimeSpan Timeout = SignInFlow.Timeout;

    /// <summary>Runs the sign-in and returns the provider's refresh token.</summary>
    /// <exception cref="SchedulingPortBusyException">Another app holds the redirect port.</exception>
    /// <exception cref="SchedulingAuthException">The sign-in was canceled or refused, or the answer had no tokens.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or answered with an error that may pass.</exception>
    /// <exception cref="TimeoutException">Nothing came back within <see cref="Timeout"/>.</exception>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        LoopbackListener listener;
        try
        {
            listener = new LoopbackListener(oauth.RedirectPort, oauth.RedirectPath);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new SchedulingPortBusyException("The sign-in port is in use by another app.", ex);
        }

        using (listener)
        {
            var verifier = Pkce.CreateVerifier();
            var state = Pkce.CreateState();
            var redirectUri = listener.RedirectUri.AbsoluteUri;

            // Start Timeout Before Opening The Browser
            using var timeout = new CancellationTokenSource(Timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            IReadOnlyDictionary<string, string> reply;
            try
            {
                await openBrowser(BuildAuthorizeUrl(redirectUri, state, Pkce.CreateChallenge(verifier))).WaitAsync(linked.Token);
                reply = await listener.WaitForCallbackAsync(state, linked.Token);
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("Sign-in timed out.", ex);
            }

            // Check Reply
            if (!Pkce.StateMatches(state, reply.GetValueOrDefault("state")) || reply.ContainsKey("error")
                || !reply.TryGetValue("code", out var code) || code.Length == 0)
            {
                throw new SchedulingAuthException("The provider didn't complete sign-in.");
            }

            // Exchange Code
            var token = await SchedulingTokenRequest.SendAsync(
                http,
                oauth,
                client,
                [
                    new("grant_type", "authorization_code"),
                    new("code", code),
                    new("redirect_uri", redirectUri),
                    new("code_verifier", verifier),
                ],
                ct);

            return token.RefreshToken is { Length: > 0 } refreshToken
                ? refreshToken
                : throw new SchedulingAuthException("The provider didn't return a refresh token.");
        }
    }

    // The consent page address: only the client ID, never the secret or the verifier
    private Uri BuildAuthorizeUrl(string redirectUri, string state, string challenge)
    {
        List<KeyValuePair<string, string>> query =
        [
            new("response_type", "code"),
            new("client_id", client.ClientId),
            new("redirect_uri", redirectUri),
            new("scope", string.Join(' ', oauth.Scopes)),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        ];

        return new Uri($"{oauth.Authorize.AbsoluteUri}?{string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))}");
    }
}
