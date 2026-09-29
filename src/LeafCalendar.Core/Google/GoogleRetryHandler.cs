using System.Net;
using System.Security.Cryptography;

namespace LeafCalendar.Core.Google;

/// <summary>
/// Retries Google calls that failed for temporary reasons, using exponential backoff with jitter.
/// </summary>
/// <remarks>
/// Retries 429, any 5xx, and 403 when Google's reason is <c>rateLimitExceeded</c> or
/// <c>userRateLimitExceeded</c>. Honors <c>Retry-After</c> up to 60 seconds. Other failures
/// return right away with their body still readable.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/guides/errors"/>
public sealed class GoogleRetryHandler(TimeProvider time) : DelegatingHandler
{
    /// <summary>Total tries, including the first.</summary>
    public const int MaxAttempts = 5;

    static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (attempt == MaxAttempts || !await IsRetryableAsync(response, cancellationToken))
            {
                return response;
            }

            var delay = GetDelay(attempt, response);
            response.Dispose();
            await Task.Delay(delay, time, cancellationToken);
        }
    }

    static async Task<bool> IsRetryableAsync(HttpResponseMessage response, CancellationToken ct)
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

    static TimeSpan GetDelay(int attempt, HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter;
        }

        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 32));
        return backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 1000));
    }
}
