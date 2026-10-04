using System.Net;
using System.Security.Cryptography;

namespace LeafCalendar.Core.Google;

/// <summary>
/// Retries Google calls that failed for temporary reasons, using exponential backoff with jitter.
/// </summary>
/// <remarks>
/// Retries 429, any 5xx, and 403 when Google's reason is <c>rateLimitExceeded</c> or
/// <c>userRateLimitExceeded</c>. Honors <c>Retry-After</c> up to 60 seconds. No try starts more than
/// <see cref="RetryBudget"/> after the first; past that the last answer is returned, so the call ends inside
/// HttpClient's default 100-second timeout (which would read as offline). Other failures
/// return right away with their body still readable. Only GET and HEAD are retried: a write may have
/// been saved before the error, so replaying it could duplicate it or report a false conflict. Writes
/// fail fast and the outbox tries them again after its backoff (<see cref="Data.OutboxStore.Backoff"/>).
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/errors"/>
public sealed class GoogleRetryHandler(TimeProvider time) : DelegatingHandler
{
    /// <summary>Total tries, including the first.</summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>Latest a retry may start after the first try, leaving the last try 30 seconds of HttpClient's 100.</summary>
    private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(70);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Writes Are Never Replayed
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var started = time.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (attempt == MaxAttempts || !await IsRetryableAsync(response, cancellationToken))
            {
                return response;
            }

            // Out Of Time: Google's answer beats HttpClient's timeout
            var delay = GetDelay(attempt, response);
            if (time.GetElapsedTime(started) + delay > RetryBudget)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(delay, time, cancellationToken);
        }
    }

    private static async Task<bool> IsRetryableAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || status is >= 500 and <= 599)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        // Buffer so the caller can still read the body
        await response.Content.LoadIntoBufferAsync(ct);
        var error = GoogleJson.TryParse(await response.Content.ReadAsStringAsync(ct), GoogleJsonContext.Default.ApiErrorEnvelope);

        return error?.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") == true;
    }

    private static TimeSpan GetDelay(int attempt, HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter;
        }

        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 32));
        return backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 1000));
    }
}
