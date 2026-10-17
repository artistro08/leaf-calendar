using System.Net.Sockets;
using LeafCalendar.Core.Auth;

namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// Signs in to a scheduling provider through the system browser with your own OAuth client, and returns the refresh token.
/// </summary>
/// <remarks>
/// Follows the Google sign-in's safety rules: a random <c>state</c>; the listener binds 127.0.0.1 only, on the provider's
/// fixed port and path, and answers only a redirect carrying this state. PKCE is on for providers that ask for it
/// (<see cref="SchedulingOAuth.UsePkce"/>, Sunsama's public client) and off for Cal.com, whose confidential clients
/// prove themselves with the secret and whose token endpoint refuses <c>code_verifier</c>. The client secret goes only
/// in the token request body. Saving the refresh token is the caller's job.
/// </remarks>
public sealed class ProviderSignIn(HttpClient http, SchedulingOAuth oauth, ProviderClient client, Func<Uri, Task> openBrowser, TimeProvider time)
{
    /// <summary>How long Leaf Calendar waits for the browser to come back (the same as the Google sign-in).</summary>
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
            // PKCE Only When Asked: Cal.com's token endpoint refuses a code_verifier ("property code_verifier should not
            // exist"); the random state guards the redirect either way
            var state = Pkce.CreateState();
            var verifier = oauth.UsePkce ? Pkce.CreateVerifier() : null;
            var redirectUri = listener.RedirectUri.AbsoluteUri;

            // Start Timeout Before Opening The Browser
            using var timeout = new CancellationTokenSource(Timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            IReadOnlyDictionary<string, string> reply;
            try
            {
                await openBrowser(BuildAuthorizeUrl(redirectUri, state, verifier)).WaitAsync(linked.Token);
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
            List<KeyValuePair<string?, string?>> form =
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri),
            ];
            if (verifier is not null)
            {
                form.Add(new("code_verifier", verifier));
            }

            var token = await SchedulingTokenRequest.SendAsync(http, oauth, client, form, ct);

            return token.RefreshToken is { Length: > 0 } refreshToken
                ? refreshToken
                : throw new SchedulingAuthException("The provider didn't return a refresh token.");
        }
    }

    // The consent page address: only the client ID and the verifier's challenge, never the secret or the verifier
    private Uri BuildAuthorizeUrl(string redirectUri, string state, string? verifier)
    {
        List<KeyValuePair<string, string>> query =
        [
            new("response_type", "code"),
            new("client_id", client.ClientId),
            new("redirect_uri", redirectUri),
            new("scope", string.Join(' ', oauth.Scopes)),
            new("state", state),
        ];
        if (verifier is not null)
        {
            query.Add(new("code_challenge", Pkce.CreateChallenge(verifier)));
            query.Add(new("code_challenge_method", "S256"));
        }

        if (oauth.Resource is { } resource)
        {
            query.Add(new("resource", resource.AbsoluteUri));
        }

        return new Uri($"{oauth.Authorize.AbsoluteUri}?{string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))}");
    }
}
